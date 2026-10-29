# PICO reBotArm VR 遥操作项目

本仓库用于部署 PICO 4 Pro、Gemini 305、腕部 USB 相机和 DM B601 机械臂遥操作系统。

## 目录

- `unity/Rebotarm_VR`：Unity 2022.3.62f3 项目源码。
- `windows/Gemini305`：Windows 双路 H.264/WebRTC 视频服务及 Orbbec 依赖。
- `ubuntu/rebotarm-http-service`：需要覆盖到 Ubuntu `rebotarm-vr` 仓库的 HTTP 控制服务、驱动和回零功能文件。
- `docs`：部署、控制服务和 PICO 操作说明。

## 首次使用

1. 阅读 `docs/PICO机械臂部署与操作指南.pdf`。
2. 将 `ubuntu/rebotarm-http-service` 按其中的 `DEPLOY_SHUTDOWN.md` 部署到完整的 Ubuntu `rebotarm-vr` 仓库。
3. 复制 `ubuntu/rebotarm-http-service/config/control_server.env.example` 为完整 Ubuntu 仓库中的 `config/control_server.env`，修改非空令牌。真实令牌不要提交到 Git。
4. Windows 安装 .NET 8 SDK、FFmpeg 和 MediaMTX，并从 `windows/Gemini305` 启动双路视频。
5. 使用 Unity Hub 2022.3.62f3 打开 `unity/Rebotarm_VR`；Unity 会重建未提交的 `Library`、`Temp` 和 `Logs`。

## 每日启动入口

Windows PowerShell：

```powershell
cd "<仓库路径>\windows\Gemini305"
.\integration\GeminiPicoBridge\start_dual_video.ps1 -LanAddress <Windows局域网IPv4>
```

Ubuntu 控制服务：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_control_server.sh
```

Ubuntu 手柄 Bridge：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_bridge.sh
```

## 重要说明

- 仓库内出现的 `192.168.*` 地址是默认值或示例。换电脑、路由器或网卡后，在 PICO 设置面板中填写实际 Windows 和 Ubuntu 桥接网卡 IP。
- Unity 控制令牌只在运行时输入，不保存在项目中。
- `ubuntu/rebotarm-http-service` 不是完整 ROS 2/机械臂仓库，仅包含本项目新增和更新的文件；完整基础仓库或可导入的 VMware 虚拟机需要单独随发布版本提供。
- 当前 Windows Gemini 视频编码默认使用 Intel QSV。没有可用 Intel 核显时，需要补充并验证 Gemini 的 `libx264` 启动方式。
- 发布到公开 GitHub 前，应确认 PICO SDK、Orbbec SDK 和场景环境素材允许再分发。本仓库目前没有统一的项目许可证。

