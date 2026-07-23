[CmdletBinding()]
param(
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$output = [System.IO.Path]::GetFullPath((Join-Path $artifacts "win-x64"))
if (!$output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
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

$vibeVoiceServiceOutput = Join-Path $output "vibevoice-service"
dotnet publish (Join-Path $root "src\SteamVRTranslator.VibeVoice.Server\SteamVRTranslator.VibeVoice.Server.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=false `
    -o $vibeVoiceServiceOutput
if ($LASTEXITCODE -ne 0) {
    throw "VibeVoice service publish failed."
}

Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $output
Copy-Item -LiteralPath (Join-Path $root "third_party\openvr\LICENSE") `
    -Destination (Join-Path $output "OPENVR-LICENSE.txt")
$archiveName = if ($SelfContained) {
    "SteamVRTranslator-win-x64-self-contained.zip"
} else {
    "SteamVRTranslator-win-x64.zip"
}
$archive = Join-Path $artifacts $archiveName
Compress-Archive -Path (Join-Path $output "*") `
    -DestinationPath $archive `
    -CompressionLevel Optimal `
    -Force
Write-Host "Published SteamVR Translator: $output"
Write-Host "Packaged SteamVR Translator: $archive"
