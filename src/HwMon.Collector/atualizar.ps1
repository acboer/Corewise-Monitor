<#
  Atualiza o coletor Hardware Watch já instalado, sem perder nada: papel, certificado e PCs pareados ficam em
  %ProgramData%\HwMon e continuam valendo. Não gera código de pareamento.

    Clique com o botão direito > Executar com o PowerShell (pede administrador)

  Para a primeira instalação use instalar.ps1.
#>

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        # A janela de administrador não enxerga pastas de rede nem unidades mapeadas: copia tudo para uma pasta
        # local antes de pedir a elevação.
        $local = Join-Path $env:TEMP 'HwMon-atualizar'
        New-Item -ItemType Directory -Force $local | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'HwMon.Collector.exe'), $PSCommandPath $local -Force
        Get-ChildItem $local | Unblock-File
        Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $local 'atualizar.ps1')`"")
    }
    catch {
        Write-Host "Erro: $_" -ForegroundColor Red
        Read-Host "Enter para fechar"
    }
    return
}

$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:ProgramFiles 'HwMon\Collector'
$exe = Join-Path $destino 'HwMon.Collector.exe'

try {
    if (-not (Get-Service HwMonCollector -ErrorAction SilentlyContinue)) {
        throw "O coletor não está instalado neste PC. Use instalar.ps1."
    }
    Write-Host "Parando o serviço..."
    Stop-Service HwMonCollector -Force
    # O executável pode levar um instante para ser liberado depois que o serviço para.
    for ($i = 0; $i -lt 10; $i++) {
        try { Copy-Item (Join-Path $PSScriptRoot 'HwMon.Collector.exe') $exe -Force; break }
        catch { if ($i -eq 9) { throw }; Start-Sleep 1 }
    }
    Write-Host "Atualizado em $destino"
    Start-Service HwMonCollector
    Write-Host "Serviço iniciado. Aguardando o coletor..."
    Start-Sleep 10
    & $exe status
    Write-Host ""
    Write-Host "Pronto. O painel reconecta sozinho." -ForegroundColor Green
}
catch {
    Write-Host "Erro: $_" -ForegroundColor Red
}
Write-Host ""
Read-Host "Enter para fechar"
