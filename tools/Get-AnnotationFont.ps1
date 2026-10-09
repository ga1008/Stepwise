[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fontSourceCommit = '523d033d6cb47f4a80c58a35753646f5c3608a78'
$fontDestination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Stepwise/Web/fonts'))
[IO.Directory]::CreateDirectory($fontDestination) | Out-Null
$fontAssets = @(
    @{
        Name = 'NotoSansCJKsc-Regular.otf'
        Source = 'Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf'
        Hash = '2C76254F6FC379FDDFCE0A7E84FB5385BB135D3E399294F6EEB6680D0365B74B'
    },
    @{
        Name = 'OFL.txt'
        Source = 'LICENSE'
        Hash = '6A73F9541C2DE74158C0E7CF6B0A58EF774F5A780BF191F2D7EC9CC53EFE2BF2'
    }
)
$fontPendingFiles = @()
try {
    foreach ($fontAsset in $fontAssets) {
        $fontTemporary = Join-Path $fontDestination ('.download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $fontPendingFiles += @{ Temporary = $fontTemporary; Destination = (Join-Path $fontDestination $fontAsset.Name) }
        $fontDownloadUrl = 'https://raw.githubusercontent.com/notofonts/noto-cjk/' + $fontSourceCommit + '/' + $fontAsset.Source
        Invoke-WebRequest -Uri $fontDownloadUrl -OutFile $fontTemporary -UseBasicParsing
        if ((Get-FileHash -LiteralPath $fontTemporary -Algorithm SHA256).Hash -ne $fontAsset.Hash) {
            throw ('Downloaded font asset failed SHA-256 validation: ' + $fontAsset.Name)
        }
    }
    foreach ($fontPending in $fontPendingFiles) {
        Move-Item -LiteralPath $fontPending.Temporary -Destination $fontPending.Destination -Force
    }
    Write-Output 'Noto Sans CJK SC 2.004 and OFL 1.1 restored; SHA-256 verified.'
}
finally {
    foreach ($fontPending in $fontPendingFiles) {
        if (Test-Path -LiteralPath $fontPending.Temporary) {
            Remove-Item -LiteralPath $fontPending.Temporary -Force
        }
    }
}
