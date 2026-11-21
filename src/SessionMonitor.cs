using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace YtmOverlay;

/// <summary>
/// Polls the Windows System Media Transport Controls session that belongs to YouTube Music
/// and exposes a snapshot. This is the same source the Windows "media popup" uses, so it
/// works with the YT Music desktop app, the browser (Chrome/Edge/Brave tabs) and PWAs.
/// </summary>
public sealed class SessionMonitor
{
    private readonly object _gate = new();
    private readonly string[] _preferredIds;
    private readonly string[] _titlePids;
    private readonly string[] _nameHints;

    private NowPlaying _current = new();
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _sessionKey = "";
    private string _artHash = "";
    private byte[]? _artBytes;

    private readonly TimelineClock _clock = new();
    private readonly BrowserBridge? _bridge;
    private long _sourceGeneration;
    private int _metadataInFlight;
    private string _artRequestedTrack = "";
    private DateTime _artRequestedAt = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime _lastReacquire = DateTime.MinValue;

    public SessionMonitor(string[] preferredIds, string[] titlePids, string[] nameHints, BrowserBridge? bridge = null)
    {
        _preferredIds = preferredIds.Select(s => s.ToLowerInvariant()).ToArray();
        _titlePids = titlePids.Select(s => s.ToLowerInvariant()).ToArray();
        _nameHints = nameHints.Select(s => s.ToLowerInvariant()).ToArray();
        _bridge = bridge;
    }

    private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>One line per Windows media session - used by the "What am I seeing?" window.</summary>
    public List<string> SessionDump()
    {
        var lines = new List<string>();
        try
        {
            var mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            foreach (var s in mgr.GetSessions())
            {
                string id = "", st = "";
                try { id = s.SourceAppUserModelId ?? ""; } catch { }
                try { st = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "?"; } catch { }
                string title = "";
                try { title = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult()?.Title ?? ""; } catch { }
                bool picked = id == _sessionKey;
                lines.Add($"{(picked ? ">>" : "  ")} {id}  [{st}]  \"{title}\"");
            }
        }
        catch (Exception ex) { lines.Add("Could not read media sessions: " + ex.Message); }
        if (lines.Count == 0) lines.Add("(no media sessions reported by Windows right now)");
        return lines;
    }

    public (byte[]? bytes, string hash) GetArt()
    {
        lock (_gate) return (_artBytes, _artHash);
    }

    public NowPlaying Current
    {
        get
        {
            if (_bridge != null && _bridge.TryGet(out var browser, out _, out _)) return browser;
            lock (_gate)
            {
                return new NowPlaying
                {
                    Title = _current.Title, Artist = _current.Artist, Album = _current.Album,
                    Position = _clock.At(MonotonicSeconds()), Duration = _current.Duration, Rate = _current.Rate,
                    Playing = _current.Playing, SourceLabel = _current.SourceLabel,
                    TrackId = _current.TrackId, Art = _current.Art,
                    ClockRevision = _clock.Revision, ClockSource = "Windows media session",
                    ClockMode = _clock.Mode, ClockRawPosition = _clock.RawPosition,
                    ClockSourceAgeSeconds = _clock.SourceAgeSeconds, ClockTimestampTrusted = _clock.TimestampTrusted
                };
            }
        }
    }

    /// <summary>Position summary used by the debug window.</summary>
    public string Describe()
    {
        var n = Current;
        if (string.IsNullOrEmpty(n.Title)) return "nothing detected";
        return $"{n.Title} - {n.Artist} ({n.Position:0.0}/{n.Duration:0.0}s, {(n.Playing ? "playing" : "paused")})";
    }

