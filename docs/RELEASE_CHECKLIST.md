# 发布前必须补齐或确认

## 必须补齐

- [ ] 完整 Ubuntu `rebotarm-vr` 基础仓库，或已经配置完成、可导入 VMware 的虚拟机镜像。当前 `ubuntu/rebotarm-http-service` 只是增量文件，不能单独运行。
- [ ] 已通过真机验收的 PICO APK，放在 GitHub Release 附件中，并记录对应源码提交版本。
- [ ] Windows 依赖安装说明或安装脚本：.NET 8 SDK、FFmpeg、MediaMTX、显卡编码要求和 USB 相机名称。
- [ ] VMware 网络与 USB 设备说明：桥接网卡、NAT 网卡用途、机械臂 USB 自动连接方式。
- [ ] 在另一台干净电脑完成一次从下载到双视频、dry-run、真机控制的完整部署验证。

## 公开仓库前必须确认

- [ ] PICO Unity Integration SDK、Orbbec SDK、Unity 场景环境素材及字体允许随项目公开再分发。
- [ ] 为自己编写的代码选择许可证；第三方文件保留其原许可证，不能用项目许可证覆盖。
- [ ] 删除真实令牌、个人账号、SSH 私钥、局域网敏感信息和不需要公开的实验数据。
- [ ] 决定仓库为 Private 还是 Public。许可证和素材授权未确认前使用 Private。

## 建议补充

- [ ] 发布版本号、日期和 CHANGELOG。
- [ ] APK、虚拟机镜像和大文件的 SHA-256 校验值。
- [ ] Windows、Ubuntu、PICO 三端的一键启动或桌面快捷方式。
- [ ] 一张硬件接线图和一段 1 分钟正常启动演示视频。

