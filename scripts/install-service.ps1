<#
.SYNOPSIS
  Instala o LayoutParserDecrypt como serviço Windows (urlacl + firewall + variáveis de ambiente do serviço).

.DESCRIPTION
  Idempotente: pode ser reexecutado para reconfigurar/atualizar. Requer PowerShell elevado.

  Autentica��o: o servi�o N�O autentica requisi��es. A prote��o � isolamento de rede � quando o bind
  não é loopback, -AllowedRemoteAddress (IP/CIDR da API) é OBRIGATÓRIO e vira o escopo da regra de firewall.

.EXAMPLE
  # Somente local (API na mesma máquina):
  .\install-service.ps1

.EXAMPLE
  # API Linux em 10.20.0.15 alcançando este host (10.20.0.30):
  .\install-service.ps1 -BindAddress layoutparserdecrypt.local -AllowedRemoteAddress 10.20.0.15
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'LayoutParserDecrypt',
    # Zip do CI: exe na raiz, scripts em .\scripts. Checkout: saída SDK-style em bin\Release\net48.
    [string]$ExeSource = $(foreach ($c in '..\LayoutParserDecrypt.exe', '..\bin\Release\net48\LayoutParserDecrypt.exe') { $f = Join-Path $PSScriptRoot $c; if (Test-Path $f) { $f; break } }),
    [string]$InstallDir = 'C:\Program Files\LayoutParserDecrypt',
    [string]$LogDir = 'C:\ProgramData\LayoutParserDecrypt\logs',

    # "localhost", um ou mais IPs (separados por vírgula) ou "+" (todas as interfaces).
    [string]$BindAddress = 'localhost',
    [ValidateRange(1, 65535)][int]$Port = 5220,
    # IP ou CIDR permitido no firewall (ex.: IP da API Linux). Obrigatório se BindAddress não for loopback.
    [string[]]$AllowedRemoteAddress = @(),

    [int]$MaxBodyBytes = 20971520,
    [int]$TimeoutSeconds = 30,
    [int]$MaxConcurrency = 4,
    [int]$QueueWaitMs = 2000
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute em um PowerShell elevado (Administrador).'
}
if (-not (Test-Path $ExeSource)) { throw "Executável não encontrado: $ExeSource (rode o build Release antes)." }

$hosts = $BindAddress -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$loopback = @('localhost', '127.0.0.1', '[::1]')
if ($hosts | Where-Object { $_ -in @('+', '*') }) {
    throw 'Bind curinga (+ ou *) proibido: a porta 5220 e compartilhada com outra API. Use um nome de host dedicado (ex.: layoutparserdecrypt.local); o http.sys roteia pelo cabecalho Host.'
}
$isLoopbackOnly = -not ($hosts | Where-Object { $loopback -notcontains $_.ToLowerInvariant() })

if (-not $isLoopbackOnly) {
    if ($AllowedRemoteAddress.Count -eq 0) {
        throw 'Bind não-loopback exige -AllowedRemoteAddress (IP/CIDR da API). O serviço não tem autenticação; o firewall é a única barreira.'
    }
    $bad = $AllowedRemoteAddress | Where-Object { $_ -in @('Any', '*', '0.0.0.0', '0.0.0.0/0', '::/0', 'LocalSubnet') }
    if ($bad) { throw "Escopo de firewall amplo demais: $($bad -join ', '). Informe o IP/CIDR específico da API." }
}

# Conta do serviço: LocalService (mínimo privilégio). Nome traduzido via SID p/ funcionar em Windows localizado.
$accountSid = New-Object Security.Principal.SecurityIdentifier 'S-1-5-19'
$accountName = $accountSid.Translate([Security.Principal.NTAccount]).Value

# 1) Arquivos
New-Item -ItemType Directory -Force -Path $InstallDir, $LogDir | Out-Null
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
Copy-Item $ExeSource $InstallDir -Force
$exeConfig = "$ExeSource.config"
if (Test-Path $exeConfig) { Copy-Item $exeConfig $InstallDir -Force }
$exePath = Join-Path $InstallDir (Split-Path $ExeSource -Leaf)

& icacls $LogDir /grant "*S-1-5-19:(OI)(CI)M" | Out-Null

# 2) urlacl (HttpListener sem admin exige reserva de URL para a conta do serviço)
foreach ($h in $hosts) {
    $url = "http://${h}:$Port/"
    & netsh http delete urlacl url=$url 2>&1 | Out-Null
    & netsh http add urlacl url=$url user="$accountName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Falha ao registrar urlacl para $url" }
}

# 3) Serviço
if ($svc) { & sc.exe delete $ServiceName | Out-Null; Start-Sleep -Seconds 2 }
& sc.exe create $ServiceName binPath= "`"$exePath`"" start= delayed-auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'LayoutParser Decrypt' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o serviço (sc create).' }
& sc.exe description $ServiceName 'Descriptografia Sysmiddle (Rijndael) exposta via HTTP para a LayoutParserApi.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

# 4) Configuração do serviço = variáveis de ambiente por serviço (REG_MULTI_SZ)
$envValues = @(
    "LAYOUTPARSER_DECRYPT_BIND=$($hosts -join ',')",
    "LAYOUTPARSER_DECRYPT_PORT=$Port",
    "LAYOUTPARSER_LOG_DIR=$LogDir",
    "LAYOUTPARSER_DECRYPT_MAX_BODY_BYTES=$MaxBodyBytes",
    "LAYOUTPARSER_DECRYPT_TIMEOUT_SECONDS=$TimeoutSeconds",
    "LAYOUTPARSER_DECRYPT_MAX_CONCURRENCY=$MaxConcurrency",
    "LAYOUTPARSER_DECRYPT_QUEUE_WAIT_MS=$QueueWaitMs"
)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $envValues

# 5) Firewall — o tráfego HTTP.sys não é atribuído ao processo, então a regra é por porta + origem.
$ruleName = "$ServiceName (TCP $Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $isLoopbackOnly) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Port -RemoteAddress $AllowedRemoteAddress -Profile Any `
        -Description 'LayoutParserDecrypt: somente a API autorizada. Sem autenticação na aplicação.' | Out-Null
    Write-Host "Firewall: TCP $Port liberado apenas para $($AllowedRemoteAddress -join ', ')"
} else {
    Write-Host 'Bind loopback: nenhuma regra de firewall criada (não exposto à rede).'
}

# 6) Start + verificação
Start-Service $ServiceName
$probe = if ($isLoopbackOnly) { 'localhost' } else { $hosts | Select-Object -First 1 }
$ok = $false
for ($i = 0; $i -lt 15 -and -not $ok; $i++) {
    # --resolve: envia o Host correto (o http.sys roteia por ele) sem exigir DNS/hosts nesta maquina.
    $code = & curl.exe -s -o NUL -w '%{http_code}' --max-time 3 --resolve "${probe}:${Port}:127.0.0.1" "http://${probe}:$Port/health"
    if ($code -eq '200') { $ok = $true } else { Start-Sleep -Seconds 1 }
}
if ($ok) { Write-Host "OK: serviço '$ServiceName' saudável em http://${probe}:$Port/health" }
else { Write-Warning "Serviço iniciado, mas /health não respondeu 200. Veja $LogDir e o Event Viewer." }
