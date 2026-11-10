<#
  Instala (ou atualiza) o coletor HwMon como serviço do Windows e já gera um código de pareamento.

    .\instalar.ps1 -Papel NAS
    .\instalar.ps1 -Papel Workstation

  Copia o executável para C:\Program Files\HwMon\Collector, cria o serviço "HwMonCollector" (inicia com o Windows,
  roda mesmo sem ninguém logado) e libera a porta 47821 no firewall para redes privadas.
#>
param([string]$Papel = "")

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Papel', "`"$Papel`"")
    return
}

$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:ProgramFiles 'HwMon\Collector'
$exe = Join-Path $destino 'HwMon.Collector.exe'

try {
    # Atualização: para o serviço antes de trocar o executável.
    if (Get-Service HwMonCollector -ErrorAction SilentlyContinue) {
        Write-Host "Parando o serviço atual..."
        Stop-Service HwMonCollector -Force -ErrorAction SilentlyContinue
        Start-Sleep 2
    }
    New-Item -ItemType Directory -Force $destino | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'HwMon.Collector.exe') $exe -Force
    Write-Host "Copiado para $destino"

    if ($Papel) { & $exe papel $Papel | Out-Null }
    & $exe instalar
    if ($LASTEXITCODE -ne 0) { throw "Falha ao instalar o serviço." }

    Write-Host ""
    Write-Host "Aguardando o coletor iniciar..."
    Start-Sleep 12
    & $exe status
    & $exe parear
}
catch {
    Write-Host "Erro: $_" -ForegroundColor Red
}
Write-Host ""
Read-Host "Enter para fechar"
