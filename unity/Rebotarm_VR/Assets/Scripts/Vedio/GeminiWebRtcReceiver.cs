using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

public class GeminiWebRtcReceiver : MonoBehaviour
{
    [Header("WHEP 信令")]
    [SerializeField] protected string whepUrl = "http://192.168.3.140:8889/gemini/whep";
    [SerializeField] private bool autoReconnect = true;
    [SerializeField, Min(0.5f)] private float reconnectDelaySeconds = 2f;

    [Header("视频输出")]
    [SerializeField] private Renderer videoScreen;
    [SerializeField] private bool flipVertically;

    [Header("运行状态（只读）")]
    [SerializeField] private string connectionStatus = "Idle";
    [SerializeField] private string negotiatedCodec = "-";
    [SerializeField] private string resolution = "-";
    [SerializeField] private double receivedFps;
    [SerializeField] private ulong droppedFrames;
    [SerializeField] private double jitterMilliseconds;
    [SerializeField] private double averageJitterBufferMilliseconds;
    [SerializeField] private string decoderImplementation = "-";
    [SerializeField] private bool powerEfficientDecoder;

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack videoTrack;
    private Coroutine runCoroutine;
    private Material runtimeMaterial;
    private Material originalMaterial;
    private Texture originalTexture;
    private string textureProperty;
    private bool stopRequested;
    private bool reconnectRequested;
    private bool manualReconnectRequested;
    private string whepSessionUrl;

    public string ConnectionStatus => connectionStatus;
    public string NegotiatedCodec => negotiatedCodec;
    public string Resolution => resolution;
    public double ReceivedFps => receivedFps;
    public ulong DroppedFrames => droppedFrames;
    public string WhepUrl => whepUrl;
    public Renderer VideoScreen => videoScreen;

    // Runtime settings UI changes only the host, preserving path and ports.
    public void SetHostAndReconnect(string host)
    {
        whepUrl = new UriBuilder(whepUrl) { Host = host }.Uri.AbsoluteUri;
        RequestReconnect();
    }

    public void RequestReconnect()
    {
        if (!isActiveAndEnabled) return;
        manualReconnectRequested = true;
        reconnectRequested = true;
        if (runCoroutine == null)
            runCoroutine = StartCoroutine(RunConnectionLoop());
    }

    protected virtual void OnEnable()
    {
        SharedWebRtcUpdate.Acquire();
        stopRequested = false;
        reconnectRequested = false;
        PrepareMaterial();
        runCoroutine = StartCoroutine(RunConnectionLoop());
    }

    protected virtual void OnDisable()
    {
        stopRequested = true;
        if (runCoroutine != null)
        {
            StopCoroutine(runCoroutine);
            runCoroutine = null;
        }

        CleanupPeerConnection();
        RestoreMaterial();
        SharedWebRtcUpdate.Release();
        connectionStatus = "Disabled";
    }

    private IEnumerator RunConnectionLoop()
    {
        // Unity.WebRTC 3.0.0-pre.8 initializes its synchronization context after
        // scene objects receive OnEnable.  Creating a peer in the same frame can
        // therefore deadlock Enter Play Mode inside WebRTC.Sync().
        yield return null;

        while (!stopRequested)
        {
            manualReconnectRequested = false;
            reconnectRequested = false;
            ResetStatistics();
            yield return ConnectOnce();

            while (!stopRequested && !reconnectRequested && peerConnection != null)
            {
                yield return UpdateStatistics();
                yield return new WaitForSecondsRealtime(1f);
            }

            yield return DeleteWhepSession();
            CleanupPeerConnection();

            if (stopRequested || (!autoReconnect && !manualReconnectRequested))
            {
                break;
            }

            connectionStatus = "Reconnecting";
            yield return new WaitForSecondsRealtime(reconnectDelaySeconds);
        }

        runCoroutine = null;

    }

