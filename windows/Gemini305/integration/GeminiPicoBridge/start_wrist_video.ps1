param(
    [string]$LanAddress = '192.168.3.140',
    [ValidateSet('h264_qsv','libx264')][string]$Encoder = 'h264_qsv',
    [string]$Device = 'USB Camera',
    [ValidateSet('none','cw','ccw')][string]$Rotation = 'none'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
dotnet run --project "$PSScriptRoot\WindowsWristStreamer\WristWebRtcStreamer.csproj" -c Release -- $LanAddress $Encoder $Device $Rotation
if ($LASTEXITCODE -ne 0) { throw "Wrist streamer failed: $LASTEXITCODE" }
