#!/usr/bin/env python3
"""Authenticated LAN HTTP control surface for existing reBotArm scripts."""

from __future__ import annotations

import argparse
import hmac
import ipaddress
import json
import logging
import os
from pathlib import Path
import signal
import subprocess
import threading
import time
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any


LOGGER = logging.getLogger("rebotarm_control_server")
MAX_BODY_BYTES = 64 * 1024


class ApiError(Exception):
    """An expected API rejection with an HTTP status code."""

    def __init__(self, status: int, message: str, state: str = "rejected") -> None:
        super().__init__(message)
        self.status = status
        self.message = message
        self.state = state


class RobotControl:
    """Serialize safety-sensitive commands and supervise long-lived launches."""

    def __init__(self, root: Path, dry_run: bool = False) -> None:
        self.root = root
        self.scripts = root / "scripts"
        self.logs = root / "logs" / "control_server"
        self.logs.mkdir(parents=True, exist_ok=True)
        self.dry_run = dry_run
        self.lock = threading.RLock()
        self.command_lock = threading.Lock()
        self.processes: dict[str, subprocess.Popen[Any]] = {}
        self.log_handles: dict[str, Any] = {}
        self.failed_processes: set[str] = set()
        self.operation_busy = False
        self.active_command: str | None = None
        self.servo_memory = "stopped" if dry_run else "unknown"
        self.snapshot_lock = threading.Lock()
        self.snapshot_time = 0.0
        self.snapshot_cache: dict[str, set[str]] | None = None
        self.dry_state = {
            "driver": "stopped",
            "model": "stopped",
            "vr": "stopped",
        }

    def _script(self, name: str) -> Path:
        path = (self.scripts / name).resolve()
        if path.parent != self.scripts.resolve() or not path.is_file():
            raise RuntimeError(f"固定脚本不存在：{name}")
        return path

    def _run_cli(self, args: list[str], timeout: float = 5.0) -> tuple[int, str]:
        if self.dry_run:
            return 0, ""
        env = os.environ.copy()
        env["ROS2CLI_NO_DAEMON"] = "1"
        try:
            result = subprocess.run(
                args,
                cwd=self.root,
                env=env,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                timeout=timeout,
                check=False,
            )
            return result.returncode, result.stdout
        except (OSError, subprocess.TimeoutExpired) as error:
            LOGGER.warning("ROS状态查询失败：%s", error)
            return 1, ""

    @staticmethod
    def _lines(output: str) -> set[str]:
        return {line.strip() for line in output.splitlines() if line.strip()}

    def _ros_snapshot(self) -> dict[str, set[str]]:
        if self.dry_run:
            return {"nodes": set(), "services": set(), "actions": set(), "topics": set()}
        with self.snapshot_lock:
            now = time.monotonic()
            if self.snapshot_cache is not None and now - self.snapshot_time < 0.75:
                return self.snapshot_cache
            _, nodes = self._run_cli(["ros2", "node", "list"])
            _, services = self._run_cli(["ros2", "service", "list"])
            _, actions = self._run_cli(["ros2", "action", "list"])
            _, topics = self._run_cli(["ros2", "topic", "list"])
            snapshot = {
                "nodes": self._lines(nodes),
                "services": self._lines(services),
                "actions": self._lines(actions),
                "topics": self._lines(topics),
            }
            self.snapshot_cache = snapshot
            self.snapshot_time = now
            return snapshot

    def _invalidate_snapshot(self) -> None:
        with self.snapshot_lock:
            self.snapshot_time = 0.0

    def _joint_state_received(self) -> bool:
        if self.dry_run:
            return True
        code, _ = self._run_cli(
            ["ros2", "topic", "echo", "/rebotarm/joint_states", "--once"],
            timeout=3.0,
        )
        return code == 0

    def _process_alive(self, name: str) -> bool:
        process = self.processes.get(name)
        return process is not None and process.poll() is None

    def _reap_processes(self) -> None:
        with self.lock:
            for name, process in list(self.processes.items()):
                code = process.poll()
                if code is None:
                    continue
                LOGGER.info("受管进程 %s 已退出，退出码=%s", name, code)
                if code != 0:
                    self.failed_processes.add(name)
                self.processes.pop(name, None)
                handle = self.log_handles.pop(name, None)
                if handle is not None:
                    handle.close()

    def status(self) -> dict[str, Any]:
        self._reap_processes()
        with self.lock:
            busy = self.operation_busy
            active_command = self.active_command
            servo = self.servo_memory
            dry_state = dict(self.dry_state)

        if self.dry_run:
            driver = dry_state["driver"]
            model = dry_state["model"]
            vr = dry_state["vr"]
        else:
            graph = self._ros_snapshot()
            nodes, services, actions, topics = (
                graph["nodes"], graph["services"], graph["actions"], graph["topics"]
            )
            driver_ready = (
                "/rebotarm_driver" in nodes
                and "/rebotarm/follow_joint_trajectory" in actions
                and "/rebotarm/joint_states" in topics
            )
            driver = "running" if driver_ready else (
                "starting" if self._process_alive("driver") else
                ("failed" if "driver" in self.failed_processes else "stopped")
            )
            model_ready = "/robot_state_publisher" in nodes
            model = "running" if model_ready else (
                "starting" if self._process_alive("model") else
                ("failed" if "model" in self.failed_processes else "stopped")
            )
            vr_ready = "/vr_to_control" in nodes and "/servo_node" in nodes
            vr = "running" if vr_ready else (
                "starting" if self._process_alive("vr") else
                ("failed" if "vr" in self.failed_processes else "stopped")
            )
            servo_services = {
                "/servo_node/start_servo", "/servo_node/stop_servo"
            }
            if not servo_services.issubset(services):
                servo = "unknown" if vr != "stopped" else "stopped"

        if busy:
            message = f"正在执行：{active_command}"
        elif driver == "running" and vr == "running":
            message = "控制系统已就绪"
        else:
            message = "等待操作"
        return {
            "ok": True,
            "dry_run": self.dry_run,
            "driver": driver,
            "model": model,
            "vr": vr,
            "servo": servo,
            "busy": busy,
            "active_command": active_command,
            "message": message,
        }

    def _start_long_process(self, name: str, script_name: str) -> None:
        with self.lock:
            if self._process_alive(name):
                return
            stamp = time.strftime("%Y%m%d-%H%M%S")
            log_path = self.logs / f"{name}-{stamp}.log"
            handle = log_path.open("ab", buffering=0)
            process = subprocess.Popen(
                [str(self._script(script_name))],
                cwd=self.root,
                stdin=subprocess.DEVNULL,
                stdout=handle,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            self.processes[name] = process
            self.log_handles[name] = handle
            self.failed_processes.discard(name)
            self._invalidate_snapshot()
            LOGGER.info("已启动 %s：pid=%s，日志=%s", name, process.pid, log_path)

    def _wait_for(self, predicate: Any, timeout: float, description: str) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.5)
        raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, f"等待{description}超时")

    def start_driver(self) -> dict[str, Any]:
        command = "start-driver"
        current = self.status()
        if current["busy"]:
            raise ApiError(
                HTTPStatus.CONFLICT,
                f"正在执行 {current['active_command']}，不能启动驱动",
            )
        if current["driver"] == "running" and current["model"] == "running":
            if not self._joint_state_received():
                raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动节点存在但没有关节反馈")
            return self._result(command, "completed", "驱动和模型已经启动")
        if self.dry_run:
            with self.lock:
                self.dry_state["driver"] = "running"
                self.dry_state["model"] = "running"
            return self._result(command, "completed", "dry-run：驱动和模型已启动")

        if current["driver"] != "running":
            self._start_long_process("driver", "start_driver.sh")
            self._wait_for(
                lambda: (
                    self.status()["driver"] == "running"
                    and self._joint_state_received()
                ),
                30.0,
                "驱动和关节反馈就绪",
            )
        if self.status()["model"] != "running":
            self._start_long_process("model", "start_model.sh")
            self._wait_for(
                lambda: self.status()["model"] == "running", 15.0, "模型就绪"
            )
        return self._result(command, "completed", "驱动和模型已启动")

    def start_vr(self) -> dict[str, Any]:
        command = "start-vr"
        current = self.status()
        if current["busy"]:
            raise ApiError(
                HTTPStatus.CONFLICT,
                f"正在执行 {current['active_command']}，不能启动VR",
            )
        if current["driver"] != "running":
            raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动尚未就绪")
        if current["vr"] == "running":
            return self._result(command, "completed", "VR控制已经启动")
        if self.dry_run:
            with self.lock:
                self.dry_state["vr"] = "running"
                self.servo_memory = "stopped"
            return self._result(command, "completed", "dry-run：VR控制已启动")
        self._start_long_process("vr", "start_vr.sh")
        self._wait_for(lambda: self.status()["vr"] == "running", 20.0, "VR控制就绪")
        with self.lock:
            self.servo_memory = "stopped"
        return self._result(command, "completed", "VR控制已启动，Servo保持停止")

    def _run_short(self, script_name: str, timeout: float = 15.0) -> tuple[int, str]:
        try:
            result = subprocess.run(
                [str(self._script(script_name))],
                cwd=self.root,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired as error:
            raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, f"命令超时：{script_name}") from error
        return result.returncode, result.stdout.strip()

    def start_servo(self) -> dict[str, Any]:
        command = "start-servo"
        current = self.status()
        if current["busy"]:
            raise ApiError(
                HTTPStatus.CONFLICT,
                f"正在执行 {current['active_command']}，不能启动Servo",
            )
        if current["driver"] != "running":
            raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动尚未就绪")
        if current["vr"] != "running":
            raise ApiError(HTTPStatus.CONFLICT, "VR控制节点尚未启动")
        if current["servo"] == "running":
            return self._result(command, "completed", "Servo已经启动")
        if not self.dry_run:
            services = self._ros_snapshot()["services"]
            required = {"/servo_node/start_servo", "/servo_node/stop_servo"}
            if not required.issubset(services):
                raise ApiError(
                    HTTPStatus.SERVICE_UNAVAILABLE,
                    "Servo节点存在，但start/stop服务尚未就绪",
                )
            code, output = self._run_short("start_servo.sh")
            normalized = output.lower().replace(" ", "")
            if code != 0 or not (
                "success=true" in normalized or "success:true" in normalized
            ):
                raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, "Servo启动失败")
        with self.lock:
            self.servo_memory = "running"
        return self._result(command, "completed", "Servo已启动")

    def stop_servo(self) -> dict[str, Any]:
        command = "stop-servo"
        current = self.status()
        if current["servo"] == "stopped":
            return self._result(command, "completed", "Servo已经停止")
        if current["vr"] != "running":
            with self.lock:
                self.servo_memory = "stopped"
            return self._result(command, "completed", "Servo节点不存在，按停止处理")
        if not self.dry_run:
            code, output = self._run_short("stop_servo.sh")
            normalized = output.lower().replace(" ", "")
            if code != 0 or not (
                "success=true" in normalized or "success:true" in normalized
            ):
                raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, "Servo停止失败")
        with self.lock:
            self.servo_memory = "stopped"
        return self._result(command, "completed", "Servo已停止")

    def stop_vr(self) -> dict[str, Any]:
        command = "stop-vr"
        current = self.status()
        if current["vr"] == "stopped":
            with self.lock:
                self.servo_memory = "stopped"
            return self._result(command, "completed", "VR控制已经停止")
        if current["servo"] == "running":
            self.stop_servo()
        if self.dry_run:
            with self.lock:
                self.dry_state["vr"] = "stopped"
                self.servo_memory = "stopped"
            return self._result(command, "completed", "dry-run：VR控制已停止")
        with self.lock:
            process = self.processes.get("vr")
        if process is None or process.poll() is not None:
            raise ApiError(
                HTTPStatus.CONFLICT,
                "VR节点不是由本服务启动，请在原终端停止，服务不会误杀外部进程",
            )
        os.killpg(process.pid, signal.SIGINT)
        try:
            process.wait(timeout=15.0)
        except subprocess.TimeoutExpired as error:
            raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, "等待VR安全退出超时") from error
        self._reap_processes()
        self._invalidate_snapshot()
        with self.lock:
            self.servo_memory = "stopped"
        return self._result(command, "completed", "VR控制已停止")

    def _claim_operation(self, command: str) -> None:
        with self.lock:
            if self.operation_busy:
                raise ApiError(
                    HTTPStatus.CONFLICT,
                    f"正在执行 {self.active_command}，拒绝并发运动命令",
                )
            self.operation_busy = True
            self.active_command = command

    def _release_operation(self) -> None:
        with self.lock:
            self.operation_busy = False
            self.active_command = None

    def _finish_late(self, process: subprocess.Popen[Any], handle: Any, command: str) -> None:
        code = process.wait()
        handle.close()
        LOGGER.warning("超时后命令 %s 最终退出，退出码=%s", command, code)
        self._release_operation()

    def _motion(self, command: str, script_name: str, timeout: float, stdin: bytes | None = None, after_success: Any = None) -> dict[str, Any]:
        self._claim_operation(command)
        if self.dry_run:
            try:
                time.sleep(0.02)
                if after_success:
                    after_success()
                return self._result(command, "completed", f"dry-run：{command} 已完成")
            finally:
                self._release_operation()
        stamp = time.strftime("%Y%m%d-%H%M%S")
        log_path = self.logs / f"{command}-{stamp}.log"
        try:
            handle = log_path.open("ab", buffering=0)
        except Exception:
            self._release_operation()
            raise
        try:
            process = subprocess.Popen(
                [str(self._script(script_name))],
                cwd=self.root,
                stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
                stdout=handle,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            try:
                process.communicate(input=stdin, timeout=timeout)
            except subprocess.TimeoutExpired as error:
                threading.Thread(
                    target=self._finish_late,
                    args=(process, handle, command),
                    daemon=True,
                ).start()
                handle = None
                raise ApiError(
                    HTTPStatus.INTERNAL_SERVER_ERROR,
                    f"{command} 超过{timeout:.0f}秒仍在执行；未强制终止，系统保持busy",
                    state="timeout",
                ) from error
            if process.returncode != 0:
                raise ApiError(
                    HTTPStatus.INTERNAL_SERVER_ERROR,
                    f"{command} 执行失败，退出码={process.returncode}，日志={log_path}",
                    state="failed",
                )
            if after_success:
                after_success()
            return self._result(command, "completed", f"{command} 已完成")
        finally:
            if handle is not None:
                handle.close()
                self._release_operation()

    def enter_safe_pose(self) -> dict[str, Any]:
        if self.status()["driver"] != "running":
            raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动尚未就绪")
        return self._motion("enter-safe-pose", "enter_safe_pose.sh", 20.0)

    def return_safe_pose(self) -> dict[str, Any]:
        current = self.status()
        if current["driver"] != "running":
            raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动尚未就绪")
        if current["vr"] != "running":
            raise ApiError(HTTPStatus.CONFLICT, "VR/Servo节点尚未启动")
        result = self._motion("return-safe-pose", "return_safe_pose.sh", 20.0)
        with self.lock:
            self.servo_memory = "stopped"
        result["message"] = "已回到工作姿态，Servo保持停止"
        return result

    def shutdown_to_zero(self, body: dict[str, Any], close_driver: bool = False) -> dict[str, Any]:
        command = "shutdown-and-disable" if close_driver else "shutdown-to-zero"
        expected = "ZERO_DISABLE" if close_driver else "ZERO"
        if body.get("confirm") != expected:
            raise ApiError(HTTPStatus.BAD_REQUEST, f"必须提供 confirm={expected}")
        current = self.status()
        if current["driver"] != "running":
            raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "驱动尚未就绪")
        if current["servo"] != "stopped":
            raise ApiError(HTTPStatus.CONFLICT, "回零前必须明确停止Servo")
        if current["vr"] != "stopped":
            raise ApiError(HTTPStatus.CONFLICT, "回零前必须停止vr_to_control")
        if close_driver and not self.dry_run:
            # Refuse BEFORE moving if these processes are not service-owned.
            for name in ("driver", "model"):
                if current[name] != "stopped" and not self._process_alive(name):
                    raise ApiError(HTTPStatus.CONFLICT, f"{name}不是本服务启动，不能自动关闭")
            if "/rebotarm/prepare_shutdown_at_zero" not in self._ros_snapshot()["services"]:
                raise ApiError(HTTPStatus.CONFLICT, "驱动尚未更新，缺少安全失能服务")
        result = self._motion(command, "shutdown_to_zero.sh", 40.0, stdin=b"Z\n",
                              after_success=self._disable_and_close if close_driver else None)
        if close_driver:
            result["message"] = ("dry-run：" if self.dry_run else "") + "回零及失能命令完成，驱动和模型已关闭"
        return result

    def _disable_and_close(self) -> None:
        if self.dry_run:
            self.dry_state.update(driver="stopped", model="stopped")
            return
        code, output = self._run_cli([
            "ros2", "service", "call", "/rebotarm/prepare_shutdown_at_zero",
            "std_srvs/srv/Trigger", "{}"], timeout=10.0)
        normalized = output.lower().replace(" ", "")
        if code != 0 or not ("success=true" in normalized or "success:true" in normalized):
            raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR,
                           "失能未确认，未关闭驱动和模型；请查看驱动日志，勿重复回零", "failed")
        for name in ("driver", "model"):
            process = self.processes.get(name)
            if process is None or process.poll() is not None:
                if name == "driver":
                    raise ApiError(HTTPStatus.CONFLICT, "驱动进程异常退出，请检查现场", "failed")
                continue
            os.killpg(process.pid, signal.SIGINT)
            try:
                process.wait(timeout=15.0)
            except subprocess.TimeoutExpired as error:
                raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR,
                               f"失能命令已完成，但{name}退出超时；未强制终止", "timeout") from error
        self._reap_processes()
        self._invalidate_snapshot()
        self._wait_for(
            lambda: not {"/rebotarm_driver", "/robot_state_publisher"}.intersection(
                self._ros_snapshot()["nodes"]), 10.0, "驱动和模型节点退出")
        with self.lock:
            self.failed_processes.difference_update({"driver", "model"})

    @staticmethod
    def _result(command: str, state: str, message: str) -> dict[str, Any]:
        return {"ok": True, "command": command, "state": state, "message": message}


class ControlHTTPServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address: tuple[str, int], control: RobotControl, token: str):
        super().__init__(address, ControlHandler)
        self.control = control
        self.token = token


class ControlHandler(BaseHTTPRequestHandler):
    server: ControlHTTPServer
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        LOGGER.info("HTTP %s - %s", self.client_address[0], fmt % args)

    def _json(self, status: int, payload: dict[str, Any]) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _authorize(self) -> None:
        try:
            address = ipaddress.ip_address(self.client_address[0])
        except ValueError as error:
            raise ApiError(HTTPStatus.FORBIDDEN, "非法客户端地址") from error
        if not (address.is_private or address.is_loopback):
            raise ApiError(HTTPStatus.FORBIDDEN, "仅允许局域网访问")
        supplied = self.headers.get("X-Rebotarm-Token", "")
        if not hmac.compare_digest(supplied, self.server.token):
            raise ApiError(HTTPStatus.UNAUTHORIZED, "访问令牌无效")

    def _body(self) -> dict[str, Any]:
        raw_length = self.headers.get("Content-Length", "0")
        try:
            length = int(raw_length)
        except ValueError as error:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Content-Length无效") from error
        if length < 0 or length > MAX_BODY_BYTES:
            raise ApiError(HTTPStatus.BAD_REQUEST, "请求体过大")
        if length == 0:
            return {}
        try:
            value = json.loads(self.rfile.read(length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ApiError(HTTPStatus.BAD_REQUEST, "请求体必须是UTF-8 JSON") from error
        if not isinstance(value, dict):
            raise ApiError(HTTPStatus.BAD_REQUEST, "JSON请求体必须是对象")
        return value

    def _dispatch(self, method: str) -> tuple[int, dict[str, Any]]:
        self._authorize()
        control = self.server.control
        if method == "GET" and self.path == "/api/status":
            return HTTPStatus.OK, control.status()
        if method != "POST":
            raise ApiError(HTTPStatus.NOT_FOUND, "接口不存在")
        body = self._body()
        routes = {
            "/api/start-driver": control.start_driver,
            "/api/enter-safe-pose": control.enter_safe_pose,
            "/api/start-vr": control.start_vr,
            "/api/start-servo": control.start_servo,
            "/api/stop-servo": control.stop_servo,
            "/api/return-safe-pose": control.return_safe_pose,
            "/api/stop-vr": control.stop_vr,
        }
        if self.path == "/api/shutdown-to-zero":
            return HTTPStatus.OK, control.shutdown_to_zero(body)
        if self.path == "/api/shutdown-and-disable":
            return HTTPStatus.OK, control.shutdown_to_zero(body, close_driver=True)
        action = routes.get(self.path)
        if action is None:
            raise ApiError(HTTPStatus.NOT_FOUND, "接口不存在")
        return HTTPStatus.OK, action()

    def _handle(self, method: str) -> None:
        # Keep start/stop/motion command preconditions atomic across HTTP clients.
        # Read-only status and stop-servo remain available during long actions.
        gate = self.server.control.command_lock
        claimed = method == "POST" and self.path != "/api/stop-servo"
        if claimed and not gate.acquire(blocking=False):
            self._json(HTTPStatus.CONFLICT, {"ok": False, "state": "rejected", "message": "另一命令正在执行"})
            return
        try:
            status, payload = self._dispatch(method)
            self._json(status, payload)
        except ApiError as error:
            command = self.path.rsplit("/", 1)[-1] or "unknown"
            self._json(
                error.status,
                {
                    "ok": False,
                    "command": command,
                    "state": error.state,
                    "message": error.message,
                },
            )
        except Exception:
            LOGGER.exception("未处理的HTTP请求异常")
            self._json(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                {"ok": False, "state": "failed", "message": "服务器内部错误"},
            )

        finally:
            if claimed:
                gate.release()

    def do_GET(self) -> None:  # noqa: N802
        self._handle("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._handle("POST")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", default=5200, type=int)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    token = os.environ.get("REBOTARM_CONTROL_TOKEN", "")
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )
    root = Path(os.environ.get("REBOTARM_ROOT", Path(__file__).resolve().parents[1])).resolve()
    control = RobotControl(root, dry_run=args.dry_run)
    server = ControlHTTPServer((args.host, args.port), control, token)
    LOGGER.info(
        "reBotArm控制服务监听 http://%s:%s，dry_run=%s",
        args.host,
        args.port,
        args.dry_run,
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