    public async Task PollAsync()
    {
        try
        {
            if (_session == null || (DateTime.UtcNow - _lastReacquire).TotalSeconds > 12)
            {
                _lastReacquire = DateTime.UtcNow;
                var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var picked = await PickAsync(mgr.GetSessions()).ConfigureAwait(false);
                var key = picked == null ? "" : Key(picked);
                if (picked == null)
                {
                    _session = null;
                    _sessionKey = "";
                    Interlocked.Increment(ref _sourceGeneration);
                    ClearLocked();
                    return;
                }
                if (key != _sessionKey) _lastRefresh = DateTime.MinValue;
                // Discard any metadata/art result still in flight for the old object.
                Interlocked.Increment(ref _sourceGeneration);
                _session = picked;
                _sessionKey = key;
            }

            if (_session == null) { ClearLocked(); return; }
            var session = _session;
            long generation = Interlocked.Read(ref _sourceGeneration);
            // Timeline polling is independent of metadata and cover-art I/O.
            var sample = ReadTimeline(session);
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_current.Title) && generation == Interlocked.Read(ref _sourceGeneration))
                    ApplyTimeline(sample, false);
            }

            bool force = (DateTime.UtcNow - _lastRefresh).TotalSeconds > 0.9;
            if ((force || _lastRefresh == DateTime.MinValue) && Interlocked.CompareExchange(ref _metadataInFlight, 1, 0) == 0)
            {
                _lastRefresh = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    try { await RefreshAsync(session, generation).ConfigureAwait(false); }
                    catch { }
                    finally { Interlocked.Exchange(ref _metadataInFlight, 0); }
                });
            }
        }
        catch
        {
            // Session disappeared / COM hiccup; drop it and reacquire next tick.
            _session = null;
            _sessionKey = "";
            Interlocked.Increment(ref _sourceGeneration);
            ClearLocked();
        }
    }

    private string Key(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.SourceAppUserModelId ?? s.GetHashCode().ToString(); }
        catch { return s.GetHashCode().ToString(); }
    }

    /// <summary>Windows' friendly name for the app that owns a media session, derived from its AUMID.</summary>
    public static string DisplayNameOf(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var id = s.SourceAppUserModelId ?? "";
            if (id.Length == 0) return "Unknown app";
            if (id.Contains('!')) id = id.Substring(0, id.IndexOf('!'));
            id = System.Text.RegularExpressions.Regex.Replace(id, @"_[a-z0-9]{13}$", "");
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 4);
            switch (id.ToLowerInvariant())
            {
                case "chrome": return "Google Chrome";
                case "msedge": return "Microsoft Edge";
                case "brave": return "Brave";
                case "firefox": return "Firefox";
                case "opera": return "Opera";
                case "vivaldi": return "Vivaldi";
                case "zen": return "Zen";
                case "librewolf": return "LibreWolf";
                case "spotify": return "Spotify";
                case "applicationframehost": return "App";
            }
            return id;
        }
        catch { return "Unknown app"; }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> PickAsync(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (sessions == null || sessions.Count == 0) return null;

        var scored = new List<(GlobalSystemMediaTransportControlsSession s, int score)>();
        foreach (var s in sessions)
        {
            string id;
            try { id = s.SourceAppUserModelId ?? ""; } catch { id = ""; }
            string idl = id.ToLowerInvariant();
            string name = DisplayNameOf(s);
            string nl = name.ToLowerInvariant();

            int score = 0;
            if (_preferredIds.Any(p => idl.Contains(p))) score += 200;
            if (_preferredIds.Any(p => nl.Contains(p))) score += 150;
            if (_nameHints.Any(p => nl.Contains(p) && p.Contains("music"))) score += 120;
            if (idl.Contains("ytmdesktop") || idl.Contains("youtube-music-desktop")) score += 160;

            bool isBrowser =
                idl.Contains("chrome") || idl.Contains("msedge") || idl.Contains("brave") ||
                idl.Contains("opera") || idl.Contains("vivaldi") || idl.Contains("firefox") ||
                idl.Contains("zen") || idl.Contains("librewolf") || idl.Contains("arc");

            if (isBrowser)
            {
                // A browser is only trusted when it is really playing YouTube Music:
                // either the tab title says so, or the reported song carries the
                // " - YouTube Music" suffix that YT Music adds to its tab title.
                string tabTitle = SafeTabTitle(idl);
                bool tabMatch = LooksLikeYtMusic(tabTitle);
                bool titleMatch = false;
                try
                {
                    var props = await s.TryGetMediaPropertiesAsync();
                    titleMatch = LooksLikeYtMusic(props?.Title ?? "");
                }
                catch { }

                if (titleMatch) score += 170;
                else if (tabMatch) score += 60;
                else score -= 140;
            }

            try
            {
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    score += 45;
            }
            catch { }

            scored.Add((s, score));
        }

        var best = scored.OrderByDescending(x => x.score).First();
        if (best.score > 0) return best.s;

        // Nothing matched by name. If there is exactly one session, that is almost
        // certainly the music the user means - show it instead of showing nothing.
        return sessions.Count == 1 ? sessions[0] : null;
    }

    private bool LooksLikeYtMusic(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        string s = t.ToLowerInvariant();
        return s.Contains("youtube music") || s.Contains("yt music") || s.Contains("ytmusic") ||
               s.Contains(" - youtube music") || s.EndsWith("youtube music");
    }

    /// <summary>Reads the top-level window title of a browser process to identify YT Music tabs.</summary>
    private string SafeTabTitle(string aumid)
    {
        try
        {
            foreach (var procName in _titlePids)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(procName); } catch { continue; }
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.MainWindowTitle.Length > 0 && LooksLikeYtMusic(p.MainWindowTitle))
                            return p.MainWindowTitle;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return "";
    }

    private void ClearLocked()
    {
        lock (_gate)
        {
            _current = new NowPlaying();
            _artBytes = null;
            _artHash = "";
            _clock.Reset();
        }
    }

    private static string NormalizeTitle(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        var s = t.Trim();
        foreach (var suffix in new[]
                 {
                     " - YouTube Music", " | YouTube Music", " on YouTube Music",
                     " - YouTube", " - Music"
                 })
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = s.Substring(0, s.Length - suffix.Length).Trim();
                if (stripped.Length > 0) return stripped;
            }
        }
        // Browsers render the tab as "Song - Artist - YouTube Music"
        return s;
    }

    /// <summary>Turns "Song - Artist - YouTube Music" into "Song" for show titles.</summary>
    private static (string title, string artist) SplitBrowserTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (title, "");
        var parts = title.Split(new[] { " - " }, StringSplitOptions.None);
        if (parts.Length == 2) return (parts[0].Trim(), parts[1].Trim());
        return (title, "");
    }

    private readonly record struct TimelineSample(bool HasTimeline, double Position, double Duration,
        double Rate, bool Playing, DateTimeOffset? UpdatedAt, DateTimeOffset ObservedAt, double Monotonic);

    private TimelineSample ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        double duration = 0, position = 0, rate;
        bool playing, has = false;
        DateTimeOffset? updated = null;
        lock (_gate) { playing = _current.Playing; rate = _current.Rate > 0 ? _current.Rate : 1; }
        try
        {
            var tl = session.GetTimelineProperties();
            if (tl != null)
            {
                has = true;
                duration = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
                position = Math.Max(0, (tl.Position - tl.StartTime).TotalSeconds);
                updated = tl.LastUpdatedTime;
            }
        }
        catch { }
        try
        {
            var info = session.GetPlaybackInfo();
            if (info != null)
            {
                playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (info.PlaybackRate is double r && r > 0 && double.IsFinite(r)) rate = r;
            }
        }
        catch { }
        return new TimelineSample(has, position, duration, rate, playing, updated,
            DateTimeOffset.UtcNow, MonotonicSeconds());
    }

    // Called with _gate held.
    private void ApplyTimeline(TimelineSample s, bool newTrack)
    {
        double duration = s.HasTimeline ? s.Duration : (newTrack ? 0 : _current.Duration);
        if (s.HasTimeline)
            _clock.Observe(s.Position, duration, s.Rate, s.Playing, s.UpdatedAt, s.ObservedAt, s.Monotonic, newTrack);
        else
            _clock.ObserveUnavailable(duration, s.Rate, s.Playing, s.Monotonic, newTrack);
        _current.Duration = duration;
        _current.Playing = s.Playing;
        _current.Rate = s.Rate;
    }

    private async Task RefreshAsync(GlobalSystemMediaTransportControlsSession session, long generation)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch { return; }
        if (props == null) return;

        string title = NormalizeTitle(props.Title ?? "");
        string artist = props.Artist ?? "";
        string album = props.AlbumTitle ?? "";
        if (string.IsNullOrWhiteSpace(artist) && title.Contains(" - "))
        {
            var (t2, a2) = SplitBrowserTitle(title);
            if (t2.Length > 0 && a2.Length > 0) { title = t2; artist = a2; }
        }

        string label = "";
        try
        {
            var id = (session.SourceAppUserModelId ?? "").ToLowerInvariant();
            if (id.Contains("ytmdesktop") || id.Contains("youtube-music") || id.Contains("music.youtube"))
                label = "YouTube Music";
            else if (_nameHints.Any(h => id.Contains(h)) ||
                     new[] { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "zen", "librewolf" }.Any(b => id.Contains(b)))
                label = "Browser (Windows session)";
            else
                label = DisplayNameOf(session);
        }
        catch { label = "Windows session"; }

        // Length is deliberately not part of the track key: browsers often change
        // an initially unknown duration after playback has started.
        string trackId = string.IsNullOrWhiteSpace(title) ? "" : Hash($"{title}|{artist}|{album}");
        var sample = ReadTimeline(session);
        bool fetchArt;
        lock (_gate)
        {
            if (generation != Interlocked.Read(ref _sourceGeneration)) return;
            bool newTrack = trackId != _current.TrackId;
            if (newTrack)
            {
                _artBytes = null;
                _artHash = "";
            }
            double priorDuration = _current.Duration;
            _current = new NowPlaying
            {
                Title = title, Artist = artist, Album = album, SourceLabel = label, TrackId = trackId,
                Duration = priorDuration,
                Art = string.IsNullOrEmpty(_artHash) ? "" : "/art/" + _artHash
            };
            ApplyTimeline(sample, newTrack);
            fetchArt = !string.IsNullOrEmpty(trackId) &&
                       (_artRequestedTrack != trackId || (DateTime.UtcNow - _artRequestedAt).TotalSeconds > 15);
            if (fetchArt) { _artRequestedTrack = trackId; _artRequestedAt = DateTime.UtcNow; }
        }
        if (fetchArt) _ = RefreshArtAsync(props, trackId, generation);
    }

    private async Task RefreshArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props, string trackId, long generation)
    {
        byte[]? art = null;
        string hash = "";
        try
        {
            if (props.Thumbnail != null)
            {
                using IRandomAccessStreamWithContentType stream = await props.Thumbnail.OpenReadAsync();
                if (stream.Size > 0 && stream.Size < 12_000_000)
                {
                    art = new byte[stream.Size];
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(art);
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    hash = Convert.ToHexString(sha.ComputeHash(art)).Substring(0, 12).ToLowerInvariant();
                }
            }
        }
        catch { }
        if (art == null) return;
        lock (_gate)
        {
            if (generation != Interlocked.Read(ref _sourceGeneration) || _current.TrackId != trackId) return;
            _artBytes = art;
            _artHash = hash;
            _current.Art = "/art/" + hash;
        }
    }

    private static string Hash(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 10).ToLowerInvariant();
    }
}
