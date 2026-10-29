using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class GeminiWebRtcSetup
{
    private const string ScenePath = "Assets/Scenes/Teleoperation.unity";
    private const string ReceiverName = "GeminiWebRtcReceiver";
    private const string VideoScreenName = "VideoScreen";

    [MenuItem("Gemini 305/配置 WebRTC 接收器")]
    public static void Configure()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        GameObject videoScreen = GameObject.Find(VideoScreenName);
        if (videoScreen == null)
        {
            throw new MissingReferenceException($"场景中未找到 {VideoScreenName}");
        }

        Renderer renderer = videoScreen.GetComponent<Renderer>();
        if (renderer == null)
        {
            throw new MissingComponentException($"{VideoScreenName} 没有 Renderer");
        }

        GameObject receiverObject = GameObject.Find(ReceiverName);
        if (receiverObject == null)
        {
            receiverObject = new GameObject(ReceiverName);
        }

        receiverObject.SetActive(true);
        GeminiWebRtcReceiver receiver = receiverObject.GetComponent<GeminiWebRtcReceiver>();
        if (receiver == null)
        {
            receiver = receiverObject.AddComponent<GeminiWebRtcReceiver>();
        }

        SerializedObject serializedReceiver = new SerializedObject(receiver);
        serializedReceiver.FindProperty("whepUrl").stringValue =
            "http://192.168.3.140:8889/gemini/whep";
        serializedReceiver.FindProperty("videoScreen").objectReferenceValue = renderer;
        serializedReceiver.ApplyModifiedPropertiesWithoutUndo();

        // 保留 MJPEG 为默认工作链路；WebRTC 物体配置完成但默认关闭。
        receiverObject.SetActive(false);
        EditorUtility.SetDirty(receiverObject);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.Android.optimizedFramePacing = false;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        AssetDatabase.SaveAssets();

        Debug.Log("Gemini WebRTC 配置完成：新接收器默认关闭，MJPEG 链路保持不变。");
    }
}
