using UnityEngine;
using Unity.WebRTC;

// WebRTC.Update updates all video tracks; run it once for both receivers.
public sealed class SharedWebRtcUpdate : MonoBehaviour
{
    private static SharedWebRtcUpdate instance;
    private static int users;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { instance = null; users = 0; }

    public static void Acquire()
    {
        if (instance == null)
        {
            var host = new GameObject("Shared WebRTC Update");
            instance = host.AddComponent<SharedWebRtcUpdate>();
            DontDestroyOnLoad(host);
            instance.StartCoroutine(WebRTC.Update());
        }
        users++;
    }

    public static void Release()
    {
        users = Mathf.Max(0, users - 1);
        if (users == 0 && instance != null)
        {
            instance.StopAllCoroutines();
            Destroy(instance.gameObject);
            instance = null;
        }
    }
}
