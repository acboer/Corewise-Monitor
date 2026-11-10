<#
  Roda o inventário como administrador e grava em .\inventario ao lado deste script.

  Exemplos:
    .\coletar.ps1                              # repouso, 60 s
    .\coletar.ps1 -Cenario carga -CargaCpu     # carga sintética em todos os núcleos
    .\coletar.ps1 -Cenario copia -Segundos 120 # inicie a cópia para o NAS antes de rodar
    .\coletar.ps1 -Energia -Minutos 60         # acompanha o repouso dos HDDs por 1 hora (só observa)
    .\coletar.ps1 -Energia -Minutos 31 -Intervalo 1800  # só 2 verificações: no início e 30 min depois
#>
param(
    [string]$Cenario = "repouso",
    [int]$Segundos = 60,
    [switch]$CargaCpu,
    [switch]$Energia,
    [int]$Minutos = 60,
    [int]$Intervalo = 60   # segundos entre verificações no modo -Energia
)

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Cenario', $Cenario, '-Segundos', $Segundos, '-Minutos', $Minutos, '-Intervalo', $Intervalo)
    if ($CargaCpu) { $argsList += '-CargaCpu' }
    if ($Energia) { $argsList += '-Energia' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argsList
    return
}

$exe = Join-Path $PSScriptRoot 'HwMon.Inventory.exe'
$saida = Join-Path $PSScriptRoot 'inventario'

if ($Energia) {
    & $exe energia --minutos $Minutos --intervalo $Intervalo --saida $saida
} else {
    $argumentos = @('--cenario', $Cenario, '--segundos', $Segundos, '--saida', $saida)
    if ($CargaCpu) { $argumentos += '--carga-cpu' }
    & $exe @argumentos
}
Write-Host ""
Read-Host "Concluído. Enter para fechar"
