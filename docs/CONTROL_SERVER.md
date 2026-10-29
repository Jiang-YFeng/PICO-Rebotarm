# PICO HTTP机械臂控制服务

服务默认监听 `0.0.0.0:5200`，仅接受回环或局域网来源，并要求请求头
`X-Rebotarm-Token`。它只调用仓库已有脚本，不提供任意命令执行接口。

## 配置

```bash
cd /home/jyf/Projects/1/rebotarm-vr
cp config/control_server.env.example config/control_server.env
```

编辑 `config/control_server.env`，把令牌替换为至少16字符的随机值。该文件不会
提交到Git，也不要把令牌打印到日志或写入Unity公共源码。

## 启动

安全联调模式：

```bash
python robot_control_server.py --dry-run
```

真机控制服务（启动服务本身不会运动）：

```bash
python robot_control_server.py
```

长期进程及运动命令日志位于 `logs/control_server/`。

## 接口

```text
GET  /api/status
POST /api/start-driver
POST /api/enter-safe-pose
POST /api/start-vr
POST /api/start-servo
POST /api/stop-servo
POST /api/return-safe-pose
POST /api/stop-vr
POST /api/shutdown-to-zero
```

除回零外，POST请求体使用 `{}`。回零必须使用：

```json
{"confirm":"ZERO"}
```

## curl示例

```bash
export CONTROL_TOKEN='替换为本机配置的令牌'

curl http://127.0.0.1:5200/api/status \
  -H "X-Rebotarm-Token: ${CONTROL_TOKEN}"

curl -X POST http://127.0.0.1:5200/api/start-driver \
  -H "X-Rebotarm-Token: ${CONTROL_TOKEN}" \
  -H 'Content-Type: application/json' \
  -d '{}'

curl -X POST http://127.0.0.1:5200/api/shutdown-to-zero \
  -H "X-Rebotarm-Token: ${CONTROL_TOKEN}" \
  -H 'Content-Type: application/json' \
  -d '{"confirm":"ZERO"}'
```

统一命令响应：

```json
{
  "ok": true,
  "command": "start-servo",
  "state": "completed",
  "message": "Servo已启动"
}
```

状态响应：

```json
{
  "ok": true,
  "dry_run": false,
  "driver": "running",
  "model": "running",
  "vr": "running",
  "servo": "stopped",
  "busy": false,
  "active_command": null,
  "message": "控制系统已就绪"
}
```

`servo`在服务重启后可能为 `unknown`，因为MoveIt Servo没有可靠的运行状态查询
接口。明确调用start/stop接口后会变为 `running`/`stopped`。
