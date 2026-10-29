// Uses the tested H.264/WHEP implementation, with an independent connection,
// video track and material for the wrist camera.
public sealed class UsbWebRtcReceiver : GeminiWebRtcReceiver
{
    private void Reset()
    {
        whepUrl = "http://192.168.3.140:8890/wrist/whep";
    }
}
