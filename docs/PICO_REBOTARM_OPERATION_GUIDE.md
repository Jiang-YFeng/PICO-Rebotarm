# PICO App 桥接与 reBotArm 遥操作指南

本文档适用于当前工作空间：

```text
/home/jyf/Projects/1/rebotarm-vr
```

目标是让 PICO Unity App 同时完成两件事：

1. 通过 HTTP 控制 Ubuntu 上的机械臂启动、Servo 启停、安全位姿和回零流程。
2. 通过 UDP 将 PICO 头显、左右手柄位姿和按键数据发送给 ROS 2 Bridge，再由 MoveIt Servo 控制机械臂。

## 1. 系统结构和端口

```text
PICO Unity 控制面板
  └─ HTTP/TCP 5200 ─> robot_control_server.py
                         ├─ 驱动和模型
                         ├─ 安全位姿脚本
                         ├─ VR/MoveIt Servo
                         └─ 停止、回零和失能

PICO Unity 手柄数据
  └─ UDP 5100 ─────> openarmx_teleop_bridge_vr_node
                         └─ /pico_* 和 /vr/* ROS 2 话题
                                └─ vr_to_control
                                       └─ MoveIt Servo
                                              └─ 机械臂
```

默认端口：

| 功能 | 协议 | Ubuntu 端口 |
|---|---|---:|
| HTTP 机械臂控制服务 | TCP | 5200 |
| PICO UDP Bridge | UDP | 5100 |
| WebRTC 视频服务（可选） | TCP/UDP | 8080 及 WebRTC 协商端口 |

HTTP 服务和 UDP Bridge 是两个独立进程。只启动 HTTP 服务不会接收手柄数据；只启动 Bridge 也不会启动真机驱动或 MoveIt Servo。

## 2. 每次操作前的安全检查

真机操作前必须确认：

- 机械臂底座固定，运动范围内无人、无障碍物。
- 急停可触及并且工作正常。
- PICO 左右扳机均已松开。
- USB 控制器是预期的 `/dev/ttyACM0`。
- 没有另一个工作空间、仿真或旧 ROS 2 进程同时控制机械臂。
- 首次测试只移动约 1 cm，逐轴确认平移和旋转方向。
- 不要在非零位直接关闭驱动或切断机械臂电源。

检查环境和设备：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
./scripts/preflight.sh
```

若修改过 C++、Python 驱动或 ROS 2 包，先编译：

```bash
./scripts/build.sh
```

普通脚本会自行加载 `scripts/env.sh`。手动执行 ROS 2 命令时，新终端先运行：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
source scripts/env.sh
```

## 3. 查询 Ubuntu 虚拟机 IP

快速查询：

```bash
hostname -I
```

更清楚地查看 IPv4 地址和网卡：

```bash
ip -4 -br address
```

选择与 PICO、Windows 宿主机处于同一局域网的地址，不要使用 `127.0.0.1`、Docker 网卡或只在虚拟机内部可达的地址。

在 PICO Unity 中配置：

```text
HTTP 服务器：http://<Ubuntu-IP>:5200
UDP 目标地址：<Ubuntu-IP>
UDP 目标端口：5100
```

虚拟机 IP 可能在重启、切换 Wi-Fi 或 DHCP 续租后变化。连接失败时首先重新执行 `hostname -I`。

## 4. HTTP 控制服务令牌

编辑配置：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
nano config/control_server.env
```

局域网临时联调允许空令牌：

```bash
REBOTARM_CONTROL_TOKEN=
```

真机长期使用应设置非空随机令牌，例如：

```bash
REBOTARM_CONTROL_TOKEN=请替换为随机长字符串
```

在 `nano` 中按 `Ctrl+O`、回车保存，再按 `Ctrl+X` 退出。令牌修改后必须重启 HTTP 服务。非空令牌由 Unity 放在请求头：

```text
X-Rebotarm-Token: <令牌>
```

空令牌仅适合可信局域网调试。

## 5. 启动机械臂 HTTP 控制服务：两种模式

### 5.1 Dry-run 安全联调模式

此模式用于验证 PICO 控制面板、按钮顺序、HTTP 返回值和状态机，不启动真实驱动，也不会让机械臂运动：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
./scripts/start_control_server.sh --dry-run
```

