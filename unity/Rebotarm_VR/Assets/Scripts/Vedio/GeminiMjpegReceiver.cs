using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public sealed class GeminiMjpegReceiver : MonoBehaviour
{
    [Header("Windows streamer")]
    [SerializeField] private string host = "192.168.1.100";
    [SerializeField] private int port = 5000;
    [Tooltip("Leave empty only on a trusted private LAN.")]
    [SerializeField] private string accessToken = "";

    [Header("Assign one target")]
    [SerializeField] private RawImage rawImage;
    [SerializeField] private Renderer targetRenderer;

    private readonly object frameLock = new object();
    private CancellationTokenSource cancellation;
    private TcpClient activeClient;
    private byte[] pendingJpeg;
    private Texture2D texture;
    private long receivedSequence;

    private void Start()
    {
        texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        texture.wrapMode = TextureWrapMode.Clamp;

        if (rawImage != null)
            rawImage.texture = texture;
        if (targetRenderer != null)
            targetRenderer.material.mainTexture = texture;

        cancellation = new CancellationTokenSource();
        _ = ReceiveWithReconnectAsync(cancellation.Token);
    }

    private void Update()
    {
        byte[] jpeg = null;
        lock (frameLock)
        {
            if (pendingJpeg != null)
            {
                jpeg = pendingJpeg;
                pendingJpeg = null;
            }
        }

        // Unity texture APIs must run on the main thread.
        if (jpeg != null && !texture.LoadImage(jpeg, false))
            Debug.LogWarning("Gemini JPEG decode failed.");
    }

    private async Task ReceiveWithReconnectAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                activeClient = client;
                await client.ConnectAsync(host, port);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    byte[] tokenBytes = Encoding.UTF8.GetBytes(accessToken);
                    await client.GetStream().WriteAsync(
                        tokenBytes, 0, tokenBytes.Length, token);
                }
                Debug.Log($"Connected to Gemini streamer {host}:{port}");
                await ReceiveFramesAsync(client.GetStream(), token);
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                    Debug.LogWarning($"Gemini stream disconnected: {exception.Message}");
            }
            finally
            {
                activeClient?.Close();
                activeClient = null;
            }

            if (!token.IsCancellationRequested)
                await Task.Delay(1000, token);
        }
    }

    private async Task ReceiveFramesAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[24];

        while (!token.IsCancellationRequested)
        {
            await ReadExactlyAsync(stream, header, token);
            if (header[0] != (byte)'O' || header[1] != (byte)'R' ||
                header[2] != (byte)'B' || header[3] != (byte)'B')
                throw new InvalidDataException("Invalid Gemini stream header.");

            int payloadLength = ReadInt32BigEndian(header, 4);
            int width = ReadInt32BigEndian(header, 8);
            int height = ReadInt32BigEndian(header, 12);
            long sequence = ReadInt64BigEndian(header, 16);

            if (payloadLength <= 0 || payloadLength > 16 * 1024 * 1024 ||
                width <= 0 || height <= 0)
                throw new InvalidDataException("Invalid Gemini frame dimensions.");

            var jpeg = new byte[payloadLength];
            await ReadExactlyAsync(stream, jpeg, token);

            // Overwrite an unrendered frame instead of building latency.
            lock (frameLock)
                pendingJpeg = jpeg;
            receivedSequence = sequence;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer, offset, buffer.Length - offset, token);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
    }

    private static int ReadInt32BigEndian(byte[] bytes, int offset)
    {
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(bytes, offset));
    }

    private static long ReadInt64BigEndian(byte[] bytes, int offset)
    {
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt64(bytes, offset));
    }

    private void OnDestroy()
    {
        cancellation?.Cancel();
        activeClient?.Close();
        cancellation?.Dispose();
        if (texture != null)
            Destroy(texture);
    }
}
