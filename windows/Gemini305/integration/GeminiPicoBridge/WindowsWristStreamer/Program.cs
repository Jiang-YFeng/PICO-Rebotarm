using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// Arguments: LAN IP, encoder, DirectShow device, rotation, optional test duration.
// rotation: none | cw | ccw. Input is 640x360 YUY2 at 30 FPS.
string address = args.ElementAtOrDefault(0) ?? "192.168.3.140";
string encoder = args.ElementAtOrDefault(1) ?? "h264_qsv";
string device = args.ElementAtOrDefault(2) ?? "USB Camera";
string rotation = args.ElementAtOrDefault(3) ?? "none";
if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
    throw new ArgumentException("Provide a local IPv4 address.");
if (encoder is not ("h264_qsv" or "libx264"))
    throw new ArgumentException("Encoder must be h264_qsv or libx264.");
if (rotation is not ("none" or "cw" or "ccw"))
    throw new ArgumentException("Rotation must be none, cw or ccw.");

using var shutdown = new CancellationTokenSource();
if (args.Length > 4) shutdown.CancelAfter(TimeSpan.FromSeconds(int.Parse(args[4])));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
Process? server = null;
Process? video = null;
string config = Path.Combine(Path.GetTempPath(), $"wrist-mediamtx-{Environment.ProcessId}.yml");
try
{
    // Check ports before spawning; never accidentally publish to another server.
    foreach (var endpoint in new[] { (IPAddress.Loopback, 8555), (ip, 8890) })
    {
        var probe = new TcpListener(endpoint.Item1, endpoint.Item2);
        try { probe.Start(); } finally { probe.Stop(); }
    }
    using (var probe = new UdpClient(new IPEndPoint(ip, 8190))) { }
    File.WriteAllText(config, $"""
        logLevel: info
        logDestinations: [stdout]
        api: false
        metrics: false
        pprof: false
        playback: false
        rtmp: false
        hls: false
        srt: false
        moq: false
        rtsp: true
        rtspTransports: [tcp]
        rtspAddress: 127.0.0.1:8555
        webrtc: true
        webrtcAddress: {address}:8890
        webrtcEncryption: false
        webrtcAllowOrigins: ["*"]
        webrtcLocalUDPAddress: {address}:8190
        webrtcLocalTCPAddress: ""
        webrtcIPsFromInterfaces: false
        webrtcAdditionalHosts: [{address}]
        paths:
          wrist:
            source: publisher
        """);
    server = Start(FindTool("mediamtx.exe", "bluenviron.mediamtx*"), new[] { config }, "Wrist MediaMTX");
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        shutdown.Token.ThrowIfCancellationRequested();
        if (server.HasExited) throw new Exception("Wrist MediaMTX exited before ready.");
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(IPAddress.Loopback, 8555, shutdown.Token); break; }
        catch (SocketException) when (deadline.Elapsed.TotalSeconds < 10)
        { await Task.Delay(100, shutdown.Token); }
    }
    string filter = rotation switch
    {
        "cw" => "transpose=clock,format=nv12",
        "ccw" => "transpose=cclock,format=nv12",
        _ => "format=nv12"
    };
    var options = new List<string>
    {
        "-hide_banner", "-loglevel", "info", "-nostdin",
        "-f", "dshow", "-rtbufsize", "2M", "-video_size", "640x360",
        "-framerate", "30", "-pixel_format", "yuyv422", "-i", $"video={device}",
        "-an", "-vf", filter, "-c:v", encoder, "-profile:v", "baseline",
        "-level:v", "3.1", "-bf", "0", "-g", "30", "-keyint_min", "30",
        "-b:v", "1200k", "-maxrate", "1200k", "-bufsize", "600k"
    };
    options.AddRange(encoder == "h264_qsv"
        ? new[] { "-low_power", "1", "-look_ahead", "0" }
        : new[] { "-preset", "ultrafast", "-tune", "zerolatency", "-sc_threshold", "0" });
    options.AddRange(new[] { "-fps_mode", "passthrough", "-f", "rtsp", "-rtsp_transport", "tcp", "rtsp://127.0.0.1:8555/wrist" });
    video = Start(FindTool("ffmpeg.exe", "Gyan.FFmpeg.Shared*"), options, "Wrist FFmpeg");
    Console.WriteLine($"USB Camera: 640x360 @ 30, YUY2 -> H264; rotation={rotation}");
    Console.WriteLine($"WHEP: http://{address}:8890/wrist/whep");
    Console.WriteLine($"Browser: http://{address}:8890/wrist");
    Console.WriteLine("Press Ctrl+C to stop.");
    while (!shutdown.IsCancellationRequested)
    {
        if (video.HasExited || server.HasExited) throw new Exception("Wrist child process exited; inspect logs above.");
        await Task.Delay(250, shutdown.Token);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
finally
{
    Stop(video);
    Stop(server);
    try { File.Delete(config); } catch { }
}

static Process Start(string executable, IEnumerable<string> arguments, string label)
{
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    var process = new Process { StartInfo = info };
    process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[{label}] {e.Data}"); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[{label}] {e.Data}"); };
    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
    return process;
}
static void Stop(Process? process)
{
    if (process == null) return;
    try { if (!process.HasExited) process.Kill(true); process.WaitForExit(3000); } catch { }
    process.Dispose();
}
static string FindTool(string name, string package)
{
    foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        string candidate = Path.Combine(dir.Trim('"'), name);
        if (File.Exists(candidate)) return candidate;
    }
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
    if (Directory.Exists(root))
        foreach (string dir in Directory.EnumerateDirectories(root, package))
        {
            string? candidate = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (candidate != null) return candidate;
        }
    throw new FileNotFoundException($"Cannot find {name}.");
}
