using System.Drawing;
using System.Text.Json;
using ScreenTask.Core.Capture;
using ScreenTask.Core.Streaming;
using ScreenTask.WebServer;

// Synthetic input only: never captures the user's desktop or changes saved settings/firewall rules.
string mode = args.ElementAtOrDefault(0) ?? "webrtc";
int fps = int.Parse(args.ElementAtOrDefault(1) ?? "30");
string address = args.ElementAtOrDefault(2) ?? "127.0.0.1";
int width = int.Parse(args.ElementAtOrDefault(3) ?? "1920");
int height = int.Parse(args.ElementAtOrDefault(4) ?? "1080");
using var broadcaster = new StreamBroadcaster(80);
using var engine = new ScreenCaptureEngine(broadcaster) { TargetFps = fps };
engine.SetSource(new Pattern(width, height));
await using var server = new EmbeddedWebServer(broadcaster, engine) { PreferWebRtc = mode == "webrtc" };
await server.StartAsync(address, 18780);
engine.Start();
Console.WriteLine($"Open {server.ServerUrl}; {broadcaster.WebRtcEncoder}. Commands: freeze, resume, small, full, pin, announce, quit.");
while (await Console.In.ReadLineAsync() is { } command && command != "quit")
{
    switch (command)
    {
        case "freeze": engine.IsFrozen = true; break;
        case "resume": engine.IsFrozen = false; break;
        case "small": engine.SetSource(new Pattern(1280, 720)); break;
        case "full": engine.SetSource(new Pattern(1920, 1080)); break;
        case "pin": server.UpdateSecurity(true, "2468"); break;
        case "announce": await broadcaster.BroadcastAnnouncementAsync("Synthetic WebRTC test"); break;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { broadcaster.CurrentFps, broadcaster.LastEncodeMilliseconds,
        broadcaster.TotalBytesTransferred, broadcaster.TotalFramesSent, broadcaster.WebRtcEncoder,
        broadcaster.WebRtcUnavailableReason }));
}
await engine.StopAsync();

sealed class Pattern(int width, int height) : ICaptureSource
{
    private int _counter;
    public string Id => "synthetic";
    public string DisplayName => "Synthetic moving pattern";
    public Rectangle Bounds => new(0, 0, width, height);
    public bool IsValid => true;
    public Bitmap CaptureFrame(bool cursor)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.DarkBlue);
        graphics.FillRectangle(Brushes.DarkRed, 0, 0, 640, 360);
        graphics.FillRectangle(Brushes.White, (_counter++ * 8) % 1150, 390, 100, 100);
        using var font = new Font("Segoe UI", 32);
        graphics.DrawString("ScreenTask WebRTC test " + _counter, font, Brushes.White, 20, 25);
        return bitmap;
    }
    public void Dispose() { }
}
