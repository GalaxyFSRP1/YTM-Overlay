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

    public void Disconnect()
    {
        lock (_gate) { _received = false; _queue.Clear(); _queueKnown = false; _clock.Reset(); _now = new NowPlaying(); }
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
