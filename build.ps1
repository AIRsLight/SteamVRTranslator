[CmdletBinding()]
param(
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$output = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts\win-x64"))
if (!$output.StartsWith((Join-Path $root "artifacts"), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved output path is outside the repository artifacts directory."
}

& (Join-Path $root "fetch-openvr.ps1")
dotnet test (Join-Path $root "SteamVRTranslator.sln") -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed."
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet publish (Join-Path $root "src\SteamVRTranslator.App\SteamVRTranslator.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=false `
    -o $output
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $output
Copy-Item -LiteralPath (Join-Path $root "third_party\openvr\LICENSE") `
    -Destination (Join-Path $output "OPENVR-LICENSE.txt")
Write-Host "Published SteamVR Translator: $output"

