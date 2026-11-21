using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YtmOverlay;

/// <summary>
/// Optional, explicitly activated browser link. It receives only playback data
/// from the YouTube Music page on loopback; it never receives account cookies.
/// A stale link automatically yields control back to the Windows media session.
/// </summary>
public sealed class BrowserBridge
{
    private readonly object _gate = new();
    private readonly string _secret;
    private readonly TimelineClock _clock = new();
    private NowPlaying _now = new();
    private List<QueueItem> _queue = new();
    private bool _queueKnown;
    private double _lastSeen;
    private bool _received;
    private const double LifetimeSeconds = 8;

    // Explicit user-initiated transport commands are in-memory, scoped to the
    // currently linked bookmark session, delivered at most once, and never
    // replayed after a restart or a lost response.
    private string _controlSession = "";
    private bool _controlCapable;
    private ControlJob? _waitingControl, _inFlightControl;
    private string _lastControlId = "", _lastControlAction = "", _lastControlStatus = "idle", _lastControlSession = "";
    private double _controlAt;

    private sealed class ControlJob
    {
        public string Id = "";
        public string Action = "";
        public string Session = "";
    }
    public sealed class ControlCommand
    {
        public string Id { get; set; } = "";
        public string Action { get; set; } = "";
    }

    public BrowserBridge(string secret) => _secret = secret ?? "";
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static double Mono() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public bool Accept(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !SecretEquals(Str(root, "key", 128))) return false;
            if (Bool(root, "stop")) { Disconnect(); return true; }
            if (Bool(root, "hello")) return true;
            string title = Str(root, "title", 300).Trim();
            if (title.Length == 0) return false;
            string artist = Str(root, "artist", 240).Trim();
            string album = Str(root, "album", 240).Trim();
            string videoId = Str(root, "videoId", 80).Trim();
            string identity = videoId.Length > 0 ? videoId : title + "|" + artist + "|" + album;
            string trackId;
            using (var sha = SHA256.Create())
                trackId = "browser-" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Substring(0, 16).ToLowerInvariant();
            double position = Num(root, "position", 0, 0, 86400);
            double duration = Num(root, "duration", 0, 0, 86400);
            double rate = Num(root, "rate", 1, 0.05, 8);
            bool ad = Bool(root, "ad");
            bool playing = !ad && Bool(root, "playing");
            string controlSession = Str(root, "session", 80);
            bool controlCapable = !ad && Bool(root, "canControl") && ValidControlSession(controlSession);
            string art = SafeImage(Str(root, "art", 2048));
            bool known = Bool(root, "queueKnown") && root.TryGetProperty("items", out var a) && a.ValueKind == JsonValueKind.Array;
            var items = new List<QueueItem>();
            if (known)
            {
                foreach (var item in root.GetProperty("items").EnumerateArray())
                {
                    if (items.Count >= 20) break;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string qtitle = Str(item, "title", 300).Trim();
                    if (qtitle.Length == 0) continue;
                    items.Add(new QueueItem { Title = qtitle, Artist = Str(item, "artist", 240),
                        Album = Str(item, "album", 240), VideoId = Str(item, "videoId", 80),
                        Thumb = SafeImage(Str(item, "thumb", 2048)) });
                }
            }
            var wall = DateTimeOffset.UtcNow;
            double mono = Mono();
            DateTimeOffset? sent = null;
            double sentMillis = Num(root, "sentAt", 0, 0, 9000000000000d);
            if (sentMillis > 0)
            {
                try { sent = DateTimeOffset.FromUnixTimeMilliseconds((long)sentMillis); }
                catch { }
            }
            lock (_gate)
            {
                bool newTrack = trackId != _now.TrackId;
                if (ad)
                    _clock.ObserveUnavailable(duration, rate, false, mono, newTrack);
                else
                    _clock.Observe(position, duration, rate, playing, sent, wall, mono, newTrack);
                _now = new NowPlaying { Title = title, Artist = artist, Album = album, Position = position,
                    Duration = duration, Rate = rate, Playing = playing, TrackId = trackId, Art = art,
                    SourceLabel = ad ? "YouTube Music (ad)" : "YouTube Music (browser link)" };
                _queue = items;
                _queueKnown = known;
                _lastSeen = mono;
                _received = true;
                _controlSession = controlCapable ? controlSession : "";
                _controlCapable = controlCapable;
                ExpireControlUnsafe(mono);
            }
            return true;
        }
        catch { return false; }
    }

    public bool TryGet(out NowPlaying now, out List<QueueItem> queue, out bool queueKnown)
    {
        lock (_gate)
        {
            double mono = Mono();
            if (!_received || mono - _lastSeen > LifetimeSeconds)
            {
                now = new NowPlaying(); queue = new List<QueueItem>(); queueKnown = false;
                return false;
            }
            now = new NowPlaying { Title = _now.Title, Artist = _now.Artist, Album = _now.Album,
                Position = _clock.At(mono), Duration = _now.Duration, Rate = _now.Rate, Playing = _now.Playing,
                TrackId = _now.TrackId, Art = _now.Art, SourceLabel = _now.SourceLabel,
                ClockRevision = _clock.Revision, ClockSource = "YouTube Music browser link", ClockMode = _clock.Mode,
                ClockRawPosition = _clock.RawPosition, ClockSourceAgeSeconds = _clock.SourceAgeSeconds,
                ClockTimestampTrusted = _clock.TimestampTrusted };
            queue = new List<QueueItem>(_queue);
            queueKnown = _queueKnown;
            return true;
        }
    }

    private static bool ValidControlSession(string value)
    {
        if (value.Length < 8 || value.Length > 80) return false;
        foreach (char c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        return true;
    }

    private void ExpireControlUnsafe(double now)
    {
        if (_waitingControl != null && now - _controlAt > 8)
        {
            _waitingControl = null;
            _lastControlStatus = "expired"; // never sent to the tab
        }
        if (_inFlightControl != null && now - _controlAt > 10)
        {
            _inFlightControl = null;
            _lastControlStatus = "uncertain"; // a click may have occurred
        }
    }

    public string ControlStateJson()
    {
        lock (_gate)
        {
            double now = Mono();
            ExpireControlUnsafe(now);
            bool linked = _received && now - _lastSeen <= LifetimeSeconds;
            return JsonSerializer.Serialize(new {
                linked, available = linked && _controlCapable,
                busy = _waitingControl != null || _inFlightControl != null,
                last = new { id = _lastControlId, action = _lastControlAction, status = _lastControlStatus }
            });
        }
    }

    public bool QueueControl(string action, out string id, out string reason)
    {
        id = ""; reason = "";
        if (action is not ("toggle" or "previous" or "next")) { reason = "Unknown player action."; return false; }
        lock (_gate)
        {
            double now = Mono();
            ExpireControlUnsafe(now);
            if (!_received || now - _lastSeen > LifetimeSeconds || !_controlCapable)
            { reason = "Connect the updated music bookmark and start a song. Controls are unavailable during ads."; return false; }
            if (_waitingControl != null || _inFlightControl != null)
            { reason = "A player action is still in progress."; return false; }
            var job = new ControlJob { Id = Guid.NewGuid().ToString("N"), Action = action, Session = _controlSession };
            _waitingControl = job; _controlAt = now;
            _lastControlId = job.Id; _lastControlAction = job.Action; _lastControlSession = job.Session; _lastControlStatus = "queued";
            id = job.Id;
            return true;
        }
    }

    // Called only after a valid music-origin bridge post. Claiming removes the
    // command from the outgoing queue; even a lost response cannot repeat it.
    public ControlCommand? ClaimControl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !SecretEquals(Str(root, "key", 128)) || !Bool(root, "canControl") || Bool(root, "stop")) return null;
            string session = Str(root, "session", 80);
            lock (_gate)
            {
                double now = Mono(); ExpireControlUnsafe(now);
                if (_waitingControl == null || !_controlCapable || session != _waitingControl.Session || session != _controlSession) return null;
                _inFlightControl = _waitingControl; _waitingControl = null; _controlAt = now; _lastControlStatus = "dispatched";
                return new ControlCommand { Id = _inFlightControl.Id, Action = _inFlightControl.Action };
            }
        }
        catch { return null; }
    }

    public bool FinishControl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !SecretEquals(Str(root, "key", 128))) return false;
            string id = Str(root, "id", 80), session = Str(root, "session", 80), result = Str(root, "result", 20);
            if (result is not ("clicked" or "failed")) return false;
            lock (_gate)
            {
                ExpireControlUnsafe(Mono());
                if (_inFlightControl == null)
                    return id == _lastControlId && session == _lastControlSession && _lastControlStatus == result;
                if (_inFlightControl.Id != id || _inFlightControl.Session != session) return false;
                _lastControlStatus = result;
                _inFlightControl = null;
                return true;
            }
        }
        catch { return false; }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            if (_waitingControl != null) _lastControlStatus = "expired";
            if (_inFlightControl != null) _lastControlStatus = "uncertain";
            _waitingControl = null; _inFlightControl = null; _controlSession = ""; _controlCapable = false;
            _received = false; _queue.Clear(); _queueKnown = false; _clock.Reset(); _now = new NowPlaying();
        }
    }

    public bool Authenticate(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && SecretEquals(Str(doc.RootElement, "key", 128));
        }
        catch { return false; }
    }

    private bool SecretEquals(string supplied)
    {
        var a = Encoding.UTF8.GetBytes(_secret);
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static string Str(JsonElement el, string key, int max)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = v.GetString() ?? "";
        return s.Length <= max ? s : s.Substring(0, max);
    }
    private static bool Bool(JsonElement el, string key) => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static double Num(JsonElement el, string key, double fallback, double min, double max)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) return fallback;
        return Math.Clamp(d, min, max);
    }
    private static string SafeImage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "";
        string h = u.Host.ToLowerInvariant();
        return h.EndsWith(".ytimg.com", StringComparison.Ordinal) || h.EndsWith(".googleusercontent.com", StringComparison.Ordinal) ||
               h.EndsWith(".ggpht.com", StringComparison.Ordinal) ? u.AbsoluteUri : "";
    }
}
