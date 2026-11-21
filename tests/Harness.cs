using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YtmOverlay;

namespace YtmOverlay
{
    // Only the Windows media-session source is replaced for portable Linux tests.
    public sealed class SessionMonitor
    {
        private NowPlaying _current = new();
        public BrowserBridge? Bridge { get; set; }
        public NowPlaying Current
        {
            get => Bridge != null && Bridge.TryGet(out var browser, out _, out _) ? browser : Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
        public (byte[]? bytes, string hash) GetArt() => (null, "");
        public List<string> SessionDump() => new() { "test media session" };
    }
    public sealed class QueueItem
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string Thumb { get; set; } = "";
    }
    public sealed class YtMusicClient
    {
        public Task<List<QueueItem>> GetQueueAsync(string title, string artist, int max) => Task.FromResult(
            new List<QueueItem> { new() { Title = "Suggested test track", Artist = "Example", VideoId = "id" } });
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    public readonly ConcurrentQueue<string> Seen = new();
    private readonly Func<Uri, CancellationToken, Task<HttpResponseMessage>> _reply;
    public FakeHandler(Func<Uri, CancellationToken, Task<HttpResponseMessage>> reply) => _reply = reply;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Seen.Enqueue(request.RequestUri!.ToString());
        return _reply(request.RequestUri!, ct);
    }
}