保持终端开启。看到以下含义的日志即表示成功：

```text
reBotArm控制服务监听 http://0.0.0.0:5200，dry_run=True
```

### 5.2 真机控制服务模式

只有完成 dry-run 联调和现场安全检查后才能使用：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
./scripts/start_control_server.sh
```

启动 HTTP 服务本身不会立即运动；PICO 后续调用 `/api/start-driver` 才会启动真实驱动并访问 `/dev/ttyACM0`。

也可以使用项目根目录入口启动同样的两种模式：

```bash
python3 robot_control_server.py --dry-run
python3 robot_control_server.py
```

不要同时启动两份服务。若出现 `OSError: [Errno 98] Address already in use`，检查端口：

```bash
sudo ss -ltnp | grep ':5200'
```

确认旧进程确实是本项目控制服务后再停止它，然后重新启动。不要直接杀死身份不明的进程。

## 6. 检查 HTTP 服务

空令牌时：

```bash
curl -sS --max-time 3 http://127.0.0.1:5200/api/status
echo
```

非空令牌时：

```bash
set -a
source config/control_server.env
set +a

curl -sS --max-time 3 \
  http://127.0.0.1:5200/api/status \
  -H "X-Rebotarm-Token: ${REBOTARM_CONTROL_TOKEN}"
echo
```

主要状态字段：

- `dry_run`：是否为安全联调模式。
- `driver`：真机驱动状态。
- `model`：模型和 TF 状态。
- `vr`：`vr_to_control` 与 `servo_node` 状态。
- `servo`：Servo 是否已明确启动或停止。
- `busy`：是否正在执行不可并发的运动命令。

服务日志位于：

```text
/home/jyf/Projects/1/rebotarm-vr/logs/control_server/
```

## 7. 启动 PICO UDP Bridge

无论使用 HTTP 控制还是手动 ROS 2 控制，PICO 遥操作都需要单独启动 Bridge：

```bash
cd /home/jyf/Projects/1/rebotarm-vr || exit 1
./scripts/start_bridge.sh
```

保持该终端开启。默认监听：

```text
0.0.0.0:5100/UDP
```

检查 UDP 端口：

```bash
ss -lunp | grep ':5100'
```

打开 PICO App 并开始发送后，检查左右手柄是否有数据：

```bash
source scripts/env.sh
ros2 topic hz /pico_left_controller/pose
ros2 topic hz /pico_right_controller/pose
```

查看单帧数据：

```bash
ros2 topic echo /pico_left_controller/pose --once
ros2 topic echo /pico_right_controller/pose --once
ros2 topic echo /pico_left_controller/trigger --once
ros2 topic echo /pico_right_controller/trigger --once
```

正常目标约为 50 Hz。只测试 Unity 控制面板 HTTP 按钮时不需要启动 Bridge；需要验证手柄数据或遥操作时必须启动。

## 8. 使用 PICO App 的推荐真机操作顺序

先在 Ubuntu 启动两个常驻进程：

1. 真机 HTTP 控制服务：`./scripts/start_control_server.sh`
2. UDP Bridge：`./scripts/start_bridge.sh`

然后在 PICO App 中严格按以下顺序操作：

1. 查询状态。
2. 启动驱动：HTTP 服务同时启动驱动、模型和 TF，并等待关节反馈。
3. 进入安全位姿。
4. 启动 VR：启动 `vr_to_control`、夹爪控制和 MoveIt Servo 节点，但 Servo 保持停止。
5. 再次确认双扳机松开、现场清空。
6. 启动 Servo，必须确认返回成功。
7. 左扳机接合机械臂控制，右手柄控制末端位姿，右扳机控制夹爪。
8. 首次只进行约 1 cm 的低速单轴测试。

对应 HTTP 接口：

| 操作 | 方法和路径 | 请求体 |
|---|---|---|
| 查询状态 | `GET /api/status` | 无 |
| 启动驱动和模型 | `POST /api/start-driver` | `{}` |
| 进入安全位姿 | `POST /api/enter-safe-pose` | `{}` |
| 启动 VR/Servo 节点 | `POST /api/start-vr` | `{}` |
| 启动 Servo | `POST /api/start-servo` | `{}` |
| 停止 Servo | `POST /api/stop-servo` | `{}` |
| 回到安全工作位姿 | `POST /api/return-safe-pose` | `{}` |
| 停止 VR | `POST /api/stop-vr` | `{}` |
| 回机械零位 | `POST /api/shutdown-to-zero` | `{"confirm":"ZERO"}` |
| 回零、失能并关闭受管驱动 | `POST /api/shutdown-and-disable` | `{"confirm":"ZERO_DISABLE"}` |

不要对运动类 POST 请求自动无限重试。网络超时时应先查询 `/api/status` 和 `busy`，确认上一条命令是否仍在执行。

## 9. 推荐停止顺序

正常退出遥操作：

1. 松开左右扳机。
2. 调用“停止 Servo”。
3. 确认 Servo 已停止。
4. 调用“停止 VR”。
5. 调用“回零并失能”，或先“回机械零位”再按现场流程关闭驱动。
6. 确认六个关节接近零位、夹爪闭合、驱动失能后再断电。
7. Bridge 终端按 `Ctrl+C`。
8. HTTP 服务终端按 `Ctrl+C`。

`/api/shutdown-and-disable` 只有在驱动和模型由当前 HTTP 服务启动、驱动提供安全失能服务时才允许自动关闭。若节点是在其他终端手动启动，服务会拒绝误杀外部进程，应改用手动停止流程。

## 10. 不使用 HTTP 服务时：ROS 2 固定步骤脚本

以下是本地终端手动控制流程。每个长期运行脚本应放在独立终端中。

### 10.1 启动驱动、模型和 Bridge

终端 1：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_driver.sh
```

