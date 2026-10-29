using TMPro;
using UnityEngine;

public class VideoStatsDisplay : MonoBehaviour
{
    public GeminiWebRtcReceiver mainReceiver;
    public GeminiWebRtcReceiver wristReceiver;
    public TMP_Text statsText;

    private float nextUpdate;

    void Update()
    {
        if (Time.unscaledTime < nextUpdate)
            return;

        nextUpdate = Time.unscaledTime + 0.5f;

        if (statsText != null)
        {
            statsText.text =
                GetStats("MAIN", mainReceiver) + "\n" +
                GetStats("WRIST", wristReceiver);
        }
    }

    private string GetStats(string name, GeminiWebRtcReceiver receiver)
    {
        if (receiver == null ||
            !receiver.isActiveAndEnabled ||
            receiver.ConnectionStatus != "Connected")
        {
            return $"{name}   -- x --   -- FPS";
        }

        return $"{name}   {receiver.Resolution}   " +
               $"{receiver.ReceivedFps:0} FPS";
    }
}