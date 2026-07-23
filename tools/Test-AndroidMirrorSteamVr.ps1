[CmdletBinding()]
param(
    [string]$AndroidSdkRoot = $(if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { 'D:\AndroidSdk' }),
    [string]$AndroidAvdHome = $(if ($env:ANDROID_AVD_HOME) { $env:ANDROID_AVD_HOME } else { 'D:\AndroidAvd' }),
    [string]$AvdName = 'SteamVRTranslator_Test',
    [string]$SteamVrPath = 'C:\Program Files (x86)\Steam\steamapps\common\SteamVR',
    [ValidateRange(4, 30)]
    [int]$PhaseSeconds = 8,
    [switch]$KeepEmulatorRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$adb = Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'
$emulator = Join-Path $AndroidSdkRoot 'emulator\emulator.exe'
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
        -ArgumentList "@$AvdName", '-no-window', '-no-audio', '-gpu', 'host', '-no-snapshot', '-no-boot-anim' `
        -WindowStyle Hidden
}

try {
    $deadline = (Get-Date).AddMinutes(3)
    $booted = ''
    do {
        Start-Sleep -Seconds 2
        $serial = (& $adb devices | Select-String '^emulator-\d+\s+device$' |
            ForEach-Object { ($_ -split '\s+')[0] } |
            Select-Object -First 1)
        if ($serial) {
            $booted = (& $adb -s $serial shell getprop sys.boot_completed 2>$null).Trim()
        }
    } while ((!$serial -or $booted -ne '1') -and (Get-Date) -lt $deadline)
    if (!$serial -or $booted -ne '1') {
        throw "Android emulator '$AvdName' did not finish booting within three minutes."
    }

    & (Join-Path $PSScriptRoot 'Test-SteamVrNullOverlay.ps1') `
        -SteamVrPath $SteamVrPath `
        -Configuration Release `
        -AndroidMirror `
        -AndroidDeviceSerial $serial `
        -PhaseSeconds $PhaseSeconds
    if ($LASTEXITCODE -ne 0) {
        throw 'SteamVR Android mirror benchmark failed.'
    }
}
finally {
    if ($startedHere -and !$KeepEmulatorRunning -and $serial) {
        & $adb -s $serial emu kill | Out-Null
    }
}
