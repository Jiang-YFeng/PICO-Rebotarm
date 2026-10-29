"""Offline tests: no ROS, network, subprocess or hardware operations."""
import ast
import functools
import importlib.util
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("server", ROOT / "scripts/robot_control_server.py")
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)

# Load actual hardware methods without importing the unavailable vendor SDK.
tree = ast.parse((ROOT / "driver/hardwaremanager.py").read_text(encoding="utf-8-sig"))
tree.body = [n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.ClassDef))]
scope = dict(np=np, functools=functools, threading=threading, time=time)
exec(compile(tree, "hardwaremanager.py", "exec"), scope)
Hardware = scope["HardwareManager"]

class ShutdownTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.control = server.RobotControl(Path(self.temp.name), dry_run=True)
        self.control.start_driver()

    def test_plain_zero_keeps_driver(self):
        self.control.shutdown_to_zero({"confirm": "ZERO"})
        self.assertEqual(self.control.status()["driver"], "running")

    def test_combined_dry_run_closes_driver_and_model(self):
        reply = self.control.shutdown_to_zero({"confirm": "ZERO_DISABLE"}, True)
        self.assertEqual(reply["state"], "completed")
        self.assertEqual(self.control.status()["driver"], "stopped")
        self.assertEqual(self.control.status()["model"], "stopped")
        self.assertFalse(self.control.operation_busy)

    def test_explicit_confirmation_and_stopped_vr_required(self):
        with self.assertRaises(server.ApiError):
            self.control.shutdown_to_zero({"confirm": "ZERO"}, True)
        self.control.start_vr()
        with self.assertRaises(server.ApiError):
            self.control.shutdown_to_zero({"confirm": "ZERO_DISABLE"}, True)

    def test_failed_disable_never_signals_processes(self):
        self.control.dry_run = False
        self.control._run_cli = Mock(return_value=(0, "success=False"))
        with patch.object(server.os, "killpg", create=True) as kill:
            with self.assertRaises(server.ApiError):
                self.control._disable_and_close()
            kill.assert_not_called()

    def test_script_failure_never_disables(self):
        self.control.dry_run = False
        self.control._script = Mock(return_value=Path("fake.sh"))
        process = Mock(returncode=1)
        callback = Mock()
        with patch.object(server.subprocess, "Popen", return_value=process):
            with self.assertRaises(server.ApiError):
                self.control._motion("test", "fake.sh", 1, after_success=callback)
        callback.assert_not_called()
        self.assertFalse(self.control.operation_busy)

    def test_timeout_keeps_busy_and_never_disables(self):
        self.control.dry_run = False
        self.control._script = Mock(return_value=Path("fake.sh"))
        process = Mock()
        process.communicate.side_effect = server.subprocess.TimeoutExpired("fake", 1)
        callback = Mock()
        with patch.object(server.subprocess, "Popen", return_value=process), \
                patch.object(server.threading, "Thread") as thread:
            with self.assertRaises(server.ApiError):
                self.control._motion("test", "fake.sh", 1, after_success=callback)
            handle = thread.call_args.kwargs["args"][1]
            handle.close()
        callback.assert_not_called()
        self.assertTrue(self.control.operation_busy)

    def hardware(self, position=0.0, velocity=0.0):
        hw = Hardware.__new__(Hardware)
        hw._cmd_lock = threading.RLock()
        hw._gravity_comp_active = False
        hw._connected = hw._enabled = True
        hw.motion_active = Mock(return_value=False)
        hw._get_arm_state = Mock(return_value=(np.full(6, position), np.full(6, velocity), np.zeros(6)))
        hw.disable = Mock()
        hw.safe_home = Mock()
        hw._robot = Mock()
        hw._joint_names = ["joint" + str(i) for i in range(1, 7)]
        hw._arm_group = Mock(joint_names=hw._joint_names)
        hw._robot._motor_map = {name: Mock() for name in hw._joint_names}
        hw._robot._ctrl_map = {}
        hw.set_state_machine = Mock()
        return hw

    def test_invalid_feedback_does_not_disable(self):
        for position, velocity in ((0.04, 0), (0, 0.04), (float("nan"), 0), (0, float("inf"))):
            hw = self.hardware(position, velocity)
            with self.assertRaises(RuntimeError):
                hw.prepare_shutdown_at_zero()
            hw.disable.assert_not_called()

    def test_shutdown_latch_prevents_reenable_and_rehome(self):
        hw = self.hardware()
        with patch.object(time, "sleep"):
            hw.prepare_shutdown_at_zero()
        self.assertEqual(hw._get_arm_state.call_count, 5)
        hw.disable.assert_called_once()
        with self.assertRaises(RuntimeError):
            hw.start_endpos_control()
        hw.shutdown()
        hw.safe_home.assert_not_called()
        hw._robot.disconnect.assert_called_once()

    def test_normal_shutdown_keeps_original_homing(self):
        hw = self.hardware()
        hw.shutdown()
        hw.safe_home.assert_called_once()

    def test_missing_feedback_does_not_disable(self):
        hw = self.hardware()
        hw._robot._motor_map["joint1"].get_state.return_value = None
        with self.assertRaises(RuntimeError):
            hw.prepare_shutdown_at_zero()
        hw.disable.assert_not_called()

    def test_real_disable_and_shutdown_cleanup_methods(self):
        hw = self.hardware()
        del hw.disable
        del hw.set_state_machine
        hw._endpos_ctrl = Mock()
        hw._endpos_ctrl._send_thread = None
        with patch.object(time, "sleep"):
            hw.prepare_shutdown_at_zero()
        self.assertFalse(hw._enabled)
        hw.shutdown()
        hw.safe_home.assert_not_called()
        hw._robot.disable_all.assert_called_once()
        hw._robot.disconnect.assert_called_once()

if __name__ == "__main__":
    unittest.main()
