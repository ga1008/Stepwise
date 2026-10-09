<#
.SYNOPSIS
Creates a public Stepwise setup ZIP without redistributing FFmpeg binaries.
.DESCRIPTION
Reads an existing self-contained Windows publish directory. Copies only into a
new, unique output directory; never changes or deletes the source package.
The supplied publish must omit developer-path debug metadata, for example:
dotnet publish ... -p:DebugType=None -p:DebugSymbols=false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$DotNetRuntimeVersion = '8.0.31',
    [string]$WebView2Version = '1.0.3537.50',
    [string]$NuGetPackagesRoot = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePackage = (Resolve-Path -LiteralPath $PackagePath).ProviderPath.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Container)) {
    throw 'PackagePath must be an existing publish directory.'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$sourcePrefix = $sourcePackage + [IO.Path]::DirectorySeparatorChar
if ($outputRoot.Equals($sourcePackage, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside the source package.'
}
if (-not $NuGetPackagesRoot) {
    $NuGetPackagesRoot = $env:NUGET_PACKAGES
    if (-not $NuGetPackagesRoot) { $NuGetPackagesRoot = Join-Path $env:USERPROFILE '.nuget/packages' }
}
$nugetRoot = [IO.Path]::GetFullPath($NuGetPackagesRoot)
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/Stepwise/Stepwise.csproj') -Raw)).Project.PropertyGroup.Version
$packageName = "Stepwise-$version-Win11-x64"
$actualVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $sourcePackage 'Stepwise.dll')).Version.ToString(3)
if ($actualVersion -ne ([Version]$version).ToString(3)) {
    throw "Application version mismatch: expected $version, found $actualVersion."
}
$runtimeConfig = Join-Path $sourcePackage 'Stepwise.runtimeconfig.json'
foreach ($required in @('Stepwise.exe', 'Stepwise.dll', 'Stepwise.runtimeconfig.json',
    'Web/fonts/NotoSansCJKsc-Regular.otf', 'Web/fonts/OFL.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePackage $required) -PathType Leaf)) {
        throw "The publish is incomplete: missing $required"
    }
}
$frameworks = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    if (-not ($frameworks | Where-Object { $_.name -eq $framework -and $_.version -eq $DotNetRuntimeVersion })) {
        throw "Runtime notice version mismatch for $framework. Check DotNetRuntimeVersion."
    }
}
$assemblyText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $sourcePackage 'Stepwise.dll')))
if ($assemblyText -match '(?i)[a-z]:[\\/](?:Users|Documents and Settings)[\\/]') {
    throw 'Stepwise.dll contains an absolute user-profile path. Publish again with DebugType=None and DebugSymbols=false.'
}

