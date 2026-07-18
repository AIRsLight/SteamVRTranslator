[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "third_party\openvr\openvr_api.dll")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceCommit = "0924064316de3effbcd1acf1e309182a2deb1c05"
$downloadUrl = "https://raw.githubusercontent.com/ValveSoftware/openvr/$sourceCommit/bin/win64/openvr_api.dll"
$expectedLength = 837272
$expectedSha256 = "bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a"
$targetPath = [System.IO.Path]::GetFullPath($Destination)
$temporaryPath = "$targetPath.download"

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ExpectedFile {
    param([Parameter(Mandatory)][string]$Path)
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $expectedLength -and
        (Get-Sha256 -Path $Path) -eq $expectedSha256
}

if (Test-ExpectedFile -Path $targetPath) {
    Write-Host "OpenVR native library is already installed and verified."
    exit 0
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPath) | Out-Null
Invoke-WebRequest -Uri $downloadUrl -OutFile $temporaryPath
if (!(Test-ExpectedFile -Path $temporaryPath)) {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw "Downloaded OpenVR library failed size or SHA-256 verification."
}

Move-Item -LiteralPath $temporaryPath -Destination $targetPath -Force
Write-Host "Installed verified OpenVR library: $targetPath"
