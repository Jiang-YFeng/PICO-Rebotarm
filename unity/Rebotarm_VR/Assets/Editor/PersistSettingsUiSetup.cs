using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

// Converts the editable settings UI from runtime-generated objects into saved scene objects.
[InitializeOnLoad]
public static class PersistSettingsUiSetup
{
    const string ScenePath = "Assets/Scenes/Teleoperation.unity";

    static PersistSettingsUiSetup()
    {
        EditorApplication.delayCall += AutoApply;
    }

    static void AutoApply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += AutoApply;
            return;
        }
        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != ScenePath) return;
        RestoreAccidentalSceneRoot(scene);
        Canvas canvas = Resources.FindObjectsOfTypeAll<Canvas>().FirstOrDefault(c =>
            c.gameObject.scene == scene && (c.name == "SettingCanvas" || c.name == "SettingsCanvas"));
        if (!canvas) return;
        Transform robotPanel = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t =>
            t.gameObject.scene == scene && t.name == "RobotControlPanel");
        Canvas[] robotCanvases = Resources.FindObjectsOfTypeAll<Canvas>().Where(c =>
            c.gameObject.scene == scene && c.name == "Robot Control Canvas").ToArray();
        Canvas preferredRobotCanvas = robotCanvases.FirstOrDefault(c =>
            c.transform.parent && c.transform.parent.name == "TeleoperationUIRoot");
        Transform openSettingsCanvas = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t =>
            t.gameObject.scene == scene && t.name == "Open Settings Canvas");
        bool complete = canvas.transform.Find("MainPanel") && robotPanel && preferredRobotCanvas &&
                        robotPanel.parent == preferredRobotCanvas.transform && robotCanvases.Length == 1 &&
                        openSettingsCanvas &&
                        FindByTrimmedName<Button>(openSettingsCanvas, "OpenRobotControlEntryButton") &&
                        FindByTrimmedName<Button>(robotPanel, "RobotCloseButton") &&
                        scene.GetRootGameObjects().Any(g => g.name == "Video Settings Controller") &&
                        FindByTrimmedName<TMP_InputField>(canvas.transform, "VideoIpInput")
                            ?.GetComponentsInChildren<Button>(true).Any(b => b.name == "ClearIpButton") == true &&
                        FindByTrimmedName<TMP_InputField>(canvas.transform, "ControlIpInput")
                            ?.GetComponentsInChildren<Button>(true).Any(b => b.name == "ClearIpButton") == true;
        if (!complete) ApplyToOpenScene();
    }

    [MenuItem("Tools/Rebotarm/Persist Settings UI In Scene")]
    public static void ApplyToOpenScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ApplyToOpenScene;
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.path != ScenePath) return;
        RestoreAccidentalSceneRoot(scene);

        Canvas canvas = Resources.FindObjectsOfTypeAll<Canvas>().FirstOrDefault(c =>
            c.gameObject.scene == scene && (c.name == "SettingCanvas" || c.name == "SettingsCanvas"));
        if (!canvas) return;

        Transform main = canvas.transform.Cast<Transform>().FirstOrDefault(t =>
            t.name.Trim() == "MainPanel" || t.name.Trim() == "Background");
        if (!main) throw new MissingReferenceException("SettingCanvas 下未找到 MainPanel/Background。");
        main.name = "MainPanel";

        Button closeTemplate = FindByTrimmedName<Button>(canvas.transform, "CloseButton");
        TMP_Text textTemplate = canvas.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        if (!closeTemplate || !textTemplate)
            throw new MissingReferenceException("设置页面缺少 CloseButton 或 TMP 文本模板。");
        Button oldOpenRobot = FindByTrimmedName<Button>(canvas.transform, "OpenRobotControlButton");
        if (oldOpenRobot) oldOpenRobot.gameObject.SetActive(false);
        EnsureIpClearButton(FindByTrimmedName<TMP_InputField>(canvas.transform, "VideoIpInput"), closeTemplate);
        EnsureIpClearButton(FindByTrimmedName<TMP_InputField>(canvas.transform, "ControlIpInput"), closeTemplate);

        Transform robot = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t =>
            t.gameObject.scene == scene &&
            (t.name.Trim() == "RobotControlPanel" || t.name.Trim() == "RebotControlPanel"));
        if (!robot)
        {
            var go = new GameObject("RobotControlPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            robot = go.transform;
            robot.SetParent(canvas.transform, false);
        }
        robot.name = "RobotControlPanel";
        // The panel itself stays a normal child; its new parent is an independent
        // world-space Canvas mirrored to the right of the main video screen.
        foreach (var raycaster in robot.GetComponents<GraphicRaycaster>())
            UnityEngine.Object.DestroyImmediate(raycaster);
        foreach (var raycaster in robot.GetComponents<TrackedDeviceGraphicRaycaster>())
            UnityEngine.Object.DestroyImmediate(raycaster);
        foreach (var scaler in robot.GetComponents<CanvasScaler>())
            UnityEngine.Object.DestroyImmediate(scaler);
        foreach (var nestedCanvas in robot.GetComponents<Canvas>())
            UnityEngine.Object.DestroyImmediate(nestedCanvas);
        SetLayerRecursively(robot.gameObject, LayerMask.NameToLayer("UI"));
        CopyPanelLayout(main as RectTransform, robot as RectTransform);
        CopyPanelImage(main.GetComponent<Image>(), robot.GetComponent<Image>());
        Canvas robotCanvas = CreateOrUpdateRobotControlCanvas(scene, canvas, robot);

        EnsureLabel(robot, textTemplate, "RobotControlTitle", "机械臂控制",
            new Vector2(0, 285), new Vector2(700, 70), 42);
        EnsureLabel(robot, textTemplate, "RobotControlHint", "选择需要执行的机械臂操作",
            new Vector2(0, 220), new Vector2(800, 50), 25);

        EnsureButton(robot, closeTemplate, "StartDriverButton", "启动机械臂", new Vector2(-220, 130));
        EnsureButton(robot, closeTemplate, "EnterSafePoseButton", "进入工作姿态", new Vector2(220, 130));
        EnsureButton(robot, closeTemplate, "StartVrButton", "启动 VR 控制", new Vector2(-220, 20));
        EnsureButton(robot, closeTemplate, "StartServoButton", "开始遥操作", new Vector2(220, 20));
        EnsureButton(robot, closeTemplate, "StopServoButton", "暂停遥操作", new Vector2(-220, -90));
        EnsureButton(robot, closeTemplate, "ReturnSafePoseButton", "回到工作姿态", new Vector2(220, -90));
        EnsureButton(robot, closeTemplate, "StopVrButton", "停止 VR", new Vector2(-220, -200));
        EnsureButton(robot, closeTemplate, "ShutdownToZeroButton", "机械臂回零", new Vector2(220, -200));
        Button oldBack = FindByTrimmedName<Button>(robot, "BackToSettingsButton");
        if (oldBack) oldBack.gameObject.SetActive(false);
        Button robotClose = FindByTrimmedName<Button>(robot, "RobotCloseButton");
        if (!robotClose)
        {
            robotClose = EnsureButton(robot, closeTemplate, "RobotCloseButton", "×", new Vector2(465, 315));
            ((RectTransform)robotClose.transform).sizeDelta = new Vector2(40, 40);
        }
        else
        {
            robotClose.onClick = new Button.ButtonClickedEvent();
            TMP_Text closeLabel = robotClose.GetComponentInChildren<TMP_Text>(true);
            if (closeLabel)
            {
                closeLabel.text = "×";
                closeLabel.raycastTarget = false;
            }
        }
        robot.gameObject.SetActive(true);
        robotCanvas.gameObject.SetActive(false);

        GameObject controller = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Video Settings Controller");
        if (!controller) controller = new GameObject("Video Settings Controller");
        if (!controller.GetComponent<VideoSettingsPanel>()) controller.AddComponent<VideoSettingsPanel>();

        CreateOrUpdateOpenSettingsCanvas(scene, canvas, closeTemplate);

        EditorUtility.SetDirty(canvas.gameObject);
        EditorUtility.SetDirty(robot.gameObject);
        EditorUtility.SetDirty(robotCanvas.gameObject);
        EditorUtility.SetDirty(controller);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Settings UI] 独立设置页、右侧机械臂控制页及双入口按钮已保存到 Teleoperation 场景。");
    }

    static void RestoreAccidentalSceneRoot(Scene scene)
    {
        GameObject accidentalRoot = scene.GetRootGameObjects()
            .FirstOrDefault(g => g.name == "TeleoperationUIRoot");
        if (!accidentalRoot) return;

        // Only repair the accidental full-scene grouping. A deliberately scoped
        // UI root would not contain the tracked Main Camera as a direct child.
        bool containsTrackedCamera = accidentalRoot.transform.Cast<Transform>()
            .Any(child => child.name == "Main Camera");
        if (!containsTrackedCamera) return;

        Transform[] children = accidentalRoot.transform.Cast<Transform>().ToArray();
        foreach (Transform child in children)
            child.SetParent(null, false);

        UnityEngine.Object.DestroyImmediate(accidentalRoot);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Settings UI] 已撤销误建的 TeleoperationUIRoot，并恢复原场景根层级与坐标。");
    }

    // Entry point for unattended project migration when the editor is not already open.
    public static void ApplyBatch()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ApplyToOpenScene();
    }

    static T FindByTrimmedName<T>(Transform root, string name) where T : Component
    {
        return root.GetComponentsInChildren<T>(true).FirstOrDefault(c =>
            c.name.Trim().Equals(name, StringComparison.Ordinal));
    }

    static void CopyPanelLayout(RectTransform source, RectTransform target)
    {
        if (!source || !target) return;
        target.SetParent(source.parent, false);
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.anchoredPosition = source.anchoredPosition;
        target.sizeDelta = source.sizeDelta;
        target.localRotation = source.localRotation;
        target.localScale = source.localScale;
    }

    static void CopyPanelImage(Image source, Image target)
    {
        if (!source || !target) return;
        target.sprite = source.sprite;
        target.type = source.type;
        target.color = source.color;
        target.raycastTarget = source.raycastTarget;
    }

    static Canvas CreateOrUpdateRobotControlCanvas(Scene scene, Canvas settingsCanvas, Transform robotPanel)
    {
        Canvas[] candidates = Resources.FindObjectsOfTypeAll<Canvas>().Where(c =>
            c.gameObject.scene == scene && c.name == "Robot Control Canvas").ToArray();
        Canvas preferred = candidates.FirstOrDefault(c =>
            c.transform.parent && c.transform.parent.name == "TeleoperationUIRoot");
        GameObject root = preferred ? preferred.gameObject : candidates.FirstOrDefault()?.gameObject;
        bool createdRoot = !root;
        if (!root)
            root = new GameObject("Robot Control Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));

        Transform uiRoot = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t =>
            t.gameObject.scene == scene && t.name == "TeleoperationUIRoot");
        if (uiRoot && root.transform.parent != uiRoot)
            root.transform.SetParent(uiRoot, true);

        foreach (Canvas duplicate in candidates)
            if (duplicate && duplicate.gameObject != root)
                UnityEngine.Object.DestroyImmediate(duplicate.gameObject);

        RectTransform rect = root.GetComponent<RectTransform>();
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = settingsCanvas.worldCamera;
        // Do not overwrite an existing canvas layout when this setup utility is
        // run again. These are defaults for a newly-created canvas only.
        if (createdRoot)
        {
            rect.sizeDelta = ((RectTransform)settingsCanvas.transform).sizeDelta;
            rect.position = new Vector3(2.4f, 1.8f, 1.1f);
            rect.rotation = Quaternion.Euler(0f, 60f, 0f);
            rect.localScale = Vector3.one * 0.002f;
        }

        CanvasScaler sourceScaler = settingsCanvas.GetComponent<CanvasScaler>();
        CanvasScaler targetScaler = root.GetComponent<CanvasScaler>();
        if (sourceScaler && targetScaler)
        {
            targetScaler.uiScaleMode = sourceScaler.uiScaleMode;
            targetScaler.referencePixelsPerUnit = sourceScaler.referencePixelsPerUnit;
            targetScaler.dynamicPixelsPerUnit = sourceScaler.dynamicPixelsPerUnit;
        }

        robotPanel.SetParent(rect, false);
        RectTransform panelRect = (RectTransform)robotPanel;
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        panelRect.localScale = Vector3.one;
        panelRect.localRotation = Quaternion.identity;
        SetLayerRecursively(root, LayerMask.NameToLayer("UI"));
        return canvas;
    }

    static void SetLayerRecursively(GameObject root, int layer)
    {
        if (!root || layer < 0) return;
        root.layer = layer;
        foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
    }

    static TMP_Text EnsureLabel(Transform parent, TMP_Text template, string name, string value,
        Vector2 position, Vector2 size, float fontSize)
    {
        TMP_Text label = parent.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == name);
        if (!label) label = UnityEngine.Object.Instantiate(template, parent);
        label.name = name;
        label.text = value;
        label.fontSize = fontSize;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        RectTransform rect = label.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return label;
    }

    static Button EnsureButton(Transform parent, Button template, string name, string value, Vector2 position)
    {
        Button button = parent.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == name);
        bool created = !button;
        if (created) button = UnityEngine.Object.Instantiate(template, parent);
        button.name = name;
        button.onClick = new Button.ButtonClickedEvent();
        button.interactable = true;
        if (created)
        {
            RectTransform rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(360, 65);
        }
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label)
        {
            label.text = value;
            if (created) label.fontSize = 26;
            label.raycastTarget = false;
        }
        return button;
    }

    static void EnsureIpClearButton(TMP_InputField input, Button template)
    {
        if (!input) return;
        Button button = input.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "ClearIpButton");
        if (!button) button = UnityEngine.Object.Instantiate(template, input.transform);
        button.name = "ClearIpButton";
        button.onClick = new Button.ButtonClickedEvent();
        button.interactable = true;
        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = new Vector2(1, 0);
        rect.anchorMax = new Vector2(1, 1);
        rect.pivot = new Vector2(1, 0.5f);
        rect.offsetMin = new Vector2(-80, 4);
        rect.offsetMax = new Vector2(-4, -4);
        rect.localScale = Vector3.one;
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label)
        {
            label.text = "清空";
            label.enableAutoSizing = true;
            label.fontSizeMin = 12;
            label.fontSizeMax = 22;
            label.raycastTarget = false;
        }
        if (!input.GetComponent<IpInputEditButtons>()) input.gameObject.AddComponent<IpInputEditButtons>();
    }

    static void CreateOrUpdateOpenSettingsCanvas(Scene scene, Canvas settingsCanvas, Button template)
    {
        Canvas[] candidates = Resources.FindObjectsOfTypeAll<Canvas>().Where(c =>
            c.gameObject.scene == scene && c.name == "Open Settings Canvas").ToArray();
        Canvas preferred = candidates.FirstOrDefault(c =>
            c.transform.parent && c.transform.parent.name == "TeleoperationUIRoot") ??
            candidates.FirstOrDefault(c => c.transform.parent) ?? candidates.FirstOrDefault();
        GameObject root = preferred ? preferred.gameObject : null;
        bool createdRoot = !root;
        if (!root)
            root = new GameObject("Open Settings Canvas", typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));

        // Remove stale copies created by older versions which only searched the
        // scene root. The user-parented canvas is preferred and retained.
        foreach (Canvas duplicate in candidates)
            if (duplicate && duplicate.gameObject != root)
                UnityEngine.Object.DestroyImmediate(duplicate.gameObject);

        RectTransform rect = root.GetComponent<RectTransform>();
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = settingsCanvas.worldCamera;
        if (createdRoot)
        {
            rect.sizeDelta = new Vector2(800, 110);
            GeminiWebRtcReceiver receiver = Resources.FindObjectsOfTypeAll<GeminiWebRtcReceiver>().FirstOrDefault(r =>
                r.gameObject.scene == scene && r.name == "GeminiWebRtcReceiver");
            Renderer screen = receiver ? receiver.VideoScreen : null;
            if (screen)
            {
                MeshFilter mesh = screen.GetComponent<MeshFilter>();
                Vector3 bottomCenter = mesh && mesh.sharedMesh
                    ? screen.transform.TransformPoint(new Vector3(mesh.sharedMesh.bounds.center.x,
                        mesh.sharedMesh.bounds.min.y, mesh.sharedMesh.bounds.center.z))
                    : new Vector3(screen.bounds.center.x, screen.bounds.min.y, screen.bounds.center.z);
                rect.position = bottomCenter - screen.transform.up * 0.15f - screen.transform.forward * 0.02f;
                rect.rotation = screen.transform.rotation;
                rect.localScale = Vector3.one * 0.0015f;
            }
        }

        Button button = root.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "OpenSettingsButton");
        if (!button) button = UnityEngine.Object.Instantiate(template, rect);
        button.name = "OpenSettingsButton";
        button.onClick = new Button.ButtonClickedEvent();
        RectTransform buttonRect = (RectTransform)button.transform;
        buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);
        buttonRect.anchoredPosition = new Vector2(-200, 0);
        buttonRect.sizeDelta = new Vector2(300, 76);
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label)
        {
            label.text = "打开设置";
            label.enableAutoSizing = false;
            label.fontSize = 26;
            label.raycastTarget = false;
        }

        Button robotButton = root.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.name == "OpenRobotControlEntryButton");
        if (!robotButton) robotButton = UnityEngine.Object.Instantiate(template, rect);
        robotButton.name = "OpenRobotControlEntryButton";
        robotButton.onClick = new Button.ButtonClickedEvent();
        RectTransform robotButtonRect = (RectTransform)robotButton.transform;
        robotButtonRect.anchorMin = robotButtonRect.anchorMax = new Vector2(0.5f, 0.5f);
        robotButtonRect.pivot = new Vector2(0.5f, 0.5f);
        robotButtonRect.anchoredPosition = new Vector2(200, 0);
        robotButtonRect.sizeDelta = new Vector2(300, 76);
        TMP_Text robotLabel = robotButton.GetComponentInChildren<TMP_Text>(true);
        if (robotLabel)
        {
            robotLabel.text = "机械臂控制";
            robotLabel.enableAutoSizing = false;
            robotLabel.fontSize = 26;
            robotLabel.raycastTarget = false;
        }
        root.SetActive(false);
        EditorUtility.SetDirty(root);
    }
}
