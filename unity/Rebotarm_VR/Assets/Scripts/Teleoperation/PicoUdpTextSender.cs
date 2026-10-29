using System;
using System.Collections;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace RebotarmVR.Teleoperation
{
    /// <summary>
    /// Sends PICO controller state to the ROS 2 UDP Bridge as one UTF-8 text
    /// message per UDP datagram. Controller messages are sent independently.
    /// </summary>
    public sealed class PicoUdpTextSender : MonoBehaviour
    {
        [Header("UDP Target")]
        [SerializeField] string targetHost = "192.168.3.140";
        [SerializeField, Range(1, 65535)] int targetPort = 5100;

        [Header("Main Messages")]
        [SerializeField, Min(1f)] float sendRateHz = 50f;
        [SerializeField] bool absolutePoseMode;
        [SerializeField] bool highRateMode;
        [SerializeField] bool sendHeadPose;
        [Tooltip("Use 0 so the Bridge timestamps packets on receipt.")]
        [SerializeField] bool sendUnixTimestampNanoseconds;

        [Header("Temporary Pose Diagnostics")]
        [SerializeField] bool logPoseDiagnostics = true;
        [SerializeField, Min(0.1f)] float poseLogIntervalSeconds = 1f;

        [Header("Joystick Messages")]
        [SerializeField] bool sendJoystick = true;
        [SerializeField] bool sendJoystickEveryTick;
        [SerializeField, Min(0f)] float joystickChangeThreshold = 0.01f;

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        UdpClient client;
        Coroutine sendLoop;
        Vector2 lastLeftJoystick;
        Vector2 lastRightJoystick;
        bool haveLeftJoystick;
        bool haveRightJoystick;
        bool lastLeftJoystickPressed;
        bool lastRightJoystickPressed;
        bool haveLeftJoystickPressed;
        bool haveRightJoystickPressed;
        float nextSendTime;
        float nextErrorLogTime;
        float nextLeftPoseLogTime;
        float nextRightPoseLogTime;
        bool uiInteractionBlocked;
        bool awaitingGripRelease;

        public string TargetHost
        {
            get => targetHost;
            set => targetHost = value;
        }

        public int TargetPort
        {
            get => targetPort;
            set => targetPort = Mathf.Clamp(value, 1, 65535);
        }

        public bool AbsolutePoseMode
        {
            get => absolutePoseMode;
            set => absolutePoseMode = value;
        }

        public bool HighRateMode
        {
            get => highRateMode;
            set => highRateMode = value;
        }

        /// <summary>
        /// Prevent robot input while the operator is using the settings/control UI.
        /// Releasing the block does not immediately re-arm motion: both side grips
        /// must first be released so a UI interaction cannot become robot motion.
        /// </summary>
        public void SetUiInteractionBlocked(bool blocked)
        {
            uiInteractionBlocked = blocked;
            awaitingGripRelease = true;
        }

        void OnEnable()
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            nextSendTime = Time.realtimeSinceStartup;
            sendLoop = StartCoroutine(SendLoop());
            Debug.Log($"[PICO UDP] Sending controller text packets to {targetHost}:{targetPort} at {sendRateHz:0.###} Hz.");
        }

        void OnDisable()
        {
            if (sendLoop != null)
            {
                StopCoroutine(sendLoop);
                sendLoop = null;
            }

            client?.Close();
            client = null;
        }

        IEnumerator SendLoop()
        {
            while (enabled)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= nextSendTime)
                {
                    float interval = 1f / Mathf.Max(1f, sendRateHz);
                    nextSendTime += interval;
                    if (nextSendTime < now - interval)
                        nextSendTime = now + interval;
                    UpdateControlInputGate();
                    bool allowControlInput = !uiInteractionBlocked && !awaitingGripRelease;
                    SendController(XRNode.LeftHand, true, allowControlInput);
                    SendController(XRNode.RightHand, false, allowControlInput);

                    if (sendHeadPose)
                        SendHead();
                }

                yield return EndOfFrame;
            }
        }

        void UpdateControlInputGate()
        {
            if (uiInteractionBlocked || !awaitingGripRelease)
                return;

            InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            InputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!left.isValid || !right.isValid)
                return;

            left.TryGetFeatureValue(CommonUsages.grip, out float leftGrip);
            right.TryGetFeatureValue(CommonUsages.grip, out float rightGrip);
            if (leftGrip <= 0.05f && rightGrip <= 0.05f)
            {
                awaitingGripRelease = false;
                Debug.Log("[PICO UDP] UI closed and both grips released; robot input re-armed.");
            }
        }

        void SendController(XRNode node, bool isLeft, bool allowControlInput)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid ||
                !device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) ||
                !device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
            {
                return;
            }

            device.TryGetFeatureValue(CommonUsages.grip, out float grip);
            device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);

            grip = Mathf.Clamp01(grip);
            if (!allowControlInput)
            {
                grip = 0f;
                primaryButton = false;
                secondaryButton = false;
            }
            // ROS consumes the protocol's trigger field for left-hand clutch
            // and right-hand gripper control. Drive it with the side squeeze
            // (Grip), leaving the index trigger available for Unity UI clicks.
            // Keep the existing grip field and all packet positions unchanged.
            float trigger = grip;

            int a = !isLeft && primaryButton ? 1 : 0;
            int b = !isLeft && secondaryButton ? 1 : 0;
            int x = isLeft && primaryButton ? 1 : 0;
            int y = isLeft && secondaryButton ? 1 : 0;
            string hand = isLeft ? "LEFT" : "RIGHT";
            if (absolutePoseMode)
                hand += "_ABS";

            // PICO exposes the controller pose in Unity coordinates (+Z forward),
            // while the existing ROS bridge expects the OpenXR-style convention
            // where forward is -Z.
            Vector3 udpPosition = position;
            udpPosition.z = -udpPosition.z;

            // Mirror orientation across Z consistently with the position.
            Quaternion udpRotation = new Quaternion(
                -rotation.x,
                -rotation.y,
                rotation.z,
                rotation.w);
            udpRotation.Normalize();

            string message = string.Format(
                Invariant,
                "{0} {1:R} {2:R} {3:R} {4:R} {5:R} {6:R} {7:R} {8:R} {9:R} {10} {11} {12} {13} {14:R} {15}",
                hand,
                udpPosition.x, udpPosition.y, udpPosition.z,
                udpRotation.x, udpRotation.y, udpRotation.z, udpRotation.w,
                trigger, grip,
                a, b, x, y,
                highRateMode ? 1.0f : 0.1f,
                TimestampNanoseconds());

            LogPoseDiagnostics(isLeft, position, udpPosition, rotation, udpRotation, message);

            SendDatagram(message);

            if (sendJoystick)
                SendJoystickState(device, isLeft, allowControlInput);
        }

        void SendHead()
        {
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid ||
                !head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) ||
                !head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
            {
                return;
            }

            SendDatagram(string.Format(
                Invariant,
                "HEAD {0:R} {1:R} {2:R} {3:R} {4:R} {5:R} {6:R} {7}",
                position.x, position.y, position.z,
                rotation.x, rotation.y, rotation.z, rotation.w,
                TimestampNanoseconds()));
        }

        void SendJoystickState(InputDevice device, bool isLeft, bool allowControlInput)
        {
            string hand = isLeft ? "LEFT" : "RIGHT";

            if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 joystick))
            {
                if (!allowControlInput)
                    joystick = Vector2.zero;
                bool havePrevious = isLeft ? haveLeftJoystick : haveRightJoystick;
                Vector2 previous = isLeft ? lastLeftJoystick : lastRightJoystick;
                bool changed = !havePrevious ||
                    Mathf.Abs(joystick.x - previous.x) >= joystickChangeThreshold ||
                    Mathf.Abs(joystick.y - previous.y) >= joystickChangeThreshold;

                if (sendJoystickEveryTick || changed)
                {
                    SendDatagram(string.Format(
                        Invariant,
                        "JOY {0} {1:R} {2:R} {3}",
                        hand, joystick.x, joystick.y, TimestampNanoseconds()));
                }

                if (isLeft)
                {
                    lastLeftJoystick = joystick;
                    haveLeftJoystick = true;
                }
                else
                {
                    lastRightJoystick = joystick;
                    haveRightJoystick = true;
                }
            }

            if (!device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool pressed))
                return;
            if (!allowControlInput)
                pressed = false;

            bool havePreviousPress = isLeft ? haveLeftJoystickPressed : haveRightJoystickPressed;
            bool previousPress = isLeft ? lastLeftJoystickPressed : lastRightJoystickPressed;
            if (!havePreviousPress || pressed != previousPress)
            {
                SendDatagram($"BTN {hand} JOYSTICK {(pressed ? 1 : 0)} {TimestampNanoseconds()}");
            }

            if (isLeft)
            {
                lastLeftJoystickPressed = pressed;
                haveLeftJoystickPressed = true;
            }
            else
            {
                lastRightJoystickPressed = pressed;
                haveRightJoystickPressed = true;
            }
        }

        /// <summary>Send a one-shot MODE message, for example SendMode("teleop").</summary>
        public void SendMode(string modeName)
        {
            if (string.IsNullOrWhiteSpace(modeName) || ContainsWhitespace(modeName))
            {
                Debug.LogWarning("[PICO UDP] MODE name must be one non-empty token without whitespace.");
                return;
            }

            SendDatagram($"MODE {modeName} {TimestampNanoseconds()}");
        }

        public void SendCalibrateDone()
        {
            SendDatagram($"CALIBRATE_DONE {TimestampNanoseconds()}");
        }

        /// <param name="handMode">False selects gripper mode (0); true selects hand mode (1).</param>
        public void SendPoseMode(bool handMode)
        {
            SendDatagram($"CFG POSE_MODE {(handMode ? 1 : 0)} {TimestampNanoseconds()}");
        }

        void SendDatagram(string message)
        {
            if (client == null || string.IsNullOrWhiteSpace(targetHost))
                return;

            byte[] payload = Encoding.UTF8.GetBytes(message);
            if (payload.Length >= 512)
            {
                Debug.LogError($"[PICO UDP] Refusing oversized datagram ({payload.Length} bytes).");
                return;
            }

            try
            {
                client.Send(payload, payload.Length, targetHost, targetPort);
            }
            catch (Exception exception)
            {
                if (Time.realtimeSinceStartup >= nextErrorLogTime)
                {
                    nextErrorLogTime = Time.realtimeSinceStartup + 2f;
                    Debug.LogWarning("[PICO UDP] Send failed: " + exception.Message);
                }
            }
        }

        void LogPoseDiagnostics(
            bool isLeft,
            Vector3 sourcePosition,
            Vector3 udpPosition,
            Quaternion sourceRotation,
            Quaternion udpRotation,
            string finalMessage)
        {
            if (!logPoseDiagnostics)
                return;

            float now = Time.realtimeSinceStartup;
            float nextLogTime = isLeft ? nextLeftPoseLogTime : nextRightPoseLogTime;
            if (now < nextLogTime)
                return;

            float interval = Mathf.Max(0.1f, poseLogIntervalSeconds);
            if (isLeft)
                nextLeftPoseLogTime = now + interval;
            else
                nextRightPoseLogTime = now + interval;

            Debug.Log(string.Format(
                Invariant,
                "[PICO UDP POSE] {0} rawPos=({1:R}, {2:R}, {3:R}) udpPos=({4:R}, {5:R}, {6:R}) " +
                "rawQuat=({7:R}, {8:R}, {9:R}, {10:R}) udpQuat=({11:R}, {12:R}, {13:R}, {14:R}) " +
                "message=\"{15}\"",
                isLeft ? "LEFT" : "RIGHT",
                sourcePosition.x, sourcePosition.y, sourcePosition.z,
                udpPosition.x, udpPosition.y, udpPosition.z,
                sourceRotation.x, sourceRotation.y, sourceRotation.z, sourceRotation.w,
                udpRotation.x, udpRotation.y, udpRotation.z, udpRotation.w,
                finalMessage));
        }

        long TimestampNanoseconds()
        {
            if (!sendUnixTimestampNanoseconds)
                return 0;

            // DateTime ticks are 100 ns. UnixEpoch keeps the value within signed Int64 range.
            return (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        }

        static bool ContainsWhitespace(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsWhiteSpace(value[index]))
                    return true;
            }

            return false;
        }
    }
}
