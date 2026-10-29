using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

// Token is entered at runtime, never serialized or stored in PlayerPrefs.
public sealed class RobotControlClient : MonoBehaviour
{
    const string ControlKey = "Rebotarm.Settings.ControlIPv4.v1";
    readonly List<Button> buttons = new List<Button>();
    readonly List<Action> unbind = new List<Action>();
    TMP_InputField tokenInput;
    TMP_Text statusText, resultText;
    Button authorizationButton;
    GameObject extraPanel, confirmation, authorizationConfirmation, teleoperationPrompt;
    string token = "";
    bool busy;
    bool realModeAuthorized;
    string authorizedHost, authorizedToken;
    string confirmedHost;
    Reply latest;
    TMP_FontAsset font;

    [Serializable] public sealed class Reply
    {
        public bool ok, dry_run, busy;
        public string command, state, message, driver, model, vr, servo, active_command;
    }
    public static string CommandPath(string command)
    {
        switch (command)
        {
            case "start-driver": case "enter-safe-pose": case "start-vr":
            case "start-servo": case "stop-servo": case "return-safe-pose":
            case "stop-vr": case "shutdown-to-zero": case "shutdown-and-disable": return "/api/" + command;
            default: throw new ArgumentException("Unknown command");
        }
    }
    public static string CommandBody(string command) => command == "shutdown-and-disable"
        ? "{\"confirm\":\"ZERO_DISABLE\"}"
        : command == "shutdown-to-zero" ? "{\"confirm\":\"ZERO\"}" : "{}";

