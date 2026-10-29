# Gemini 305 → Windows → PICO Unity 视频链路操作手册

## 1. 结论：每次需要启动哪些程序

正常使用时，Windows 端只需要运行下面两个发送端中的一个：

- WebRTC：`GeminiWebRtcStreamer`，推荐使用，1280×720、30 FPS、H.264。
- MJPEG：`GeminiPicoStreamer`，备用链路，当前代码配置为 1280×720、20 FPS。

二者不能同时运行，因为同一时刻只能有一个程序打开 Gemini 305。

WebRTC 模式不需要另外手动启动 FFmpeg 或 MediaMTX。`GeminiWebRtcStreamer` 会自动启动它们，并在退出时自动关闭它们。也不需要运行 Orbbec 官方示例。

## 2. 每次运行前检查

1. 将 Gemini 305 连接到 Windows USB 3.x 接口。
2. 确认 Windows 和 PICO 位于同一 Wi-Fi，且可以互相访问。
3. 确认 Windows 当前局域网 IPv4 是 `192.168.3.140`。如果 IP 改变，需要同时修改启动参数和 Unity 接收地址。
4. 关闭 Orbbec 示例程序以及另一个视频发送端。
5. 第一次使用时，Windows 防火墙提示只允许“专用网络”访问。

查询 Windows IPv4：

```powershell
ipconfig
```

## 3. 推荐模式：启动 H.264/WebRTC

先在 PowerShell 中运行：

```powershell
cd "D:\Common Application\Gemini305"

$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"

dotnet run `
  --project ".\integration\GeminiPicoBridge\WindowsWebRtcStreamer\GeminiWebRtcStreamer.csproj" `
  -c Release -- `
  192.168.3.140 h264_qsv
```

参数说明：

- `192.168.3.140`：Windows 当前局域网 IPv4。
- `h264_qsv`：使用 Intel Quick Sync 硬件编码 H.264。
- 如果 QSV 无法启动，可将最后一个参数改成 `libx264`，使用 CPU 软件编码。

发送端正常时应看到：

```text
Gemini 305: 1280x720 @ 30 FPS, MJPEG input
stream is available and online, 1 track (H264)
Capture 30.0 FPS
```

测试地址：

```text
浏览器：http://192.168.3.140:8889/gemini
Unity WHEP：http://192.168.3.140:8889/gemini/whep
```

推荐启动顺序：

1. 先启动 Windows WebRTC 发送端。
2. 等待终端出现 `stream is available and online, 1 track (H264)`。
3. Unity 中关闭 `GeminiReceiver`。
4. Unity 中启用 `GeminiWebRtcReceiver`。
5. 再进入 Unity Play，或启动 PICO 应用。

Unity 中 `GeminiWebRtcReceiver` 应配置为：

```text
Whep Url = http://192.168.3.140:8889/gemini/whep
Video Screen = 场景中的 VideoScreen Renderer
Auto Reconnect = 开启
Reconnect Delay Seconds = 2
```

如果 Unity 先于发送端启动，控制台可能暂时出现 WHEP 404。发送流上线后自动重连即可；按上述启动顺序可以避免该提示。

## 4. 备用模式：启动 MJPEG/TCP

先停止 WebRTC 发送端，然后运行：

```powershell
cd "D:\Common Application\Gemini305"

$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"

dotnet run `
  --project ".\integration\GeminiPicoBridge\WindowsStreamer\GeminiPicoStreamer.csproj" `
  -c Release -- `
  5000 - 192.168.3.140
```

参数说明：

- `5000`：MJPEG TCP 端口。
- `-`：关闭 Access Token 身份验证，因此 Unity 的 `Access Token` 留空。
- `192.168.3.140`：Windows 当前局域网 IPv4。

Unity 中切换为：

1. 关闭 `GeminiWebRtcReceiver`。
2. 启用 `GeminiReceiver`。
3. 确认 `Host = 192.168.3.140`、`Port = 5000`、`Access Token` 为空。
4. 再进入 Play 或启动 PICO 应用。

## 5. 两种链路切换规则

| 模式 | Windows 发送端 | Unity 启用 | Unity 关闭 |
|---|---|---|---|
| H.264/WebRTC | `GeminiWebRtcStreamer` | `GeminiWebRtcReceiver` | `GeminiReceiver` |
| MJPEG/TCP | `GeminiPicoStreamer` | `GeminiReceiver` | `GeminiWebRtcReceiver` |

禁止同时启动两个 Windows 发送端，也不要同时启用两个 Unity 接收器，否则会争用摄像头或同时覆盖 `VideoScreen` 材质。

## 6. 正常停止

在运行发送端的 PowerShell 窗口按：

```text
Ctrl+C
```

WebRTC 程序会一并关闭其启动的 FFmpeg 和 MediaMTX。切换链路前，应等待当前程序完全退出，再启动另一个发送端。

## 7. 只需执行一次的安装命令

下面不是每次启动都要运行。只有新电脑、工具被卸载或找不到程序时才执行：

```powershell
winget install --id Gyan.FFmpeg.Shared --exact
winget install --id bluenviron.mediamtx --exact
```

.NET SDK、Orbbec SDK、FFmpeg 和 MediaMTX 当前机器已经安装完成，无需重复安装。

## 8. 常见故障检查

### Unity 报 WHEP 404

确认 Windows 终端已经出现：

```text
stream is available and online, 1 track (H264)
```

如果尚未出现，等待摄像头和编码器启动，或先停止 Unity Play，待发送端上线后再进入 Play。

### 摄像头无法打开

关闭以下可能占用 Gemini 305 的程序：

- Orbbec 官方示例。
- 另一个 Gemini 视频发送端。
- 其他正在读取摄像头的程序。

### PICO 无法连接 WebRTC

检查 Windows 端口：

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -in 8554, 8889, 5000

Get-NetUDPEndpoint |
  Where-Object LocalPort -eq 8189
```

WebRTC 正常运行时主要应看到：

- TCP `8889`：WHEP/HTTP 信令。
- UDP `8189`：WebRTC ICE 媒体传输。
- TCP `8554`：仅 Windows 本机内部的 RTSP 发布链路。

MJPEG 正常运行时应看到 TCP `5000`。

### 更换 Wi-Fi 后无法连接

假设 Windows 新地址变为 `192.168.1.50`：

1. WebRTC 启动参数改成 `192.168.1.50 h264_qsv`。
2. Unity `Whep Url` 改成 `http://192.168.1.50:8889/gemini/whep`。
3. MJPEG 模式的启动地址和 Unity `Host` 同样改成 `192.168.1.50`。
4. 确认新网络未启用 AP 隔离或客户端隔离。

当前方案面向同一局域网使用。跨局域网不能只修改 IP，还需要 VPN，或配置公网可达的 ICE/STUN/TURN 服务。

## 9. 日常最简操作

WebRTC：运行第 3 节命令，等待 H.264 流上线，然后只启用 Unity 的 `GeminiWebRtcReceiver`。

MJPEG：先按 `Ctrl+C` 停止 WebRTC，运行第 4 节命令，然后只启用 Unity 的 `GeminiReceiver`。

除所选发送端和 Unity/PICO 应用外，没有其他必须手动启动的 Windows 命令。