internal static class Program
{
    private static int checks;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS " + label);
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage NotFound() => Json("{}", HttpStatusCode.NotFound);
    private static string Item(string title, string artist, string? synced, string? plain = null, bool instrumental = false) =>
        JsonSerializer.Serialize(new { trackName = title, artistName = artist, duration = 100, syncedLyrics = synced, plainLyrics = plain, instrumental });
    private static LyricsClient Client(FakeHandler handler) => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) }, 0);
    private static async Task WaitFor(Func<Task<bool>> condition, string label, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(40);
        }
        throw new Exception("Timed out: " + label);
    }
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static NowPlaying Song(string title = "Sample Track", string artist = "Example Artist", string id = "track-1") => new()
    {
        Title = title, Artist = artist, TrackId = id, Duration = 100, Position = 8, Rate = 1, Playing = false, SourceLabel = "YouTube Music"
    };
    private static FakeHandler LiveHandler() => new((uri, ct) =>
    {
        string u = Uri.UnescapeDataString(uri.ToString());
        if (uri.Host == "lrclib.net" && uri.AbsolutePath == "/api/get")
            return Task.FromResult(Json(Item("Sample Track", "Example Artist", "[00:00.00]First test line\n[00:05.00]Second test line\n[00:15.00]Third test line")));
        if (uri.Host == "lrclib.net") return Task.FromResult(Json("[]"));
        return Task.FromResult(NotFound());
    });

    public static async Task<int> Main(string[] args)
    {
        // Portable tests never read or modify a real user's app settings.
        AppSettings.TestingDirectory = Path.Combine(Path.GetTempPath(), "ytm-overlay-harness-" + Environment.ProcessId);
        if (args.Length > 0 && args[0] == "serve")
        {
            int port = args.Length > 1 ? int.Parse(args[1]) : 7511;
            var st = new AppSettings { ShowLyrics = true, ShowQueue = true };
            st.Sanitize();
            var bridge = new BrowserBridge(st.BrowserBridgeKey);
            var monitor = new SessionMonitor { Current = Song(), Bridge = bridge };
            var temp = Path.Combine(Path.GetTempPath(), "overlay-test-manual-" + port + ".json");
            var requestTemp = Path.Combine(Path.GetTempPath(), "overlay-test-requests-" + port + ".json");
            try { File.Delete(temp); File.Delete(requestTemp); } catch { }
            var requests = new SongRequests(() => st, requestTemp);
            var server = new WebServer(monitor, new YtMusicClient(), Client(LiveHandler()), new ManualLyricsStore(temp), () => st, bridge, requests);
            if (!server.Start(port)) throw new Exception("Could not start test server");
            Console.WriteLine("TEST SERVER READY " + port);
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Length > 0 && args[0] == "real")
        {
            var client = new LyricsClient();
            foreach (var song in new[] { ("No Surprises", "Radiohead", 239d), ("Blinding Lights", "The Weeknd", 200d), ("Creep (Official Video)", "Radiohead", 239d) })
            {
                var r = await client.GetAsync(song.Item1, song.Item2, "", song.Item3);
                Console.WriteLine($"{song.Item1}: found={r.Found}, synced={r.Synced}, lines={r.Lines.Count}, source={r.Source}, reason={r.Reason}, detail={r.Detail}");
                Check(r.Found && r.HasLines, "real service " + song.Item1);
            }
            return 0;
        }
        try
        {
            await RunTests();
            Console.WriteLine("ALL " + checks + " CHECKS PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunTests()
    {
        TestTimelineClock();
        Check(LyricsClient.CleanQuery("Creep (Official Video)") == "Creep", "clean YouTube title");
        var parsed = LyricsClient.ParseLrc("[00:01.50][00:03.25] repeated\n[00:02]middle\n[ar:test]");
        Check(parsed.Count == 3 && parsed[0].Time == 1.5 && parsed[1].Time == 2 && parsed[2].Time == 3.25, "LRC multiple timestamps, fractions and sort");
        var oldSettings = new AppSettings { ShowLyrics = true, LyricsSource = "off", LyricsOffsetSeconds = 999 };
        oldSettings.Sanitize();
        Check(oldSettings.ShowLyrics && oldSettings.LyricsSource == "lrclib", "old hidden source switch repaired when checkbox is enabled");
        Check(oldSettings.LyricsOffsetSeconds == 30, "timing adjustment clamped");
        var squareCorners = new AppSettings { Corner = 0 };
        squareCorners.Sanitize();
        Check(squareCorners.Corner == 0, "square-corner setting remains usable");
        var intentionallyOff = new AppSettings { ShowLyrics = false, LyricsSource = "off" };
        intentionallyOff.Sanitize();
        Check(!intentionallyOff.ShowLyrics, "explicit user choice to hide main lyrics is preserved");

        var empty = Item("Sample Track", "Example Artist", null, null);
        var good = Item("Sample Track", "Example Artist", "[00:01]hello\n[00:05]world");
        var poison = new FakeHandler((uri, ct) => Task.FromResult(uri.AbsolutePath == "/api/get"
            ? Json(empty) : Json("[" + empty + "," + good + "]")));
        var client = Client(poison);
        var r1 = await client.GetAsync("Sample Track", "Example Artist", "", 100);
        Check(r1.Found && r1.Synced && r1.Lines.Count == 2, "empty exact result cannot beat search result with lyrics");
        int n = poison.Seen.Count;
        var r2 = await client.GetAsync("Sample Track", "Example Artist", "", 101.4);
        Check(r2.HasLines && poison.Seen.Count == n, "small duration drift reuses successful cache");
        client.Forget("Sample Track", "Example Artist");
        await client.GetAsync("Sample Track", "Example Artist", "", 100);
        Check(poison.Seen.Count > n, "Retry forgets cached result");

        var fallback = new FakeHandler((uri, ct) => Task.FromResult(uri.Host == "api.lyrics.ovh" ? Json("{\"lyrics\":\"First plain line\\nSecond plain line\"}") : uri.AbsolutePath == "/api/get" ? NotFound() : Json("[]")));
        var rf = await Client(fallback).GetAsync("Some song", "Some artist", "", 100);
        Check(rf.HasLines && !rf.Synced && rf.Source == "lyrics.ovh" && rf.Lines.Count == 2, "plain fallback lyrics source");

        var unavailable = new FakeHandler((uri, ct) => throw new HttpRequestException("Simulated network down"));
        var re = await Client(unavailable).GetAsync("Offline", "Artist", "", 100);
        Check(!re.Found && re.Reason == "error" && re.Detail.Contains("reached"), "connection failures are not falsely reported as no match");

        var broaden = new FakeHandler((uri, ct) =>
        {
            string decoded = Uri.UnescapeDataString(uri.Query);
            if (uri.AbsolutePath == "/api/get") return Task.FromResult(NotFound());
            if (uri.Host == "lrclib.net" && decoded.StartsWith("?track_name=") && !decoded.Contains("artist_name="))
                return Task.FromResult(Json("[" + Item("星空", "歌手", "[00:01]First\n[00:02]Second") + "]"));
            return Task.FromResult(uri.Host == "lrclib.net" ? Json("[]") : NotFound());
        });
        var unicode = await Client(broaden).GetAsync("星空", "Unknown channel", "", 100);
        Check(unicode.HasLines && unicode.Synced, "title-only broadening preserves non-Latin titles");

        string path = Path.Combine(Path.GetTempPath(), "manual-lyrics-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ManualLyricsStore(path);
            store.Set("Song A", "Artist A", "[00:01.25]One\n[00:02.50]Two");
            Check(store.TryGet("song a", "artist a", out var manual) && manual.Synced && manual.Lines[0].Time == 1.25, "manual timed lyrics saved and matched to the right song");
            Check(!store.TryGet("Song B", "Artist A", out _), "manual lyrics never leak into another track");
            var reloaded = new ManualLyricsStore(path);
            Check(reloaded.TryGet("Song A", "Artist A", out var reloadedResult) && reloadedResult.Lines.Count == 2, "saved lyrics survive restart");
            reloaded.Set("Song A", "Artist A", "One plain line\nAnother plain line");
            Check(reloaded.TryGet("Song A", "Artist A", out var plain) && !plain.Synced && plain.Lines.Count == 2, "manual plain lyrics supported");
            Check(reloaded.Remove("Song A", "Artist A") && !new ManualLyricsStore(path).TryGet("Song A", "Artist A", out _), "remove saved lyrics persists");
        }
        finally { try { File.Delete(path); } catch { } }

        await TestWebServer();
        await TestBrowserBridge();
        await TestSongRequests();
        await TestV001Features();
        await TestNoMatchRetry();
        await TestSlowSongRace();
    }

    private static bool Near(double a, double b, double tolerance = 0.03) => Math.Abs(a - b) <= tolerance;

    private static void TestTimelineClock()
    {
        var t = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var c = new TimelineClock();
        c.Observe(10, 100, 1, true, t.AddSeconds(-5), t, 100, true);
        Check(Near(c.At(100), 15), "timeline projects Windows Position from LastUpdatedTime");
        c.Observe(10, 100, 1, true, t.AddSeconds(-5), t.AddSeconds(1), 101, false);
        Check(Near(c.At(101), 16), "unchanged stale Windows snapshot never rewinds");
        c.Observe(10, 100, 1, true, t.AddSeconds(2), t.AddSeconds(2), 102, false);
        Check(Near(c.At(102), 17), "repeated raw position with a fresh timestamp also cannot rewind");
        long beforeSeek = c.Revision;
        c.Observe(5, 100, 1, true, t.AddSeconds(3), t.AddSeconds(3), 103, false);
        Check(Near(c.At(103), 5) && c.Revision > beforeSeek, "genuine backward seek snaps and changes revision");
        c.Observe(8, 100, 1, false, t.AddSeconds(6), t.AddSeconds(6), 106, false);
        Check(Near(c.At(109), 8), "pause freezes at the reported position");
        c.Observe(8, 100, 1, true, t.AddSeconds(10), t.AddSeconds(10), 110, false);
        Check(Near(c.At(112), 10), "resume continues from the frozen position");
        c.Observe(10, 100, 2, true, t.AddSeconds(12), t.AddSeconds(12), 112, false);
        Check(Near(c.At(113), 12), "playback speed is respected");
        c.Observe(99, 100, 2, true, t.AddSeconds(14), t.AddSeconds(14), 114, false);
        Check(Near(c.At(115), 100), "forward seek and duration clamp");
        c.Observe(0, 200, 1, true, t.AddSeconds(16), t.AddSeconds(16), 116, true);
        Check(Near(c.At(116), 0), "new song resets the clock");
        c.ObserveUnavailable(200, 1, false, 117, false);
        Check(Near(c.At(120), 1), "temporary missing timeline preserves pause position");

        var bad = new TimelineClock();
        bad.Observe(20, 100, 1, true, null, t, 0, true);
        bad.Observe(20, 100, 1, true, null, t.AddSeconds(2), 2, false);
        Check(Near(bad.At(2), 22) && !bad.TimestampTrusted, "missing timestamp uses a monotonic local clock");
        var future = new TimelineClock();
        future.Observe(12, 100, 1, true, t.AddHours(1), t, 0, true);
        Check(Near(future.At(3), 15) && !future.TimestampTrusted, "invalid future timestamp is not projected");
        var outOfOrder = new TimelineClock();
        outOfOrder.Observe(20, 100, 1, true, t, t, 0, true);
        outOfOrder.Observe(5, 100, 1, true, t.AddSeconds(-2), t.AddSeconds(1), 1, false);
        Check(Near(outOfOrder.At(1), 21), "older out-of-order snapshot is ignored");
        outOfOrder.Observe(6, 100, 1, true, t.AddSeconds(-1), t.AddSeconds(2), 2, false);
        Check(Near(outOfOrder.At(2), 22), "a series of old snapshots cannot reset the timestamp watermark");
        var inconsistent = new TimelineClock();
        inconsistent.Observe(10, 100, 1, true, t, t, 0, true);
        inconsistent.Observe(11, 100, 1, true, t, t.AddSeconds(1), 1, false);
        Check(Near(inconsistent.At(1), 11) && !inconsistent.TimestampTrusted, "moving position with frozen timestamp is not double-projected");
        var jitter = new TimelineClock();
        jitter.Observe(10, 100, 1, true, t, t, 0, true);
        jitter.Observe(10.8, 100, 1, true, t.AddSeconds(1), t.AddSeconds(1), 1, false);
        Check(Near(jitter.At(1), 11), "small backward jitter is smoothed without freezing");
        var stalePause = new TimelineClock();
        stalePause.Observe(10, 100, 1, true, t, t, 0, true);
        stalePause.Observe(10, 100, 1, false, t.AddSeconds(4), t.AddSeconds(4), 4, false);
        Check(Near(stalePause.At(10), 14), "pause with stale raw value holds the projected position");
    }

    private static async Task TestBrowserBridge()
    {
        int port = FreePort();
        string path = Path.Combine(Path.GetTempPath(), "bridge-" + Guid.NewGuid().ToString("N") + ".json");
        var st = new AppSettings { ShowLyrics = false, ShowQueue = false, QueueCount = 0 };
        st.Sanitize();
        string key = st.BrowserBridgeKey;
        var bridge = new BrowserBridge(key);
        var monitor = new SessionMonitor { Current = Song(), Bridge = bridge };
        var server = new WebServer(monitor, new YtMusicClient(), Client(LiveHandler()), new ManualLyricsStore(path), () => st, bridge);
        Check(server.Start(port), "browser bridge test server starts");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        async Task<HttpResponseMessage> Post(string target, string origin, string json)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(json, Encoding.UTF8, "text/plain") };
            if (origin.Length > 0) req.Headers.TryAddWithoutValidation("Origin", origin);
            return await http.SendAsync(req);
        }
        string Payload(string secret, bool known, bool ad = false, bool playing = true, object? items = null) => JsonSerializer.Serialize(new {
            key = secret, title = "Browser track", artist = "Browser artist", videoId = "video1",
            art = "https://evil.example/not-an-image", position = 12.0, duration = 100.0, rate = 1.0,
            playing, ad, queueKnown = known, sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            items = items ?? new[] { new { title = "First actually queued", artist = "A", videoId = "b", thumb = "https://i.ytimg.com/a.jpg" },
                                     new { title = "Second actually queued", artist = "B", videoId = "c", thumb = "https://evil.example/x" } }
        });
        try
        {
            string setup = await http.GetStringAsync("connect");
            Check(setup.Contains("YTM Live Sync") && setup.Contains("javascript:"), "integrated setup page contains the bookmark without extra files");
            using (var badHost = new HttpRequestMessage(HttpMethod.Get, "api/state"))
            {
                badHost.Headers.Host = "attacker.example:" + port;
                using var response = await http.SendAsync(badHost);
                Check(response.StatusCode == HttpStatusCode.BadRequest, "DNS-rebinding Host is rejected");
            }
            using (var evil = await Post("api/settings", "https://evil.example", "{\"showLyrics\":true}"))
                Check(evil.StatusCode == HttpStatusCode.Forbidden && !st.ShowLyrics, "cross-site settings mutation rejected");
            using (var badOrigin = await Post("api/bridge", "https://evil.example", Payload(key, true)))
                Check(badOrigin.StatusCode == HttpStatusCode.Forbidden, "browser link rejects other origins");
            using (var noOrigin = await Post("api/bridge", "", Payload(key, true)))
                Check(noOrigin.StatusCode == HttpStatusCode.Forbidden, "browser link requires YouTube Music origin");
            using (var wrongKey = await Post("api/bridge", "https://music.youtube.com", Payload("wrong", true)))
                Check(wrongKey.StatusCode == HttpStatusCode.Forbidden, "browser link rejects incorrect pairing key");
            using (var ok = await Post("api/bridge", "https://music.youtube.com", Payload(key, true)))
                Check(ok.IsSuccessStatusCode && ok.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://music.youtube.com", "paired YouTube Music browser data accepted with narrow CORS");
            await Task.Delay(250);
            using (var state = JsonDocument.Parse(await http.GetStringAsync("api/queue-state")))
            {
                var root = state.RootElement; var now = root.GetProperty("now"); var q = root.GetProperty("queue");
                Check(now.GetProperty("title").GetString() == "Browser track" && now.GetProperty("position").GetDouble() > 12.15,
                    "direct browser position advances smoothly between updates");
                Check(now.GetProperty("art").GetString() == "" && q.GetProperty("items")[1].GetProperty("thumb").GetString() == "", "untrusted artwork hosts are rejected");
                Check(q.GetProperty("source").GetString() == "browser" && q.GetProperty("items")[0].GetProperty("title").GetString() == "First actually queued", "actual browser Up next overrides public suggestions");
                Check(root.GetProperty("settings").GetProperty("showQueue").GetBoolean() && root.GetProperty("settings").GetProperty("queueCount").GetInt32() == 4, "queue-only source works even when main queue is off and count is zero");
                Check(!root.ToString().Contains(key, StringComparison.Ordinal), "pairing key is not present in overlay state");
            }
            string debug = await http.GetStringAsync("api/debug");
            Check(!debug.Contains(key, StringComparison.Ordinal) && debug.Contains("clockSource"), "debug exposes clock diagnostics without exposing pairing key");
            using (var patch = await Post("api/settings", "", "{\"browserBridgeKey\":\"attacker\"}"))
                Check(patch.IsSuccessStatusCode && st.BrowserBridgeKey == key, "settings API cannot overwrite the pairing key");
            using (var ad = await Post("api/bridge", "https://music.youtube.com", Payload(key, true, ad: true))) Check(ad.IsSuccessStatusCode, "ad snapshot accepted");
            var ad1 = monitor.Current;
            await Task.Delay(200);
            var ad2 = monitor.Current;
            Check(!ad2.Playing && Math.Abs(ad1.Position - ad2.Position) < .03, "ad freezes song clock instead of showing ad timing");
            using (var empty = await Post("api/bridge", "https://music.youtube.com", Payload(key, true, items: Array.Empty<object>()))) Check(empty.IsSuccessStatusCode, "empty real queue accepted");
            using (var state = JsonDocument.Parse(await http.GetStringAsync("api/queue-state")))
                Check(state.RootElement.GetProperty("queue").GetProperty("source").GetString() == "browser" &&
                      state.RootElement.GetProperty("queue").GetProperty("items").GetArrayLength() == 0, "known empty real queue is not replaced with suggestions");
            using (var unknown = await Post("api/bridge", "https://music.youtube.com", Payload(key, false))) Check(unknown.IsSuccessStatusCode, "unknown page queue state accepted");
            await WaitFor(async () =>
            {
                using var state = JsonDocument.Parse(await http.GetStringAsync("api/queue-state"));
                return state.RootElement.GetProperty("queue").GetProperty("source").GetString() == "suggestions" &&
                       state.RootElement.GetProperty("queue").GetProperty("items").GetArrayLength() > 0;
            }, "honest suggested fallback");
            Check(true, "unknown actual queue is labeled suggestions rather than misrepresented");
            using (var stop = await Post("api/bridge", "https://music.youtube.com", JsonSerializer.Serialize(new { key, stop = true })))
                Check(stop.IsSuccessStatusCode && monitor.Current.TrackId == "track-1", "disconnect falls back to Windows session");
        }
        finally { server.Stop(); try { File.Delete(path); } catch { } }
    }

    private static async Task TestSongRequests()
    {
        Check(SongRequests.TryParseSong("https://youtu.be/dQw4w9WgXcQ?t=3", out var canonical, out var video) &&
              video == "dQw4w9WgXcQ" && canonical == "https://music.youtube.com/watch?v=dQw4w9WgXcQ", "single video URL is validated and canonicalized");
        Check(!SongRequests.TryParseSong("https://evil.example/watch?v=dQw4w9WgXcQ", out _, out _) &&
              !SongRequests.TryParseSong("http://youtu.be/dQw4w9WgXcQ", out _, out _) &&
              !SongRequests.TryParseSong("https://youtube.com/watch?v=x", out _, out _), "foreign, insecure and malformed request URLs rejected");
        Check(SongRequests.TryParseSong("Song title - Artist", out _, out var noVideo) && noVideo == "", "song search text accepted");
        var st = new AppSettings(); st.Sanitize();
        Check(!st.RequestIntakeEnabled && st.RequestMode == "approval" && !st.RequestAutoQueue, "requests and player insertion are opt-in; approval is default");
        Check(st.ChatBridgeKey != st.BrowserBridgeKey, "chat and music links use different secrets");
        string temp = Path.Combine(Path.GetTempPath(), "request-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string store = Path.Combine(temp, "requests.json");
        var manager = new SongRequests(() => st, store);
        var bridge = new BrowserBridge(st.BrowserBridgeKey);
        var monitor = new SessionMonitor { Current = Song(), Bridge = bridge };
        var server = new WebServer(monitor, new YtMusicClient(), Client(LiveHandler()), new ManualLyricsStore(Path.Combine(temp,"lyrics.json")), () => st, bridge, manager);
        int port = FreePort(); Check(server.Start(port), "request HTTP server starts");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpResponseMessage> Post(string route, string origin, object data)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "text/plain") };
            if (origin.Length > 0) req.Headers.TryAddWithoutValidation("Origin", origin);
            return await http.SendAsync(req);
        }
        object Chat(string platform, string key, string id, string author, string text) => new { key, platform, session = "session-12345", channel = "creator", messages = new[] { new { id, author, text } } };
        async Task<JsonDocument> State() => JsonDocument.Parse(await http.GetStringAsync("api/request-state"));
        async Task<int> Count() { using var d = await State(); return d.RootElement.GetProperty("items").GetArrayLength(); }
        string local = $"http://127.0.0.1:{port}";
        string music = "https://music.youtube.com";
        try
        {
            string setup = await http.GetStringAsync("chat-connect");
            Check(setup.Contains("YTM Chat Requests") && setup.Contains(st.ChatBridgeKey) && !setup.Contains(st.BrowserBridgeKey), "chat setup includes only its separate private key");
            string musicSetup = await http.GetStringAsync("connect");
            Check(musicSetup.Contains(st.BrowserBridgeKey) && !musicSetup.Contains(st.ChatBridgeKey), "music setup does not expose the chat key");
            using (var foreign = await Post("api/chat", "https://evil.example", Chat("twitch", st.ChatBridgeKey,"a","Alice","!sr Song A")))
                Check(foreign.StatusCode == HttpStatusCode.Forbidden, "chat rejects untrusted origins");
            using (var wrong = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", "wrong","a","Alice","!sr Song A")))
                Check(wrong.StatusCode == HttpStatusCode.Forbidden, "chat rejects wrong secret");
            using (var mismatch = await Post("api/chat", "https://www.youtube.com", Chat("twitch", st.ChatBridgeKey,"a","Alice","!sr Song A")))
                Check(mismatch.StatusCode == HttpStatusCode.Forbidden, "platform identity must match chat origin");
            using (var stopped = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"a","Alice","!sr Song A")))
                Check(stopped.IsSuccessStatusCode && await Count() == 0, "paused request intake ignores chat commands");
            using (var cross = await Post("api/request-action", "https://evil.example", new { action = "config", enabled = true }))
                Check(cross.StatusCode == HttpStatusCode.Forbidden && !st.RequestIntakeEnabled, "cross-site request control is blocked");
            using (var badConfig = await Post("api/request-action", local, new { action = "config", enabled = true, cooldown = "bad" }))
                Check(!badConfig.IsSuccessStatusCode && !st.RequestIntakeEnabled, "invalid request configuration has no partial effects");
            using (var settingsKey = await Post("api/settings", local, new { chatBridgeKey = "attacker", requestAutoQueue = true }))
                Check(settingsKey.IsSuccessStatusCode && st.ChatBridgeKey != "attacker" && !st.RequestAutoQueue, "generic settings cannot change chat key or request controls");
            using (var enable = await Post("api/request-action", local, new { action = "config", enabled = true, mode = "approval", cooldown = 60, limit = 50 }))
                Check(enable.IsSuccessStatusCode && st.RequestIntakeEnabled, "request intake can be enabled locally");
            using (var twitch = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"b","Alice","!sr Song A")))
                Check(twitch.IsSuccessStatusCode && await Count() == 1, "Twitch request reaches approval inbox");
            using (var replay = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"b","Alice","!sr Song A")))
                Check(replay.IsSuccessStatusCode && await Count() == 1, "replayed message id is deduplicated");
            using (var dupe = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"c","Bob","!sr song a")))
                Check(dupe.IsSuccessStatusCode && await Count() == 1, "duplicate song across viewers is blocked");
            using (var viewer = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"d","Alice","!sr Other song")))
                Check(viewer.IsSuccessStatusCode && await Count() == 1, "one outstanding request per viewer is enforced");
            using (var badurl = await Post("api/chat", "https://kick.com", Chat("kick", st.ChatBridgeKey,"e","C","!sr https://evil.example/x")))
                Check(badurl.IsSuccessStatusCode && await Count() == 1, "chat cannot submit arbitrary external URLs");
            using (var kick = await Post("api/chat", "https://kick.com", Chat("kick", st.ChatBridgeKey,"f","C","!songrequest Kick Song")))
                Check(kick.IsSuccessStatusCode && await Count() == 2, "Kick request reaches inbox");
            using (var yt = await Post("api/chat", "https://www.youtube.com", Chat("youtube", st.ChatBridgeKey,"g","D","!sr YouTube Song")))
                Check(yt.IsSuccessStatusCode && await Count() == 3, "YouTube Live request reaches inbox");
            string itemId;
            using (var state = await State())
            {
                var items = state.RootElement.GetProperty("items");
                itemId = items.EnumerateArray().First(x => x.GetProperty("viewer").GetString() == "Alice").GetProperty("id").GetString()!;
                Check(items.EnumerateArray().All(x => x.GetProperty("status").GetString() == "pending"), "approval mode keeps all requests pending");
                Check(state.RootElement.GetProperty("sources").GetArrayLength() == 3, "separate platform heartbeat statuses are shown");
                Check(!state.RootElement.ToString().Contains(st.ChatBridgeKey) && !state.RootElement.ToString().Contains(st.BrowserBridgeKey), "request state excludes both secrets");
            }
            using (var publicBefore = JsonDocument.Parse(await http.GetStringAsync("api/request-overlay")))
                Check(publicBefore.RootElement.GetProperty("items").GetArrayLength() == 0, "pending content never appears in request OBS source");
            using (var approve = await Post("api/request-action", local, new { action = "approve", id = itemId }))
                Check(approve.IsSuccessStatusCode, "streamer can approve a pending request");
            using (var publicAfter = JsonDocument.Parse(await http.GetStringAsync("api/request-overlay")))
                Check(publicAfter.RootElement.GetProperty("items").GetArrayLength() == 1, "approved content appears in request OBS source");
            object ready = new { key = st.BrowserBridgeKey, title = "Music", artist = "Artist", videoId = "fJ9rUzIMcZQ", position = 2, duration = 200, playing = true, canQueue = true };
            using (var noAuto = await Post("api/bridge", music, ready))
            using (var doc = JsonDocument.Parse(await noAuto.Content.ReadAsStringAsync()))
                Check(!doc.RootElement.TryGetProperty("command", out _), "approval alone does not secretly enqueue without opt-in");
            using (var optIn = await Post("api/request-action", local, new { action = "config", autoQueue = true }))
                Check(optIn.IsSuccessStatusCode, "experimental player insertion can be enabled");
            using (var impostor = await Post("api/bridge", music, new { key = st.ChatBridgeKey, title = "Music", canQueue = true }))
                Check(impostor.StatusCode == HttpStatusCode.Forbidden, "chat key cannot claim music-player commands");
            string claim;
            using (var command = await Post("api/bridge", music, ready))
            using (var doc = JsonDocument.Parse(await command.Content.ReadAsStringAsync()))
            {
                var job = doc.RootElement.GetProperty("command"); claim = job.GetProperty("claim").GetString()!;
                Check(job.GetProperty("id").GetString() == itemId && claim.Length == 32, "approved request is leased to linked music browser");
            }
            using (var concurrent = await Post("api/bridge", music, ready))
            using (var doc = JsonDocument.Parse(await concurrent.Content.ReadAsStringAsync()))
                Check(!doc.RootElement.TryGetProperty("command", out _), "only one player job may be in flight");
            using (var invalidAck = await Post("api/bridge/ack", music, new { key = st.BrowserBridgeKey, id = itemId, claim = "wrong", result = "added", videoId = "dQw4w9WgXcQ" }))
                Check(invalidAck.StatusCode == HttpStatusCode.Forbidden, "wrong lease cannot acknowledge an insertion");
            using (var wrongAckOrigin = await Post("api/bridge/ack", "https://kick.com", new { key = st.BrowserBridgeKey, id = itemId, claim, result = "added", videoId = "dQw4w9WgXcQ" }))
                Check(wrongAckOrigin.StatusCode == HttpStatusCode.Forbidden, "chat origin cannot acknowledge player commands");
            object ack = new { key = st.BrowserBridgeKey, id = itemId, claim, result = "added", videoId = "dQw4w9WgXcQ", title = "Resolved result" };
            using (var success = await Post("api/bridge/ack", music, ack)) Check(success.IsSuccessStatusCode, "valid player confirmation is accepted");
            using (var repeat = await Post("api/bridge/ack", music, ack)) Check(repeat.IsSuccessStatusCode, "lost acknowledgment can be repeated idempotently");
            using (var state = await State())
            {
                var item = state.RootElement.GetProperty("items").EnumerateArray().First(x => x.GetProperty("id").GetString() == itemId);
                Check(item.GetProperty("status").GetString() == "added" && item.GetProperty("resolvedTitle").GetString() == "Resolved result", "only player confirmation marks a request added");
                Check(!state.RootElement.ToString().Contains(claim), "command lease is not exposed in dashboard state");
            }
            using (var dupeAfterResolve = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"dupe1","OtherViewer","!sr Song A")))
                Check(dupeAfterResolve.IsSuccessStatusCode && await Count() == 3, "resolved text requests still suppress the original duplicate text");
            using (var dupeId = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"dupe2","OtherViewer","!sr https://youtu.be/dQw4w9WgXcQ")))
                Check(dupeId.IsSuccessStatusCode && await Count() == 3, "resolved song id blocks a duplicate direct link");
            using (var done = await Post("api/request-action", local, new { action = "done", id = itemId }))
                Check(done.IsSuccessStatusCode, "added song can be marked done without altering the player");
            using (var cool = await Post("api/chat", "https://www.twitch.tv", Chat("twitch", st.ChatBridgeKey,"cool","Alice","!sr Another Song")))
                Check(cool.IsSuccessStatusCode && await Count() == 3, "viewer cooldown still applies after marking a request done");
            using (var config = await Post("api/request-action", local, new { action = "config", mode = "automatic" }))
                Check(config.IsSuccessStatusCode, "automatic handling can be selected");
            using (var auto = await Post("api/chat", "https://kick.com", Chat("kick", st.ChatBridgeKey,"h","NewViewer","!sr Automatic Song")))
                Check(auto.IsSuccessStatusCode, "automatic-mode chat accepted");
            string autoId;
            using (var state = await State())
            {
                var item = state.RootElement.GetProperty("items").EnumerateArray().First(x => x.GetProperty("viewer").GetString() == "NewViewer");
                autoId = item.GetProperty("id").GetString()!;
                Check(item.GetProperty("status").GetString() == "ready", "automatic mode accepts new requests without approval");
            }
            var lost = manager.ClaimNext();
            Check(lost != null && lost.Id == autoId, "automatic accepted request can be leased");
            var restarted = new SongRequests(() => st, store);
            using (var reload = JsonDocument.Parse(restarted.StateJson()))
            {
                var item = reload.RootElement.GetProperty("items").EnumerateArray().First(x => x.GetProperty("id").GetString() == autoId);
                Check(item.GetProperty("status").GetString() == "uncertain" && restarted.ClaimNext() == null,
                    "restart during insertion becomes uncertain and is never replayed automatically");
            }
            Check(restarted.ApplyAction(JsonSerializer.Serialize(new { action = "retry", id = autoId })), "uncertain request requires explicit retry");
            var retry = restarted.ClaimNext();
            Check(retry != null && restarted.FinishClaim(JsonSerializer.Serialize(new { id = retry.Id, claim = retry.Claim, result = "failed", reason = "No result" })), "failed insertion is recorded without marking added");
            using (var reload = JsonDocument.Parse(restarted.StateJson()))
                Check(reload.RootElement.GetProperty("items").EnumerateArray().First(x => x.GetProperty("id").GetString() == autoId).GetProperty("status").GetString() == "failed", "failed status persists for manual retry");
            var raceSettings = new AppSettings { RequestIntakeEnabled = true, RequestMode = "automatic", RequestCooldownSeconds = 0 }; raceSettings.Sanitize();
            var race = new SongRequests(() => raceSettings, Path.Combine(temp, "race.json"));
            await Task.WhenAll(Enumerable.Range(0,40).Select(i => Task.Run(() => race.ReceiveChat(JsonSerializer.Serialize(Chat("twitch",raceSettings.ChatBridgeKey,"id"+i,"Viewer"+i,"!sr Same Song")),"twitch"))));
            using (var doc = JsonDocument.Parse(race.StateJson()))
                Check(doc.RootElement.GetProperty("items").GetArrayLength() == 1, "concurrent duplicate submissions are serialized");
            var blockedPath = Path.Combine(temp, "blocked"); Directory.CreateDirectory(blockedPath);
            raceSettings.RequestAutoQueue = true;
            var blocked = new SongRequests(() => raceSettings, blockedPath);
            Check(blocked.ApplyAction(JsonSerializer.Serialize(new { action = "manual", song = "Disk failure test" })) && blocked.ClaimNext() == null,
                "player insertion fails closed if the lease cannot be persisted");
            Check(blocked.StateJson().Contains("could not be saved", StringComparison.OrdinalIgnoreCase), "disk failure is visible to the streamer");
            string corruptPath = Path.Combine(temp, "corrupt.json"); File.WriteAllText(corruptPath, "{broken");
            var corrupt = new SongRequests(() => raceSettings, corruptPath);
            corrupt.ApplyAction(JsonSerializer.Serialize(new { action = "manual", song = "Corruption test" }));
            Check(corrupt.ClaimNext() == null && File.ReadAllText(corruptPath) == "{broken", "unreadable request history is preserved and blocks player insertion");
            string nullPath = Path.Combine(temp, "null.json"); File.WriteAllText(nullPath, "null");
            var nullHistory = new SongRequests(() => raceSettings, nullPath);
            nullHistory.ApplyAction(JsonSerializer.Serialize(new { action = "manual", song = "Null history test" }));
            Check(nullHistory.ClaimNext() == null && File.ReadAllText(nullPath) == "null", "invalid null history is preserved and cannot launch queue commands");
            Check(File.Exists(store), "request history is persisted locally");
        }
        finally { server.Stop(); try { Directory.Delete(temp, true); } catch { } }
    }

    private static async Task TestV001Features()
    {
        var upgraded = JsonSerializer.Deserialize<AppSettings>("{\"RequestIntakeEnabled\":true}")!;
        upgraded.Sanitize();
        Check(upgraded.RequestTwitchEnabled && upgraded.RequestYouTubeEnabled && upgraded.RequestKickEnabled && upgraded.RequestBlockedTerms == "",
            "upgrading old settings preserves all three platform defaults and an empty filter");
        Check(SongRequests.TryNormalizeBlockedTerms(" Bad Phrase \r\nＢＡＤ ＰＨＲＡＳＥ\nSpoiler", out var normalized) && normalized == "Bad Phrase\nSpoiler",
            "blocked phrases normalize Unicode, trim and deduplicate");
        Check(!SongRequests.TryNormalizeBlockedTerms("x", out _) && !SongRequests.TryNormalizeBlockedTerms(string.Join("\n", Enumerable.Range(0, 31).Select(i => "term" + i)), out _),
            "invalid or oversized blocked-phrase lists are rejected");
        string expanded = string.Join("\n", Enumerable.Range(0, 21).Select(i => new string('ﬃ', 19) + i.ToString("D2")));
        Check(!SongRequests.TryNormalizeBlockedTerms(expanded, out _), "Unicode expansion cannot create an oversized persisted filter");
        var corruptSettings = new AppSettings { RequestIntakeEnabled = true, RequestBlockedTerms = "x" }; corruptSettings.Sanitize();
        Check(!corruptSettings.RequestIntakeEnabled, "invalid persisted filter fails closed by pausing request intake");

        string temp = Path.Combine(Path.GetTempPath(), "ytm-v001-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var s = new AppSettings { RequestIntakeEnabled = true, RequestMode = "automatic", RequestCooldownSeconds = 0,
                RequestKickEnabled = false, RequestBlockedTerms = "bad phrase\nspoiler" }; s.Sanitize();
            var requests = new SongRequests(() => s, Path.Combine(temp, "filtered.json"));
            object Chat(string platform, string id, string viewer, string text) => new { key = s.ChatBridgeKey, platform,
                session = "chat-session-123", channel = "creator", messages = new[] { new { id, author = viewer, text } } };
            int Count(SongRequests r) { using var d = JsonDocument.Parse(r.StateJson()); return d.RootElement.GetProperty("items").GetArrayLength(); }
            Check(requests.ReceiveChat(JsonSerializer.Serialize(Chat("kick", "1", "A", "!sr A good song")), "kick") && Count(requests) == 0,
                "per-platform switch blocks requests while accepting the chat heartbeat");
            Check(requests.ReceiveChat(JsonSerializer.Serialize(Chat("twitch", "2", "B", "!sr ＢＡＤ ＰＨＲＡＳＥ")), "twitch") && Count(requests) == 0,
                "blocked phrase matches normalized request text without case sensitivity");
            Check(requests.ReceiveChat(JsonSerializer.Serialize(Chat("youtube", "3", "SpoilerFan", "!sr A good song")), "youtube") && Count(requests) == 0,
                "blocked phrase also filters viewer display names");
            Check(requests.ReceiveChat(JsonSerializer.Serialize(Chat("twitch", "4", "C", "!sr A good song")), "twitch") && Count(requests) == 1,
                "unblocked enabled-platform request still arrives");
            Check(requests.ApplyAction(JsonSerializer.Serialize(new { action = "manual", song = "bad phrase by streamer" })) && Count(requests) == 2,
                "streamer-added requests deliberately bypass viewer phrase filtering");
            Check(!requests.ApplyAction(JsonSerializer.Serialize(new { action = "config", enabled = false, platforms = new { kick = true }, blockedTerms = "x" })) &&
                s.RequestIntakeEnabled && !s.RequestKickEnabled, "invalid filter configuration has no partial effects");
            Check(requests.ApplyAction(JsonSerializer.Serialize(new { action = "config", platforms = new { kick = true }, blockedTerms = "two words\nrepeat\nREPEAT" })) &&
                s.RequestKickEnabled && s.RequestBlockedTerms == "two words\nrepeat", "platform choices and normalized filters can be saved");
            var savedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppSettings.FilePath));
            Check(savedSettings != null && savedSettings.RequestKickEnabled && savedSettings.RequestBlockedTerms == "two words\nrepeat", "request filter settings persist");

            var orderedSettings = new AppSettings { RequestMode = "automatic", RequestCooldownSeconds = 0 }; orderedSettings.Sanitize();
            string orderFile = Path.Combine(temp, "ordered.json");
            var ordered = new SongRequests(() => orderedSettings, orderFile);
            foreach (string song in new[] { "First song", "Second song", "Third song" })
                Check(ordered.ApplyAction(JsonSerializer.Serialize(new { action = "manual", song })), "manual queue item added: " + song);
            string third;
            using (var state = JsonDocument.Parse(ordered.StateJson()))
                third = state.RootElement.GetProperty("items").EnumerateArray().First(x => x.GetProperty("song").GetString() == "Third song").GetProperty("id").GetString()!;
            Check(ordered.ApplyAction(JsonSerializer.Serialize(new { action = "moveEarlier", id = third })) &&
                ordered.ApplyAction(JsonSerializer.Serialize(new { action = "moveEarlier", id = third })) &&
                !ordered.ApplyAction(JsonSerializer.Serialize(new { action = "moveEarlier", id = third })), "waiting requests can move earlier but not past the start");
            var reloaded = new SongRequests(() => orderedSettings, orderFile);
            using (var visible = JsonDocument.Parse(reloaded.StateJson(true)))
                Check(visible.RootElement.GetProperty("items")[0].GetProperty("song").GetString() == "Third song", "request order persists and appears in the OBS list");
            orderedSettings.RequestAutoQueue = true;
            Check(reloaded.ClaimNext()?.Id == third, "reordered waiting request is claimed first by the player bridge");
            Check(!reloaded.ApplyAction(JsonSerializer.Serialize(new { action = "moveLater", id = third })), "an in-flight request cannot be reordered");

            string secret = BrowserBridge.NewSecret(); var bridge = new BrowserBridge(secret);
            object Snap(string sess, bool can, bool ad = false) => new { key = secret, title = "Song", artist = "Artist", position = 5,
                duration = 100, playing = true, session = sess, canControl = can, ad };
            Check(bridge.Accept(JsonSerializer.Serialize(Snap("old-bookmark", false))) && !bridge.QueueControl("toggle", out _, out _),
                "old or control-incapable bookmark cannot receive player actions");
            Check(bridge.Accept(JsonSerializer.Serialize(Snap("music-tab-123", true))), "updated music bookmark registers transport capability");
            Check(bridge.QueueControl("next", out var id, out _), "a linked capable music tab accepts an explicit player action");
            Check(!bridge.QueueControl("previous", out _, out _), "only one transport action may be outstanding");
            Check(bridge.ClaimControl(JsonSerializer.Serialize(Snap("other-tab-123", true))) == null, "transport command cannot be stolen by a different bookmark session");
            var command = bridge.ClaimControl(JsonSerializer.Serialize(Snap("music-tab-123", true)));
            Check(command != null && command.Id == id && command.Action == "next" && bridge.ClaimControl(JsonSerializer.Serialize(Snap("music-tab-123", true))) == null,
                "player action is claimed at most once even if the response is lost");
            Check(!bridge.FinishControl(JsonSerializer.Serialize(new { key = "wrong", id, session = "music-tab-123", result = "clicked" })) &&
                !bridge.FinishControl(JsonSerializer.Serialize(new { key = secret, id, session = "other-tab-123", result = "clicked" })), "wrong key or session cannot acknowledge a player action");
            var ack = JsonSerializer.Serialize(new { key = secret, id, session = "music-tab-123", result = "clicked" });
            Check(bridge.FinishControl(ack) && bridge.FinishControl(ack), "player action acknowledgment is idempotent");
            Check(!bridge.FinishControl(JsonSerializer.Serialize(new { key = secret, id, session = "music-tab-123", result = "failed" })), "a conflicting duplicate control acknowledgment is rejected");
            string publicStatus = bridge.ControlStateJson();
            Check(publicStatus.Contains("clicked") && !publicStatus.Contains(secret) && !publicStatus.Contains("music-tab-123"), "player status never exposes the key or bookmark session");
            Check(bridge.Accept(JsonSerializer.Serialize(Snap("music-tab-123", true, true))) && !bridge.QueueControl("next", out _, out _), "player controls are unavailable during ads");
            bridge.Accept(JsonSerializer.Serialize(Snap("music-tab-123", true)));
            Check(!bridge.QueueControl("delete-everything", out _, out _), "only whitelisted player actions are accepted");

            var httpSettings = new AppSettings { BrowserBridgeKey = secret }; httpSettings.Sanitize();
            var monitor = new SessionMonitor { Current = Song(), Bridge = bridge };
            var server = new WebServer(monitor, new YtMusicClient(), Client(LiveHandler()), new ManualLyricsStore(Path.Combine(temp, "lyrics.json")), () => httpSettings, bridge);
            int port = FreePort(); Check(server.Start(port), "v0.0.1 feature server starts");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            try
            {
                using var foreign = new HttpRequestMessage(HttpMethod.Post, "api/player-action") { Content = new StringContent("{\"action\":\"next\"}", Encoding.UTF8, "application/json") };
                foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                using var bad = await http.SendAsync(foreign);
                Check(bad.StatusCode == HttpStatusCode.Forbidden, "foreign websites cannot issue local playback actions");
                using var ok = await http.PostAsync("api/player-action", new StringContent("{\"action\":\"toggle\"}", Encoding.UTF8, "application/json"));
                Check(ok.IsSuccessStatusCode && (await ok.Content.ReadAsStringAsync()).Contains("\"ok\":true"), "local player action API accepts a whitelisted action");
                using var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "api/bridge/control-ack") { Content = new StringContent(ack, Encoding.UTF8, "text/plain") };
                wrongOrigin.Headers.TryAddWithoutValidation("Origin", "https://kick.com");
                using var denied = await http.SendAsync(wrongOrigin);
                Check(denied.StatusCode == HttpStatusCode.Forbidden, "chat origins cannot acknowledge player controls");
                string player = await http.GetStringAsync("player"), compact = await http.GetStringAsync("now-playing");
                Check(player.Contains("Player &amp; tools") && compact.Contains("now-only"), "player tools and compact OBS source are served");
            }
            finally { server.Stop(); }
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static async Task TestWebServer()
    {
        int port = FreePort();
        string path = Path.Combine(Path.GetTempPath(), "overlay-web-test-" + Guid.NewGuid().ToString("N") + ".json");
        var st = new AppSettings { ShowLyrics = false, ShowQueue = false, LyricsSource = "off" };
        st.Sanitize();
        var monitor = new SessionMonitor { Current = Song() };
        var handler = LiveHandler();
        var store = new ManualLyricsStore(path);
        var server = new WebServer(monitor, new YtMusicClient(), Client(handler), store, () => st);
        Check(server.Start(port), "loopback HTTP server starts");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using (var main = JsonDocument.Parse(await http.GetStringAsync("api/state")))
            {
                Check(!main.RootElement.GetProperty("settings").GetProperty("showLyrics").GetBoolean(), "main overlay respects explicit off");
                Check(main.RootElement.GetProperty("version").GetString() == "0.0.1", "API exposes installed version");
            }
            await WaitFor(async () =>
            {
                using var state = JsonDocument.Parse(await http.GetStringAsync("api/lyrics-state"));
                return state.RootElement.GetProperty("lyrics").GetProperty("available").GetBoolean();
            }, "independent lyrics lookup");
            using (var forced = JsonDocument.Parse(await http.GetStringAsync("api/lyrics-state")))
            {
                Check(forced.RootElement.GetProperty("settings").GetProperty("showLyrics").GetBoolean(), "lyrics-only source works despite main checkbox being off");
                Check(forced.RootElement.GetProperty("lyrics").GetProperty("lineCount").GetInt32() == 3, "full HTTP state carries synced lyrics");
            }
            string page = await http.GetStringAsync("lyrics");
            Check(page.Contains("lyrics-only") && page.Contains("Waiting for a song"), "standalone lyrics HTML is served");
            store.Set("Sample Track", "Example Artist", "Saved first\nSaved second");
            using (var manual = JsonDocument.Parse(await http.GetStringAsync("api/lyrics-state")))
            {
                var ly = manual.RootElement.GetProperty("lyrics");
                Check(ly.GetProperty("saved").GetBoolean() && ly.GetProperty("lines")[0].GetProperty("x").GetString() == "Saved first", "saved lyrics immediately override online result");
            }
            Check(store.Remove("Sample Track", "Example Artist"), "saved lyrics can be removed");
            int before = handler.Seen.Count;
            server.RetryLyrics();
            await WaitFor(async () =>
            {
                using var state = JsonDocument.Parse(await http.GetStringAsync("api/lyrics-state"));
                return state.RootElement.GetProperty("lyrics").GetProperty("available").GetBoolean() && handler.Seen.Count > before;
            }, "manual removal + refresh");
            Check(handler.Seen.Count > before, "retry performs a fresh automatic request");
            using (var debug = JsonDocument.Parse(await http.GetStringAsync("api/debug")))
                Check(debug.RootElement.GetProperty("detectedTitle").GetString() == "Sample Track" && debug.RootElement.GetProperty("version").GetString() == "0.0.1", "diagnostics include accurate track and version");
            monitor.Current = new NowPlaying();
            using (var idle = JsonDocument.Parse(await http.GetStringAsync("api/lyrics-state")))
                Check(idle.RootElement.GetProperty("now").ValueKind == JsonValueKind.Null && idle.RootElement.GetProperty("lyrics").GetProperty("reason").GetString() == "no-title", "idle state says waiting for a song");
        }
        finally { server.Stop(); try { File.Delete(path); } catch { } }
    }

    private static async Task TestNoMatchRetry()
    {
        int port = FreePort();
        var st = new AppSettings { ShowLyrics = true, ShowQueue = false };
        var monitor = new SessionMonitor { Current = Song("A nonexistent song", "Nobody", "missing") };
        var handler = new FakeHandler((uri, ct) => Task.FromResult(uri.Host == "lrclib.net" && uri.AbsolutePath == "/api/search" ? Json("[]") : NotFound()));
        string path = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json");
        var server = new WebServer(monitor, new YtMusicClient(), Client(handler), new ManualLyricsStore(path), () => st);
        Check(server.Start(port), "no-match test server starts");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            await WaitFor(async () =>
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync("api/state"));
                return doc.RootElement.GetProperty("lyrics").GetProperty("reason").GetString() == "no-match";
            }, "no-match result");
            int before = handler.Seen.Count;
            for (int i = 0; i < 15; i++) await http.GetStringAsync("api/state");
            await Task.Delay(150);
            Check(handler.Seen.Count == before, "no-match does not trigger a fresh lookup on every OBS poll");
            server.RetryLyrics();
            await WaitFor(() => Task.FromResult(handler.Seen.Count > before), "explicit retry of no-match");
            Check(true, "explicit Retry bypasses the no-match cache");
        }
        finally { server.Stop(); try { File.Delete(path); } catch { } }
    }

    private static async Task TestSlowSongRace()
    {
        int port = FreePort();
        var st = new AppSettings { ShowLyrics = true, ShowQueue = false };
        var monitor = new SessionMonitor { Current = Song("First Song", "Band", "A") };
        monitor.Current.Duration = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (uri, ct) =>
        {
            string query = Uri.UnescapeDataString(uri.Query);
            if (query.Contains("First Song")) { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Json("[" + Item("First Song", "Band", "[00:00]First words") + "]"); }
            if (query.Contains("Second Song")) return Json("[" + Item("Second Song", "Band", "[00:00]Second words") + "]");
            return Json("[]");
        });
        string path = Path.Combine(Path.GetTempPath(), "race-" + Guid.NewGuid().ToString("N") + ".json");
        var server = new WebServer(monitor, new YtMusicClient(), Client(handler), new ManualLyricsStore(path), () => st);
        Check(server.Start(port), "race test server starts");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            await http.GetStringAsync("api/state");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            monitor.Current = Song("Second Song", "Band", "B");
            monitor.Current.Duration = 0;
            await http.GetStringAsync("api/state");
            release.TrySetResult();
            await WaitFor(async () =>
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync("api/state"));
                var ly = doc.RootElement.GetProperty("lyrics");
                return ly.GetProperty("available").GetBoolean() && ly.GetProperty("lines")[0].GetProperty("x").GetString() == "Second words";
            }, "new song result beats old in-flight result");
            Check(true, "slow previous song cannot overwrite new song lyrics");
        }
        finally { release.TrySetResult(); server.Stop(); try { File.Delete(path); } catch { } }
    }
}