$copyDocuments = @(
    @{ Source = (Join-Path $repositoryRoot 'README.md'); Target = 'README.md' },
    @{ Source = (Join-Path $repositoryRoot 'AUTHORS.md'); Target = 'AUTHORS.md' },
    @{ Source = (Join-Path $repositoryRoot 'LICENSE.txt'); Target = 'LICENSE.txt' },
    @{ Source = (Join-Path $repositoryRoot 'docs/USER_GUIDE.md'); Target = 'docs/USER_GUIDE.md' },
    @{ Source = (Join-Path $PSScriptRoot 'THIRD-PARTY.md'); Target = 'tools/THIRD-PARTY.md' },
    @{ Source = (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1'); Target = 'tools/Get-FFmpeg.ps1' },
    @{ Source = (Join-Path $PSScriptRoot 'Prepare-Stepwise.cmd'); Target = 'Prepare-Stepwise.cmd' },
    @{ Source = (Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64/$DotNetRuntimeVersion/LICENSE.TXT"); Target = 'tools/licenses/DOTNET-LICENSE.txt' },
    @{ Source = (Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64/$DotNetRuntimeVersion/THIRD-PARTY-NOTICES.TXT"); Target = 'tools/licenses/DOTNET-THIRD-PARTY-NOTICES.txt' },
    @{ Source = (Join-Path $nugetRoot "microsoft.windowsdesktop.app.runtime.win-x64/$DotNetRuntimeVersion/LICENSE"); Target = 'tools/licenses/WINDOWSDESKTOP-LICENSE.txt' },
    @{ Source = (Join-Path $nugetRoot "microsoft.web.webview2/$WebView2Version/LICENSE.txt"); Target = 'tools/licenses/WebView2-LICENSE.txt' },
    @{ Source = (Join-Path $nugetRoot "microsoft.web.webview2/$WebView2Version/NOTICE.txt"); Target = 'tools/licenses/WebView2-NOTICE.txt' }
)
foreach ($document in $copyDocuments) {
    if (-not (Test-Path -LiteralPath $document.Source -PathType Leaf)) {
        throw "Required public document or dependency notice missing: $($document.Source)"
    }
}

# Enumerate before creating output so recursive copying can never consume itself.
$sourceFiles = @(Get-ChildItem -LiteralPath $sourcePackage -Recurse -File -Force)
# Cloud-sync placeholders may have ReparsePoint without being filesystem links.
# Reject actual symlinks/junctions, while allowing hydrated ordinary cloud files.
if ((Get-Item -LiteralPath $sourcePackage).LinkType -or
    (Get-ChildItem -LiteralPath $sourcePackage -Recurse -Force | Where-Object { $_.LinkType -or $_.Target })) {
    throw 'The source package contains filesystem links; use a plain publish directory.'
}
$runName = 'public-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $outputRoot $runName
$destination = Join-Path $runRoot $packageName
if (Test-Path -LiteralPath $runRoot) { throw 'The unique public output already exists.' }
[IO.Directory]::CreateDirectory($destination) | Out-Null
$skipped = [Collections.Generic.List[string]]::new()
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($sourcePrefix.Length)
    $normalized = $relative.Replace('\', '/')
    if ($file.Extension -ieq '.pdb' -or
        $file.Name -in @('ffmpeg.exe', 'ffprobe.exe') -or
        $file.Name -like '*ACCEPTANCE.md' -or
        $normalized -match '(?i)(^|/)(verification|recordings|logs|\.cache|\.git)(/|$)' -or
        $file.Extension -in @('.log', '.tmp', '.bak') -or
        $file.Name -in @('project.json', 'settings.json', '.project.lock')) {
        $skipped.Add($normalized)
        continue
    }
    $target = Join-Path $destination $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
foreach ($document in $copyDocuments) {
    $target = Join-Path $destination $document.Target
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $document.Source -Destination $target -Force
}

$forbidden = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
    $_.Extension -ieq '.pdb' -or $_.Name -in @('ffmpeg.exe', 'ffprobe.exe') -or $_.Name -like '*ACCEPTANCE.md'
})
if ($forbidden.Count -ne 0) { throw 'The public staging contains excluded files; no ZIP was created.' }
$archive = Join-Path $runRoot ($packageName + '-Setup.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$stagedFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
$expectedEntries = @{}
$destinationPrefix = $destination + [IO.Path]::DirectorySeparatorChar
foreach ($file in $stagedFiles) {
    $entryName = $packageName + '/' + $file.FullName.Substring($destinationPrefix.Length).Replace('\', '/')
    $expectedEntries[$entryName] = $file
}
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
$verified = 0
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $expectedEntries.ContainsKey($entry.FullName)) { throw 'ZIP contains an unexpected file.' }
        $sourceFile = $expectedEntries[$entry.FullName]
        if ($entry.Length -ne $sourceFile.Length) { throw "ZIP size mismatch: $($entry.FullName)" }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $hasher.Dispose() }
        if ($entryHash -ne (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash) {
            throw "ZIP content mismatch: $($entry.FullName)"
        }
        $verified++
        $expectedEntries.Remove($entry.FullName)
    }
    if ($expectedEntries.Count -ne 0 -or $verified -ne $stagedFiles.Count) { throw 'ZIP omitted one or more staged files.' }
}
finally { $zip.Dispose() }
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums = Join-Path $runRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksums, "$digest  $([IO.Path]::GetFileName($archive))`n", [Text.Encoding]::ASCII)

[pscustomobject]@{
    PackageDirectory = $destination
    Archive = $archive
    Checksums = $checksums
    SHA256 = $digest
    Bytes = (Get-Item -LiteralPath $archive).Length
    Files = @(Get-ChildItem -LiteralPath $destination -Recurse -File).Count
    VerifiedZipFiles = $verified
    ExcludedFiles = @($skipped)
    FFmpegIncluded = $false
    FirstRun = 'Prepare-Stepwise.cmd'
} | ConvertTo-Json -Depth 3