    IEnumerator Start()
    {
        yield return null;
        var panel = Resources.FindObjectsOfTypeAll<RectTransform>().FirstOrDefault(t =>
            t.gameObject.scene == gameObject.scene && t.name.Trim() == "RobotControlPanel");
        if (!panel) { Debug.LogError("[Robot HTTP] RobotControlPanel not found."); yield break; }
        font = panel.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f);
        if (!font) font = TMP_Settings.defaultFontAsset;
        extraPanel = Box("HTTP Control", panel, new Vector2(450, 560));
        var rect = (RectTransform)extraPanel.transform;
        // Center below the robot panel, with a 20-unit gap; retain Box's
        // local Z offset so the controls sit slightly in front of the panel.
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0);
        rect.pivot = new Vector2(0.5f, 1);
        rect.anchoredPosition = new Vector2(0, -20);
        Label(extraPanel.transform, "连接状态与真机授权", 245, 32);
        Label(extraPanel.transform, "令牌（本次运行有效）", 200, 24);
        tokenInput = Input(extraPanel.transform, 153);
        UnityAction<string> change = value =>
        {
            token = value;
            latest = null;
            RevokeRealAuthorization("令牌已修改，请刷新状态");
        };
        tokenInput.onValueChanged.AddListener(change);
        unbind.Add(() => { if (tokenInput) tokenInput.onValueChanged.RemoveListener(change); });
        MakeButton(extraPanel.transform, "刷新状态", 95, RefreshStatus);
        authorizationButton = MakeButton(extraPanel.transform, "启用真机控制", 37, RequestRealAuthorization);
        statusText = Label(extraPanel.transform, "请填写令牌，再刷新状态", -55, 23, 110);
        resultText = Label(extraPanel.transform, "使用设置页已保存的控制 IP；默认 192.168.3.132:5200", -193, 22, 140);
        Bind(panel, "StartDriverButton", StartDriver);
        Bind(panel, "EnterSafePoseButton", EnterSafePose);
        Bind(panel, "StartVrButton", StartVr);
        Bind(panel, "StartServoButton", StartServo);
        Bind(panel, "StopServoButton", StopServo);
        Bind(panel, "ReturnSafePoseButton", ReturnSafePose);
        Bind(panel, "ShutdownToZeroButton", ShowZeroConfirmation);

        // Preserve positions authored in the Unity scene. Runtime code only
        // creates Stop VR for older scenes where the button is genuinely absent.
        var zeroButton = panel.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name.Trim() == "ShutdownToZeroButton");
        var stopVrButton = panel.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name.Trim() == "StopVrButton");
        if (stopVrButton)
        {
            Bind(panel, "StopVrButton", StopVr);
        }
        else
        {
            stopVrButton = MakeButton(panel, "停止 VR", -140, StopVr);
            stopVrButton.name = "StopVrButton";
            RectTransform stopRect = (RectTransform)stopVrButton.transform;
            if (zeroButton)
            {
                Vector2 zeroPosition = ((RectTransform)zeroButton.transform).anchoredPosition;
                stopRect.anchoredPosition = new Vector2(-Mathf.Abs(zeroPosition.x), zeroPosition.y);
                stopRect.sizeDelta = ((RectTransform)zeroButton.transform).sizeDelta;
            }
            else
            {
                stopRect.anchoredPosition = new Vector2(-220, -140);
            }
        }

        confirmation = Box("Zero Confirmation", panel, new Vector2(660, 400));
        Label(confirmation.transform, "请选择回零方式", 150, 34);
        Label(confirmation.transform, "请放下物品，确认周围无遮挡。\n关闭机械臂后电机将失能。", 80, 26, 75);
        MakeButton(confirmation.transform, "仅回零", 0, ConfirmZero);
        MakeButton(confirmation.transform, "回零并关闭机械臂", -65, ConfirmZeroAndDisable);
        MakeButton(confirmation.transform, "取消", -135, CancelZero);
        confirmation.SetActive(false);

        authorizationConfirmation = Box("Real Mode Confirmation", panel, new Vector2(680, 360));
        Label(authorizationConfirmation.transform, "确认启用真机控制？", 125, 34);
        Label(authorizationConfirmation.transform,
            "机械臂可能立即使能或运动。\n请确认周围无人、无遮挡，急停可用。", 45, 25, 90);
        MakeButton(authorizationConfirmation.transform, "取消", -55, CancelRealAuthorization);
        MakeButton(authorizationConfirmation.transform, "确认启用", -120, ConfirmRealAuthorization);
        authorizationConfirmation.SetActive(false);

        teleoperationPrompt = Box("Teleoperation Ready", panel, new Vector2(700, 330));
        Label(teleoperationPrompt.transform, "遥操作已启动", 110, 34);
        Label(teleoperationPrompt.transform,
            "请关闭所有控制面板，松开左右握把，\n然后重新按住左握把开始遥操作。", 30, 25, 90);
        MakeButton(teleoperationPrompt.transform, "关闭面板并准备遥操作", -105, ClosePanelsAndOperate);
        teleoperationPrompt.SetActive(false);
    }

    void Bind(Transform panel, string name, UnityAction action)
    {
        var button = panel.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name.Trim() == name);
        if (!button) { Debug.LogWarning("[Robot HTTP] Missing button: " + name); return; }
        // Refuse to layer commands over another persistent handler.
        if (button.onClick.GetPersistentEventCount() > 0)
        { Debug.LogError("[Robot HTTP] Remove old OnClick binding before using: " + name); button.interactable = false; return; }
        button.onClick.AddListener(action); buttons.Add(button);
        unbind.Add(() => { if (button) button.onClick.RemoveListener(action); });
    }

    public void RefreshStatus() => Begin(null);
    public void StartDriver() => Begin("start-driver");
    public void EnterSafePose() => Begin("enter-safe-pose");
    public void StartVr() => Begin("start-vr");
    public void StartServo() => Begin("start-servo");
    public void StopServo() => Begin("stop-servo");
    public void ReturnSafePose() => Begin("return-safe-pose");
    public void StopVr() => Begin("stop-vr");
    public void ClosePanelsAndOperate()
    {
        if (teleoperationPrompt) teleoperationPrompt.SetActive(false);
        // The modal prompt temporarily disables every robot command button.
        // Restore them before hiding the panel so reopening it never leaves a
        // dark, unusable control surface.
        foreach (var button in buttons)
            if (button) button.interactable = !busy;
        var settings = FindObjectsOfType<VideoSettingsPanel>(true)
            .FirstOrDefault(item => item.gameObject.scene == gameObject.scene);
        if (settings) settings.CloseAllPanelsForTeleoperation();
    }
    public void RequestRealAuthorization()
    {
        if (busy || !authorizationConfirmation) return;
        if (latest == null) { ShowResult("请先刷新状态，再启用真机控制。"); return; }
        if (latest.dry_run) { ShowResult("当前是模拟模式，不需要真机授权。"); return; }
        if (latest.busy) { ShowResult("服务正在执行其他操作，请稍后刷新。"); return; }
        authorizationConfirmation.SetActive(true);
        authorizationConfirmation.transform.SetAsLastSibling();
        foreach (var b in buttons)
            if (b) b.interactable = b.transform.IsChildOf(authorizationConfirmation.transform);
    }
    public void CancelRealAuthorization()
    {
        if (authorizationConfirmation) authorizationConfirmation.SetActive(false);
        foreach (var b in buttons) if (b) b.interactable = !busy;
    }
    public void ConfirmRealAuthorization()
    {
        if (!authorizationConfirmation || !authorizationConfirmation.activeInHierarchy || busy) return;
        realModeAuthorized = true;
        authorizedHost = Host();
        authorizedToken = token;
        CancelRealAuthorization();
        UpdateAuthorizationDisplay("真机模式：已授权");
        ShowResult("真机控制已启用。本次运行有效；请按安全顺序操作。");
    }
    public void ShowZeroConfirmation()
    {
        if (busy || !confirmation) return;
        confirmedHost = Host();
        confirmation.SetActive(true);
        confirmation.transform.SetAsLastSibling();
        foreach (var b in buttons) if (b) b.interactable = b.transform.IsChildOf(confirmation.transform);
    }
    public void CancelZero()
    {
        confirmation.SetActive(false);
        foreach (var b in buttons) if (b) b.interactable = !busy;
    }
    public void ConfirmZero() => ConfirmZeroCommand("shutdown-to-zero");
    public void ConfirmZeroAndDisable() => ConfirmZeroCommand("shutdown-and-disable");
    void ConfirmZeroCommand(string command)
    {
        if (!confirmation || !confirmation.activeInHierarchy || busy) return;
        CancelZero();
        if (confirmedHost != Host()) { ShowResult("地址已改变，请重新确认。"); return; }
        Begin(command);
    }
    string Host() => PlayerPrefs.GetString(ControlKey, "192.168.3.132").Trim();
    void Begin(string command)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(token)) { ShowResult("请先填写令牌。"); return; }
        if (token.Any(char.IsControl)) { ShowResult("令牌包含换行等无效字符，请重新输入。"); return; }
        string host = Host();
        if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { ShowResult("控制 IP 无效，请在设置页保存正确地址。"); return; }
        if (realModeAuthorized && (authorizedHost != host || authorizedToken != token))
            RevokeRealAuthorization("地址或令牌已改变，请刷新状态并重新授权。");
        busy = true;
        foreach (var b in buttons) if (b) b.interactable = false;
        tokenInput.interactable = false;
        StartCoroutine(Run("http://" + host + ":5200", token, command));
    }
    IEnumerator Run(string url, string credential, string command)
    {
        bool showTeleoperationPrompt = false;
        try
        {
            latest = null;
            bool valid = false;
            yield return Request(url + "/api/status", credential, null, r => { latest = r; valid = true; }, true);
            if (!valid || command == null) yield break;
            if (!latest.dry_run && !IsRealModeAuthorized())
            { ShowResult("真机控制尚未授权，请先点击“启用真机控制”。"); yield break; }
            if (latest.busy) { ShowResult("服务正在执行其他操作，请稍后刷新。"); yield break; }
            string result = null;
            bool commandSucceeded = false;
            yield return Request(url + CommandPath(command), credential, CommandBody(command),
                r => commandSucceeded = true, false);
            result = resultText.text;
            // Never retry a command. Refresh is read-only, including after timeout.
            yield return Request(url + "/api/status", credential, null, r => latest = r, true);
            resultText.text = result;
            showTeleoperationPrompt = commandSucceeded && command == "start-servo";
        }
        finally
        {
            busy = false;
            foreach (var b in buttons) if (b) b.interactable = true;
            if (tokenInput) tokenInput.interactable = true;
        }
        if (showTeleoperationPrompt)
            ShowTeleoperationPrompt();
    }
    void ShowTeleoperationPrompt()
    {
        if (!teleoperationPrompt) return;
        teleoperationPrompt.SetActive(true);
        teleoperationPrompt.transform.SetAsLastSibling();
        foreach (var b in buttons)
            if (b) b.interactable = b.transform.IsChildOf(teleoperationPrompt.transform);
    }
    IEnumerator Request(string url, string credential, string body, Action<Reply> accepted, bool status)
    {
        using (var request = new UnityWebRequest(url, body == null ? "GET" : "POST"))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("X-Rebotarm-Token", credential);
            request.SetRequestHeader("Accept", "application/json");
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.timeout = body == null ? 5 : 100;
            ShowResult(body == null ? "正在刷新状态…" : "请求已发送，等待结果…");
            yield return request.SendWebRequest();
            Reply reply = null;
            try { reply = JsonUtility.FromJson<Reply>(request.downloadHandler.text); } catch (ArgumentException) { }
            bool success = request.result == UnityWebRequest.Result.Success && reply != null && reply.ok
                && (status || reply.state == "completed");
            string detail = reply != null && !string.IsNullOrEmpty(reply.message) ? reply.message :
                request.responseCode == 401 ? "令牌不正确" : request.responseCode == 0 ?
                (body == null ? "无法连接服务，请检查地址和服务状态" : "未收到结果，请刷新状态确认；不要重复提交") : "服务返回了无法解析的响应";
            ShowResult("HTTP " + request.responseCode + " · ok=" + (reply == null ? "未知" : reply.ok.ToString()) + "\n" + detail);
            if (status)
            {
                if (!success)
                    RevokeRealAuthorization(null);
                else if (reply.dry_run && realModeAuthorized)
                    RevokeRealAuthorization(null);
                statusText.text = success ? (reply.dry_run ? "模拟模式 dry_run=true" :
                    (IsRealModeAuthorized() ? "真机模式：已授权" : "真机模式：尚未授权")) +
                    "\n驱动：" + reply.driver + "  模型：" + reply.model + "\nVR：" + reply.vr + "  Servo：" + reply.servo +
                    (reply.busy ? "\n执行中：" + reply.active_command : "") : "状态未知：刷新失败";
                if (authorizationButton)
                    authorizationButton.GetComponentInChildren<TMP_Text>().text = IsRealModeAuthorized() ? "真机控制已启用" : "启用真机控制";
            }
            if (success) accepted(reply);
        }
    }
    void ShowResult(string value) { if (resultText) resultText.text = value; }
    bool IsRealModeAuthorized() => realModeAuthorized && authorizedHost == Host() && authorizedToken == token;
    void RevokeRealAuthorization(string message)
    {
        realModeAuthorized = false;
        authorizedHost = authorizedToken = null;
        UpdateAuthorizationDisplay(message);
    }
    void UpdateAuthorizationDisplay(string message)
    {
        if (authorizationButton)
            authorizationButton.GetComponentInChildren<TMP_Text>().text = realModeAuthorized ? "真机控制已启用" : "启用真机控制";
        if (!string.IsNullOrEmpty(message) && statusText) statusText.text = message;
    }
    GameObject Box(string name, Transform parent, Vector2 size)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        var r = (RectTransform)obj.transform; r.SetParent(parent, false);
        r.sizeDelta = size; r.localPosition = new Vector3(0, 0, -2);
        obj.GetComponent<Image>().color = new Color(0.08f, 0.10f, 0.14f, 0.98f);
        return obj;
    }
    TMP_Text Label(Transform parent, string text, float y, float size, float height = 45)
    {
        var obj = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        var r = (RectTransform)obj.transform; r.SetParent(parent, false);
        r.sizeDelta = new Vector2(((RectTransform)parent).sizeDelta.x - 30, height);
        r.anchoredPosition = new Vector2(0, y);
        var label = obj.GetComponent<TextMeshProUGUI>(); label.font = font; label.fontSize = size;
        label.text = text; label.alignment = TextAlignmentOptions.Center; label.raycastTarget = false;
        return label;
    }
    Button MakeButton(Transform parent, string text, float y, UnityAction action)
    {
        var obj = Box(text, parent, new Vector2(300, 48));
        ((RectTransform)obj.transform).anchoredPosition = new Vector2(0, y);
        var b = obj.AddComponent<Button>(); b.targetGraphic = obj.GetComponent<Image>();
        b.targetGraphic.color = new Color(0.18f, 0.36f, 0.48f);
        Label(obj.transform, text, 0, 26);
        b.onClick.AddListener(action); buttons.Add(b); return b;
    }
    TMP_InputField Input(Transform parent, float y)
    {
        var obj = Box("Control Token", parent, new Vector2(410, 48));
        ((RectTransform)obj.transform).anchoredPosition = new Vector2(0, y);
        var input = obj.AddComponent<TMP_InputField>();
        var text = Label(obj.transform, "", 0, 24);
        input.textViewport = (RectTransform)obj.transform;
        input.textComponent = text;
        input.targetGraphic = obj.GetComponent<Image>();
        input.contentType = TMP_InputField.ContentType.Password;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 256;
        input.shouldHideSoftKeyboard = false;
        return input;
    }
    void OnDestroy()
    {
        foreach (var detach in unbind) detach();
        if (extraPanel) Destroy(extraPanel);
        if (confirmation) Destroy(confirmation);
        token = "";
    }
}
