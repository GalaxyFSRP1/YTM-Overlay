using System;
using System.IO;
using System.Text.Json;

namespace YtmOverlay;

public sealed class AppSettings
{
    // ---- look ----
    public string Theme { get; set; } = "glass";            // glass | minimal | neon | light
    public string Accent { get; set; } = "#ff2d55";
    public string Accent2 { get; set; } = "#7c5cff";        // progress gradient end / neon secondary
    public double Scale { get; set; } = 1;                  // 1 = 100%
    public string Align { get; set; } = "center";
    public double Opacity { get; set; } = 1;                // 0.3 - 1
    public double Corner { get; set; } = 1;                 // corner radius multiplier 0 - 1.8
    public double Blur { get; set; } = 1;                   // backdrop blur multiplier 0 - 2
    public bool TransparentBackground { get; set; } = false; // text/art only; no panel backgrounds
    public bool ReduceMotion { get; set; } = false;

    // ---- content ----
    public bool ShowQueue { get; set; } = true;
    public int QueueCount { get; set; } = 4;
    public bool ShowQueueArt { get; set; } = true;
    public bool ShowQueueNumbers { get; set; } = true;
    public bool ShowQueueArtist { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowTimes { get; set; } = true;
    public bool ShowRemainingTime { get; set; } = false;
    public bool ShowAlbum { get; set; } = true;
    public bool ShowArt { get; set; } = true;
    public bool ShowLabel { get; set; } = true;
    public bool DimWhenPaused { get; set; } = true;
    public bool HideWhenPaused { get; set; } = false;
    public string Label { get; set; } = "Now playing";

    // ---- lyrics ----
    public bool ShowLyrics { get; set; } = true;
    public string LyricsSource { get; set; } = "lrclib";    // lrclib | off
    public int LyricsLines { get; set; } = 2;               // visible lines
    public bool LyricsKaraoke { get; set; } = true;         // highlight the current line
    public bool FollowLyricsInApp { get; set; } = true;
    public bool LyricsHideInstrumental { get; set; } = false;
    // Positive values make the displayed lyric advance earlier.
    public double LyricsOffsetSeconds { get; set; } = 0;
    public string LyricsAlign { get; set; } = "left";
    // Adjusts the displayed song timer and therefore the lyric clock together.
    public double PositionOffsetSeconds { get; set; } = 0;

    // ---- behaviour ----
    public string TextCase { get; set; } = "normal";         // normal | upper
    public bool TitleMarquee { get; set; } = true;
    public string Placeholder { get; set; } = "Nothing playing";
    public int CacheSeconds { get; set; } = 30;
    public int Port { get; set; } = 7190;
    /// <summary>Extra text matched against the app id / name, comma separated.</summary>
    public string SourceHints { get; set; } = "";
    /// <summary>Local pairing secret. Never exposed in overlay state or diagnostic JSON.</summary>
    public string BrowserBridgeKey { get; set; } = "";

    // ---- viewer requests (disabled until the streamer explicitly enables them) ----
    public string ChatBridgeKey { get; set; } = "";
    public bool RequestIntakeEnabled { get; set; } = false;
    public string RequestMode { get; set; } = "approval"; // approval | automatic
    public bool RequestAutoQueue { get; set; } = false;    // experimental YouTube Music browser queue
    public int RequestCooldownSeconds { get; set; } = 60;
    public int RequestLimit { get; set; } = 50;

    internal static string? TestingDirectory { get; set; }

    public static string Dir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TestingDirectory))
            {
                Directory.CreateDirectory(TestingDirectory);
                return TestingDirectory;
            }
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var d = Path.Combine(root, "YTM-Overlay");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
        }
        catch { }

        var s = loaded ?? new AppSettings();
        bool needsKey = !ValidKey(s.BrowserBridgeKey) || !ValidKey(s.ChatBridgeKey) || s.ChatBridgeKey == s.BrowserBridgeKey;
        s.Sanitize();
        if (needsKey) s.Save(); // Keep the opt-in bookmark working across app restarts.
        return s;
    }

    public void Sanitize()
    {
        QueueCount = Math.Clamp(QueueCount, 0, 8);
        Scale = Math.Clamp(Scale <= 0 ? 1 : Scale, 0.4, 3);
        Opacity = Math.Clamp(Opacity <= 0 ? 1 : Opacity, 0.3, 1);
        Corner = double.IsFinite(Corner) ? Math.Clamp(Corner, 0, 1.8) : 1;
        Blur = Math.Clamp(Blur < 0 ? 1 : Blur, 0, 2);
        LyricsLines = Math.Clamp(LyricsLines, 1, 6);
        LyricsOffsetSeconds = double.IsFinite(LyricsOffsetSeconds) ? Math.Clamp(LyricsOffsetSeconds, -30, 30) : 0;
        PositionOffsetSeconds = double.IsFinite(PositionOffsetSeconds) ? Math.Clamp(PositionOffsetSeconds, -30, 30) : 0;
        if (LyricsAlign != "center") LyricsAlign = "left";
        if (!ValidKey(BrowserBridgeKey)) BrowserBridgeKey = BrowserBridge.NewSecret();
        if (!ValidKey(ChatBridgeKey) || ChatBridgeKey == BrowserBridgeKey) ChatBridgeKey = BrowserBridge.NewSecret();
        if (RequestMode != "automatic") RequestMode = "approval";
        RequestCooldownSeconds = Math.Clamp(RequestCooldownSeconds, 0, 3600);
        RequestLimit = Math.Clamp(RequestLimit, 1, 100);
        CacheSeconds = Math.Clamp(CacheSeconds, 0, 300);
        if (Port is < 1024 or > 65000) Port = 7190;
        if (string.IsNullOrWhiteSpace(Accent)) Accent = "#ff2d55";
        if (string.IsNullOrWhiteSpace(Accent2)) Accent2 = "#7c5cff";
        if (string.IsNullOrWhiteSpace(Theme)) Theme = "glass";
        if (string.IsNullOrWhiteSpace(Align)) Align = "center";
        if (string.IsNullOrWhiteSpace(Label)) Label = "Now playing";
        // Older builds had a hidden second off-switch with no control in the UI.
        // ShowLyrics is now the single source of truth; otherwise a checked box
        // could still result in no lyrics card. Preserve the explicit checkbox.
        LyricsSource = "lrclib";
        if (TextCase != "upper") TextCase = "normal";
    }

    private static bool ValidKey(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
