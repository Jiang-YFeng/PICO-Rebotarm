# Windows H.264/WebRTC 发送端

本项目与 `WindowsStreamer`（MJPEG/TCP）相互独立，不会覆盖原链路。同一时刻只能运行其中一个摄像头采集程序。

数据链路：

`Gemini 305 MJPEG → FFmpeg JPEG解码 → Intel QSV H.264编码 → RTSP → MediaMTX → WHEP/WebRTC`

## 依赖

```powershell
winget install --id Gyan.FFmpeg.Shared --exact
winget install --id bluenviron.mediamtx --exact
```

当前机器实测：`h264_qsv` 可用；`h264_nvenc` 因 NVIDIA 驱动提供的 NVENC API 版本过旧而不可用。

## 启动

```powershell
cd "D:\Common Application\Gemini305"
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"

dotnet run `
  --project ".\integration\GeminiPicoBridge\WindowsWebRtcStreamer\GeminiWebRtcStreamer.csproj" `
  -c Release -- `
  192.168.3.140 h264_qsv
```

浏览器测试地址：`http://192.168.3.140:8889/gemini`

Unity WHEP地址：`http://192.168.3.140:8889/gemini/whep`

如果 QSV 启动失败，可以将最后一个参数改为 `libx264`。

第一个参数是 Windows 当前可被 PICO 访问的局域网 IPv4。更换网络后，同时修改该参数和 Unity 接收器的 WHEP 地址；程序会据此生成本次运行的 MediaMTX 配置。

## Unity 场景配置与链路切换

Unity 项目位于 `D:\PICO\Rebotarm_VR`。项目包含两个相互独立的接收组件：

- `GeminiReceiver`：原 MJPEG/TCP 备用链路。
- `GeminiWebRtcReceiver`：新增 H.264/WebRTC 链路。

首次导入新增脚本并编译完成后，在 Unity 菜单执行：

`Gemini 305 → 配置 WebRTC 接收器`

该命令会在 `Teleoperation` 场景中创建并配置 `GeminiWebRtcReceiver`，同时设置 Android 为 IL2CPP、ARM64、Internet Access Require、关闭 Optimized Frame Pacing，并保持新物体默认关闭。测试 WebRTC 时只启用 `GeminiWebRtcReceiver`、关闭 `GeminiReceiver`；切回 MJPEG 时反向操作，避免两个组件同时写入 `VideoScreen`。

Unity 采用 WHEP 标准信令：请求体和响应体都是 `application/sdp` 的原始 SDP，不使用旧 ROS 服务的 JSON `/offer` 格式。接收脚本只提交 H.264 codec capability，并检查 Answer 中是否包含 `H264/90000`。
