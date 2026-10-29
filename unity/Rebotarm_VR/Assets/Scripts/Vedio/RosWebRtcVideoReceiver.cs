using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Unity.WebRTC;

// Unity WebRTC 3.0.0-pre.6. One instance per scene.
public sealed class RosWebRtcVideoReceiver : MonoBehaviour
{
    public string offerUrl = "http://192.168.3.132:8080/offer";
    public Renderer screenRenderer;
    public bool flipVertical;
    public string status = "Idle";
    RTCPeerConnection peer;
    VideoStreamTrack video;
    Material material;
    Material originalMaterial;
    Texture originalTexture;
    string textureProperty;
    UnityWebRequest request;
    bool stopping;
    [Serializable] class Description { public string sdp; public string type; }

    IEnumerator Start()
    {
        if (screenRenderer == null) { Fail("Assign VideoScreen Renderer"); yield break; }
        originalMaterial = screenRenderer.sharedMaterial;
        material = screenRenderer.material;
        textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
        originalTexture = material.GetTexture(textureProperty);
        Debug.Log("[ROS VIDEO] Graphics API: " + SystemInfo.graphicsDeviceType);
        StartCoroutine(WebRTC.Update());
        var config = new RTCConfiguration { iceServers = new RTCIceServer[0] };
        peer = new RTCPeerConnection(ref config);
        peer.OnConnectionStateChange = state => {
            if (stopping || this == null || !isActiveAndEnabled || material == null)
                return;
            status = state.ToString(); Debug.Log("[ROS VIDEO] " + status);
            if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Disconnected)
                material.SetTexture(textureProperty, originalTexture);
        };
        peer.OnTrack = e => {
            if (stopping || this == null || !isActiveAndEnabled)
                return;
            if (e.Track is VideoStreamTrack track) {
                video = track;
                video.OnVideoReceived += texture => {
                    if (stopping || this == null || !isActiveAndEnabled ||
                        screenRenderer == null || material == null || texture == null)
                        return;
                    material.SetTexture(textureProperty, texture);
                    material.SetTextureScale(textureProperty, new Vector2(1, flipVertical ? -1 : 1));
                    material.SetTextureOffset(textureProperty, new Vector2(0, flipVertical ? 1 : 0));
                    Debug.Log($"[ROS VIDEO] Texture {texture.width}x{texture.height}");
                };
            }
        };
        var transceiver = peer.AddTransceiver(TrackKind.Video);
        transceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;
        status = "Creating offer";
        var offer = peer.CreateOffer(); yield return offer;
        if (offer.IsError) { Fail(offer.Error.message); yield break; }
        var description = offer.Desc;
        var local = peer.SetLocalDescription(ref description); yield return local;
        if (local.IsError) { Fail(local.Error.message); yield break; }
        // Non-trickle ICE: include all candidates in the SDP posted to aiortc.
        float deadline = Time.realtimeSinceStartup + 15;
        while (peer.GatheringState != RTCIceGatheringState.Complete) {
            if (Time.realtimeSinceStartup > deadline) { Fail("ICE gathering timeout"); yield break; }
            yield return null;
        }
        string json = JsonUtility.ToJson(new Description { type = "offer", sdp = peer.LocalDescription.sdp });
        request = new UnityWebRequest(offerUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = 25;
        status = "Requesting answer";
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) {
            Fail(request.error + " " + request.downloadHandler.text); request.Dispose(); request = null; yield break;
        }
        var answer = JsonUtility.FromJson<Description>(request.downloadHandler.text);
        request.Dispose(); request = null;
        if (answer == null || answer.type != "answer" || string.IsNullOrEmpty(answer.sdp)) {
            Fail("Invalid SDP answer"); yield break;
        }
        Debug.Log("[ROS VIDEO] SDP answer (codec negotiation):\n" + answer.sdp);
        var remoteDescription = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answer.sdp };
        var remote = peer.SetRemoteDescription(ref remoteDescription); yield return remote;
        if (remote.IsError) Fail(remote.Error.message);
    }
    void Fail(string message) { status = "Error: " + message; Debug.LogError("[ROS VIDEO] " + status); }
    void OnDisable()
    {
        stopping = true;
        StopAllCoroutines();
        if (request != null) { request.Abort(); request.Dispose(); request = null; }
        if (peer != null) { peer.OnTrack = null; peer.OnConnectionStateChange = null; peer.Close(); }
        // Scene teardown may have already destroyed the screen's Renderer.
        if (screenRenderer != null && material != null)
            screenRenderer.sharedMaterial = originalMaterial;
        video?.Dispose(); video = null;
        peer?.Dispose(); peer = null;
        if (material != null) { Destroy(material); material = null; }
        status = "Stopped (reload scene to reconnect)";
    }
}
