using System;
using System.IO;
using System.Windows.Forms;
using YtmOverlay;

internal static class EditorTest
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var monitor = new SessionMonitor(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var lyrics = new LyricsClient();
        var store = new ManualLyricsStore(Path.Combine(Path.GetTempPath(), "ytm-editor-test.json"));
        var settings = new AppSettings();
        var server = new WebServer(monitor, new YtMusicClient(), lyrics, store, () => settings);
        var now = new NowPlaying { Title = "Sample Track", Artist = "Example Artist", TrackId = "test", Duration = 100 };
        Application.Run(new LyricsEditorForm(now, monitor, lyrics, store, server));
    }
}
