# LayoutParserDecrypt

Serviço Windows (**.NET Framework 4.8.1**) que **descriptografa** os *mappers* e *layouts* da **Sysmiddle** e os devolve por HTTP para a `LayoutParserApi`, que os grava no Redis.

## Por que existe

A cripto legada usa `RijndaelManaged` com chave (96 bits) e IV (152 bits) que **não são tamanhos válidos de AES**. No .NET moderno (Core/5+) `RijndaelManaged` é só um shim sobre AES e recusa esses tamanhos; por isso a descriptografia roda **neste serviço, em .NET Framework**, e a API (Linux / .NET moderno) o consome por HTTP em rede isolada.

```
Sysmiddle → SQL Server → LayoutParserApi (Linux) ──HTTP──► LayoutParserDecrypt (Windows) → texto claro
                                     └──► Redis → LayoutParserReact
```

## Contrato HTTP (congelado)

| Rota | Descrição |
|---|---|
| `GET /health` | `200 {"status":"ok"}` — ou `503 {"status":"degraded","reason":"..."}` se o self-test de descriptografia falhar (payload embutido, cache de 15 s). |
| `POST /decrypt` | Header `X-Correlation-ID` (opcional; GUID se ausente). Body `text/plain` UTF-8 com a cifra (Base64 com prefixo de 3 caracteres, que é descartado antes de decriptar). |
| outras | `404 {"error":"not found"}` |

Respostas de `/decrypt`: `200` texto claro · `400` corpo vazio · `422` falha de descriptografia · `413` corpo acima do limite · `503` servidor ocupado (com `Retry-After`) ou em desligamento · `504` timeout · `500` erro interno. Erros vêm em JSON `{"error":"..."}`. A cifra nunca é devolvida como texto claro em caso de falha, e o conteúdo nunca é logado (só tamanhos e correlation id).

## Configuração

Variáveis de ambiente (para o serviço, gravadas por `install-service.ps1` em `HKLM\SYSTEM\CurrentControlSet\Services\LayoutParserDecrypt\Environment`). Valores inválidos caem no default.

| Variável | Default | Efeito |
|---|---|---|
| `LAYOUTPARSER_DECRYPT_BIND` | `localhost` | Hosts do bind, separados por vírgula: nome de host, IP ou `localhost` |
| `LAYOUTPARSER_DECRYPT_PORT` | `5220` | Porta |
| `LAYOUTPARSER_DECRYPT_MAX_BODY_BYTES` | 20 MiB | Acima → `413` |
| `LAYOUTPARSER_DECRYPT_TIMEOUT_SECONDS` | `30` | Estouro → `504` |
| `LAYOUTPARSER_DECRYPT_MAX_CONCURRENCY` | `4` | Acima disso, após `QUEUE_WAIT_MS` → `503` |
| `LAYOUTPARSER_DECRYPT_QUEUE_WAIT_MS` | `2000` | Espera na fila antes do `503` |
| `LAYOUTPARSER_DECRYPT_SHUTDOWN_SECONDS` | `15` | Tempo que o stop aguarda requests em andamento (novos recebem `503`) |
| `LAYOUTPARSER_LOG_DIR` | `<exe>\logs` | Logs com rotação (`layoutparserlib.log`, 2 MiB × 10 arquivos) |

## Instalação (host Windows)

