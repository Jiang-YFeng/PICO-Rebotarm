# Gemini + 腕部 USB 双摄像头

## 已确认的设备和实现

2026-10-21 实机枚举：USB Camera，USB VID_1E45 / PID_0209，YUY2。
480x640、360x640 支持 30 FPS；600x800 等较大模式只有 5–10 FPS。
默认选 480x640、30 FPS，H.264 QSV，1.2 Mbps，无 B 帧。
5 秒裸采集得到 150 帧；双服务 20 秒测试均发布 H.264，Gemini 采集约 30 FPS，腕部稳定阶段约 30 FPS。
QSV 初始化期间出现过缓冲丢帧；稳定阶段未见持续丢帧。累计 fps 会从低值逐渐接近 30。
尚未完成 PICO 双解码、双画面端到端延迟测试。

## 一次启动两个发送端

先关闭占用摄像头的预览程序，停止原来单独运行的 Gemini 发送端。
在 PowerShell 执行：

```powershell
cd "D:\Common Application\Gemini305"
.\integration\GeminiPicoBridge\start_dual_video.ps1
```

脚本设置项目原有 dotnet 环境、编译、隐藏启动两个独立进程并监测退出。
按 Ctrl+C 会结束本次脚本拥有的两个进程树。不要直接关闭终端窗口。
日志在 integration/GeminiPicoBridge/dual-video-logs，按启动时间分别记录两个发送端。
脚本应在 PowerShell 终端中使用，不要双击。

单独启动腕部：

```powershell
.\integration\GeminiPicoBridge\start_wrist_video.ps1
```

若相机实物横装，画面需要顺时针转 90 度：

```powershell
.\integration\GeminiPicoBridge\start_dual_video.ps1 -WristRotation cw
```

逆时针为 ccw，默认 none。不进行猜测性旋转；按实际图像选择。
旋转后输出 640x480，未旋转为 480x640。
腕部 QSV 不可用时，使用 -WristEncoder libx264。
IP 改变时，使用 -LanAddress 新IP，并同步修改 Unity 两个 WHEP URL。

| 通道 | 浏览器预览 | Unity WHEP | ICE UDP | 本机 RTSP |
|---|---|---|---|---|
| Gemini | http://192.168.3.140:8889/gemini | http://192.168.3.140:8889/gemini/whep | 8189 | 8554 |
| 腕部 | http://192.168.3.140:8890/wrist | http://192.168.3.140:8890/wrist/whep | 8190 | 8555 |

Windows 防火墙若阻止腕部服务，应允许专用局域网入站 TCP 8890、UDP 8190。
RTSP 8555 只绑定回环地址，无需对外开放。

## Unity 绑定

脚本已经写入 D:/PICO/Rebotarm_VR/Assets/Scripts/Vedio/：

- GeminiWebRtcReceiver.cs：原主相机接收器，保留原有配置，改为共享更新循环、独立材质实例。
- UsbWebRtcReceiver.cs：继承已验证的接收实现，默认腕部 WHEP URL。
- SharedWebRtcUpdate.cs：自动创建，管理全局唯一 WebRTC.Update，无需手动挂载。

Unity 等待重新编译。退出 Play 后进行以下配置，并保存场景：

1. 保留原 GeminiWebRtcReceiver 对象及其 Video Screen 引用。
2. 将新 Quad 命名 WristVideoScreen。
3. 给小屏幕创建独立的 Unlit/Texture 材质，例如 WristVideoMaterial。
4. 在 Hierarchy 创建空物体 WristVideoReceiver，Add Component -> Usb Web Rtc Receiver。
5. WHEP URL 填 http://192.168.3.140:8890/wrist/whep。
6. 将小 Quad 的 Mesh Renderer 拖入 Video Screen。不要误拖主屏幕。
7. Auto Reconnect 开启，Reconnect Delay Seconds = 2。
8. Flip Vertically 默认关闭，仅上下颠倒时开启；90 度转向使用发送端参数。
9. 原 MJPEG 和 RosWebRtcVideoReceiver 保持禁用，避免覆盖主屏幕材质。

无需给小 Quad 添加 Camera 或 VideoPlayer。
两个视频共享同一个全局更新循环，各自拥有 PeerConnection、轨道、材质和重连状态。

## 小屏幕布局

默认竖幅宽高比 3:4；若无缩放父对象，可先用 Scale X=0.6、Y=0.8、Z=1。
旋转为横幅后改用 4:3，例如 X=0.8、Y=0.6。
若父对象有非均匀缩放，要按最终世界尺寸检查比例，不能只看局部 Scale。
小屏幕面向视点，并比主屏幕略靠近观察者，避免两平面共面闪烁。
在目前主屏幕 z=2 且观察者在原点的布局下，小屏幕可尝试 z=1.98。
先保留既有右上角位置，根据头显内实际可读性调整大小，避免遮挡夹爪和目标。
不需要交互时可以移除小屏幕 Mesh Collider，并关闭 Cast Shadows / Receive Shadows。

## 验证与构建

1. 启动两路服务，两个日志都应出现 stream is available and online, 1 track (H264)。
2. 浏览器分别打开两个预览地址，遮挡一个镜头，确认主、腕部没有接反。
3. Unity Play：两组件 Connection Status 应为 Connected，腕部 Resolution=480x640（或旋转后640x480）。
4. 连续移动手指或计时器检查画面更新，观察 Received FPS、Dropped Frames 与抖动缓冲值。
5. 只禁用腕部接收组件再启用，主画面应继续播放；腕部应重新连接。
6. 在两路运行时测量手柄 UDP 频率和延迟，确认没有回退。
7. File > Build Settings：确认 Android、Teleoperation 场景已勾选，沿用已经成功的 ARM64、图形 API 和网络权限设置。
8. 保存场景，构建新的 APK（建议 Builds/Rebotarm_VR_DualCamera.apk），连接 PICO 后 Build And Run。
9. PICO 上先验证双画面、方向、清晰度、两路解码稳定性；实测 10 分钟以上再进行抓取体验比较。

当前没有通过 ADB 连接的 PICO，因此尚未执行安装或真机双流验收。
视频帧率正常不能直接证明端到端延迟低；可用同一计时器对比现场与头显画面，或高速拍摄动作及屏幕测量。
