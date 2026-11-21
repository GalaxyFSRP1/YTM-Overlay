using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YtmOverlay;

public sealed class LyricLine
{
    // -1 means this line has no timestamp.
    public double Time { get; set; } = -1;
    public string Text { get; set; } = "";
}

public sealed class LyricsResult
{
    public bool Found { get; set; }
    public bool Synced { get; set; }
    public bool Instrumental { get; set; }
    public List<LyricLine> Lines { get; set; } = new();
    public string Source { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Query { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool HasLines => Lines != null && Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));
}

/// <summary>Fetches timed/plain lyrics from LRCLIB and, if needed, plain lyrics from lyrics.ovh.</summary>
public sealed class LyricsClient
{
    private static readonly HttpClient SharedHttp = CreateClient();
    private static readonly Regex LrcTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly int _minDelayMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (LyricsResult result, DateTime at)> _cache = new();
    private readonly Dictionary<string, int> _cacheGeneration = new();
    private long _lastRequestTicks = long.MinValue / 2;

    // The optional arguments make network failure and malformed responses testable without the internet.
    public LyricsClient(HttpClient? http = null, int minDelayMs = 650)
    {
        _http = http ?? SharedHttp;
        _minDelayMs = Math.Clamp(minDelayMs, 0, 5000);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "YTM-Overlay/1.3 (personal OBS overlay)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    private static string CacheKey(string title, string artist) =>
        (title.Trim() + "|" + artist.Trim()).ToLowerInvariant();

    // Explicit retry must really bypass even a recently cached no-match.
    public void Forget(string title, string artist)
    {
        string key = CacheKey(title ?? "", artist ?? "");
        lock (_cache)
        {
            _cache.Remove(key);
            _cacheGeneration.TryGetValue(key, out var old);
            _cacheGeneration[key] = old + 1;
        }
    }

    public async Task<LyricsResult> GetAsync(string title, string artist, string album, double duration)
    {
        title ??= "";
        artist ??= "";
        string key = CacheKey(title, artist);
        int generation;
        lock (_cache)
        {
            if (TryCached(key, out var cached)) return cached;
            _cacheGeneration.TryGetValue(key, out generation);
        }

        // A blocked service or an obsolete song should not leave the UI loading forever.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        bool entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            lock (_cache)
            {
                if (TryCached(key, out var cached)) return cached;
            }
            var result = await FetchAsync(title, artist, duration, deadline.Token).ConfigureAwait(false);
            lock (_cache)
            {
                _cacheGeneration.TryGetValue(key, out var currentGeneration);
                // If the user pressed Retry while this lookup ran, don't put its stale result back.
                if (generation == currentGeneration && (result.Found || result.Reason == "no-match"))
                {
                    if (_cache.Count > 300) _cache.Clear();
                    _cache[key] = (result, DateTime.UtcNow);
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return new LyricsResult { Reason = "error", Query = $"{title} / {artist}", Detail = "Lyrics lookup timed out. Check your connection and retry." };
        }
        catch (Exception ex)
        {
            return new LyricsResult { Reason = "error", Query = $"{title} / {artist}", Detail = "Lyrics lookup failed: " + ex.GetType().Name };
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    // Caller holds _cache. A miss is cached briefly, a good result for longer.
    private bool TryCached(string key, out LyricsResult result)
    {
        if (_cache.TryGetValue(key, out var hit))
        {
            double age = (DateTime.UtcNow - hit.at).TotalSeconds;
            if (age >= 0 && age < (hit.result.Found ? 6 * 3600 : 60))
            {
                result = hit.result;
                return true;
            }
        }
        result = new LyricsResult();
        return false;
    }

    private sealed class LookupStatus
    {
        public bool SearchAnswered;
        public bool HadError;
        public string Detail = "";
        public void Error(string detail)
        {
            HadError = true;
            Detail = detail;
        }
    }

    private async Task<LyricsResult> FetchAsync(string title, string artist, double duration, CancellationToken ct)
    {
        string cleanTitle = CleanQuery(title);
        string cleanArtist = CleanQuery(artist);
        string query = cleanArtist.Length > 0 ? $"{cleanTitle} / {cleanArtist}" : cleanTitle;
        if (cleanTitle.Length == 0) return new LyricsResult { Reason = "no-title", Query = query };
        var status = new LookupStatus();

        if (cleanArtist.Length > 0 && duration > 5)
        {
            string url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(cleanArtist) +
                         "&track_name=" + Uri.EscapeDataString(cleanTitle) +
                         "&duration=" + ((int)Math.Round(duration)).ToString(CultureInfo.InvariantCulture);
            var exact = await RequestJsonAsync(url, status, "LRCLIB", false, ct).ConfigureAwait(false);
            if (exact.HasValue)
            {
                var parsed = ParseItem(exact.Value, query);
                if (parsed != null && CanShow(parsed)) return parsed;
            }
        }

        string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(cleanTitle);
        if (cleanArtist.Length > 0) search += "&artist_name=" + Uri.EscapeDataString(cleanArtist);
        var candidates = await GetSearchAsync(search, status, ct).ConfigureAwait(false);
        var best = PickBest(candidates, cleanTitle, cleanArtist, duration, query);
        if (best != null && CanShow(best)) return best;

        // Search responses can contain entries with no lyrics. Broaden the search even
        // when there were entries, but none of them actually had usable text.
        if (status.SearchAnswered)
        {
            string free = (cleanTitle + " " + cleanArtist).Trim();
            candidates = await GetSearchAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(free), status, ct).ConfigureAwait(false);
            best = PickBest(candidates, cleanTitle, cleanArtist, duration, query);
            if (best != null && CanShow(best)) return best;

            // Desktop players sometimes report a channel or a list of artists instead
            // of the database's artist spelling. A title-only search can still match.
            if (cleanArtist.Length > 0)
            {
                candidates = await GetSearchAsync("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(cleanTitle), status, ct).ConfigureAwait(false);
                best = PickBest(candidates, cleanTitle, cleanArtist, duration, query);
                if (best != null && CanShow(best)) return best;
            }
        }

        if (cleanArtist.Length > 0)
        {
            var url = "https://api.lyrics.ovh/v1/" + Uri.EscapeDataString(cleanArtist) + "/" + Uri.EscapeDataString(cleanTitle);
            var fallback = await RequestJsonAsync(url, status, "lyrics.ovh", false, ct).ConfigureAwait(false);
            if (fallback.HasValue && fallback.Value.ValueKind == JsonValueKind.Object &&
                fallback.Value.TryGetProperty("lyrics", out var ly) && ly.ValueKind == JsonValueKind.String)
            {
                var lines = PlainLines(ly.GetString() ?? "");
                if (lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
                    return new LyricsResult { Found = true, Synced = false, Lines = lines, Source = "lyrics.ovh", Reason = "plain", Query = query };
            }
        }

        return new LyricsResult
        {
            Found = false,
            Reason = status.HadError ? "error" : "no-match",
            Detail = status.Detail,
            Query = query
        };
    }

    private static bool CanShow(LyricsResult r) => r.Found && (r.HasLines || r.Instrumental);

    private static LyricsResult? PickBest(List<JsonElement> items, string title, string artist, double duration, string query)
    {
        string wantedTitle = Norm(title), wantedArtist = Norm(artist);
        LyricsResult? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var el in items)
        {
            var parsed = ParseItem(el, query);
            if (parsed == null || !CanShow(parsed)) continue; // empty LRCLIB records are NOT results
            string t = Norm(GetString(el, "trackName"));
            string a = Norm(GetString(el, "artistName"));
            if (t.Length == 0 || wantedTitle.Length == 0) continue;
            bool titleMatch = t == wantedTitle ||
                              (t.Length >= 3 && wantedTitle.Length >= 3 && (t.Contains(wantedTitle) || wantedTitle.Contains(t)));
            if (!titleMatch) continue; // never select a completely unrelated song just because it has timestamps
            double score = t == wantedTitle ? 50 : 25;
            if (wantedArtist.Length > 1 && a.Length > 1)
            {
                if (a == wantedArtist) score += 35;
                else if (a.Contains(wantedArtist) || wantedArtist.Contains(a)) score += 18;
                else
                {
                    var words = wantedArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Any(x => x.Length > 2 && a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(x))) score += 8;
                }
            }
            if (parsed.Synced) score += 25;
            if (el.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number && duration > 5)
            {
                double diff = Math.Abs(dur.GetDouble() - duration);
                score += diff <= 2 ? 35 : diff <= 5 ? 20 : diff <= 15 ? 8 : diff <= 40 ? 0 : -10;
            }
            if (Regex.IsMatch(t, @"\b(live|remix|acoustic|cover|karaoke|instrumental|sped up|slowed|8d)\b")) score -= 10;
            if (score > bestScore) { best = parsed; bestScore = score; }
        }
        return best;
    }

    private static string GetString(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static LyricsResult? ParseItem(JsonElement el, string query)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
            return new LyricsResult { Found = true, Instrumental = true, Reason = "instrumental", Query = query, Source = "LRCLIB" };
        var timed = ParseLrc(GetString(el, "syncedLyrics"));
        if (timed.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
            return new LyricsResult { Found = true, Synced = true, Lines = timed, Source = "LRCLIB", Reason = "synced", Query = query };
        var plain = PlainLines(GetString(el, "plainLyrics"));
        if (plain.Count > 0)
            return new LyricsResult { Found = true, Synced = false, Lines = plain, Source = "LRCLIB", Reason = "plain", Query = query };
        return new LyricsResult { Reason = "no-lyrics", Query = query };
    }

    public static List<LyricLine> PlainLines(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => new LyricLine { Time = -1, Text = l.TrimEnd() })
            .Where(l => !string.IsNullOrWhiteSpace(l.Text)).Take(1200).ToList();

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in (lrc ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var matches = LrcTag.Matches(raw);
            if (matches.Count == 0) continue;
            string text = LrcTag.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double frac = 0;
                if (m.Groups[3].Success)
                {
                    string f = m.Groups[3].Value;
                    frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                lines.Add(new LyricLine { Time = min * 60 + sec + frac, Text = text });
                if (lines.Count >= 1200) break;
            }
            if (lines.Count >= 1200) break;
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private async Task<List<JsonElement>> GetSearchAsync(string url, LookupStatus status, CancellationToken ct)
    {
        var doc = await RequestJsonAsync(url, status, "LRCLIB", true, ct).ConfigureAwait(false);
        if (doc.HasValue && doc.Value.ValueKind == JsonValueKind.Array)
            return doc.Value.EnumerateArray().Select(e => e.Clone()).ToList();
        return new List<JsonElement>();
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        long since = Environment.TickCount64 - Interlocked.Read(ref _lastRequestTicks);
        if (since < _minDelayMs) await Task.Delay((int)(_minDelayMs - since), ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastRequestTicks, Environment.TickCount64);
    }

    private async Task<JsonElement?> RequestJsonAsync(string url, LookupStatus status, string provider, bool search, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await PaceAsync(ct).ConfigureAwait(false);
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (search) status.SearchAnswered = true;
                    return null;
                }
                if ((response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500) && attempt == 0)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    status.Error(provider + " returned HTTP " + (int)response.StatusCode + ".");
                    return null;
                }
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var parsed = JsonDocument.Parse(text);
                if (search && parsed.RootElement.ValueKind != JsonValueKind.Array)
                {
                    status.Error(provider + " returned an unexpected search response.");
                    return null;
                }
                if (search) status.SearchAnswered = true;
                return parsed.RootElement.Clone();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // HttpClient's per-request timeout also comes through as TaskCanceledException.
                status.Error(provider + " could not be reached (" + ex.GetType().Name + ").");
                return null;
            }
        }
        status.Error(provider + " did not respond.");
        return null;
    }

    public static string CleanQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s;
        t = Regex.Replace(t, @"\((?:official\s*)?(?:music\s*)?video\)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\[(?:official\s*)?(?:music\s*)?video\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\((?:official\s*)?(?:audio|music)\)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\((?:lyrics?|lyric\s*video|visuali[sz]er)\)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\[(?:lyrics?|hd|hq|4k|1080p)\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\((?:hd|hq|4k|1080p|explicit|remaster(ed)?(\s*\d{4})?)\)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\[(?:remaster(ed)?(\s*\d{4})?)\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*[-–—]\s*topic\s*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\|\s*official.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\(feat\..*?\)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\[feat\..*?\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+feat\.\s.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ");
        return t.Trim(' ', '-', '–', '—', '|', ',', '.');
    }

    // Keep Unicode letters and numbers, so non-English titles don't collapse to an empty string.
    private static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
