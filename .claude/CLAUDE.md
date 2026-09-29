<!-- PROJECT-CUSTOMIZED: guia de máquina do LayoutParserDecrypt. Seguro editar. -->
# CLAUDE.md — LayoutParserDecrypt

Guia de contexto para IA (Claude Code) neste repositório. A documentação **humana** está em [`../README.md`](../README.md); este arquivo é o índice **de máquina**: convenções, agentes, invariantes e ferramentas.

> Convenção de marcadores (herdada do aiox): blocos podem ser anotados com
> `<!-- FRAMEWORK-OWNED -->` (não customizar) ou `<!-- PROJECT-CUSTOMIZED -->` (seguro editar).

---

## Identidade do projeto

- **O que é:** utilitário de linha de comando (**.NET Framework 4.8.1**, console `Exe`) que **descriptografa** mappers/layouts da **Sysmiddle** para serem gravados no **Redis** pela API.
- **Por que existe:** a API `LayoutParserApi` (**net10.0**) não roda o `RijndaelManaged` legado em processo → descriptografia é feita **fora do processo**, por este `.exe`, invocado via `Process.Start`.
- **Posição no pipeline:** `Sysmiddle → SQL Server → LayoutParserApi → [este .exe] → Redis → LayoutParserReact`. Mapa completo no README.

---

## Stack & comandos

| | |
|---|---|
| Target | `.NET Framework v4.8.1`, `AnyCPU`, `OutputType=Exe` |
| Build local | `msbuild .\LayoutParserDecrypt.sln /m /p:Configuration=Release /p:Platform="Any CPU"` |
| Build (alternativo) | `dotnet build .\LayoutParserDecrypt.sln -c Release` — funciona sem VS/msbuild no PATH (validado) |
| Restore | `nuget restore .\LayoutParserDecrypt.sln` (no-op: sem pacotes NuGet) |
| Saída | `bin\Release\LayoutParserDecrypt.exe` |
| CI | [`.github/workflows/build.yml`](../.github/workflows/build.yml) — MSBuild Release em `windows-latest` |
| Executar | `LayoutParserDecrypt.exe <inputFile> <outputFile> [correlationId] [logDir]` (exit `0`=ok, `1`=falha) |

> ⚠️ Não há suíte de testes nem `dotnet test`: é projeto `.NET Framework` clássico (msbuild). Para validar uma mudança, faça um **roundtrip** real (veja `/decrypt-roundtrip`).

---

## Arquivos-chave

| Arquivo | Papel |
|---|---|
| [`Program.cs`](../Program.cs) | Entrypoint. Parse de args → `Substring(3)` → `Decrypt` → grava saída → exit code. |
| [`LayoutParserLib/CryptographySysMiddle.cs`](../LayoutParserLib/CryptographySysMiddle.cs) | Algoritmo Rijndael/AES (fonte da verdade, embutida como fonte local). |
| [`LayoutParserLib/RollingFileLogger.cs`](../LayoutParserLib/RollingFileLogger.cs) | Logger `layoutparserlib.log` (namespace `LayoutParserLib`). |
| [`RollingFileLogger.cs`](../RollingFileLogger.cs) | Logger `layoutparserdecrypt.log` (namespace `LayoutParserDecrypt`, usado pelo `Program`). |
| [`LayoutParserDecrypt.csproj`](../LayoutParserDecrypt.csproj) | Inclui as cópias da lib como fontes locais (CI autocontido). |

---

## Invariantes / convenções (LEIA antes de editar)

Detalhe completo em [`rules/conventions.md`](rules/conventions.md). Resumo dos **footguns**:

1. **O strip de 3 caracteres mora no `Program.cs`**, não na lib. Não mova sem alinhar com a API.
2. **`LayoutParserLib/` (cripto/logger) é a fonte da verdade aqui.** O repo `layoutparser-lib` foi arquivado; não há cópia para sincronizar. Não mexa na chave/IV sem decisão do dono.
3. **Contrato de CLI é estável.** A API depende da ordem dos args e dos exit codes — não quebre.
4. **Chave/IV hardcoded.** Não introduzir novos segredos no código; se mexer, documentar.
5. **Logging nunca lança.** Mantenha os `catch {}` dos loggers.

