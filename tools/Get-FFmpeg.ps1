param([switch]$Force)
$ErrorActionPreference = 'Stop'
$version = '8.1.2'
$archiveName = "ffmpeg-$version-essentials_build.7z"
$url = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$sha256 = 'E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6'
$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
if (-not $Force -and (Test-Path -LiteralPath (Join-Path $toolRoot 'ffmpeg.exe')) -and (Test-Path -LiteralPath (Join-Path $toolRoot 'ffprobe.exe'))) {
    Write-Output 'Bundled FFmpeg already exists.'
    exit 0
}
$archivePath = Join-Path $toolRoot $archiveName
Invoke-WebRequest -Uri $url -OutFile $archivePath -TimeoutSec 600
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $sha256) {
    throw 'FFmpeg archive checksum mismatch. Existing tools have not been replaced.'
}
& tar.exe -xf $archivePath -C $toolRoot --strip-components 2 "ffmpeg-$version-essentials_build/bin/ffmpeg.exe" "ffmpeg-$version-essentials_build/bin/ffprobe.exe"
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg extraction failed.' }
& tar.exe -xf $archivePath -C $toolRoot --strip-components 1 "ffmpeg-$version-essentials_build/LICENSE" "ffmpeg-$version-essentials_build/README.txt"
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg license extraction failed.' }
Move-Item -LiteralPath (Join-Path $toolRoot 'LICENSE') -Destination (Join-Path $toolRoot 'FFmpeg-LICENSE.txt') -Force
Move-Item -LiteralPath (Join-Path $toolRoot 'README.txt') -Destination (Join-Path $toolRoot 'FFmpeg-README.txt') -Force
Remove-Item -LiteralPath $archivePath
Write-Output "Bundled FFmpeg $version extracted and SHA256 verified."
