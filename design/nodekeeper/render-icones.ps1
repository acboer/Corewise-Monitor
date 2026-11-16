# Nodekeeper — renderiza o ícone do app (Edge headless) em PNG e monta o app.ico.
# Rode depois de: node design/nodekeeper/gerar.mjs
# Uso (na raiz do projeto): powershell -ExecutionPolicy Bypass -File design/nodekeeper/render-icones.ps1
$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$icons  = Join-Path $here 'icons'
$render = Join-Path $icons '_render'
$appDir = Join-Path $icons 'app'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Edge ou Chrome não encontrado.' }

# 1. PNG 256 de cada variante (fundo transparente)
$userData = Join-Path $env:TEMP 'nodekeeper-render-profile'
foreach ($v in 'full', '48', '32', '16') {
  $png = Join-Path $render "r-$v.png"
  if (Test-Path $png) { Remove-Item $png }
  # o caminho do projeto pode ter espaço: aspas nos argumentos e %20 na URL
  $url = 'file:///' + (((Join-Path $render "r-$v.html") -replace '\\', '/') -replace ' ', '%20')
  Start-Process -FilePath $edge -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000', "`"--user-data-dir=$userData`"", '--window-size=256,256', "`"--screenshot=$png`"", $url) -Wait -WindowStyle Hidden
  if (-not (Test-Path $png)) { throw "falhou o render de $v" }
}

# 2. reduz para cada tamanho com a variante certa
Add-Type -AssemblyName System.Drawing
$plan = @( @(256, 'full'), @(48, '48'), @(32, '32'), @(24, '32'), @(20, '16'), @(16, '16') )
$pngs = @()
foreach ($entry in $plan) {
  $size = [int]$entry[0]
  $src = [System.Drawing.Image]::FromFile((Join-Path $render "r-$($entry[1]).png"))
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gr = [System.Drawing.Graphics]::FromImage($bmp)
  $gr.Clear([System.Drawing.Color]::Transparent)
  $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $gr.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $attr = New-Object System.Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
  $gr.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
  $gr.Dispose(); $src.Dispose()
  $out = Join-Path $appDir "app-$size.png"
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  $pngs += @{ Size = $size; Bytes = [System.IO.File]::ReadAllBytes($out) }
}

# 3. .ico com PNG embutido em cada entrada
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
  $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
  $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$p.Bytes.Length); $w.Write([UInt32]$offset)
  $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write($p.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $icons 'app.ico'), $ms.ToArray())
$w.Dispose()
'app.ico: ' + (($pngs | ForEach-Object { $_.Size }) -join ', ')
