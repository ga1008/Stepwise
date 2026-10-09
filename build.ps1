param([string]$DotNet = 'dotnet', [switch]$SkipZip)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src/Stepwise/Stepwise.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$name = "Stepwise-$version-Win11-x64"
$output = Join-Path $repo (Join-Path 'dist' $name)
if (-not (Test-Path -LiteralPath (Join-Path $repo 'tools/ffmpeg.exe'))) {
    throw 'Bundled FFmpeg missing. Run tools/Get-FFmpeg.ps1 first.'
}
if (-not (Test-Path -LiteralPath (Join-Path $repo 'src/Stepwise/Web/fonts/NotoSansCJKsc-Regular.otf'))) {
    throw 'Bundled annotation font missing. Run tools/Get-AnnotationFont.ps1 first.'
}
& $DotNet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'AUTHORS.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $output -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $repo 'verification/ACCEPTANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo 'verification/ACCEPTANCE.md') -Destination $output
}
if (Test-Path -LiteralPath (Join-Path $repo 'verification/ANNOTATION-ACCEPTANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo 'verification/ANNOTATION-ACCEPTANCE.md') -Destination $output
}
if (Test-Path -LiteralPath (Join-Path $repo 'verification/PREFERENCES-ACCEPTANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo 'verification/PREFERENCES-ACCEPTANCE.md') -Destination $output
}
if (Test-Path -LiteralPath (Join-Path $repo 'verification/SETTINGS-ACCEPTANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo 'verification/SETTINGS-ACCEPTANCE.md') -Destination $output
}
if (Test-Path -LiteralPath (Join-Path $repo 'verification/EXPORT-ACCEPTANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo 'verification/EXPORT-ACCEPTANCE.md') -Destination $output
}
if (-not $SkipZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $repo ('dist/' + $name + '.zip')
    if (Test-Path -LiteralPath $archive) {
        $archive = Join-Path $repo ('dist/' + $name + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($output, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
}
Write-Output $output
