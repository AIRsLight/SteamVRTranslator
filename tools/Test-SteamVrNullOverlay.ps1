param(
    [string]$SteamVrPath = 'C:\Program Files (x86)\Steam\steamapps\common\SteamVR',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Stress,
    [switch]$Realistic,
    [switch]$AndroidMirror,
    [string]$AndroidDeviceSerial = '',
    [ValidateRange(10, 120)]
    [int]$AndroidSourceFps = 120,
    [ValidateRange(1, 120)]
    [int]$OverlayFps = 120,
    [ValidateSet('720', '1080', '720,1080')]
    [string]$AndroidSizes = '1080',
    [ValidateRange(1, 32)]
    [int]$MaxWindows = 12,
    [ValidateRange(2, 30)]
    [int]$PhaseSeconds = 5
)

$ErrorActionPreference = 'Stop'
$running = Get-Process vrserver, vrcompositor, vrmonitor -ErrorAction SilentlyContinue
if ($running) {
    throw 'SteamVR is already running. Close it before starting the isolated Null Driver benchmark.'
}

$vrPathReg = Join-Path $SteamVrPath 'bin\win64\vrpathreg.exe'
$vrStartup = Join-Path $SteamVrPath 'bin\win64\vrstartup.exe'
if (-not (Test-Path -LiteralPath $vrPathReg) -or -not (Test-Path -LiteralPath $vrStartup)) {
    throw "SteamVR developer tools were not found under '$SteamVrPath'."
}

Add-Type -AssemblyName System.Windows.Forms
$primaryScreen = [System.Windows.Forms.Screen]::PrimaryScreen
if ($null -eq $primaryScreen -or
    $primaryScreen.DeviceName -eq 'WinDisc' -or
    $primaryScreen.Bounds.Width -le 640 -or
    $primaryScreen.Bounds.Height -le 480) {
    throw @'
SteamVR Null Driver requires an attached interactive Windows desktop. The current
session is disconnected (WinDisc), so vrcompositor cannot resolve a DXGI output.
Log in locally or keep the remote desktop/Parsec display attached, then rerun this script.
'@
}
$windowWidth = $primaryScreen.Bounds.Width
$windowHeight = $primaryScreen.Bounds.Height

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactFolder = if ($AndroidMirror) {
    'steamvr-android-mirror'
}
elseif ($Realistic) {
    'steamvr-overlay-realistic'
}
elseif ($Stress) {
    'steamvr-overlay-stress'
}
else {
    'steamvr-null-overlay'
}
$artifactRoot = Join-Path $repoRoot "artifacts\$artifactFolder"
$configPath = Join-Path $artifactRoot 'config'
$logPath = Join-Path $artifactRoot 'logs'
$reportName = if ($AndroidMirror) {
    'android-mirror-benchmark.json'
}
elseif ($Realistic) {
    'overlay-realistic.json'
}
elseif ($Stress) {
    'overlay-stress.json'
}
else {
    'overlay-benchmark.json'
}
$reportPath = Join-Path $artifactRoot $reportName
$gpuLogPath = Join-Path $artifactRoot 'nvidia-dmon.txt'
New-Item -ItemType Directory -Force -Path $configPath, $logPath | Out-Null

$pathOutput = & $vrPathReg show
$originalConfig = (($pathOutput | Where-Object { $_ -like 'Config path = *' }) -replace '^Config path = ', '').Trim()
$originalLog = (($pathOutput | Where-Object { $_ -like 'Log path = *' }) -replace '^Log path = ', '').Trim()
if (-not $originalConfig -or -not $originalLog) {
    throw 'Unable to read the current OpenVR config and log paths.'
}

$settings = [ordered]@{
    steamvr = [ordered]@{
        activateMultipleDrivers = $true
        forcedDriver = 'null'
        requireHmd = $false
    }
    driver_null = [ordered]@{
        enable = $true
        windowX = $primaryScreen.Bounds.X
        windowY = $primaryScreen.Bounds.Y
        windowWidth = $windowWidth
        windowHeight = $windowHeight
        displayFrequency = if ($AndroidMirror) { [double]$OverlayFps } else { 90.0 }
        renderWidth = 1512
        renderHeight = 1680
    }
} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText(
    (Join-Path $configPath 'steamvr.vrsettings'),
    $settings,
    [System.Text.UTF8Encoding]::new($false))

