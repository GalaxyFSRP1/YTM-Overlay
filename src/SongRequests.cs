using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YtmOverlay;

// A local, bounded moderation inbox. Chat links can submit requests, but only the
// separate music link can claim queue jobs. Neither key is included in state JSON.
public sealed class SongRequests
{
    private readonly object _gate = new();
    private readonly Func<AppSettings> _get;
    private readonly string _file;
    private readonly List<RequestRecord> _items = new();
    private readonly Dictionary<string, Source> _sources = new();
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _acks = new();
    private string _persistenceWarning = "";
    private bool _loadFailed;
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex Command = new(@"^!(?:sr|songrequest)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Video = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);

    public sealed class RequestRecord
    {
        public string Id { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Viewer { get; set; } = "";
        public string Song { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string ResolvedTitle { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string Note { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonIgnore] public string Claim { get; set; } = "";
        [JsonIgnore] public DateTimeOffset ClaimAt { get; set; }
    }
    private sealed class Source
    {
        public string Platform = "";
        public string Channel = "";
        public DateTimeOffset LastSeen;
        public string Note = "";
    }
    public sealed class QueueCommand
    {
        public string Id { get; set; } = "";
        public string Claim { get; set; } = "";
        public string Query { get; set; } = "";
        public string VideoId { get; set; } = "";
    }

    public SongRequests(Func<AppSettings> get, string? file = null)
    {
        _get = get;
        _file = file ?? Path.Combine(AppSettings.Dir, "requests.json");
        try
        {
            if (File.Exists(_file))
            {
                var saved = JsonSerializer.Deserialize<List<RequestRecord>>(File.ReadAllText(_file));
                if (saved == null || saved.Count > 1000) throw new InvalidDataException("Invalid request history");
                foreach (var r in saved.TakeLast(500))
                {
                    if (r == null || !Guid.TryParseExact(r.Id, "N", out _) || !TryParseSong(r.Song ?? "", out _, out _) ||
                        r.Platform is not ("twitch" or "youtube" or "kick" or "manual") ||
                        r.Status is not ("pending" or "ready" or "adding" or "added" or "failed" or "uncertain" or "done" or "rejected") ||
                        (!string.IsNullOrEmpty(r.VideoId) && !Video.IsMatch(r.VideoId)))
                        throw new InvalidDataException("Invalid request record");
                    r.Viewer = Clean(r.Viewer ?? "", 60); r.Channel = Clean(r.Channel ?? "", 100);
                    r.ResolvedTitle = Clean(r.ResolvedTitle ?? "", 160); r.Note = Clean(r.Note ?? "", 160);
                    r.VideoId ??= "";
                    // A job may have reached the player just before a crash. Never replay it automatically.
                    if (r.Status == "adding")
                    {
                        r.Status = "uncertain";
                        r.Note = "App restarted during queue insertion. Check YouTube Music before retrying.";
                        r.UpdatedUtc = DateTimeOffset.UtcNow;
                    }
                    _items.Add(r);
                }
            }
        }
        catch
        {
            _items.Clear();
            _loadFailed = true;
            _persistenceWarning = "Could not read saved requests; the original file is preserved. Restore or move it before enabling player insertion.";
        }
    }

    private static string Get(JsonElement e, string name, int max)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        string s = v.GetString() ?? "";
        return s.Length > max ? s.Substring(0, max) : s;
    }
    private static string Clean(string s, int max)
    {
        var b = new StringBuilder();
        foreach (char c in s)
        {
            if (b.Length >= max) break;
            if (char.IsControl(c)) { if (char.IsWhiteSpace(c)) b.Append(' '); continue; }
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            b.Append(c);
        }
        return Regex.Replace(b.ToString(), @"\s+", " ").Trim();
    }
    private static bool EqualSecret(string actual, string given)
    {
        byte[] a = Encoding.UTF8.GetBytes(actual ?? ""), b = Encoding.UTF8.GetBytes(given ?? "");
        return a.Length > 0 && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static bool Active(RequestRecord r) => r.Status is "pending" or "ready" or "adding" or "added" or "failed" or "uncertain";
    private static bool SameSong(RequestRecord a, RequestRecord b) =>
        a.Song.Equals(b.Song, StringComparison.OrdinalIgnoreCase) ||
        (a.VideoId.Length > 0 && b.VideoId.Length > 0 && a.VideoId == b.VideoId);

    // Only single YouTube/YouTube Music video URLs or plain search text are accepted.
    // URLs are parsed, never fetched, and later links are reconstructed from a validated id.
    public static bool TryParseSong(string input, out string song, out string videoId)
    {
        song = Clean(input, 200);
        videoId = "";
        if (song.Length < 2 || song.Length > 160) return false;
        if (!Regex.IsMatch(song, @"(?:https?://|www\.)", RegexOptions.IgnoreCase)) return !song.Contains("://", StringComparison.Ordinal);
        string candidate = song.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + song : song;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo)) return false;
        string host = u.Host.ToLowerInvariant();
        if (host == "youtu.be") videoId = u.AbsolutePath.Trim('/');
        else if (host is "youtube.com" or "www.youtube.com" or "music.youtube.com" or "m.youtube.com")
        {
            if (u.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string piece in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = piece.Split('=', 2);
                    if (pair.Length == 2 && pair[0] == "v") { videoId = Uri.UnescapeDataString(pair[1]); break; }
                }
            }
            else if (u.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                videoId = u.AbsolutePath.Substring(8).Trim('/');
        }
        if (!Video.IsMatch(videoId)) { videoId = ""; return false; }
        song = "https://music.youtube.com/watch?v=" + videoId;
        return true;
    }

    private void Remember(string id)
    {
        if (!_seen.Add(id)) return;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 5000) _seen.Remove(_seenOrder.Dequeue());
    }

    // Called only after the web server has mapped an exact platform origin.
    public bool ReceiveChat(string json, string platform)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!EqualSecret(_get().ChatBridgeKey, Get(root, "key", 128))) return false;
            if (platform is not ("twitch" or "youtube" or "kick")) return false;
            if (Get(root, "platform", 20) != platform) return false;
            string session = Clean(Get(root, "session", 80), 80);
            if (session.Length < 8) return false;
            string channel = Clean(Get(root, "channel", 100), 100);
            if (channel.Length == 0) channel = "live chat";
            string sourceKey = platform + ":" + session;
            lock (_gate)
            {
                if (root.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True)
                {
                    _sources.Remove(sourceKey);
                    return true;
                }
                var now = DateTimeOffset.UtcNow;
                foreach (var dead in _sources.Where(x => (now - x.Value.LastSeen).TotalMinutes > 3).Select(x => x.Key).ToList()) _sources.Remove(dead);
                if (!_sources.TryGetValue(sourceKey, out var source))
                {
                    if (_sources.Count >= 30) return false;
                    source = new Source { Platform = platform };
                    _sources[sourceKey] = source;
                }
                source.Channel = channel; source.LastSeen = now;
                source.Note = _get().RequestIntakeEnabled ? "Link active; waiting for new !sr messages." : "Link active; request intake is paused.";
                bool changed = false;
                if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var msg in messages.EnumerateArray().Take(20))
                    {
                        string id = Clean(Get(msg, "id", 120), 120);
                        string viewer = Clean(Get(msg, "author", 80), 60);
                        string text = Clean(Get(msg, "text", 500), 400);
                        if (id.Length == 0 || viewer.Length == 0 || text.Length == 0) continue;
                        string seenId = sourceKey + ":" + id;
                        if (_seen.Contains(seenId)) continue;
                        Remember(seenId);
                        var match = Command.Match(text);
                        if (!match.Success) continue;
                        if (!_get().RequestIntakeEnabled) { source.Note = "Request ignored because intake is paused."; continue; }
                        if (!TryParseSong(match.Groups[1].Value, out string song, out string videoId))
                        { source.Note = "A request was rejected: use song text or a single YouTube video link."; continue; }
                        var incoming = new RequestRecord { Platform = platform, Channel = channel, Viewer = viewer,
                            Song = song, VideoId = videoId, Id = Guid.NewGuid().ToString("N"), CreatedUtc = now, UpdatedUtc = now };
                        if (_items.Count(Active) >= _get().RequestLimit) { source.Note = "Request limit reached."; continue; }
                        if (_items.Any(x => Active(x) && SameSong(x, incoming)))
                        { source.Note = "Duplicate song ignored."; continue; }
                        bool sameViewer(RequestRecord x) => x.Platform == platform && x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && x.Viewer.Equals(viewer, StringComparison.OrdinalIgnoreCase);
                        if (_items.Any(x => Active(x) && sameViewer(x))) { source.Note = "Viewer already has an open request."; continue; }
                        int cooldown = _get().RequestCooldownSeconds;
                        if (cooldown > 0 && _items.Any(x => sameViewer(x) && (now - x.CreatedUtc).TotalSeconds >= 0 && (now - x.CreatedUtc).TotalSeconds < cooldown))
                        { source.Note = "Viewer is on cooldown."; continue; }
                        incoming.Status = _get().RequestMode == "automatic" ? "ready" : "pending";
                        _items.Add(incoming); changed = true;
                        source.Note = incoming.Status == "pending" ? "New request awaiting approval." : "New request automatically accepted.";
                    }
                }
                if (changed) SaveUnsafe();
                return true;
            }
        }
        catch { return false; }
    }

    private void ExpireClaimsUnsafe()
    {
        bool changed = false;
        foreach (var item in _items.Where(x => x.Status == "adding" && (DateTimeOffset.UtcNow - x.ClaimAt).TotalSeconds > 60))
        {
            item.Status = "uncertain"; item.Claim = ""; item.UpdatedUtc = DateTimeOffset.UtcNow;
            item.Note = "Player did not confirm in time. Check its queue before retrying.";
            changed = true;
        }
        if (changed) SaveUnsafe();
    }

    // One leased job at a time across all music tabs. Lost acknowledgments become
    // uncertain, never auto-retried, because the actual insertion may have happened.
    public QueueCommand? ClaimNext()
    {
        lock (_gate)
        {
            ExpireClaimsUnsafe();
            if (!_get().RequestAutoQueue || _items.Any(x => x.Status == "adding")) return null;
            var item = _items.FirstOrDefault(x => x.Status == "ready");
            if (item == null) return null;
            item.Status = "adding"; item.UpdatedUtc = item.ClaimAt = DateTimeOffset.UtcNow;
            item.Claim = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            item.Note = "Sending to the linked YouTube Music tab.";
            if (!SaveUnsafe())
            {
                // Never issue a command unless its lease is durably recorded.
                // Otherwise a crash could reload it as ready and enqueue twice.
                item.Status = "failed"; item.Claim = "";
                item.Note = "Request history could not be saved; player insertion was blocked to avoid a duplicate.";
                return null;
            }
            return new QueueCommand { Id = item.Id, Claim = item.Claim, Query = item.Song, VideoId = item.VideoId };
        }
    }

    public bool FinishClaim(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            string id = Get(e, "id", 80), claim = Get(e, "claim", 80), result = Get(e, "result", 20);
            lock (_gate)
            {
                ExpireClaimsUnsafe();
                string ackKey = id + ":" + claim;
                if (_acks.Contains(ackKey)) return true;
                var item = _items.FirstOrDefault(x => x.Id == id);
                if (item == null || item.Status != "adding" || !EqualSecret(item.Claim, claim)) return false;
                if (result == "added")
                {
                    string videoId = Get(e, "videoId", 20);
                    if (!Video.IsMatch(videoId) || (item.VideoId.Length > 0 && item.VideoId != videoId))
                    { item.Status = "uncertain"; item.Note = "The player confirmation did not match. Check the queue."; }
                    else
                    {
                        item.VideoId = videoId;
                        item.ResolvedTitle = Clean(Get(e, "title", 220), 160);
                        item.Status = "added";
                        item.Note = "Seen in the linked player's queue. Later player edits are not tracked.";
                    }
                }
                else if (result == "failed")
                {
                    item.Status = "failed";
                    item.Note = Clean(Get(e, "reason", 200), 160);
                    if (item.Note.Length == 0) item.Note = "Could not add the song. Check the player and retry.";
                }
                else
                {
                    item.Status = "uncertain";
                    item.Note = "The player did not clearly confirm. Check its queue before retrying.";
                }
                item.UpdatedUtc = DateTimeOffset.UtcNow; item.Claim = "";
                if (_acks.Count > 2000) _acks.Clear();
                _acks.Add(ackKey); SaveUnsafe();
                return true;
            }
        }
        catch { return false; }
    }

    public string StateJson(bool overlay = false)
    {
        lock (_gate)
        {
            ExpireClaimsUnsafe();
            var now = DateTimeOffset.UtcNow;
            if (overlay)
            {
                // Pending requests are never shown on stream. In automatic mode the
                // streamer has opted into displaying unreviewed accepted requests.
                var approved = _items.Where(x => x.Status is "ready" or "adding" or "added").ToList();
                return JsonSerializer.Serialize(new { total = approved.Count, items = approved.Take(8)
                    .Select(x => new { song = x.ResolvedTitle.Length > 0 ? x.ResolvedTitle : x.Song, x.Viewer, x.Platform, x.Status }).ToList() }, Wire);
            }
            return JsonSerializer.Serialize(new {
                settings = new { enabled = _get().RequestIntakeEnabled, mode = _get().RequestMode,
                    autoQueue = _get().RequestAutoQueue, cooldown = _get().RequestCooldownSeconds, limit = _get().RequestLimit },
                sources = _sources.Values.GroupBy(x => x.Platform + "|" + x.Channel, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.LastSeen).First())
                    .Select(x => new { x.Platform, x.Channel, online = (now - x.LastSeen).TotalSeconds <= 15, x.Note }).ToList(),
                items = _items.AsEnumerable().Reverse().ToList(), persistenceWarning = _persistenceWarning
            }, Wire);
        }
    }

    public bool ApplyAction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            string action = Get(e, "action", 30);
            lock (_gate)
            {
                if (action == "config")
                {
                    var s = _get();
                    bool enabledValue = s.RequestIntakeEnabled, queueValue = s.RequestAutoQueue;
                    string modeValue = s.RequestMode; int cooldownValue = s.RequestCooldownSeconds, limitValue = s.RequestLimit;
                    if (e.TryGetProperty("enabled", out var enabled))
                    { if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; enabledValue = enabled.GetBoolean(); }
                    if (e.TryGetProperty("mode", out var mode))
                    { modeValue = mode.ValueKind == JsonValueKind.String ? mode.GetString() ?? "" : ""; if (modeValue is not ("approval" or "automatic")) return false; }
                    if (e.TryGetProperty("autoQueue", out var aq))
                    { if (aq.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; queueValue = aq.GetBoolean(); }
                    if (e.TryGetProperty("cooldown", out var cool))
                    { if (cool.ValueKind != JsonValueKind.Number || !cool.TryGetInt32(out int n)) return false; cooldownValue = Math.Clamp(n, 0, 3600); }
                    if (e.TryGetProperty("limit", out var limit))
                    { if (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out int n)) return false; limitValue = Math.Clamp(n, 1, 100); }
                    s.RequestIntakeEnabled = enabledValue; s.RequestMode = modeValue; s.RequestAutoQueue = queueValue;
                    s.RequestCooldownSeconds = cooldownValue; s.RequestLimit = limitValue;
                    s.Save(); return true;
                }
                if (action == "clearHistory")
                {
                    _items.RemoveAll(x => !Active(x)); SaveUnsafe(); return true;
                }
                if (action == "manual")
                {
                    if (!TryParseSong(Get(e, "song", 250), out string song, out string videoId) || _items.Count(Active) >= _get().RequestLimit) return false;
                    var manual = new RequestRecord { Id = Guid.NewGuid().ToString("N"), Platform = "manual", Channel = "control panel", Viewer = "Streamer",
                        Song = song, VideoId = videoId, Status = _get().RequestMode == "automatic" ? "ready" : "pending" };
                    if (_items.Any(x => Active(x) && SameSong(x, manual))) return false;
                    _items.Add(manual); SaveUnsafe(); return true;
                }
                string id = Get(e, "id", 80);
                var item = _items.FirstOrDefault(x => x.Id == id);
                if (item == null || item.Status == "adding") return false;
                switch (action)
                {
                    case "approve" when item.Status == "pending": item.Status = "ready"; item.Note = "Approved."; break;
                    case "retry" when item.Status is "failed" or "uncertain": item.Status = "ready"; item.Note = "Manually returned to the request queue."; break;
                    case "reject" when item.Status is "pending" or "ready" or "failed" or "uncertain": item.Status = "rejected"; item.Note = "Rejected by the streamer."; break;
                    case "done" when item.Status is "ready" or "added" or "failed" or "uncertain": item.Status = "done"; item.Note = "Marked done; this does not remove a song from the player."; break;
                    case "delete" when item.Status is "done" or "rejected": _items.Remove(item); SaveUnsafe(); return true;
                    default: return false;
                }
                item.UpdatedUtc = DateTimeOffset.UtcNow; SaveUnsafe(); return true;
            }
        }
        catch { return false; }
    }

    private bool SaveUnsafe()
    {
        if (_loadFailed) return false; // Preserve an unreadable original instead of replacing it.
        try
        {
            // Keep active entries and bound old history; never discard live requests.
            while (_items.Count > 500)
            {
                int old = _items.FindIndex(x => !Active(x));
                if (old < 0) break;
                _items.RemoveAt(old);
            }
            string? dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, true);
            _persistenceWarning = "";
            return true;
        }
        catch
        {
            _persistenceWarning = "Could not save requests to disk; changes may be lost when the app closes.";
            return false;
        }
    }
}
