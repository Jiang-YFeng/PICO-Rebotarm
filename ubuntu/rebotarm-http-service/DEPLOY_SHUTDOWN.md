# 回零并关闭机械臂：部署与验证

本目录是 Windows 共享文件副本，尚未修改 Ubuntu 运行中的服务。
Unity 的 RobotControlClient.cs 已直接修改，无需额外挂组件；运行时回零窗口新增三项选择。

## 部署文件

在机械臂已按原有流程停止并断电后操作，先备份 Ubuntu 对应文件。
以 /home/jyf/Projects/1/rebotarm-vr 为根目录，将以下文件对应覆盖：

| 本共享目录 | Ubuntu 项目内路径 |
|---|---|
| scripts/robot_control_server.py | scripts/robot_control_server.py |
| driver/hardwaremanager.py | driver/src/rebotarm_driver/rebotarm_driver/hardwaremanager.py |
| driver/services.py | driver/src/rebotarm_driver/rebotarm_driver/services.py |

其余导出文件未改动。不覆盖 token 配置，不需要更换 token。

Ubuntu 编译及模拟启动：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/build.sh
./scripts/start_control_server.sh --dry-run
```

上述编译脚本已从仓库导出包核查；不直接启动内部 Python 文件。
Unity 等待脚本编译完成，在 Editor 中检查新弹窗，再构建 APK 到 PICO。
先完成本次 dry-run，停止模拟服务后，真实服务入口为：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_control_server.sh
```

## 操作顺序

1. 松开遥操作握把，在面板停止 Servo，再停止 VR。
2. 点击回零，选择“仅回零”或“回零并关闭机械臂”。取消不发送命令。
3. 仅回零：保持原有轨迹及驱动运行。
4. 回零并关闭：原有回零脚本结束后，调用新增 /rebotarm/prepare_shutdown_at_zero。
5. 驱动检查运动未运行、重力补偿未运行、电机反馈非空、六关节连续五次位置和速度均在阈值内。
   当前阈值为位置绝对值 <= 0.03 rad，速度绝对值 <= 0.03 rad/s；采样间隔 0.1 秒。
6. 调用原有 disable；完成后锁止新的运动指令。进程退出跳过 safe_home，避免重新使能。
7. HTTP 服务只关闭本次服务自己启动的 driver/model 进程，并等待相关 ROS 节点退出。
8. 完成后驱动、模型显示 stopped；HTTP 服务、UDP Bridge 和视频服务仍保留。
   再次使用需重新启动驱动并按原顺序进入工作姿态。

新接口 POST /api/shutdown-and-disable，请求体 {"confirm":"ZERO_DISABLE"}。
原 /api/shutdown-to-zero 与 {"confirm":"ZERO"} 保留。
旧后端不会识别新接口，应先部署后端再使用新按钮。

## 失败处理与验证边界

- 回零脚本失败、超时：不进入失能和进程关闭步骤。超时不强杀轨迹，busy 保留到脚本退出；不会晚到后自动追加失能。
- 失能服务失败或超时：不发送关闭驱动/模型的信号。超时意味着结果未知，不能据此认定电机仍使能。
- 驱动或模型不是本服务启动、新失能服务不存在：在回零前拒绝组合命令。
- 进程退出超时：报告部分完成，不强杀；不得直接重复回零。
- 位置/速度阈值需要现场验证；本次没有操作真实机械臂。
- 底层 motorbridge 不在所给源码内。当前五次读取会主动请求反馈并拒绝 None、非有限数值和总线调用异常，
  但尚不能证明 get_state 返回的都是新数据；底层 disable_all 也没有在这些文件中提供独立失能确认。
  因此返回成功表示失能调用完成，不是对每个电机断力的独立确认。
  **在完成反馈新鲜度/电机状态验证前，不应把本功能当作已验证的安全关机或断电许可。**
  后续需核查 motorbridge 的反馈时间戳/序号及电机使能状态 API，再加入反馈过期拒绝与失能状态确认。

## 验收

先 dry-run 验证取消、仅回零、组合关闭、未停止 VR 时拒绝、再次启动。
部署后先查看新服务存在；真实测试需要现场监护，先单次无负载回零，确认驱动退出不再次运动。
检查失能前关节实测值、失能后电机状态、driver/model 退出以及 HTTP/视频保持在线。
不得通过 HTTP 成功、驱动进程退出或关节角度为零，单独推断所有电机已经失能。

离线测试位于 tests/test_shutdown.py，使用 mock 隔离 ROS、进程和硬件。
Unity 已通过引用本机 Unity DLL 的 C# 编译检查；尚未完成 Unity 场景显示、Android 构建和真机验收。
