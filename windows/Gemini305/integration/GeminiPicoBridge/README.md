# Gemini 305 到 PICO Unity 图像桥接

这是一个用于打通端到端链路的低延迟原型：

`Gemini 305 → OrbbecSharp → TCP 传输 MJPEG → PICO Unity Texture2D`

Windows 发送端请求摄像头原生的 `1280×720、30 FPS、MJPEG` 视频流，并直接转发 JPEG 数据，不进行二次编码。程序始终只保留最新帧，避免客户端处理速度下降时不断积累播放延迟。

## 1. 编译并运行 Windows 发送端

首先关闭 `Samples.Color.exe`，因为它和发送端不能同时打开同一台摄像头。

在项目根目录打开 PowerShell：

```powershell
cd "D:\Common Application\Gemini305"
$env:DOTNET_CLI_HOME="$PWD\.dotnet-cli"
$env:NUGET_PACKAGES="$PWD\.nuget\packages"
```

### 仅在本机进行 Unity Editor 测试

下面的命令只监听本机回环地址 `127.0.0.1`，不会向局域网开放：

```powershell
dotnet run --project .\integration\GeminiPicoBridge\WindowsStreamer\GeminiPicoStreamer.csproj -c Release -- 5000 - 127.0.0.1
```

### 在当前可信局域网中连接 PICO

当前 Windows 电脑的局域网地址是 `192.168.3.140`。使用以下命令启动无令牌模式：

```powershell
dotnet run --project .\integration\GeminiPicoBridge\WindowsStreamer\GeminiPicoStreamer.csproj -c Release -- 5000 - 192.168.3.140
```

参数含义：

- `5000`：TCP 监听端口。
- `-`：关闭访问令牌认证。
- `192.168.3.140`：只监听电脑的这一块局域网接口。

即使关闭认证，也不要将监听地址改成 `0.0.0.0`。Windows 弹出防火墙提示时，只允许程序访问“专用网络”。不要在路由器上把 TCP 5000 端口映射到互联网。

### 启用访问令牌

如果网络中存在不受信任的设备，或者以后需要跨局域网使用，请配置至少 12 个字符的令牌：

```powershell
dotnet run --project .\integration\GeminiPicoBridge\WindowsStreamer\GeminiPicoStreamer.csproj -c Release -- 5000 your-private-token 192.168.3.140
```

Unity 端必须填写相同的令牌。不要把正式令牌发布或提交到代码仓库。令牌只用于阻止未经授权的客户端直接接收画面，它不提供传输加密。

发送端正常运行时会输出类似信息：

```text
Gemini 305: 1280x720 @ 30 FPS, MJPEG
TCP server: 192.168.3.140:5000 (authentication disabled)
Capture: 30.0 FPS, latest JPEG: 132 KiB
```

## 2. 将接收端加入 PICO Unity 项目

1. 将 `PicoUnity/GeminiMjpegReceiver.cs` 复制到 Unity 项目的 `Assets/Scripts` 目录。
2. 创建一个 `Canvas` 和 `Raw Image`；如果需要将画面放在三维场景中，也可以创建带材质的 `Quad` 或 `Plane`。
3. 创建一个 GameObject，并将 `GeminiMjpegReceiver` 组件挂载到该对象。
4. 在组件的 `Host` 中填写 Windows 局域网地址 `192.168.3.140`。
5. 将 `Port` 保持为 `5000`。
6. Windows 发送端使用 `-` 关闭认证时，将 `Access Token` 留空；发送端启用令牌时，两端填写完全相同的令牌。
7. 将场景中的 `Raw Image` 拖到 `Raw Image` 字段，或者将三维物体的 Renderer 拖到 `Target Renderer` 字段。二者设置一个即可。
8. 切换到 Android 平台，构建应用并在 PICO 上运行。

PICO 和 Windows 电脑必须能够通过网络互相访问。使用访客 Wi-Fi 时，需要确认路由器没有启用“客户端隔离”或“AP 隔离”。

Unity 使用网络 API 时通常会自动添加 Android 的 `INTERNET` 权限。如果项目使用了自定义 `AndroidManifest.xml`，请确认其中包含：

```xml
<uses-permission android:name="android.permission.INTERNET" />
```

## 3. 先在 Unity Editor 中测试

在 Unity 场景中挂载接收脚本，并进行以下设置：

```text
Host         = 127.0.0.1
Port         = 5000
Access Token = 留空
```

然后使用前面的本机测试命令启动 Windows 发送端，并在 Unity Editor 中点击 Play。确认画面能够正常显示后，再将 `Host` 改为 `192.168.3.140`，构建并部署到 PICO。

## 4. PICO 局域网配置

在 PICO 上使用以下设置：

```text
Host         = 192.168.3.140
Port         = 5000
Access Token = 留空（Windows 端使用 - 时）
```

如果无法连接，请依次检查：

1. Windows 发送端是否持续输出采集帧率。
2. PICO 与电脑是否连接同一个 Wi-Fi 或可互相路由的网络。
3. Windows 防火墙是否允许 TCP 5000 端口通过专用网络。
4. 路由器是否启用了客户端隔离。
5. Unity 中的 IP、端口和令牌是否与 Windows 端一致。
6. `Samples.Color.exe` 或其他程序是否正在占用 Gemini 305。

## 5. 跨不同局域网使用

不要直接把当前 TCP 5000 端口暴露到公网。跨局域网原型建议使用 Tailscale、ZeroTier 或 WireGuard，让 Windows 和 PICO 处于一个虚拟局域网中，然后：

1. Windows 发送端绑定电脑的虚拟网络 IP。
2. Unity 的 `Host` 填写相同的虚拟网络 IP。
3. 启用访问令牌。
4. 防火墙只允许对应的虚拟网络接口访问发送端。

正式版本建议将 MJPEG/TCP 升级为 H.264/WebRTC，通过 ICE、STUN 和 TURN 处理跨网络连接，并利用 PICO 的硬件视频解码降低功耗和延迟。

## 6. 数据传输协议

建立 TCP 连接后，如果发送端启用了访问令牌，客户端必须先发送与服务器配置完全一致的 UTF-8 令牌。无令牌模式下不发送任何认证数据。

认证完成后，每一帧由一个固定的 24 字节帧头和一段 JPEG 数据组成：

- 字节 0–3：ASCII 字符串 `ORBB`。
- 字节 4–7：JPEG 数据长度，有符号 32 位大端整数。
- 字节 8–11：图像宽度，有符号 32 位大端整数。
- 字节 12–15：图像高度，有符号 32 位大端整数。
- 字节 16–23：帧序号，有符号 64 位大端整数。
- 后续字节：指定长度的完整 JPEG 图像。

该原型优先保证结构简单、方便验证完整链路。确认 Windows 到 PICO 的画面传输稳定后，再升级为硬件 H.264 编码和 WebRTC 传输。
