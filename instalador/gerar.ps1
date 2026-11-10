<#
  Gera o instalador do Corewise: publica o painel e o serviço em publish\corewise e compila Corewise.iss com o
  Inno Setup 6. Saída: publish\CorewiseSetup-<versão>.exe

    powershell -ExecutionPolicy Bypass -File instalador\gerar.ps1                 # tudo
    powershell -ExecutionPolicy Bypass -File instalador\gerar.ps1 -SoPublicar     # só os .exe (para assinar)
    powershell -ExecutionPolicy Bypass -File instalador\gerar.ps1 -SoInstalador   # só o instalador, com os .exe já em publish\corewise

  O GitHub Actions (.github\workflows\release.yml) usa as duas metades, com a assinatura dos .exe no meio.
#>
param([switch]$SoPublicar, [switch]$SoInstalador)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content (Join-Path $root 'src\HwMon.App\HwMon.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root 'publish\corewise'

if (-not $SoInstalador) {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host "Publicando o painel e o serviço $version..."
    dotnet publish src\HwMon.App -c Release -o $out -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o painel.' }
    dotnet publish src\HwMon.Collector -c Release -o $out -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o serviço.' }
    if ($SoPublicar) { Write-Host "Pronto: $out" -ForegroundColor Green; return }
}

$iscc = @("$env:LOCALAPPDATA\Programs", ${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } |
    ForEach-Object { Get-ChildItem -Path $_ -Filter ISCC.exe -Recurse -Depth 1 -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -like '*Inno Setup*' } } |
    Sort-Object DirectoryName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup).' }

Write-Host "Compilando o instalador..."
& $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'Corewise.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador.' }
$setup = Join-Path $root "publish\CorewiseSetup-$version.exe"
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
