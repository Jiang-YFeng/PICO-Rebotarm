using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using RebotarmVR.Teleoperation;

// Binds the existing SettingCanvas by child names without changing its layout.
// Lives outside the panel so video/settings state survives closing the panel.
public sealed class VideoSettingsPanel : MonoBehaviour
{
    const string VideoKey = "Rebotarm.Settings.VideoIPv4.v1";
    const string ControlKey = "Rebotarm.Settings.ControlIPv4.v1";
    Transform panel;
    TMP_InputField videoInput, controlInput;
    TMP_Text mainStatus, wristStatus, message;
    Button test, save, reconnect, swap, close, openRobotControl, robotClose;
    GameObject mainPanel, robotControlPanel, robotControlCanvas;
    GeminiWebRtcReceiver main, wrist;
    PicoUdpTextSender sender;
    Coroutine testing;
    bool swapped;
    float nextStatus;
    Vector3 mainPosition, wristPosition, mainScale, wristScale;
    Quaternion mainRotation, wristRotation;
    bool haveLayout;
    GameObject reopen;
    Button openSettings;
    bool initialized;

    void Start()
    {
        var canvas = Resources.FindObjectsOfTypeAll<Canvas>().FirstOrDefault(c =>
            c.gameObject.scene == gameObject.scene &&
            (c.name == "SettingCanvas" || c.name == "SettingsCanvas"));
        if (canvas == null) { enabled = false; return; }
        panel = canvas.transform;
        // The saved panel uses StandaloneInputModule; XR rays require XRUIInputModule.
        var eventSystem = UnityEngine.EventSystems.EventSystem.current;
        if (eventSystem != null)
        {
            foreach (var module in eventSystem.GetComponents<UnityEngine.EventSystems.BaseInputModule>())
                if (!(module is UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule)) module.enabled = false;
            var xrModule = eventSystem.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
            if (!xrModule) xrModule = eventSystem.gameObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
            xrModule.enabled = true;
        }
        if (!canvas.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>())
            canvas.gameObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();
        videoInput = Find<TMP_InputField>("VideoIpInput");
        controlInput = Find<TMP_InputField>("ControlIpInput");
        mainStatus = Find<TMP_Text>("MainVideoStatus");
        wristStatus = Find<TMP_Text>("WristVideoStatus");
        message = Find<TMP_Text>("MessageText");
        test = Find<Button>("TestConnectionButton");
        save = Find<Button>("SaveConnectButton");
        reconnect = Find<Button>("ReconnectVideoButton");
        swap = Find<Button>("SwapViewButton");
        close = Find<Button>("CloseButton");
        mainPanel = FindTrimmed<RectTransform>("MainPanel")?.gameObject ??
                    FindTrimmed<RectTransform>("Background")?.gameObject;
        robotControlPanel = Resources.FindObjectsOfTypeAll<RectTransform>().FirstOrDefault(t =>
            t.gameObject.scene == gameObject.scene &&
            (t.name.Trim() == "RobotControlPanel" || t.name.Trim() == "RebotControlPanel"))?.gameObject;
        robotControlCanvas = Resources.FindObjectsOfTypeAll<Canvas>().FirstOrDefault(c =>
            c.gameObject.scene == gameObject.scene && c.name == "Robot Control Canvas")?.gameObject;
        robotClose = robotControlPanel ? robotControlPanel.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name.Trim() == "RobotCloseButton") : null;
        var receivers = FindObjectsOfType<GeminiWebRtcReceiver>().Where(r => r.gameObject.scene == gameObject.scene).ToArray();
        // Use the named, established pair, not the extra VideoReceiver2 object.
        main = receivers.FirstOrDefault(r => r.name == "GeminiWebRtcReceiver");
        wrist = receivers.FirstOrDefault(r => r.name == "WhistVideoReceiver" || r.name == "WristVideoReceiver");
        sender = FindObjectsOfType<PicoUdpTextSender>(true).FirstOrDefault(s => s.gameObject.scene == gameObject.scene);
        EnsureSeparatedControlUi(canvas);
        if (!videoInput || !controlInput || !mainStatus || !wristStatus || !message ||
            !test || !save || !reconnect || !swap || !main || !wrist || !sender)
        {
            Say("设置绑定不完整，请检查 UI 对象名称及两个接收器是否启用。");
            Debug.LogError("[Video Settings] Missing panel objects or named receivers.");
            enabled = false; return;
        }
        foreach (var input in new[] { videoInput, controlInput })
        {
            input.contentType = TMP_InputField.ContentType.Standard;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 15;
            input.onFocusSelectAll = false;
            input.resetOnDeActivation = false;
            input.characterValidation = TMP_InputField.CharacterValidation.None;
            input.keyboardType = TouchScreenKeyboardType.Default;
            input.shouldHideSoftKeyboard = false;
            input.shouldHideMobileInput = false;
            input.onValidateInput += ValidateCharacter;
        }
        string video = PlayerPrefs.GetString(VideoKey, new Uri(main.WhepUrl).Host);
        string control = PlayerPrefs.GetString(ControlKey, sender.TargetHost);
        videoInput.SetTextWithoutNotify(video);
        controlInput.SetTextWithoutNotify(control);
        if (ValidIp(video) && PlayerPrefs.HasKey(VideoKey)) ApplyVideoHost(video);
        if (ValidIp(control) && PlayerPrefs.HasKey(ControlKey)) sender.TargetHost = control;
        test.onClick.AddListener(TestConnection);
        save.onClick.AddListener(SaveAndConnect);
        reconnect.onClick.AddListener(ReconnectVideo);
        swap.onClick.AddListener(SwapViews);
        foreach (var input in new[] { videoInput, controlInput })
        {
            var editButtons = input.GetComponent<IpInputEditButtons>();
            if (!editButtons) editButtons = input.gameObject.AddComponent<IpInputEditButtons>();
            // Existing scene components also need their runtime references and
            // button listeners initialized.
            editButtons.Initialize(input, test);
        }
        ConfigurePointerFeedback();
        reopen = Resources.FindObjectsOfTypeAll<Transform>().Where(t =>
                t.gameObject.scene == gameObject.scene && t.name == "Open Settings Canvas")
            .OrderByDescending(t => t.parent && t.parent.name == "TeleoperationUIRoot")
            .ThenByDescending(t => t.parent != null).FirstOrDefault()?.gameObject;
        if (reopen)
        {
            openSettings = reopen.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(b => b.name == "OpenSettingsButton");
            openRobotControl = reopen.GetComponentsInChildren<Button>(true)
                .FirstOrDefault(b => b.name == "OpenRobotControlEntryButton");
            ConfigurePanelFeedback(reopen.transform);
        }
        if (close) close.onClick.AddListener(ClosePanel);
        if (openSettings) openSettings.onClick.AddListener(OpenPanel);
        if (openRobotControl) openRobotControl.onClick.AddListener(OpenRobotControlPanel);
        if (robotClose) robotClose.onClick.AddListener(CloseAllPanels);
        initialized = true;
        Say("设置已加载；UDP 为单向发送，无法确认 Ubuntu 接收状态。");
        // The panel starts open. Establish the same release-to-rearm rule used
        // for every later UI session before hiding it for normal operation.
        sender.SetUiInteractionBlocked(true);
        // Hide the actual canvas so its graphics cannot intercept XR rays.
        if (reopen) ClosePanel();
    }

    T Find<T>(string objectName) where T : Component => panel.GetComponentsInChildren<T>(true).FirstOrDefault(c => c.name == objectName);
    T FindTrimmed<T>(string objectName) where T : Component => panel.GetComponentsInChildren<T>(true)
        .FirstOrDefault(c => c.name.Trim().Equals(objectName, StringComparison.Ordinal));

    void EnsureSeparatedControlUi(Canvas settingsCanvas)
    {
        if (!robotControlPanel) return;

        Canvas[] robotCanvases = Resources.FindObjectsOfTypeAll<Canvas>().Where(c =>
            c.gameObject.scene == gameObject.scene && c.name == "Robot Control Canvas").ToArray();
        GameObject robotRoot = robotCanvases.FirstOrDefault(c =>
            c.transform.parent && c.transform.parent.name == "TeleoperationUIRoot")?.gameObject ??
            robotCanvases.FirstOrDefault()?.gameObject;
        bool createdRobotRoot = !robotRoot;
        if (!robotRoot)
        {
            robotRoot = new GameObject("Robot Control Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster));
        }

        var robotRootRect = (RectTransform)robotRoot.transform;
        var settingsRect = (RectTransform)settingsCanvas.transform;
        var robotCanvas = robotRoot.GetComponent<Canvas>();
        robotCanvas.renderMode = RenderMode.WorldSpace;
        robotCanvas.worldCamera = settingsCanvas.worldCamera;
        // Only apply a useful default pose when the canvas is created at runtime.
        // Existing scene objects must keep the position, rotation, scale and size
        // configured by the user in the Inspector.
        if (createdRobotRoot)
        {
            robotRootRect.sizeDelta = settingsRect.sizeDelta;
            robotRootRect.position = new Vector3(-settingsRect.position.x, settingsRect.position.y, settingsRect.position.z);
            robotRootRect.rotation = Quaternion.Euler(0f, -settingsRect.eulerAngles.y, 0f);
            robotRootRect.localScale = settingsRect.localScale;
        }

        var robotRect = (RectTransform)robotControlPanel.transform;
        robotRect.SetParent(robotRootRect, false);
        robotRect.anchorMin = Vector2.zero;
        robotRect.anchorMax = Vector2.one;
        robotRect.offsetMin = Vector2.zero;
        robotRect.offsetMax = Vector2.zero;
        robotRect.localScale = Vector3.one;
        robotRect.localRotation = Quaternion.identity;
        robotControlCanvas = robotRoot;

        // Remove empty/legacy duplicates after the actual panel has been moved
        // into the preferred canvas under TeleoperationUIRoot.
        foreach (Canvas duplicate in robotCanvases)
            if (duplicate && duplicate.gameObject != robotRoot)
                Destroy(duplicate.gameObject);

        Button closeTemplate = FindTrimmed<Button>("CloseButton");
        robotClose = robotControlPanel.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name.Trim() == "RobotCloseButton");
        bool createdRobotClose = false;
        if (!robotClose && closeTemplate)
        {
            robotClose = Instantiate(closeTemplate, robotControlPanel.transform);
            robotClose.name = "RobotCloseButton";
            createdRobotClose = true;
        }
        if (robotClose)
        {
            if (createdRobotClose)
            {
                var closeRect = (RectTransform)robotClose.transform;
                closeRect.anchorMin = closeRect.anchorMax = new Vector2(0.5f, 0.5f);
                closeRect.pivot = new Vector2(0.5f, 0.5f);
                closeRect.anchoredPosition = new Vector2(465, 315);
                closeRect.sizeDelta = new Vector2(40, 40);
            }
            var closeLabel = robotClose.GetComponentInChildren<TMP_Text>(true);
            if (closeLabel) closeLabel.text = "×";
        }

        foreach (string obsolete in new[] { "OpenRobotControlButton", "BackToSettingsButton" })
        {
            Button oldButton = Resources.FindObjectsOfTypeAll<Button>().FirstOrDefault(b =>
                b.gameObject.scene == gameObject.scene && b.name.Trim() == obsolete);
            if (oldButton) oldButton.gameObject.SetActive(false);
        }

        reopen = Resources.FindObjectsOfTypeAll<Transform>().Where(t =>
                t.gameObject.scene == gameObject.scene && t.name == "Open Settings Canvas")
            .OrderByDescending(t => t.parent && t.parent.name == "TeleoperationUIRoot")
            .ThenByDescending(t => t.parent != null).FirstOrDefault()?.gameObject;
        if (!reopen) return;

        openSettings = reopen.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name == "OpenSettingsButton");
        if (openSettings)
        {
            var settingsButtonRect = (RectTransform)openSettings.transform;
            settingsButtonRect.anchoredPosition = new Vector2(-200, 0);
            settingsButtonRect.sizeDelta = new Vector2(300, 76);
            var settingsLabel = openSettings.GetComponentInChildren<TMP_Text>(true);
            if (settingsLabel)
            {
                settingsLabel.enableAutoSizing = false;
                settingsLabel.fontSize = 26;
                settingsLabel.raycastTarget = false;
            }
        }

        openRobotControl = reopen.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name == "OpenRobotControlEntryButton");
        if (!openRobotControl && openSettings)
        {
            openRobotControl = Instantiate(openSettings, reopen.transform);
            openRobotControl.name = "OpenRobotControlEntryButton";
            openRobotControl.onClick = new Button.ButtonClickedEvent();
        }
        if (openRobotControl)
        {
            var robotButtonRect = (RectTransform)openRobotControl.transform;
            robotButtonRect.anchoredPosition = new Vector2(200, 0);
            robotButtonRect.sizeDelta = new Vector2(300, 76);
            var robotLabel = openRobotControl.GetComponentInChildren<TMP_Text>(true);
            if (robotLabel)
            {
                robotLabel.text = "机械臂控制";
                robotLabel.enableAutoSizing = false;
                robotLabel.fontSize = 26;
                robotLabel.raycastTarget = false;
            }
        }

        SetLayerRecursively(robotRoot, LayerMask.NameToLayer("UI"));
        robotRoot.SetActive(false);
    }

    static void SetLayerRecursively(GameObject root, int layer)
    {
        if (!root || layer < 0) return;
        root.layer = layer;
        foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
    }

    public void OpenRobotControlPanel()
    {
        if (!robotControlPanel || !robotControlCanvas) return;
        sender.SetUiInteractionBlocked(true);
        panel.gameObject.SetActive(false);
        robotControlCanvas.SetActive(true);
        if (reopen) reopen.SetActive(false);
        ConfigurePanelFeedback(robotControlPanel.transform);
    }

    public void BackToSettingsPanel()
    {
        OpenPanel();
    }

    public void CloseAllPanels()
    {
        if (videoInput) videoInput.DeactivateInputField();
        if (controlInput) controlInput.DeactivateInputField();
        if (panel) panel.gameObject.SetActive(false);
        if (robotControlCanvas) robotControlCanvas.SetActive(false);
        if (reopen) reopen.SetActive(true);
        sender.SetUiInteractionBlocked(false);
    }

    public void CloseAllPanelsForTeleoperation() => CloseAllPanels();
    static char ValidateCharacter(string text, int index, char c) => (c >= '0' && c <= '9') || c == '.' ? c : '\0';
    public static bool ValidIp(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 || p.Any(c => c < '0' || c > '9') || !byte.TryParse(p, out _))) return false;
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] > 0 && bytes[0] < 224 && !value.Equals("255.255.255.255");
    }
    bool ReadInputs(out string video, out string control)
    {
        video = videoInput.text.Trim(); control = controlInput.text.Trim();
        if (!ValidIp(video)) { Say("视频 IP 无效，请填写类似 192.168.3.140 的 IPv4 地址。"); return false; }
        if (!ValidIp(control)) { Say("控制 IP 无效，请填写 Ubuntu 的局域网 IPv4 地址。"); return false; }
        return true;
    }

    public void TestConnection()
    {
        if (testing != null || !ReadInputs(out string video, out _)) return;
        SetBusy(true);
        testing = StartCoroutine(TestServices(video));
    }
    IEnumerator TestServices(string host)
    {
        string result = "";
        foreach (var item in new[] { main, wrist })
        {
            var uri = new UriBuilder(item.WhepUrl) { Host = host };
            // Preview HTTP reachability only: a 200 is not proof of live media.
            uri.Path = uri.Path.EndsWith("/whep", StringComparison.Ordinal) ? uri.Path.Substring(0, uri.Path.Length - 5) + "/" : uri.Path;
            Say("正在测试视频服务…");
            using (var request = UnityWebRequest.Get(uri.Uri.AbsoluteUri))
            {
                request.timeout = 3;
                yield return request.SendWebRequest();
                result += (item == main ? "主相机服务：" : "腕部服务：") +
                    (request.result == UnityWebRequest.Result.Success ? "可访问" : "不可访问 (HTTP " + request.responseCode + ")") + "；";
            }
        }
        Say(result + "实际视频以左侧状态为准。UDP 无应答，控制接收未验证。");
        testing = null; SetBusy(false);
    }
    public void SaveAndConnect()
    {
        if (!ReadInputs(out string video, out string control)) return;
        PlayerPrefs.SetString(VideoKey, video); PlayerPrefs.SetString(ControlKey, control); PlayerPrefs.Save();
        sender.TargetHost = control;
        ApplyVideoHost(video);
        Say("已保存并请求视频重连；UDP 目标 " + control + ":" + sender.TargetPort + "，接收未验证。");
    }
    void ApplyVideoHost(string host) { main.SetHostAndReconnect(host); wrist.SetHostAndReconnect(host); }
    public void ReconnectVideo()
    {
        main.RequestReconnect(); wrist.RequestReconnect();
        Say("已请求两路视频重连，使用当前生效地址；编辑 IP 后请先保存。");
    }
    void SetBusy(bool busy) { test.interactable = !busy; save.interactable = !busy; reconnect.interactable = !busy; }
    void Say(string value) { if (message) message.text = "状态：" + value; }
    void Update()
    {
        if (!initialized || Time.unscaledTime < nextStatus) return;
        nextStatus = Time.unscaledTime + 0.5f;
        mainStatus.text = Status("主相机", main);
        wristStatus.text = Status("腕部", wrist);
    }
    static string Status(string label, GeminiWebRtcReceiver receiver)
    {
        if (!receiver || !receiver.isActiveAndEnabled) return label + "：已停用";
        switch (receiver.ConnectionStatus)
        {
            case "Connected": return label + "：已连接  " + receiver.Resolution + " · " + receiver.ReceivedFps.ToString("0") + " FPS";
            case "Connecting": case "New": return label + "：连接中";
            case "Reconnecting": return label + "：重连中";
            case "Failed": return label + "：连接失败";
            default: return label + "：未连接";
        }
    }
    public void SwapViews()
    {
        var a = main.VideoScreen ? main.VideoScreen.transform : null;
        var b = wrist.VideoScreen ? wrist.VideoScreen.transform : null;
        if (!a || !b || a == b || a.parent != b.parent)
        { Say("切换要求两块不同的屏幕位于同一父节点下。"); return; }
        if (!haveLayout)
        {
            mainPosition = a.localPosition; mainRotation = a.localRotation; mainScale = a.localScale;
            wristPosition = b.localPosition; wristRotation = b.localRotation; wristScale = b.localScale;
            haveLayout = true;
        }
        swapped = !swapped;
        a.localPosition = swapped ? wristPosition : mainPosition; a.localRotation = swapped ? wristRotation : mainRotation;
        b.localPosition = swapped ? mainPosition : wristPosition; b.localRotation = swapped ? mainRotation : wristRotation;
        a.localScale = swapped ? Fit(mainScale, wristScale) : mainScale;
        b.localScale = swapped ? Fit(wristScale, mainScale) : wristScale;
        Say(swapped ? "腕部画面已放大，主相机显示在小屏幕。" : "已恢复主相机大屏幕。");
    }
    static Vector3 Fit(Vector3 source, Vector3 slot)
    {
        float factor = Mathf.Min(Mathf.Abs(slot.x / source.x), Mathf.Abs(slot.y / source.y));
        return new Vector3(source.x * factor, source.y * factor, source.z);
    }
    void ConfigurePointerFeedback()
    {
        // Text should not obscure the hit target. Input-field backgrounds retain raycasts.
        foreach (var label in panel.GetComponentsInChildren<TMP_Text>(true)) label.raycastTarget = false;
        foreach (var background in panel.GetComponentsInChildren<Image>(true))
            if (background.name == "Background") background.raycastTarget = true;
        ConfigurePanelFeedback(panel);
        foreach (var ray in FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.XRRayInteractor>())
        {
            if (ray.gameObject.scene != gameObject.scene) continue;
            ray.enableUIInteraction = true;
            var visual = ray.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRInteractorLineVisual>();
            if (visual)
            {
                visual.stopLineAtFirstRaycastHit = true;
                visual.lineWidth = 0.004f;
                visual.smoothMovement = false;
            }
            if (!ray.GetComponent<SettingsRayPointer>()) ray.gameObject.AddComponent<SettingsRayPointer>();
        }
    }

    void ConfigurePanelFeedback(Transform root)
    {
        if (!root) return;
        foreach (var label in root.GetComponentsInChildren<TMP_Text>(true)) label.raycastTarget = false;
        foreach (var selectable in root.GetComponentsInChildren<Selectable>(true))
        {
            selectable.transition = Selectable.Transition.ColorTint;
            var colors = selectable.colors;
            colors.highlightedColor = new Color(0.55f, 0.9f, 1f);
            colors.selectedColor = new Color(0.55f, 0.9f, 1f);
            colors.pressedColor = new Color(1f, 0.75f, 0.2f);
            selectable.colors = colors;
            if (selectable.targetGraphic) selectable.targetGraphic.raycastTarget = true;
        }
    }

    public void ClosePanel()
    {
        CloseAllPanels();
    }
    public void OpenPanel()
    {
        sender.SetUiInteractionBlocked(true);
        if (robotControlCanvas) robotControlCanvas.SetActive(false);
        panel.gameObject.SetActive(true);
        if (mainPanel) mainPanel.SetActive(true);
        if (reopen) reopen.SetActive(false);
    }
    void OnDestroy()
    {
        if (test) test.onClick.RemoveListener(TestConnection);
        if (save) save.onClick.RemoveListener(SaveAndConnect);
        if (reconnect) reconnect.onClick.RemoveListener(ReconnectVideo);
        if (swap) swap.onClick.RemoveListener(SwapViews);
        if (close) close.onClick.RemoveListener(ClosePanel);
        if (openSettings) openSettings.onClick.RemoveListener(OpenPanel);
        if (openRobotControl) openRobotControl.onClick.RemoveListener(OpenRobotControlPanel);
        if (robotClose) robotClose.onClick.RemoveListener(CloseAllPanels);
        if (videoInput) videoInput.onValidateInput -= ValidateCharacter;
        if (controlInput) controlInput.onValidateInput -= ValidateCharacter;
        if (reopen) Destroy(reopen);
    }
}
