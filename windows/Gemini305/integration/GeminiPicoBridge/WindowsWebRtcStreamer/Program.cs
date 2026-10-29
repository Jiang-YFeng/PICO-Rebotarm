using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Orbbec;

const int width = 1280;
const int height = 720;
const int fps = 30;

string lanAddress = args.Length > 0 ? args[0] : "192.168.3.140";
string encoderName = args.Length > 1 ? args[1] : "h264_qsv";

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Process? mediaMtx = null;
Process? ffmpeg = null;
Pipeline? pipeline = null;
string? runtimeConfigPath = null;

try
{
    string ffmpegPath = ToolLocator.Find("ffmpeg.exe", "Gyan.FFmpeg.Shared*");
    string mediaMtxPath = ToolLocator.Find("mediamtx.exe", "bluenviron.mediamtx*");
    string configTemplatePath = Path.Combine(AppContext.BaseDirectory, "mediamtx.yml");
    runtimeConfigPath = Path.Combine(
        Path.GetTempPath(), $"gemini-mediamtx-{Environment.ProcessId}.yml");
    string configText = File.ReadAllText(configTemplatePath)
        .Replace("__LAN_ADDRESS__", lanAddress, StringComparison.Ordinal);
    File.WriteAllText(runtimeConfigPath, configText);

    Console.WriteLine($"FFmpeg:  {ffmpegPath}");
    Console.WriteLine($"MediaMTX: {mediaMtxPath}");
    Console.WriteLine($"Encoder:  {encoderName}");

    mediaMtx = StartLoggedProcess(mediaMtxPath, $"\"{runtimeConfigPath}\"", "MediaMTX");
    await WaitForTcpPortAsync("127.0.0.1", 8554, TimeSpan.FromSeconds(10), shutdown.Token);

    string ffmpegArguments = BuildFfmpegArguments(encoderName);
    ffmpeg = StartLoggedProcess(ffmpegPath, ffmpegArguments, "FFmpeg", redirectInput: true);

    var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    Task encoderTask = FeedEncoderAsync(ffmpeg, frames.Reader, shutdown.Token);

    pipeline = new Pipeline();
    using var config = new Config();
    using var profiles = pipeline.GetStreamProfileList(SensorType.OB_SENSOR_COLOR);
    using var colorProfile = profiles.GetVideoStreamProfile(
        width, height, Format.OB_FORMAT_MJPG, fps);
    config.EnableStream(colorProfile);
    pipeline.Start(config);

    Console.WriteLine($"Gemini 305: {width}x{height} @ {fps} FPS, MJPEG input");
    Console.WriteLine($"WHEP URL: http://{lanAddress}:8889/gemini/whep");
    Console.WriteLine("Browser test: http://" + lanAddress + ":8889/gemini");
    Console.WriteLine("Press Ctrl+C to stop.");

    long captured = 0;
    long queued = 0;
    var reportTimer = Stopwatch.StartNew();

    while (!shutdown.IsCancellationRequested)
    {
        if (ffmpeg.HasExited)
            throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}.");
        if (mediaMtx.HasExited)
            throw new InvalidOperationException($"MediaMTX exited with code {mediaMtx.ExitCode}.");

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
        captured++;
        if (frames.Writer.TryWrite(jpeg))
            queued++;

        if (reportTimer.Elapsed >= TimeSpan.FromSeconds(2))
        {
            double seconds = reportTimer.Elapsed.TotalSeconds;
            Console.WriteLine($"Capture {captured / seconds:F1} FPS, " +
                              $"encoder queue {queued / seconds:F1} FPS, " +
                              $"JPEG {jpeg.Length / 1024.0:F0} KiB");
            captured = 0;
            queued = 0;
            reportTimer.Restart();
        }
    }

    frames.Writer.TryComplete();
    await encoderTask;
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
    try { pipeline?.Stop(); } catch { }
    pipeline?.Dispose();
    StopProcess(ffmpeg);
    StopProcess(mediaMtx);
    if (runtimeConfigPath != null)
    {
        try { File.Delete(runtimeConfigPath); } catch { }
    }
}

static string BuildFfmpegArguments(string encoder)
{
    string common = "-hide_banner -loglevel warning " +
                    "-fflags nobuffer -flags low_delay " +
                    "-f image2pipe -framerate 30 -vcodec mjpeg -i pipe:0 -an " +
                    "-vf format=nv12 ";

    string codec = encoder switch
    {
        "h264_qsv" => "-c:v h264_qsv -profile:v baseline -level:v 3.1 " +
                      "-low_power 1 -look_ahead 0 -bf 0 -g 30 -keyint_min 30 " +
                      "-b:v 3M -maxrate 3M -bufsize 3M ",
        "libx264" => "-c:v libx264 -profile:v baseline -level:v 3.1 " +
                     "-preset ultrafast -tune zerolatency -bf 0 -g 30 -keyint_min 30 " +
                     "-sc_threshold 0 -b:v 3M -maxrate 3M -bufsize 3M ",
        _ => throw new ArgumentException(
            $"Unsupported encoder '{encoder}'. Use h264_qsv or libx264.")
    };

    return common + codec +
           "-fps_mode passthrough -f rtsp -rtsp_transport tcp " +
           "rtsp://127.0.0.1:8554/gemini";
}

static async Task FeedEncoderAsync(
    Process encoder,
    ChannelReader<byte[]> frames,
    CancellationToken cancellationToken)
{
    Stream input = encoder.StandardInput.BaseStream;
    try
    {
        await foreach (byte[] jpeg in frames.ReadAllAsync(cancellationToken))
            await input.WriteAsync(jpeg, cancellationToken);
    }
    catch (IOException) when (encoder.HasExited)
    {
        throw new InvalidOperationException(
            $"FFmpeg input closed unexpectedly (exit code {encoder.ExitCode}).");
    }
    finally
    {
        try { await input.FlushAsync(CancellationToken.None); } catch { }
        try { input.Close(); } catch { }
    }
}

static Process StartLoggedProcess(
    string fileName,
    string arguments,
    string prefix,
    bool redirectInput = false)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = redirectInput,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[{prefix}] {e.Data}"); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[{prefix}] {e.Data}"); };
    if (!process.Start())
        throw new InvalidOperationException($"Failed to start {prefix}.");
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

static async Task WaitForTcpPortAsync(
    string host,
    int port,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < timeout)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return;
        }
        catch (SocketException)
        {
            await Task.Delay(100, cancellationToken);
        }
    }
    throw new TimeoutException($"Timed out waiting for {host}:{port}.");
}

static void StopProcess(Process? process)
{
    if (process == null)
        return;
    try
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        process.WaitForExit(3000);
    }
    catch { }
    process.Dispose();
}

static class ToolLocator
{
    public static string Find(string executableName, string wingetPackagePattern)
    {
        string? fromPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), executableName))
            .FirstOrDefault(File.Exists);
        if (fromPath != null)
            return fromPath;

        string packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packageRoot))
        {
            string? fromWinget = Directory
                .EnumerateDirectories(packageRoot, wingetPackagePattern)
                .SelectMany(directory => Directory.EnumerateFiles(
                    directory, executableName, SearchOption.AllDirectories))
                .FirstOrDefault();
            if (fromWinget != null)
                return fromWinget;
        }

        throw new FileNotFoundException(
            $"Could not find {executableName}. Install it with winget and restart the shell.");
    }
}