$gpuMonitor = $null
try {
    & $vrPathReg setconfig $configPath
    if ($LASTEXITCODE -ne 0) { throw 'vrpathreg setconfig failed.' }
    & $vrPathReg setlog $logPath
    if ($LASTEXITCODE -ne 0) { throw 'vrpathreg setlog failed.' }

    Start-Process -FilePath $vrStartup -WorkingDirectory $SteamVrPath -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    $readySince = $null
    do {
        Start-Sleep -Milliseconds 250
        $server = Get-Process vrserver -ErrorAction SilentlyContinue
        $compositor = Get-Process vrcompositor -ErrorAction SilentlyContinue
        if ($server -and $compositor) {
            if ($null -eq $readySince) {
                $readySince = [DateTime]::UtcNow
            }
        }
        else {
            $readySince = $null
        }
        $stable = $null -ne $readySince -and
                  ([DateTime]::UtcNow - $readySince).TotalSeconds -ge 2
    } until ($stable -or [DateTime]::UtcNow -ge $deadline)
    if (-not $stable) {
        throw "SteamVR Null Driver did not become ready. Inspect '$logPath'."
    }

    $benchmarkArguments = @("--output=$reportPath")
    if ($AndroidMirror) {
        if ([string]::IsNullOrWhiteSpace($AndroidDeviceSerial)) {
            throw 'AndroidDeviceSerial is required for the Android mirror benchmark.'
        }
        $benchmarkArguments += '--android-mirror'
        $benchmarkArguments += "--device=$AndroidDeviceSerial"
        $benchmarkArguments += "--phase-seconds=$PhaseSeconds"
        $benchmarkArguments += "--fps=$AndroidSourceFps"
        $benchmarkArguments += "--overlay-fps=$OverlayFps"
        $benchmarkArguments += "--sizes=$AndroidSizes"
    }
    elseif ($Stress -or $Realistic) {
        if ($Realistic) {
            $benchmarkArguments += '--realistic'
        }
        else {
            $benchmarkArguments += '--stress'
        }
        $benchmarkArguments += "--max-windows=$MaxWindows"
        $benchmarkArguments += "--phase-seconds=$PhaseSeconds"
    }
    if ($Stress -or $Realistic -or $AndroidMirror) {
        $nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
        if ($nvidiaSmi) {
            Remove-Item -LiteralPath $gpuLogPath -Force -ErrorAction SilentlyContinue
            $gpuMonitor = Start-Process -FilePath $nvidiaSmi.Source `
                -ArgumentList @('dmon', '-s', 'pucm', '-d', '1', '-o', 'DT') `
                -RedirectStandardOutput $gpuLogPath `
                -WindowStyle Hidden `
                -PassThru
        }
    }

    dotnet run --project (Join-Path $repoRoot 'tools\SteamVRTranslator.OverlayBenchmark\SteamVRTranslator.OverlayBenchmark.csproj') `
        -c $Configuration -- $benchmarkArguments
    $benchmarkExitCode = $LASTEXITCODE

    if ($gpuMonitor) {
        Stop-Process -Id $gpuMonitor.Id -Force -ErrorAction SilentlyContinue
        $gpuMonitor = $null
        Start-Sleep -Milliseconds 300
    }

    if (($Stress -or $Realistic -or $AndroidMirror) -and (Test-Path -LiteralPath $gpuLogPath)) {
        $samples = @(Get-Content -LiteralPath $gpuLogPath | ForEach-Object {
            $columns = @($_ -split '\s+' | Where-Object { $_ })
            if ($columns.Count -lt 16 -or $columns[0].StartsWith('#')) {
                return
            }
            try {
                [pscustomobject]@{
                    Timestamp = [DateTimeOffset]([datetime]::ParseExact(
                        "$($columns[0]) $($columns[1])",
                        'yyyyMMdd HH:mm:ss',
                        [Globalization.CultureInfo]::InvariantCulture))
                    SmPercent = [double]$columns[6]
                    MemoryPercent = [double]$columns[7]
                    PowerWatts = [double]$columns[3]
                    FramebufferMiB = [double]$columns[14]
                }
            }
            catch {
            }
        })
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $gpuIdentity = & nvidia-smi `
            --query-gpu=name,driver_version,memory.total `
            --format=csv,noheader,nounits | Select-Object -First 1
        $identityColumns = @($gpuIdentity -split ',' | ForEach-Object { $_.Trim() })
        $report | Add-Member -NotePropertyName GpuName -NotePropertyValue $identityColumns[0] -Force
        $report | Add-Member -NotePropertyName GpuDriverVersion -NotePropertyValue $identityColumns[1] -Force
        $report | Add-Member -NotePropertyName GpuMemoryTotalMiB -NotePropertyValue ([int]$identityColumns[2]) -Force
        foreach ($stage in $report.Stages) {
            $startedAt = [DateTimeOffset]::Parse($stage.StartedAt)
            $endedAt = [DateTimeOffset]::Parse($stage.EndedAt)
            $stageSamples = @($samples | Where-Object {
                $_.Timestamp -ge $startedAt -and $_.Timestamp -le $endedAt
            })
            $stage | Add-Member -NotePropertyName GpuSampleCount -NotePropertyValue $stageSamples.Count -Force
            if ($stageSamples.Count -gt 0) {
                $stage | Add-Member -NotePropertyName GpuAverageSmPercent -NotePropertyValue ([math]::Round(($stageSamples | Measure-Object SmPercent -Average).Average, 2)) -Force
                $stage | Add-Member -NotePropertyName GpuPeakSmPercent -NotePropertyValue (($stageSamples | Measure-Object SmPercent -Maximum).Maximum) -Force
                $stage | Add-Member -NotePropertyName GpuAverageMemoryPercent -NotePropertyValue ([math]::Round(($stageSamples | Measure-Object MemoryPercent -Average).Average, 2)) -Force
                $stage | Add-Member -NotePropertyName GpuPeakMemoryPercent -NotePropertyValue (($stageSamples | Measure-Object MemoryPercent -Maximum).Maximum) -Force
                $stage | Add-Member -NotePropertyName GpuAveragePowerWatts -NotePropertyValue ([math]::Round(($stageSamples | Measure-Object PowerWatts -Average).Average, 2)) -Force
                $stage | Add-Member -NotePropertyName GpuPeakPowerWatts -NotePropertyValue (($stageSamples | Measure-Object PowerWatts -Maximum).Maximum) -Force
                $stage | Add-Member -NotePropertyName GpuPeakFramebufferMiB -NotePropertyValue (($stageSamples | Measure-Object FramebufferMiB -Maximum).Maximum) -Force
            }
        }
        [System.IO.File]::WriteAllText(
            $reportPath,
            ($report | ConvertTo-Json -Depth 8),
            [System.Text.UTF8Encoding]::new($false))
    }

    if ($benchmarkExitCode -ne 0) {
        throw "Overlay benchmark missed its requested FPS target. Inspect '$reportPath'."
    }
}
finally {
    if ($gpuMonitor) {
        Stop-Process -Id $gpuMonitor.Id -Force -ErrorAction SilentlyContinue
    }
    $monitor = Get-Process vrmonitor -ErrorAction SilentlyContinue
    if ($monitor) {
        $null = $monitor.CloseMainWindow()
    }
    Start-Sleep -Seconds 2
    Get-Process vrserver, vrcompositor, vrmonitor, vrdashboard -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    & $vrPathReg setconfig $originalConfig
    & $vrPathReg setlog $originalLog
}

if ($AndroidMirror) {
    Write-Host "SteamVR Android mirror benchmark completed: $reportPath"
}
elseif ($Realistic) {
    Write-Host "SteamVR Null Driver realistic overlay stress test completed: $reportPath"
}
elseif ($Stress) {
    Write-Host "SteamVR Null Driver overlay stress test completed: $reportPath"
}
else {
    Write-Host "SteamVR Null Driver overlay benchmark passed: $reportPath"
}