终端 2：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_model.sh
```

需要 RViz 时，使用下面的脚本替代 `start_model.sh`，二者不要同时运行：

```bash
./scripts/start_rviz.sh
```

终端 3：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_bridge.sh
```

### 10.2 进入安全位姿

驱动已就绪且有 `/rebotarm/joint_states` 后执行：

```bash
./scripts/enter_safe_pose.sh
```

该脚本把六个关节移动到项目定义的安全工作位姿。执行期间不要触碰扳机。

### 10.3 启动 VR 和 Servo

终端 4：

```bash
./scripts/start_vr.sh
```

节点全部就绪后，在临时终端执行：

```bash
./scripts/start_servo.sh
```

必须看到 `success=True` 后才能开始小幅遥操作。

### 10.4 暂停控制并返回安全位姿

```bash
./scripts/return_safe_pose.sh
```

该脚本先停止 Servo，再返回安全工作位姿。想继续遥操作时，重新执行：

```bash
./scripts/start_servo.sh
```

### 10.5 停止 Servo

```bash
./scripts/stop_servo.sh
```

停止后确认服务调用返回成功，再关闭运行 `start_vr.sh` 的终端。

### 10.6 安全回机械零位

确认 `vr_to_control` 已关闭后执行：

```bash
./scripts/shutdown_to_zero.sh
```

脚本要求输入大写 `Z`。它会回零并关闭夹爪，然后显示一次关节状态。确认六个关节接近零位后：

1. 关闭 Bridge 终端。
2. 关闭模型/RViz 终端。
3. 最后关闭驱动终端，让驱动执行电机失能。
4. 确认失能后再断电。

## 11. 仿真测试

仿真与真机驱动不能同时运行。确保真实机械臂驱动已关闭后：

终端 1：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_bridge.sh
```

终端 2：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_simulation.sh use_rviz:=true
```

