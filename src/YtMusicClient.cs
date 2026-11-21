using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YtmOverlay;

public sealed class QueueItem
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string Thumb { get; set; } = "";
}

/// <summary>
/// Resolves the song Windows is reporting against YouTube Music, then asks for public
/// radio suggestions. This is NOT the user's signed-in/private desktop-app queue.
/// Only unauthenticated endpoints are used (no login, no cookies).
/// </summary>
public sealed class YtMusicClient
{
    private const string Key = "AIzaSyC9XL3ZjWDDXya6X74dJoCTL-WEYFDNX30";
    private const string ClientVersion = "1.20241118.01.00";

    private static readonly HttpClient Http = CreateClient();

    private readonly Dictionary<string, (string videoId, DateTime at)> _resolved = new();
    private string _visitorData = "";
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(9) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-Goog-Api-Format-Version", "1");
        return c;
    }

    public async Task<List<QueueItem>> GetQueueAsync(string title, string artist, int max)
    {
        if (string.IsNullOrWhiteSpace(title)) return new List<QueueItem>();

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            string videoId = await ResolveVideoIdAsync(title, artist).ConfigureAwait(false);
            if (string.IsNullOrEmpty(videoId)) return new List<QueueItem>();

            var root = await PostAsync("next", new Dictionary<string, object>
            {
                ["videoId"] = videoId,
                ["playlistId"] = "RDAMVM" + videoId,
                ["isAudioOnly"] = true,
                ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
                ["enablePersistentPlaylistPanel"] = true
            }).ConfigureAwait(false);
            if (root == null) return new List<QueueItem>();

            var panel = new List<JsonElement>();
            Collect(root.RootElement, "playlistPanelVideoRenderer", panel);
            if (panel.Count == 0) return new List<QueueItem>();

            // Find where the current song sits, then read forward from there.
            int start = 0;
            for (int i = 0; i < panel.Count; i++)
            {
                bool selected = panel[i].TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.True;
                string vid = Str(panel[i], "videoId");
                if (selected || vid == videoId) { start = i + 1; break; }
            }
            if (start <= 0) start = Math.Min(1, panel.Count);

            var items = new List<QueueItem>();
            var seen = new HashSet<string>();
            for (int i = start; i < panel.Count && items.Count < max; i++)
            {
                var it = ParsePanelItem(panel[i]);
                if (it == null || string.IsNullOrWhiteSpace(it.Title)) continue;
                if (!string.IsNullOrEmpty(it.VideoId) && !seen.Add(it.VideoId)) continue;
                items.Add(it);
            }
            return items;
        }
        catch
        {
            return new List<QueueItem>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private QueueItem? ParsePanelItem(JsonElement e)
    {
        var item = new QueueItem
        {
            Title = RunsText(e, "title"),
            VideoId = Str(e, "videoId")
        };

        if (e.TryGetProperty("longBylineText", out var by) && by.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var r in runs.EnumerateArray())
            {
                string t = Str(r, "text");
                if (!string.IsNullOrWhiteSpace(t) && t.Trim() != "\u2022") parts.Add(t.Trim());
            }
            if (parts.Count > 0) item.Artist = parts[0];
            if (parts.Count > 2) item.Album = parts[1];
        }

        if (e.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            string best = "";
            foreach (var t in thumbs.EnumerateArray())
            {
                string u = Str(t, "url");
                if (u.Length > 0) best = u;
            }
            item.Thumb = best;
        }

        return item;
    }

    private async Task<string> ResolveVideoIdAsync(string title, string artist)
    {
        string cacheKey = title.Trim().ToLowerInvariant() + "|" + artist.Trim().ToLowerInvariant();
        if (_resolved.TryGetValue(cacheKey, out var hit) && (DateTime.UtcNow - hit.at).TotalHours < 6)
            return hit.videoId;

        string query = (title + " " + artist).Trim();
        var root = await PostAsync("search", new Dictionary<string, object>
        {
            ["query"] = query,
            ["params"] = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D" // songs only
        }).ConfigureAwait(false);
        string best = "";
        if (root != null)
        {
            var lists = new List<JsonElement>();
            Collect(root.RootElement, "musicResponsiveListItemRenderer", lists);

            double bestScore = -1;
            string wantTitle = Norm(title);
            string wantArtist = Norm(artist);
            foreach (var li in lists)
            {
                string vid = "";
                foreach (var ov in Elements(li, "overlay"))
                {
                    var playButtons = new List<JsonElement>();
                    Collect(ov, "playButton", playButtons);
                    foreach (var pb in playButtons)
                        foreach (var ne in Elements(pb, "navigationEndpoint"))
                            foreach (var vc in Elements(ne, "watchEndpoint"))
                            {
                                string v = Str(vc, "videoId");
                                if (v.Length > 0) { vid = v; break; }
                            }
                    if (vid.Length > 0) break;
                }
                if (vid.Length == 0)
                {
                    foreach (var ne in Elements(li, "navigationEndpoint"))
                        foreach (var vc in Elements(ne, "watchEndpoint"))
                        {
                            string v = Str(vc, "videoId");
                            if (v.Length > 0) { vid = v; break; }
                        }
                }
                if (vid.Length == 0) continue;

                var texts = new List<string>();
                CollectStrings(li, "text", texts);
                string joined = Norm(string.Join(" ", texts));
                double score = 0;
                if (joined.Contains(wantTitle)) score += 3;
                if (wantArtist.Length > 1 && joined.Contains(wantArtist)) score += 2;
                score -= joined.Length * 0.001;
                if (score > bestScore) { bestScore = score; best = vid; }
            }
        }

        if (best.Length > 0) _resolved[cacheKey] = (best, DateTime.UtcNow);
        return best;
    }

    private static string Norm(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9 ]", " ").Replace("  ", " ").Trim();

    private async Task<JsonDocument?> PostAsync(string endpoint, Dictionary<string, object> body)
    {
        foreach (var client in new[] { "WEB_REMIX", "WEB_REMIX" })
        {
            try
            {
                var ctx = new Dictionary<string, object>
                {
                    ["client"] = new Dictionary<string, object>
                    {
                        ["clientName"] = client,
                        ["clientVersion"] = ClientVersion,
                        ["hl"] = "en",
                        ["gl"] = "US",
                        ["visitorData"] = _visitorData
                    },
                    ["user"] = new Dictionary<string, object> { ["lockedSafetyMode"] = false }
                };
                var payload = new Dictionary<string, object>(body) { ["context"] = ctx };

                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(
                    $"https://music.youtube.com/youtubei/v1/{endpoint}?key={Key}&prettyPrint=false",
                    content).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var doc = JsonDocument.Parse(json);

                if (string.IsNullOrEmpty(_visitorData) &&
                    doc.RootElement.TryGetProperty("responseContext", out var rc) &&
                    rc.TryGetProperty("visitorData", out var vd))
                {
                    _visitorData = vd.GetString() ?? "";
                }
                return doc;
            }
            catch { }
        }
        return null;
    }

    // ---- generic JSON walking helpers (ytmusic responses nest unpredictably) ----

    private static void Collect(JsonElement e, string key, List<JsonElement> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(key)) { into.Add(p.Value); continue; }
                Collect(p.Value, key, into);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray()) Collect(i, key, into);
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement e, string key)
    {
        var list = new List<JsonElement>();
        Collect(e, key, list);
        return list;
    }

    private static void CollectStrings(JsonElement e, string key, List<string> into)
    {
        var els = new List<JsonElement>();
        Collect(e, key, els);
        foreach (var x in els)
            if (x.ValueKind == JsonValueKind.String)
            {
                var s = x.GetString();
                if (!string.IsNullOrEmpty(s)) into.Add(s);
            }
    }

    private static string Str(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static string RunsText(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var obj)) return "";
        if (obj.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray()) sb.Append(Str(r, "text"));
            return sb.ToString().Trim();
        }
        if (obj.TryGetProperty("simpleText", out var st) && st.ValueKind == JsonValueKind.String)
            return st.GetString() ?? "";
        return "";
    }
}