---

## Sistema de agentes

Três agentes especializados (escopo deste repo). Invoque com `@<nome>` ou via Task. Detalhes em `.claude/agents/`.

| Agente | Quando usar | Pode escrever código? |
|---|---|---|
| **`lpd-dev`** | Implementar/corrigir C#, csproj, CI, build, logging. | ✅ Sim (código de produção). |
| **`lpd-integrator`** | Entender/projetar a integração entre os 4 repos (cripto, API, Redis, React), trade-offs, pesquisa. | ❌ Só design/docs. |
| **`lpd-docs`** | Manter README, este CLAUDE.md, diagramas, docs para a faculdade. | ✅ Só documentação. |

- **Memória por agente:** `.claude/agent-memory/<agente>/MEMORY.md` — cada agente lê a sua ao ativar e registra aprendizados (file-path-anchored).
- **Handoff entre agentes:** protocolo em [`rules/agent-handoff.md`](rules/agent-handoff.md) (artefato compacto em `.claude/handoffs/`).

---

## Comandos (slash commands)

| Comando | O que faz |
|---|---|
| [`/decrypt-roundtrip`](commands/decrypt-roundtrip.md) | Build Release + executa uma descriptografia de teste e valida o resultado. |

---

## MCP servers

**Preferir ferramentas nativas** (Read/Grep/Glob/Edit/Bash) a MCP. Dois servers estão disponíveis:

### 1. `layoutparser` (principal) — operações da API
Server MCP do ecossistema, mantido no repo **`LayoutParserApi`** (`mcp/LayoutParserMcp`, C#/.NET 10, **stdio**, cliente fino sobre a API HTTP). Tools: `parse_document`, `list_endpoints`, `api_get`, `api_post`. Registrado em [`../.mcp.json`](../.mcp.json) (server `layoutparser`).

> **Não** é um MCP custom deste repo — reusamos o que já existe na API. A "URL que sobe" é a **API** (`LAYOUTPARSER_API_URL`, default `http://localhost:5000`); o server MCP em si é stdio, iniciado pelo Claude Code.

Pré-requisitos para ativar:
```bash
# 1) compilar a DLL do MCP server (no repo da API)
cd ..\LayoutParserApi\mcp\LayoutParserMcp ; dotnet build -c Release
# 2) subir a API em http://localhost:5000 (ou ajustar LAYOUTPARSER_API_URL no ../.mcp.json)
```
O Claude Code pedirá aprovação do server antes de iniciá-lo.

### 2. `btca` (better-context, opcional) — dúvidas sobre dependências
Para perguntas sobre libs externas (ex.: `RijndaelManaged`, .NET Framework). Requer **Bun**:
```bash
bun add -g btca opencode-ai
btca connect --provider opencode --model claude-haiku-4-5
btca mcp local        # escolha "Claude Code"
```
Config: [`../btca.config.jsonc`](../btca.config.jsonc). Cache em `.btca/` (gitignored).

---

## Guia de seleção de ferramentas

| Tarefa | Use | Em vez de |
|---|---|---|
| Ler arquivo | `Read` | `cat` / `Get-Content` |
| Buscar conteúdo | `Grep` | `grep` / `Select-String` |
| Achar arquivos | `Glob` | `find` / `Get-ChildItem -Recurse` |
| Editar | `Edit` | sed/regex manual |
| Build/git/exe | `Bash` ou `PowerShell` | — |
| Operação na API (parse, endpoints) | `layoutparser` (MCP) | hardcodar rotas |
| Dúvida sobre lib externa | `btca` (MCP, opcional) | adivinhar de memória |

---

*Configuração Claude Code do LayoutParserDecrypt — adaptada das convenções aiox-core.*
