# 打包内容与排除项

## 已包含

- Unity：`Assets`、`Packages`、`ProjectSettings`，包含全部 `.meta` 文件。
- Windows：双路视频服务、Orbbec .NET 源码和运行库、相机头文件、示例与许可证目录。
- Ubuntu：共享文件夹中的 HTTP 服务、驱动、脚本、测试和部署说明。
- 文档：`CONTROL_SERVER.md`、`PICO_REBOTARM_OPERATION_GUIDE.md` 和新手 PDF 指南。

## 有意排除

- Unity `Library`、`Temp`、`Logs`、`UserSettings` 和已有 APK 构建。
- Windows `.nuget`、`.dotnet-cli`、`bin`、`obj` 和运行日志。
- Ubuntu 的真实 `control_server.env`、日志、Python 缓存及 ROS 构建产物。
- Windows 原目录中的离线安装包集合。

这些内容属于可重建缓存、本机状态、密钥或不适合直接进入 Git 历史的大文件。

