param(
    [string]$LanAddress = '192.168.3.140',
    [ValidateSet('h264_qsv','libx264')][string]$WristEncoder = 'h264_qsv',
    [ValidateSet('none','cw','ccw')][string]$WristRotation = 'none',
    [ValidateRange(0,86400)][int]$TestSeconds = 0
)
$ErrorActionPreference = 'Stop'
$parsedAddress = $null
if (![System.Net.IPAddress]::TryParse($LanAddress, [ref]$parsedAddress) -or $parsedAddress.AddressFamily -ne 'InterNetwork') {
    throw 'LanAddress must be an IPv4 address.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$geminiProject = "$PSScriptRoot\WindowsWebRtcStreamer\GeminiWebRtcStreamer.csproj"
$wristProject = "$PSScriptRoot\WindowsWristStreamer\WristWebRtcStreamer.csproj"
foreach ($project in @($geminiProject, $wristProject)) {
    dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
$logDir = Join-Path $PSScriptRoot 'dual-video-logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$tag = Get-Date -Format 'yyyyMMdd-HHmmss'
$geminiProcess = $null
$wristProcess = $null
$wristRestartCount = 0
$wristUnavailable = $false
$wristHasBeenStable = $false
$wristStartupFailureReported = $false
$wristCandidateStartedAt = [DateTime]::MinValue
$wristRecoveryStableSeconds = 5

function Test-TcpEndpoint {
    param(
        [string]$Address,
        [int]$Port
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connection = $client.BeginConnect($Address, $Port, $null, $null)
        if (!$connection.AsyncWaitHandle.WaitOne(500)) { return $false }
        $client.EndConnect($connection)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Start-WristStreamer {
    param([int]$RestartCount)

    $suffix = if ($RestartCount -eq 0) { 'wrist' } else { 'wrist-r{0:D3}' -f $RestartCount }
    $wristDll = "$PSScriptRoot\WindowsWristStreamer\bin\Release\net8.0\WristWebRtcStreamer.dll"
    return Start-Process dotnet `
        -ArgumentList "`"$wristDll`" $LanAddress $WristEncoder `"USB Camera`" $WristRotation" `
        -WindowStyle Hidden `
        -PassThru `
        -RedirectStandardOutput "$logDir\$tag-$suffix.out.log" `
        -RedirectStandardError "$logDir\$tag-$suffix.err.log"
}

if ((Test-TcpEndpoint -Address $LanAddress -Port 8889) -or (Test-TcpEndpoint -Address $LanAddress -Port 8890)) {
    throw "检测到之前启动的视频服务仍在运行。请先关闭旧的视频服务窗口，再重新运行本脚本。"
}

try {
    $geminiDll = "$PSScriptRoot\WindowsWebRtcStreamer\bin\Release\net8.0\win-x64\GeminiWebRtcStreamer.dll"
    $geminiProcess = Start-Process dotnet -ArgumentList "`"$geminiDll`" $LanAddress h264_qsv" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logDir\$tag-gemini.out.log" -RedirectStandardError "$logDir\$tag-gemini.err.log"
    $wristProcess = Start-WristStreamer -RestartCount $wristRestartCount
    $wristCandidateStartedAt = [DateTime]::UtcNow
    Write-Host "Gemini: http://${LanAddress}:8889/gemini/whep"
    Write-Host "Wrist:  http://${LanAddress}:8890/wrist/whep"
    Write-Host "Logs: $logDir ($tag). Ctrl+C stops both owned process trees."
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($TestSeconds -eq 0 -or $timer.Elapsed.TotalSeconds -lt $TestSeconds) {
        if ($geminiProcess.HasExited) {
            throw "Gemini streamer exited; inspect $logDir\$tag-gemini*.log"
        }

        if ($wristProcess.HasExited) {
            if ($wristHasBeenStable -and !$wristUnavailable) {
                Write-Host "腕部相机连接已断开，主相机不受影响，正在等待重新连接……" -ForegroundColor Yellow
                $wristUnavailable = $true
            }
            elseif (!$wristHasBeenStable -and !$wristStartupFailureReported) {
                Write-Host "腕部视频暂时无法启动，正在后台重试；主相机不受影响。" -ForegroundColor Yellow
                $wristStartupFailureReported = $true
            }
            $wristProcess.Dispose()
            $wristProcess = $null
            $wristRestartCount++
            Start-Sleep -Seconds 2
            $wristProcess = Start-WristStreamer -RestartCount $wristRestartCount
            $wristCandidateStartedAt = [DateTime]::UtcNow
        }
        elseif (([DateTime]::UtcNow - $wristCandidateStartedAt).TotalSeconds -ge $wristRecoveryStableSeconds) {
            if ($wristUnavailable -or $wristStartupFailureReported) {
                Write-Host "腕部相机已连接，画面正在恢复。" -ForegroundColor Green
            }
            $wristHasBeenStable = $true
            $wristUnavailable = $false
            $wristStartupFailureReported = $false
        }

        Start-Sleep -Milliseconds 500
    }
}
finally {
    foreach ($process in @($geminiProcess, $wristProcess)) {
        if ($null -ne $process) {
            if (!$process.HasExited) { & taskkill.exe /PID $process.Id /T /F | Out-Null }
            $process.Dispose()
        }
    }
}
