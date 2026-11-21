using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YtmOverlay;

internal static class Program
{
    private const string MutexName = "Global\\YTMOverlay_SingleInstance_v1";

    private static AppSettings _settings = new();
    private static SessionMonitor? _monitor;
    private static BrowserBridge? _bridge;
    private static YtMusicClient? _yt;
    private static LyricsClient? _lyrics;
    private static ManualLyricsStore? _manualLyrics;
    private static WebServer? _server;
    private static ControlForm? _form;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("YouTube Music Overlay is already running.\n\n" +
                "If you are upgrading, right-click the old music-note icon in the system tray, choose Exit, " +
                "then start the new v0.0.0 beta file again.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _settings = AppSettings.Load();

        _bridge = new BrowserBridge(_settings.BrowserBridgeKey);
        _monitor = new SessionMonitor(
            preferredIds: PreferredIds(),
            titlePids: new[] { "chrome", "msedge", "brave", "opera", "vivaldi", "firefox", "librewolf", "zen" },
            nameHints: new[] { "youtube music", "yt music", "ytmdesktop", "youtube-music", "google chrome", "microsoft edge" },
            bridge: _bridge);

        _yt = new YtMusicClient();
        _lyrics = new LyricsClient();
        _manualLyrics = new ManualLyricsStore();

        _server = new WebServer(_monitor, _yt, _lyrics, _manualLyrics, () => _settings, _bridge);
        bool online = false;
        int port = _settings.Port;
        for (int i = 0; i < 12 && !online; i++)
        {
            online = _server.Start(port);
            if (!online) port++;
        }

        if (!online)
        {
            MessageBox.Show(
                "The overlay could not start its local web server on ports " + _settings.Port + "\u2013" + port + ".\n\n" +
                "Something else on this PC is using those ports. Close the other copy of this app (or the app using the port) and try again.",
                "Port unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (port != _settings.Port)
        {
            _settings.Port = port;
            _settings.Save();
        }

        // Background polling loop: media session every 400 ms.
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await _monitor.PollAsync().ConfigureAwait(false); } catch { }
                try { await Task.Delay(400, cts.Token).ConfigureAwait(false); } catch { }
            }
        });

        string url = $"http://localhost:{port}/";
        bool queueOnline = CheckOnline();

        _form = new ControlForm(() => _settings, _monitor, _server, _lyrics, _manualLyrics, url, queueOnline);
        Application.Run(_form);

        cts.Cancel();
        try { _server?.Stop(); } catch { }
    }

    private static string[] PreferredIds()
    {
        var extra = (_settings.SourceHints ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0);

        return new[]
        {
            "ytmdesktop", "youtube-music-desktop", "com.github.th-ch.youtube-music",
            "youtube music", "yt music", "music.youtube"
        }.Concat(extra).ToArray();
    }

    private static bool CheckOnline()
    {
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses("music.youtube.com");
            return addresses != null && addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