    private IEnumerator ConnectOnce()
    {
        if (videoScreen == null)
        {
            Fail("未配置 VideoScreen Renderer");
            yield break;
        }

        connectionStatus = "Connecting";
        Debug.Log($"[Gemini WebRTC] 正在连接 {whepUrl}");

        peerConnection = new RTCPeerConnection();
        peerConnection.OnConnectionStateChange = OnConnectionStateChanged;
        peerConnection.OnTrack = OnTrack;

        RTCRtpTransceiver transceiver = peerConnection.AddTransceiver(
            TrackKind.Video,
            new RTCRtpTransceiverInit { direction = RTCRtpTransceiverDirection.RecvOnly });

        RTCRtpCodecCapability[] h264Codecs = RTCRtpSender.GetCapabilities(TrackKind.Video).codecs
            .Where(codec => string.Equals(codec.mimeType, "video/H264", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (h264Codecs.Length == 0)
        {
            Fail("Unity WebRTC 当前平台未提供 H.264 能力");
            yield break;
        }

        RTCErrorType codecError = transceiver.SetCodecPreferences(h264Codecs);
        if (codecError != RTCErrorType.None)
        {
            Fail($"设置 H.264 优先级失败：{codecError}");
            yield break;
        }

        RTCSessionDescriptionAsyncOperation offerOperation = peerConnection.CreateOffer();
        yield return offerOperation;
        if (offerOperation.IsError)
        {
            Fail($"创建 Offer 失败：{offerOperation.Error.message}");
            yield break;
        }

        RTCSessionDescription offer = offerOperation.Desc;
        RTCSetSessionDescriptionAsyncOperation localOperation =
            peerConnection.SetLocalDescription(ref offer);
        yield return localOperation;
        if (localOperation.IsError)
        {
            Fail($"设置本地 SDP 失败：{localOperation.Error.message}");
            yield break;
        }

        float iceDeadline = Time.realtimeSinceStartup + 10f;
        while (!stopRequested && peerConnection != null &&
               peerConnection.GatheringState != RTCIceGatheringState.Complete &&
               Time.realtimeSinceStartup < iceDeadline)
        {
            yield return null;
        }

        if (stopRequested || peerConnection == null)
        {
            yield break;
        }

        if (peerConnection.GatheringState != RTCIceGatheringState.Complete)
        {
            Fail("ICE 候选收集超过 10 秒");
            yield break;
        }

        string offerSdp = peerConnection.LocalDescription.sdp;
        using (UnityWebRequest request = new UnityWebRequest(whepUrl, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(offerSdp));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/sdp");
            request.SetRequestHeader("Accept", "application/sdp");
            request.timeout = 10;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Fail($"WHEP 请求失败：HTTP {request.responseCode} {request.error}");
                yield break;
            }

            string location = request.GetResponseHeader("Location");
            if (!string.IsNullOrWhiteSpace(location))
            {
                whepSessionUrl = new Uri(new Uri(whepUrl), location).AbsoluteUri;
            }

            string answerSdp = request.downloadHandler.text;
            Debug.Log($"[Gemini WebRTC] SDP Answer:\n{answerSdp}");
            if (answerSdp.IndexOf("H264/90000", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Fail("SDP Answer 未协商到 H264/90000");
                yield break;
            }

            RTCSessionDescription answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = answerSdp
            };
            RTCSetSessionDescriptionAsyncOperation remoteOperation =
                peerConnection.SetRemoteDescription(ref answer);
            yield return remoteOperation;
            if (remoteOperation.IsError)
            {
                Fail($"设置远端 SDP 失败：{remoteOperation.Error.message}");
                yield break;
            }

            negotiatedCodec = "H264/90000";
        }
    }

    private void OnTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack incomingTrack))
        {
            return;
        }

        videoTrack = incomingTrack;
        videoTrack.OnVideoReceived += texture =>
        {
            if (runtimeMaterial == null || string.IsNullOrEmpty(textureProperty))
            {
                return;
            }

            runtimeMaterial.SetTexture(textureProperty, texture);
            runtimeMaterial.SetTextureScale(textureProperty, flipVertically ? new Vector2(1f, -1f) : Vector2.one);
            runtimeMaterial.SetTextureOffset(textureProperty, flipVertically ? new Vector2(0f, 1f) : Vector2.zero);
        };
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state)
    {
        connectionStatus = state.ToString();
        Debug.Log($"[Gemini WebRTC] 连接状态：{state}");

        if (state == RTCPeerConnectionState.Disconnected ||
            state == RTCPeerConnectionState.Failed ||
            state == RTCPeerConnectionState.Closed)
        {
            reconnectRequested = true;
        }
    }

    private IEnumerator UpdateStatistics()
    {
        if (peerConnection == null || peerConnection.ConnectionState != RTCPeerConnectionState.Connected)
        {
            yield break;
        }

        RTCStatsReportAsyncOperation operation = peerConnection.GetStats();
        yield return operation;
        if (operation.IsError)
        {
            yield break;
        }

        using (RTCStatsReport report = operation.Value)
        {
            RTCInboundRTPStreamStats inbound = report.Stats.Values
                .OfType<RTCInboundRTPStreamStats>()
                .FirstOrDefault(stat => stat.kind == "video");
            if (inbound == null)
            {
                yield break;
            }

            receivedFps = inbound.framesPerSecond;
            droppedFrames = inbound.framesDropped;
            resolution = $"{inbound.frameWidth}x{inbound.frameHeight}";
            jitterMilliseconds = inbound.jitter * 1000d;
            averageJitterBufferMilliseconds = inbound.jitterBufferEmittedCount > 0
                ? inbound.jitterBufferDelay / inbound.jitterBufferEmittedCount * 1000d
                : 0d;
            decoderImplementation = string.IsNullOrWhiteSpace(inbound.decoderImplementation)
                ? "未知"
                : inbound.decoderImplementation;
            powerEfficientDecoder = inbound.powerEfficientDecoder;

            if (!string.IsNullOrWhiteSpace(inbound.codecId) &&
                report.Stats.TryGetValue(inbound.codecId, out RTCStats codecBase) &&
                codecBase is RTCCodecStats codec)
            {
                negotiatedCodec = string.IsNullOrWhiteSpace(codec.sdpFmtpLine)
                    ? codec.mimeType
                    : $"{codec.mimeType} ({codec.sdpFmtpLine})";
            }
        }
    }

    private IEnumerator DeleteWhepSession()
    {
        if (string.IsNullOrWhiteSpace(whepSessionUrl))
        {
            yield break;
        }

        using (UnityWebRequest request = UnityWebRequest.Delete(whepSessionUrl))
        {
            request.timeout = 3;
            yield return request.SendWebRequest();
        }

        whepSessionUrl = null;
    }

    private void PrepareMaterial()
    {
        if (videoScreen == null)
        {
            return;
        }

        originalMaterial = videoScreen.sharedMaterial;
        runtimeMaterial = new Material(originalMaterial);
        videoScreen.sharedMaterial = runtimeMaterial;
        if (runtimeMaterial.HasProperty("_BaseMap"))
        {
            textureProperty = "_BaseMap";
        }
        else if (runtimeMaterial.HasProperty("_MainTex"))
        {
            textureProperty = "_MainTex";
        }
        else
        {
            Debug.LogError("[Gemini WebRTC] VideoScreen 材质没有 _BaseMap 或 _MainTex 属性");
            textureProperty = null;
            return;
        }

        originalTexture = runtimeMaterial.GetTexture(textureProperty);
    }

    private void RestoreMaterial()
    {
        if (runtimeMaterial == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(textureProperty))
        {
            runtimeMaterial.SetTexture(textureProperty, originalTexture);
            runtimeMaterial.SetTextureScale(textureProperty, Vector2.one);
            runtimeMaterial.SetTextureOffset(textureProperty, Vector2.zero);
        }

        if (videoScreen != null && videoScreen.sharedMaterial == runtimeMaterial)
            videoScreen.sharedMaterial = originalMaterial;
        Destroy(runtimeMaterial);
        runtimeMaterial = null;
    }

    private void CleanupPeerConnection()
    {
        if (videoTrack != null)
        {
            videoTrack.Dispose();
            videoTrack = null;
        }

        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
    }

    private void ResetStatistics()
    {
        negotiatedCodec = "-";
        resolution = "-";
        receivedFps = 0d;
        droppedFrames = 0;
        jitterMilliseconds = 0d;
        averageJitterBufferMilliseconds = 0d;
        decoderImplementation = "-";
        powerEfficientDecoder = false;
    }

    private void Fail(string message)
    {
        connectionStatus = "Failed";
        reconnectRequested = true;
        Debug.LogError($"[Gemini WebRTC] {message}");
    }
}
