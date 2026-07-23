[CmdletBinding()]
param(
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { "D:\AndroidSdk" }),
    [string]$AndroidAvdHome = $(if ($env:ANDROID_AVD_HOME) { $env:ANDROID_AVD_HOME } else { "D:\AndroidAvd" }),
    [string]$AvdName = "SteamVRTranslator_Test",
    [switch]$KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$adb = Join-Path $AndroidSdkRoot "platform-tools\adb.exe"
$emulator = Join-Path $AndroidSdkRoot "emulator\emulator.exe"
if (!(Test-Path -LiteralPath $adb) -or !(Test-Path -LiteralPath $emulator)) {
    throw "Android SDK emulator tools were not found under '$AndroidSdkRoot'."
}

$env:ANDROID_SDK_ROOT = $AndroidSdkRoot
$env:ANDROID_AVD_HOME = $AndroidAvdHome
$serial = (& $adb devices | Select-String '^emulator-\d+\s+device$' |
    ForEach-Object { ($_ -split '\s+')[0] } |
    Select-Object -First 1)
$startedHere = !$serial
if ($startedHere) {
    Start-Process -FilePath $emulator `
        -ArgumentList "@$AvdName", "-no-window", "-no-audio", "-gpu", "host", "-no-snapshot", "-no-boot-anim" `
        -WindowStyle Hidden
}

try {
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $serial = (& $adb devices | Select-String '^emulator-\d+\s+device$' |
            ForEach-Object { ($_ -split '\s+')[0] } |
            Select-Object -First 1)
        if ($serial) {
            $booted = (& $adb -s $serial shell getprop sys.boot_completed 2>$null).Trim()
            if ($booted -eq "1") {
                break
            }
        }
    } while ((Get-Date) -lt $deadline)

    if (!$serial -or $booted -ne "1") {
        throw "Android emulator '$AvdName' did not finish booting within three minutes."
    }

    $env:STEAMVR_TRANSLATOR_ANDROID_DEVICE = $serial
    $env:STEAMVR_TRANSLATOR_ANDROID_REQUIRED = '1'
    dotnet test (Join-Path $root "tests\SteamVRTranslator.App.Tests\SteamVRTranslator.App.Tests.csproj") `
        -c Debug `
        --filter "FullyQualifiedName~StreamsAndDecodesAFrameFromConfiguredAndroidDevice"
    if ($LASTEXITCODE -ne 0) {
        throw "Android mirror integration test failed."
    }
}
finally {
    Remove-Item Env:STEAMVR_TRANSLATOR_ANDROID_DEVICE -ErrorAction SilentlyContinue
    Remove-Item Env:STEAMVR_TRANSLATOR_ANDROID_REQUIRED -ErrorAction SilentlyContinue
    if ($startedHere -and !$KeepRunning -and $serial) {
        & $adb -s $serial emu kill | Out-Null
    }
}
