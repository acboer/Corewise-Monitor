<#
  Gera o instalador do Corewise: publica o painel e o serviço em publish\corewise e compila Corewise.iss com o
  Inno Setup 6. Saída: publish\CorewiseSetup-<versão>.exe

    powershell -ExecutionPolicy Bypass -File instalador\gerar.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content (Join-Path $root 'src\HwMon.App\HwMon.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root 'publish\corewise'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publicando o painel e o serviço $version..."
dotnet publish src\HwMon.App -c Release -o $out -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o painel.' }
dotnet publish src\HwMon.Collector -c Release -o $out -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o serviço.' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup).' }

Write-Host "Compilando o instalador..."
& $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'Corewise.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador.' }
$setup = Join-Path $root "publish\CorewiseSetup-$version.exe"
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
