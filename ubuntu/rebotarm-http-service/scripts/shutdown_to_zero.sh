#!/usr/bin/env bash

set -euo pipefail
source "$(dirname "$0")/env.sh"

echo "The arm will enter the safe pose first; the gripper will close while the arm returns from the safe pose to zero."
echo "Release both triggers, remove held objects, and clear the workspace."
if ros2 node list 2>/dev/null | grep -qx '/vr_to_control'; then
  echo "ERROR: VR control is still running. Stop Servo and close the VR terminal first."
  exit 1
fi
read -r -p "Input Z to continue: " confirmation
[[ "${confirmation}" == "Z" ]] || { echo "Canceled."; exit 1; }

# First enter the safe pose, then close the gripper during the move from the
# safe pose to mechanical zero. This prevents a mode switch overriding it.
(
  sleep 3
  ros2 service call /rebotarm/gripper/close \
    rebotarm_interfaces/srv/GripperCommand \
    "{position: 0.0, timeout: 8.0}"
) &
gripper_pid=$!

ros2 action send_goal \
  /rebotarm/follow_joint_trajectory \
  control_msgs/action/FollowJointTrajectory \
  "{trajectory: {joint_names: [joint1, joint2, joint3, joint4, joint5, joint6], points: [{positions: [0.0, -0.75, -0.55, 0.0, 0.0, 0.0], time_from_start: {sec: 3, nanosec: 0}}, {positions: [0.0, -0.75, -0.55, -0.10, 0.0, 0.0], time_from_start: {sec: 4, nanosec: 0}}, {positions: [0.0, -0.75, -0.55, -0.10, 0.0, 0.0], time_from_start: {sec: 4, nanosec: 750000000}}, {positions: [0.0, 0.0, 0.0, -0.10, 0.0, 0.0], time_from_start: {sec: 7, nanosec: 750000000}}, {positions: [0.0, 0.0, 0.0, 0.0, 0.0, 0.0], time_from_start: {sec: 8, nanosec: 750000000}}]}}" \
  --feedback

wait "${gripper_pid}"

echo "Confirm the six joints are near zero and the gripper is closed:"
timeout 5 ros2 topic echo /rebotarm/joint_states --once
echo "Zero return complete. Close bridge/model terminals, then close the driver terminal to disable the motors."
