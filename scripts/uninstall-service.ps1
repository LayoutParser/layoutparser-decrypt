<#
.SYNOPSIS
  Remove o serviço LayoutParserDecrypt, as reservas urlacl e a regra de firewall. Mantém os logs.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'LayoutParserDecrypt',
    [ValidateRange(1, 65535)][int]$Port = 8080,
    [string]$InstallDir = 'C:\Program Files\LayoutParserDecrypt',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute em um PowerShell elevado (Administrador).'
}

# Lê as URLs reservadas a partir do Environment do serviço, antes de removê-lo.
$hosts = @('localhost')
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (Test-Path $key) {
    $envValues = (Get-ItemProperty $key -Name Environment -ErrorAction SilentlyContinue).Environment
    foreach ($line in $envValues) {
        if ($line -like 'LAYOUTPARSER_DECRYPT_BIND=*') { $hosts = ($line -split '=', 2)[1] -split ',' }
        if ($line -like 'LAYOUTPARSER_DECRYPT_PORT=*') { $Port = [int](($line -split '=', 2)[1]) }
    }
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    & sc.exe delete $ServiceName | Out-Null
}

foreach ($h in $hosts) { & netsh http delete urlacl url="http://${h}:$Port/" 2>&1 | Out-Null }
Get-NetFirewallRule -DisplayName "$ServiceName (TCP $Port)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($RemoveFiles -and (Test-Path $InstallDir)) { Remove-Item $InstallDir -Recurse -Force }
Write-Host "Serviço '$ServiceName' removido."
