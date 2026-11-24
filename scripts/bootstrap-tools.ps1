# SPDX-License-Identifier: AGPL-3.0-or-later
# Network setup only. The invoice processing CLI itself makes no HTTP requests.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$toolRoot = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (!(Test-Path -LiteralPath $sevenZip)) { throw '7-Zip fehlt; offiziellen lokalen Entpacker bereitstellen.' }
if (!(Get-Command java -ErrorAction SilentlyContinue)) { throw 'Java fehlt.' }
foreach ($item in (Get-Content (Join-Path $PSScriptRoot 'tool-downloads.json') -Raw | ConvertFrom-Json)) {
    $target = Join-Path $projectRoot $item.path
    if (!(Test-Path -LiteralPath $target)) { Invoke-WebRequest -Uri $item.url -OutFile $target }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $item.sha256) { throw "SHA256 stimmt nicht: $target" }
}
$gs = Join-Path $toolRoot 'ghostscript-10.08.0'
if (!(Test-Path -LiteralPath (Join-Path $gs 'bin\gswin64c.exe'))) {
    & $sevenZip x (Join-Path $toolRoot 'gs10080w64.exe') "-o$gs" -y
    if ($LASTEXITCODE -ne 0) { throw 'Ghostscript konnte nicht entpackt werden.' }
}
$vera = Join-Path $toolRoot 'verapdf'
if (!(Test-Path -LiteralPath (Join-Path $vera 'verapdf.bat'))) {
    $installer = Join-Path $toolRoot 'verapdf-1.30.3-installer'
    Expand-Archive -LiteralPath (Join-Path $toolRoot 'verapdf-1.30.3.zip') -DestinationPath $installer -Force
    [xml]$config = Get-Content (Join-Path $PSScriptRoot 'verapdf-install.xml') -Raw
    $config.SelectSingleNode('//installpath').InnerText = $vera
    $auto = Join-Path $toolRoot 'verapdf-auto-install.xml'
    $config.Save($auto)
    & java -jar (Join-Path $installer 'verapdf-greenfield-1.30.3\verapdf-izpack-installer-1.30.3.jar') $auto
    if ($LASTEXITCODE -ne 0) { throw 'veraPDF konnte nicht lokal bereitgestellt werden.' }
}
Write-Output 'Lokale Werkzeuge mit festgelegten Archiv-Hashes bereit.'
