using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Orbbec;

// 画面分辨率、帧率（1280x720，640x480）
const int width = 1280;
const int height = 720;
const int fps = 20;

int port = args.Length > 0 && int.TryParse(args[0], out int requestedPort)
    ? requestedPort
    : 5000;
string accessToken = args.Length > 1 && args[1] != "-" ? args[1] : string.Empty;
IPAddress bindAddress = args.Length > 2 && IPAddress.TryParse(args[2], out IPAddress? requestedAddress)
    ? requestedAddress
    : IPAddress.Loopback;

if (accessToken.Length is > 0 and < 12)
{
    Console.Error.WriteLine("Token must contain at least 12 characters, or use - to disable authentication.");
    Environment.ExitCode = 2;
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var frameStore = new LatestFrameStore();
Task serverTask = RunServerAsync(bindAddress, port, accessToken, frameStore, shutdown.Token);

Pipeline? pipeline = null;
try
{
    pipeline = new Pipeline();
    using var config = new Config();
    using var profiles = pipeline.GetStreamProfileList(SensorType.OB_SENSOR_COLOR);
    using var colorProfile = profiles.GetVideoStreamProfile(
        width, height, Format.OB_FORMAT_MJPG, fps);
    config.EnableStream(colorProfile);
    pipeline.Start(config);

    Console.WriteLine($"Gemini 305: {width}x{height} @ {fps} FPS, MJPEG");
    Console.WriteLine($"TCP server: {bindAddress}:{port} " +
                      (accessToken.Length == 0 ? "(authentication disabled)" : "(token required)"));
    Console.WriteLine("Press Ctrl+C to stop.");

    long capturedFrames = 0;
    var reportTimer = Stopwatch.StartNew();

    while (!shutdown.IsCancellationRequested)
    {
        using var frameSet = pipeline.WaitForFrames(100);
        if (frameSet == null)
            continue;

        using var colorFrame = frameSet.GetColorFrame();
        if (colorFrame == null)
            continue;

        if (colorFrame.GetFormat() != Format.OB_FORMAT_MJPG)
            throw new InvalidOperationException(
                $"Expected MJPEG but camera returned {colorFrame.GetFormat()}.");

        byte[] jpeg = new byte[colorFrame.GetDataSize()];
        colorFrame.CopyData(ref jpeg);
        frameStore.Publish(jpeg, (int)colorFrame.GetWidth(), (int)colorFrame.GetHeight());
        capturedFrames++;

        if (reportTimer.Elapsed >= TimeSpan.FromSeconds(2))
        {
            Console.WriteLine($"Capture: {capturedFrames / reportTimer.Elapsed.TotalSeconds:F1} FPS, " +
                              $"latest JPEG: {jpeg.Length / 1024.0:F0} KiB");
            capturedFrames = 0;
            reportTimer.Restart();
        }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    shutdown.Cancel();
    try
    {
        pipeline?.Stop();
    }
    catch
    {
        // The pipeline may already be stopped after a device disconnect.
    }

    pipeline?.Dispose();
    await serverTask;
}

static async Task RunServerAsync(
    IPAddress bindAddress,
    int port,
    string accessToken,
    LatestFrameStore frames,
    CancellationToken cancellationToken)
{
    var listener = new TcpListener(bindAddress, port);
    listener.Start();

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            client.NoDelay = true;
            string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Console.WriteLine($"PICO connected: {endpoint}");

            try
            {
                NetworkStream stream = client.GetStream();
                bool authenticated = accessToken.Length == 0 ||
                    await AuthenticateAsync(stream, accessToken, cancellationToken);
                if (!authenticated)
                {
                    Console.WriteLine($"Rejected unauthenticated client: {endpoint}");
                    continue;
                }

                await SendFramesAsync(stream, frames, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Console.WriteLine($"PICO disconnected: {endpoint}");
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    finally
    {
        listener.Stop();
    }
}

static async Task<bool> AuthenticateAsync(
    NetworkStream stream,
    string expectedToken,
    CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));

    byte[] request = new byte[Encoding.UTF8.GetByteCount(expectedToken)];
    int offset = 0;
    while (offset < request.Length)
    {
        int count = await stream.ReadAsync(
            request.AsMemory(offset, request.Length - offset), timeout.Token);
        if (count == 0)
            return false;
        offset += count;
    }

    byte[] expected = Encoding.UTF8.GetBytes(expectedToken);
    return CryptographicOperations.FixedTimeEquals(request, expected);
}

static async Task SendFramesAsync(
    NetworkStream stream,
    LatestFrameStore frames,
    CancellationToken cancellationToken)
{
    long lastSequence = -1;
    byte[] header = new byte[24];

    while (!cancellationToken.IsCancellationRequested)
    {
        VideoFrame? frame = frames.TryGetAfter(lastSequence);
        if (frame == null)
        {
            await Task.Delay(1, cancellationToken);
            continue;
        }

        // Protocol: ASCII ORBB, payload length, width, height, sequence; integers are big-endian.
        "ORBB"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), frame.Jpeg.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), frame.Height);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16, 8), frame.Sequence);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(frame.Jpeg, cancellationToken);
        lastSequence = frame.Sequence;
    }
}

sealed record VideoFrame(byte[] Jpeg, int Width, int Height, long Sequence);

sealed class LatestFrameStore
{
    private readonly object _gate = new();
    private VideoFrame? _latest;
    private long _sequence;

    public void Publish(byte[] jpeg, int width, int height)
    {
        lock (_gate)
            _latest = new VideoFrame(jpeg, width, height, ++_sequence);
    }

    public VideoFrame? TryGetAfter(long sequence)
    {
        lock (_gate)
            return _latest is { } frame && frame.Sequence > sequence ? frame : null;
    }
}