Pré-requisitos: PowerShell **elevado** e o exe (zip do CI: `LayoutParserDecrypt.exe` na raiz e `scripts\`; build local: `bin\Release\net48\`).

```powershell
# API no MESMO host (ex.: ambiente Dev): só loopback, sem firewall
.\scripts\install-service.ps1 -BindAddress localhost -Port 5220

# API em outro host (Linux): bind por nome + firewall só para o IP da API
.\scripts\install-service.ps1 -BindAddress layoutparserdecrypt.local -AllowedRemoteAddress 172.25.32.5
```

O script é idempotente e: copia o exe para `C:\Program Files\LayoutParserDecrypt`; cria a reserva `urlacl` para `LocalService`; cria o serviço (`LocalService`, sem admin, início automático atrasado, **restart automático em falha**); grava a configuração; cria a regra de firewall (TCP na porta, apenas `-AllowedRemoteAddress`); inicia e confere `/health`. Remoção: `.\scripts\uninstall-service.ps1`.

O nome/parâmetros dos scripts são consumidos pelo CI da API — não renomeie sem avisar.

**Sem autenticação por design.** A única barreira é o isolamento de rede: com bind não-loopback o instalador exige `-AllowedRemoteAddress` e recusa escopos amplos (`Any`, `0.0.0.0/0`, `LocalSubnet`) e bind curinga (`+`/`*`).

### Bind por nome de host (`layoutparserdecrypt.local`)

O `http.sys` roteia pelo cabeçalho `Host`; cada serviço registra o próprio nome. Outra API do host usa a porta 8080 e não conflita.

- **Chame sempre pelo nome:** `http://layoutparserdecrypt.local:5220`. Chamar por IP (`Host: 172.x.x.x`) não casa com o prefixo e o `http.sys` responde **`400`**.
- **Registrar o nome no Linux** (VM da API, `172.25.32.5`): adicione o IP do Windows em `/etc/hosts`:
  ```bash
  echo "<IP-do-Windows> layoutparserdecrypt.local" | sudo tee -a /etc/hosts
  ```
- `.local` é reservado a mDNS; por decisão do dono o risco é aceito e a resolução é feita por `/etc/hosts`, que tem prioridade sobre mDNS na configuração padrão.

### Validar

A partir da VM Linux (deve responder `{"status":"ok"}`):

```bash
curl http://layoutparserdecrypt.local:5220/health
```

No Windows: `sc query LayoutParserDecrypt` → `RUNNING`. De uma máquina fora da sub-rede autorizada a conexão deve dar timeout.

### Troubleshooting

- **Erro 1053 no `sc start`:** o exe não responde ao SCM (versão antiga só de console) ou falhou no `OnStart`. Veja o Event Viewer (Application) e o log em `LAYOUTPARSER_LOG_DIR`.
- **Access denied / erro 5 no start:** falta o `urlacl` para a conta do serviço: `netsh http add urlacl url=http://layoutparserdecrypt.local:5220/ user="NT AUTHORITY\LocalService"` (o instalador faz isso; uma reserva por host do bind).
- **`400` ao chamar:** chamada por IP, ou nome diferente do registrado no bind.
- **Funciona local, não da API:** bind ainda em `localhost`, `/etc/hosts` sem a entrada, ou `-AllowedRemoteAddress` errado (`Get-NetFirewallRule -DisplayName "LayoutParserDecrypt*" | Get-NetFirewallAddressFilter`).
- **`/health` 503:** o self-test falhou (runtime .NET Framework/`RijndaelManaged`); veja `Self-test de descriptografia falhou` no log.
- **`503` em `/decrypt`:** limite de concorrência atingido (ajuste `MAX_CONCURRENCY`) ou serviço parando.

## Desenvolvimento

```powershell
dotnet build .\LayoutParserDecrypt.sln -c Release   # saída: bin\Release\net48\LayoutParserDecrypt.exe
dotnet test  .\LayoutParserDecrypt.sln -c Release
.\bin\Release\net48\LayoutParserDecrypt.exe --console   # roda em primeiro plano (Ctrl+C para parar)
```

Estrutura: `Program.cs` (entrypoint: serviço ou `--console`) · `DecryptWindowsService.cs` (`ServiceBase`) · `DecryptServer.cs` (HttpListener, limites, self-test, shutdown) · `RequestHandler.cs` (`/decrypt`) · `ServiceOptions.cs` (configuração) · `LayoutParserLib\` (cripto e logger embutidos como fontes) · `scripts\` · `tests\`.

O CI (`.github/workflows/build.yml`, `windows-latest`) faz build + testes e publica `LayoutParserDecrypt.zip` (exe e `.config` na raiz, `scripts\`). Não há DLLs proprietárias.

### Pontos de atenção

- O descarte dos 3 primeiros caracteres mora em `RequestHandler` (não na lib). Não mova sem alinhar com a API.
- A chave/IV estão hardcoded em `LayoutParserLib\CryptographySysMiddle.cs`. Não introduza novos segredos no código.
- O logger nunca lança exceção (log não pode derrubar a descriptografia).