终端 3：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_servo.sh
```

仿真检查：

```bash
source scripts/env.sh
ros2 control list_controllers
ros2 topic hz /joint_states
ros2 topic echo /servo_node/status
```

## 12. 网络和防火墙检查

确认端口监听：

```bash
ss -ltnp | grep ':5200'
ss -lunp | grep ':5100'
```

如果启用了 UFW，只允许 PICO 所在局域网访问。例如局域网为 `192.168.3.0/24`：

```bash
sudo ufw allow from 192.168.3.0/24 to any port 5200 proto tcp
sudo ufw allow from 192.168.3.0/24 to any port 5100 proto udp
```

请按实际网段修改，不要照抄错误网段。若虚拟机使用 NAT 且 PICO 无法直连，应改用桥接网络，或正确配置宿主机到虚拟机的端口转发。

## 13. ROS 2 运行状态检查

常用命令：

```bash
source scripts/env.sh

ros2 node list
ros2 topic list -t
ros2 service list | grep servo
ros2 action list
ros2 topic hz /rebotarm/joint_states
ros2 topic hz /pico_right_controller/pose
ros2 topic echo /servo_node/status
```

正常真机遥操作至少应看到：

- `/rebotarm_driver`
- `/robot_state_publisher`
- `/vr_to_control`
- `/servo_node`
- `/rebotarm/joint_states`
- `/servo_node/start_servo`
- `/servo_node/stop_servo`

## 14. 常见问题

### 14.1 HTTP 服务提示端口已占用

```text
OSError: [Errno 98] Address already in use
```

检查：

```bash
sudo ss -ltnp | grep ':5200'
ps -fp <PID>
```

确认是旧控制服务后停止旧进程，或临时使用 `--port 5201`；如果换端口，Unity 地址也必须同步修改。

### 14.2 PICO HTTP 按钮可用，但手柄没有 ROS 2 数据

HTTP 和 UDP 是两条独立链路。确认 `start_bridge.sh` 正在运行、PICO UDP 目标 IP 正确、UDP 5100 未被防火墙拦截。

### 14.3 有手柄话题，但机械臂不动

依次检查：

1. 驱动和模型是否就绪。
2. `/vr_to_control` 和 `/servo_node` 是否存在。
3. `start_servo.sh` 是否返回 `success=True`。
4. 左扳机是否超过接合阈值。
5. `/servo_node/status` 是否报告碰撞、奇异位形或关节限位。

### 14.4 HTTP 返回 `busy=true`

说明安全位姿、回零等运动命令仍在执行。不要重复发送运动命令；等待完成并轮询 `/api/status`。若服务返回超时，它不会强杀仍在运动的脚本，状态会保持 busy，直到脚本真正退出。

### 14.5 服务重启后 Servo 显示 `unknown`

MoveIt Servo 没有可靠的运行状态查询接口。先明确执行“停止 Servo”，确认成功后再按顺序启动。

## 15. 可选：视频服务

如果 PICO App 需要显示 Ubuntu 摄像头画面，可另开终端：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_video_server.sh \
  --topic /camera/color/image_raw \
  --host 0.0.0.0 \
  --port 8080 \
  --width 640 \
  --height 400 \
  --fps 15
```

视频链路独立于 HTTP 控制和 UDP 手柄链路。先单独验证浏览器/WebRTC画面，再集成 PICO，便于定位问题。

## 16. 最简操作速查

### 安全联调

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_control_server.sh --dry-run
```

需要验证 PICO 手柄 UDP 时，另开终端：

```bash
cd /home/jyf/Projects/1/rebotarm-vr
./scripts/start_bridge.sh
```

### 真机遥操作

```text
安全检查 → 启动 HTTP 服务 → 启动 Bridge → 启动驱动 → 进入安全位姿
→ 启动 VR → 启动 Servo → 小幅遥操作
```

### 安全停止

```text
松开扳机 → 停止 Servo → 停止 VR → 回零并失能
→ 关闭 Bridge → 确认失能 → 断电
```
