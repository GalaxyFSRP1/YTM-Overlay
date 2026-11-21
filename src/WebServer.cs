using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YtmOverlay;

/// <summary>
/// Tiny HTTP server on a loopback TCP socket. Deliberately avoids HttpListener,
/// which needs URL ACL reservations on some Windows setups - this needs nothing.
/// </summary>
public sealed class WebServer
{
    public const string Version = "0.0.1";
    private readonly SessionMonitor _monitor;
    private readonly YtMusicClient _yt;
    private readonly LyricsClient _lyrics;
    private readonly ManualLyricsStore _manualLyrics;
    private readonly Func<AppSettings> _settings;
    private readonly BrowserBridge _bridge;
    private readonly SongRequests _requests;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _overlayHtml = "";

    private readonly object _lyricsGate = new();
    private LyricsResult _lyricsResult = new() { Found = false };
    private string _lyricsForTrack = "";
    private DateTime _lyricsAt = DateTime.MinValue;
    private string _lyricsInFlight = "";
    private long _lyricsGeneration;

    private readonly object _queueGate = new();
    private List<QueueItem> _queue = new();
    private string _queueForTrack = "";
    private DateTime _queueAt = DateTime.MinValue;
    private string _queueInFlight = "";
    private long _queueGeneration;

    public int Port { get; private set; }
    public bool Running => _listener != null;

    public WebServer(SessionMonitor monitor, YtMusicClient yt, LyricsClient lyrics, ManualLyricsStore manualLyrics,
        Func<AppSettings> settings, BrowserBridge? bridge = null, SongRequests? requests = null)
    {
        _monitor = monitor;
        _yt = yt;
        _lyrics = lyrics;
        _manualLyrics = manualLyrics;
        _settings = settings;
        if (bridge == null)
        {
            _settings().Sanitize();
            bridge = new BrowserBridge(_settings().BrowserBridgeKey);
        }
        _bridge = bridge;
        _requests = requests ?? new SongRequests(_settings);
    }

    public bool Start(int port)
    {
        _overlayHtml = LoadEmbedded("YtmOverlay.overlay.html");
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(64);
            _listener = listener;
            Port = port;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return true;
        }
        catch (SocketException)
        {
            try { _listener?.Stop(); } catch { }
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private static string LoadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name);
        if (s == null)
            return "<html><body style='background:#111;color:#eee;font-family:Segoe UI,sans-serif;padding:2em'>" +
                   "Overlay page is missing from this build.</body></html>";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private string BuildConnectPage()
    {
        string script = LoadEmbedded("YtmOverlay.bridge.js");
        if (!script.Contains("__BRIDGE_KEY__", StringComparison.Ordinal))
            return "<html><body>Browser link is missing from this build.</body></html>";
        script = script.Replace("__BRIDGE_KEY__", _settings().BrowserBridgeKey, StringComparison.Ordinal)
                       .Replace("__BRIDGE_PORT__", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        string bookmark = WebUtility.HtmlEncode("javascript:" + Uri.EscapeDataString(script));
        const string page = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Connect YouTube Music • YTM Overlay</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#101016;color:#f5f5fb;font:16px/1.55 Segoe UI,Arial,sans-serif}
main{max-width:780px;margin:48px auto;padding:0 22px}.eyebrow{color:#ff5d80;font-size:12px;font-weight:800;letter-spacing:.2em}
h1{font-size:36px;line-height:1.15;margin:10px 0}p{color:#b9bbc9}section{background:#1b1b24;border:1px solid #333341;border-radius:16px;padding:24px;margin:22px 0}
.step{display:flex;gap:15px;margin:18px 0}.num{flex:none;background:#332434;color:#ff7b99;border-radius:50%;width:30px;height:30px;text-align:center;line-height:30px;font-weight:700}
.step p{margin:3px 0}.bookmark{display:inline-block;background:#fa315d;color:white;text-decoration:none;border-radius:9px;padding:11px 18px;font-weight:700;cursor:grab;margin:8px 0}
button{background:#323343;color:white;border:0;border-radius:9px;padding:11px 15px;font-weight:600;cursor:pointer}code{color:#a8ddff}small{color:#9b9dad}a{color:#a8ddff}strong{color:#f4f4fc}
</style></head><body><main><div class="eyebrow">OPTIONAL BROWSER LINK</div><h1>Your real queue, right in OBS.</h1>
<p>Link the YouTube Music tab for a direct playback clock and the actual visible Up next list. The normal Windows media-session mode still works without this link.</p>
<section><div class="step"><span class="num">1</span><div><strong>Show your bookmarks bar.</strong><p>In Chrome, Edge or Brave, press <code>Ctrl + Shift + B</code>.</p></div></div>
<div class="step"><span class="num">2</span><div><strong>Drag this button onto the bookmarks bar.</strong><p>It becomes your personal one-click connection bookmark.</p>
<a id="bookmark" class="bookmark" href="__BOOKMARK__" title="Drag onto your bookmarks bar">♫ YTM Live Sync</a> &nbsp;
<button id="copy" type="button">Copy bookmark code</button><p><small>If dragging is blocked, create a new bookmark and paste the copied code into its URL field (not the address bar).</small></p></div></div>
<div class="step"><span class="num">3</span><div><strong>Open <a href="https://music.youtube.com/" target="_blank" rel="noopener">YouTube Music</a>, play a song, then click the bookmark.</strong>
<p>If prompted, allow <strong>Apps on device</strong> or local-network access. You may need to set it to Allow in the browser's site settings.</p></div></div>
<div class="step"><span class="num">4</span><div><strong>Keep the music tab open.</strong><p>After refreshing or reopening it, click the bookmark again. Click it a second time to disconnect.</p></div></div></section>
<section><strong>Player controls and song requests</strong><p>Open <a href="/player">Player &amp; tools</a> for previous, play/pause and next. These explicitly click controls in the linked music tab and may be unavailable during ads or if YouTube Music changes. Recreate this bookmark when upgrading to v0.0.1; older bookmarks continue playback sync but do not provide the new controls.</p><p>Separately, the request dashboard can send approved songs to this player only if you explicitly enable experimental insertion. Text searches use the first result. A request can fail or be uncertain. <a href="/requests">Open song requests</a>.</p></section>
<section><strong>Private by design</strong><p>The bookmark sends the song title, artist, playback time, artwork URL and visible queue to this program on your own computer. It does not send passwords, account cookies, or music audio. Automatic lyrics lookups still use online lyric services. If enabled, request searches are also sent to YouTube Music.</p>
<p><small>Device/loopback permission applies to the YouTube Music site, not just this overlay; it can permit access to other local services. Grant it only if you trust the site, and revoke it in browser settings. The bookmark includes a local pairing key: don't share it. If the program's port changes, make a new bookmark.</small></p></section>
<script>document.getElementById('copy').onclick=async function(){try{await navigator.clipboard.writeText(document.getElementById('bookmark').getAttribute('href'));this.textContent='Copied!';}catch(e){this.textContent='Drag the button instead';}};</script>
</main></body></html>
""";
        return page.Replace("__BOOKMARK__", bookmark, StringComparison.Ordinal);
    }

    private string BuildChatConnectPage()
    {
        string script = LoadEmbedded("YtmOverlay.chat_bridge.js");
        if (!script.Contains("__CHAT_KEY__", StringComparison.Ordinal))
            return "<html><body>Chat link is missing from this build.</body></html>";
        script = script.Replace("__CHAT_KEY__", _settings().ChatBridgeKey, StringComparison.Ordinal)
                       .Replace("__BRIDGE_PORT__", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        string bookmark = WebUtility.HtmlEncode("javascript:" + Uri.EscapeDataString(script));
        return LoadEmbedded("YtmOverlay.chat_connect.html").Replace("__CHAT_BOOKMARK__", bookmark, StringComparison.Ordinal);
    }

    private static string ChatPlatform(string origin) => origin.ToLowerInvariant() switch
    {
        "https://twitch.tv" or "https://www.twitch.tv" => "twitch",
        "https://youtube.com" or "https://www.youtube.com" => "youtube",
        "https://kick.com" or "https://www.kick.com" => "kick",
        _ => ""
    };

    private static bool WantsQueueCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("canQueue", out var value) && value.ValueKind == JsonValueKind.True &&
                !(root.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True);
        }
        catch { return false; }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                stream.ReadTimeout = 8000;
                stream.WriteTimeout = 8000;

                var (head, leftover) = await ReadHeadAsync(stream).ConfigureAwait(false);
                if (head == null) return;

                string requestLine = head[0];
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();
                string target = parts[1];
                string path = target.Split('?')[0].ToLowerInvariant();

                int contentLength = 0;
                string host = "", origin = "", fetchSite = "";
                foreach (var line in head)
                {
                    int idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    string name = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
                    else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                    else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
                    else if (name.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase)) fetchSite = value;
                }
                // Block DNS rebinding: no public hostname may address this local server.
                if (!ValidHost(host) || contentLength < 0 || contentLength > 65_536)
                {
                    await RespondAsync(stream, 400, "text/plain", Encoding.UTF8.GetBytes("bad request")).ConfigureAwait(false);
                    return;
                }

                byte[] body = Array.Empty<byte>();
                if (contentLength > 0)
                {
                    body = new byte[contentLength];
                    int got = Math.Min(contentLength, leftover.Length);
                    if (got > 0) Buffer.BlockCopy(leftover, 0, body, 0, got);
                    while (got < contentLength)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(got, contentLength - got)).ConfigureAwait(false);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < contentLength) Array.Resize(ref body, got);
                }

                await RouteAsync(stream, method, path, body, origin, fetchSite).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Reads request headers. Returns the header lines plus any body bytes that
    /// arrived in the same read, so a POST body is never lost.
    /// </summary>
    private static async Task<(List<string>? lines, byte[] leftover)> ReadHeadAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length < 65_536)
        {
            int n = await stream.ReadAsync(chunk.AsMemory()).ConfigureAwait(false);
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
            var bytes = buffer.ToArray();
            int end = IndexOf(bytes, "\r\n\r\n");
            if (end < 0) continue;

            var headText = Encoding.ASCII.GetString(bytes, 0, end);
            var lines = new List<string>(headText.Split(new[] { "\r\n" }, StringSplitOptions.None));
            int bodyStart = end + 4;
            var leftover = new byte[Math.Max(0, bytes.Length - bodyStart)];
            if (leftover.Length > 0) Buffer.BlockCopy(bytes, bodyStart, leftover, 0, leftover.Length);
            return (lines.Count > 0 ? lines : null, leftover);
        }
        return (null, Array.Empty<byte>());
    }

    private static int IndexOf(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= haystack.Length - n.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++)
                if (haystack[i + j] != n[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    private bool ValidHost(string host)
    {
        if (!Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)) return false;
        return uri.Port == Port && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1");
    }

    private bool SameOriginOrLocalClient(string origin, string fetchSite)
    {
        if (string.IsNullOrEmpty(origin)) return !fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        return u.Scheme == "http" && u.Port == Port &&
               (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host == "127.0.0.1");
    }

    private async Task RouteAsync(NetworkStream stream, string method, string path, byte[] body, string origin, string fetchSite)
    {
        if (path == "/api/chat")
        {
            string platform = ChatPlatform(origin);
            if (platform.Length == 0)
            {
                await RespondAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")).ConfigureAwait(false);
                return;
            }
            if (method == "OPTIONS")
            {
                await RespondAsync(stream, 200, "text/plain", Array.Empty<byte>(), corsOrigin: origin, bridgeCors: true).ConfigureAwait(false);
                return;
            }
            bool accepted = method == "POST" && _requests.ReceiveChat(Encoding.UTF8.GetString(body), platform);
            await RespondAsync(stream, accepted ? 200 : 403, "application/json", Encoding.UTF8.GetBytes(accepted ? "{\"ok\":true}" : "{\"ok\":false}"),
                corsOrigin: origin, bridgeCors: true).ConfigureAwait(false);
            return;
        }

        if (path is "/api/bridge" or "/api/bridge/ack" or "/api/bridge/control-ack")
        {
            const string musicOrigin = "https://music.youtube.com";
            if (!origin.Equals(musicOrigin, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")).ConfigureAwait(false);
                return;
            }
            if (method == "OPTIONS")
            {
                await RespondAsync(stream, 200, "text/plain", Array.Empty<byte>(), corsOrigin: musicOrigin, bridgeCors: true).ConfigureAwait(false);
                return;
            }
            string json = Encoding.UTF8.GetString(body);
            bool accepted = method == "POST" && (path == "/api/bridge" ? _bridge.Accept(json) :
                path == "/api/bridge/control-ack" ? _bridge.FinishControl(json) : _bridge.Authenticate(json) && _requests.FinishClaim(json));
            string result = accepted ? "{\"ok\":true}" : "{\"ok\":false}";
            if (accepted && path == "/api/bridge")
            {
                // An explicit transport click has priority. Never send an insert
                // and a transport action together to the same tab.
                var transport = _bridge.ClaimControl(json);
                var command = transport == null && WantsQueueCommand(json) ? _requests.ClaimNext() : null;
                var wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                if (transport != null) result = JsonSerializer.Serialize(new { ok = true, transport }, wire);
                else if (command != null) result = JsonSerializer.Serialize(new { ok = true, command }, wire);
            }
            await RespondAsync(stream, accepted ? 200 : 403, "application/json", Encoding.UTF8.GetBytes(result),
                corsOrigin: musicOrigin, bridgeCors: true).ConfigureAwait(false);
            return;
        }

        if (path == "/chat-connect" && method == "GET")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")).ConfigureAwait(false);
                return;
            }
            await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(BuildChatConnectPage()), noFrame: true).ConfigureAwait(false);
            return;
        }

        if (path == "/player" && method == "GET")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")).ConfigureAwait(false);
                return;
            }
            await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LoadEmbedded("YtmOverlay.player.html")), noFrame: true).ConfigureAwait(false);
            return;
        }
        if (path == "/api/player-status" && method == "GET")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                return;
            }
            await RespondAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_bridge.ControlStateJson()), noFrame: true).ConfigureAwait(false);
            return;
        }
        if (path == "/api/player-action" && method == "POST")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                return;
            }
            bool ok = false; string id = "", reason = "Invalid player action.";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                    ok = _bridge.QueueControl(a.GetString() ?? "", out id, out reason);
            }
            catch { }
            string answer = JsonSerializer.Serialize(new { ok, id, error = reason });
            await RespondAsync(stream, ok ? 200 : 400, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(answer), noFrame: true).ConfigureAwait(false);
            return;
        }

        if (path == "/requests" && method == "GET")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden")).ConfigureAwait(false);
                return;
            }
            await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LoadEmbedded("YtmOverlay.requests.html")), noFrame: true).ConfigureAwait(false);
            return;
        }
        if (path == "/request-overlay" && method == "GET")
        {
            await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LoadEmbedded("YtmOverlay.request_overlay.html"))).ConfigureAwait(false);
            return;
        }
        if (path == "/api/request-overlay" && method == "GET")
        {
            await RespondAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_requests.StateJson(true))).ConfigureAwait(false);
            return;
        }
        if (path == "/api/request-state" && method == "GET")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                return;
            }
            await RespondAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_requests.StateJson()), noFrame: true).ConfigureAwait(false);
            return;
        }
        if (path == "/api/request-action" && method == "POST")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                return;
            }
            bool ok = _requests.ApplyAction(Encoding.UTF8.GetString(body));
            await RespondAsync(stream, ok ? 200 : 400, "application/json", Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}"), noFrame: true).ConfigureAwait(false);
            return;
        }

        if (path == "/connect" && method == "GET")
        {
            await RespondAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(BuildConnectPage()),
                noFrame: true).ConfigureAwait(false);
            return;
        }

        if (path == "/" || path == "/overlay" || path == "/overlay.html" || path == "/index.html" || path == "/lyrics" || path == "/lyrics/" || path == "/queue" || path == "/queue/" || path == "/now-playing" || path == "/now-playing/")
        {
            await RespondAsync(stream, 200, "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(_overlayHtml)).ConfigureAwait(false);
            return;
        }

        if (path == "/api/state" || path == "/api/lyrics-state" || path == "/api/queue-state" || path == "/api/now-state")
        {
            // Separate sources work even if their main-overlay checkboxes are off.
            // The compact player does not initiate extra lyrics/suggestions lookups.
            var json = BuildStateJson(path == "/api/lyrics-state", path == "/api/queue-state", path == "/api/now-state");
            await RespondAsync(stream, 200, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            return;
        }

        if (path == "/api/debug")
        {
            var dbg = BuildDebugJson();
            await RespondAsync(stream, 200, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(dbg)).ConfigureAwait(false);
            return;
        }

        if (path == "/api/settings" && method == "POST")
        {
            if (!SameOriginOrLocalClient(origin, fetchSite))
            {
                await RespondAsync(stream, 403, "application/json", Encoding.UTF8.GetBytes("{\"ok\":false}")).ConfigureAwait(false);
                return;
            }
            bool ok = false;
            try
            {
                ApplySettingsPatch(Encoding.UTF8.GetString(body));
                ok = true;
            }
            catch { }
            await RespondAsync(stream, ok ? 200 : 400, "application/json",
                Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}")).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/art/"))
        {
            var (bytes, hash) = _monitor.GetArt();
            if (bytes != null && bytes.Length > 0 && path.Substring(5).Equals(hash, StringComparison.Ordinal))
            {
                await RespondAsync(stream, 200, Sniff(bytes), bytes,
                    "public, max-age=86400").ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(stream, 404, "text/plain", Array.Empty<byte>()).ConfigureAwait(false);
            }
            return;
        }

        await RespondAsync(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found")).ConfigureAwait(false);
    }

    private static string Sniff(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b.Length > 8 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length > 8 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
        if (b.Length > 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return "image/jpeg";
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, byte[] body,
        string cacheControl = "no-store", string? corsOrigin = null, bool bridgeCors = false, bool noFrame = false)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: ").Append(cacheControl).Append("\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (corsOrigin != null) sb.Append("Access-Control-Allow-Origin: ").Append(corsOrigin).Append("\r\nVary: Origin\r\n");
            if (bridgeCors) sb.Append("Access-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\n");
            if (noFrame) sb.Append("X-Frame-Options: DENY\r\nContent-Security-Policy: frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\n");
            sb.Append("Connection: close\r\n\r\n");

            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head.AsMemory()).ConfigureAwait(false);
            if (body.Length > 0)
                await stream.WriteAsync(body.AsMemory()).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        _ => "OK"
    };

    // ---------------- state ----------------

    private string BuildStateJson(bool forceLyrics = false, bool forceQueue = false, bool nowOnly = false)
    {
        var now = _monitor.Current;
        var st = _settings();
        bool liveQueue = _bridge.TryGet(out var browserNow, out var browserQueue, out var queueKnown) &&
                         queueKnown && !string.IsNullOrEmpty(now.TrackId) && now.TrackId == browserNow.TrackId;

        if (!nowOnly && !string.IsNullOrEmpty(now.Title))
        {
            if (!forceLyrics && !liveQueue) EnsureQueue(now, forceQueue);
            if (!forceQueue) EnsureLyrics(now, forceLyrics);
        }

        List<QueueItem> items;
        if (liveQueue) items = browserQueue;
        else lock (_queueGate)
            items = !string.IsNullOrEmpty(now.TrackId) && _queueForTrack == now.TrackId
                ? new List<QueueItem>(_queue) : new List<QueueItem>();

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("version", Version);

            w.WritePropertyName("now");
            if (string.IsNullOrEmpty(now.Title))
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                w.WriteString("title", now.Title);
                w.WriteString("artist", now.Artist);
                w.WriteString("album", now.Album);
                double offset = Math.Clamp(st.PositionOffsetSeconds, -30, 30);
                double shownPosition = Math.Max(0, now.Position + offset);
                if (now.Duration > 0) shownPosition = Math.Min(shownPosition, now.Duration);
                w.WriteNumber("position", Math.Round(shownPosition, 3));
                w.WriteNumber("duration", Math.Round(now.Duration, 2));
                w.WriteNumber("rate", now.Rate);
                w.WriteBoolean("playing", now.Playing);
                w.WriteNumber("clockRevision", now.ClockRevision);
                w.WriteNumber("clockOffset", Math.Round(offset, 2));
                w.WriteString("clockSource", now.ClockSource);
                w.WriteString("sourceLabel", now.SourceLabel);
                w.WriteString("trackId", now.TrackId);
                w.WriteString("art", now.Art);
                w.WriteEndObject();
            }

            w.WritePropertyName("queue");
            w.WriteStartObject();
            w.WriteString("source", liveQueue ? "browser" : "suggestions");
            w.WritePropertyName("items");
            w.WriteStartArray();
            foreach (var it in items)
            {
                w.WriteStartObject();
                w.WriteString("title", it.Title);
                w.WriteString("artist", it.Artist);
                w.WriteString("album", it.Album);
                w.WriteString("videoId", it.VideoId);
                w.WriteString("thumb", it.Thumb);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WritePropertyName("lyrics");
            WriteLyrics(w, now, st, forceLyrics);

            w.WritePropertyName("settings");
            w.WriteStartObject();
            w.WriteString("theme", st.Theme);
            w.WriteString("accent", st.Accent);
            w.WriteString("accent2", st.Accent2);
            w.WriteBoolean("showQueue", forceQueue || st.ShowQueue);
            w.WriteNumber("queueCount", forceQueue && st.QueueCount <= 0 ? 4 : st.QueueCount);
            w.WriteBoolean("showQueueArt", st.ShowQueueArt);
            w.WriteBoolean("showQueueNumbers", st.ShowQueueNumbers);
            w.WriteBoolean("showQueueArtist", st.ShowQueueArtist);
            w.WriteBoolean("showProgress", st.ShowProgress);
            w.WriteBoolean("showTimes", st.ShowTimes);
            w.WriteBoolean("showRemainingTime", st.ShowRemainingTime);
            w.WriteBoolean("showAlbum", st.ShowAlbum);
            w.WriteBoolean("showArt", st.ShowArt);
            w.WriteBoolean("showLabel", st.ShowLabel);
            w.WriteBoolean("showLyrics", forceLyrics || st.ShowLyrics);
            w.WriteNumber("lyricsLines", st.LyricsLines);
            w.WriteBoolean("lyricsKaraoke", st.LyricsKaraoke);
            w.WriteBoolean("lyricsHideInstrumental", st.LyricsHideInstrumental);
            w.WriteNumber("lyricsOffsetSeconds", Math.Round(Math.Clamp(st.LyricsOffsetSeconds, -30, 30), 2));
            w.WriteString("lyricsAlign", st.LyricsAlign);
            w.WriteBoolean("transparentBackground", st.TransparentBackground);
            w.WriteBoolean("reduceMotion", st.ReduceMotion);
            w.WriteString("textCase", st.TextCase);
            w.WriteBoolean("titleMarquee", st.TitleMarquee);
            w.WriteString("placeholder", st.Placeholder);
            w.WriteString("label", st.Label);
            w.WriteBoolean("dimWhenPaused", st.DimWhenPaused);
            w.WriteBoolean("hideWhenPaused", st.HideWhenPaused);
            w.WriteNumber("scale", Math.Round(Math.Clamp(st.Scale, 0.4, 3), 3));
            w.WriteString("align", st.Align);
            w.WriteNumber("opacity", Math.Round(Math.Clamp(st.Opacity, 0.3, 1), 2));
            w.WriteNumber("corner", Math.Round(Math.Clamp(st.Corner, 0, 1.8), 2));
            w.WriteNumber("blur", Math.Round(Math.Clamp(st.Blur, 0, 2), 2));
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private LyricsResult LyricsSnapshot(NowPlaying now, out bool pending, out bool saved)
    {
        pending = false;
        saved = false;
        if (string.IsNullOrWhiteSpace(now.Title)) return new LyricsResult { Reason = "no-title" };
        if (_manualLyrics.TryGet(now.Title, now.Artist, out var manual))
        {
            saved = true;
            return manual;
        }
        lock (_lyricsGate)
        {
            if (_lyricsForTrack == now.TrackId && !string.IsNullOrEmpty(now.TrackId))
                return _lyricsResult;
            pending = true;
            return new LyricsResult { Reason = "pending" };
        }
    }

    private void WriteLyrics(Utf8JsonWriter w, NowPlaying now, AppSettings st, bool forceLyrics)
    {
        w.WriteStartObject();
        var res = LyricsSnapshot(now, out bool pending, out bool saved);
        bool want = (st.ShowLyrics || forceLyrics) && !string.IsNullOrWhiteSpace(now.Title);
        bool haveLines = want && res.Found && res.HasLines;
        w.WriteBoolean("available", haveLines);
        w.WriteBoolean("synced", haveLines && res.Synced);
        w.WriteBoolean("instrumental", res.Instrumental);
        w.WriteBoolean("pending", want && pending);
        w.WriteBoolean("saved", saved);
        w.WriteString("source", res.Source ?? "");
        w.WriteString("reason", res.Reason ?? "");
        w.WriteString("query", res.Query ?? "");
        w.WriteString("detail", res.Detail ?? "");
        w.WriteNumber("lineCount", haveLines ? res.Lines.Count : 0);
        w.WritePropertyName("lines");
        w.WriteStartArray();
        if (haveLines)
        {
            int guard = 0;
            foreach (var line in res.Lines)
            {
                if (guard++ >= 1200) break;
                w.WriteStartObject();
                w.WriteNumber("t", Math.Round(line.Time, 2));
                w.WriteString("x", line.Text);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private void EnsureLyrics(NowPlaying now, bool force = false)
    {
        var st = _settings();
        if ((!st.ShowLyrics && !force) || string.IsNullOrWhiteSpace(now.Title)) return;
        if (_manualLyrics.TryGet(now.Title, now.Artist, out _)) return;
        string track = now.TrackId;
        if (string.IsNullOrEmpty(track)) return;
        long ticket;
        lock (_lyricsGate)
        {
            if (_lyricsInFlight == track) return;
            // Wait before retrying misses/errors instead of hammering the providers on each poll.
            double settleSeconds = _lyricsResult.HasLines || _lyricsResult.Instrumental ? 120 :
                                   _lyricsResult.Reason == "error" ? 15 : 60;
            bool fresh = track == _lyricsForTrack && (DateTime.UtcNow - _lyricsAt).TotalSeconds < settleSeconds;
            if (fresh) return;
            _lyricsInFlight = track;
            ticket = ++_lyricsGeneration;
        }

        _ = Task.Run(async () =>
        {
            LyricsResult result;
            try { result = await _lyrics.GetAsync(now.Title, now.Artist, now.Album, now.Duration).ConfigureAwait(false); }
            catch (Exception ex) { result = new LyricsResult { Reason = "error", Detail = ex.GetType().Name }; }
            lock (_lyricsGate)
            {
                // A slow previous song must never overwrite a newer song or an explicit retry.
                if (ticket != _lyricsGeneration || _lyricsInFlight != track) return;
                _lyricsResult = result;
                _lyricsForTrack = track;
                _lyricsAt = DateTime.UtcNow;
                _lyricsInFlight = "";
            }
        });
    }

    public void RetryLyrics()
    {
        var now = _monitor.Current;
        if (string.IsNullOrWhiteSpace(now.Title)) return;
        _lyrics.Forget(now.Title, now.Artist);
        lock (_lyricsGate)
        {
            ++_lyricsGeneration;
            _lyricsResult = new LyricsResult { Reason = "pending" };
            _lyricsForTrack = "";
            _lyricsInFlight = "";
            _lyricsAt = DateTime.MinValue;
        }
        EnsureLyrics(now, true);
    }

    // Native live preview is independent of the main-overlay visibility checkbox.
    public (NowPlaying now, LyricsResult result, bool pending, bool saved) GetLyricsPreview()
    {
        var now = _monitor.Current;
        if (!string.IsNullOrWhiteSpace(now.Title)) EnsureLyrics(now, true);
        var result = LyricsSnapshot(now, out bool pending, out bool saved);
        return (now, result, pending, saved);
    }

    // Used by the native control panel. It starts lookups even before OBS opens the page.
    public string GetLyricsStatus()
    {
        var now = _monitor.Current;
        var st = _settings();
        if (!st.ShowLyrics) return "Lyrics: off in the main overlay (the separate source still works).";
        if (string.IsNullOrWhiteSpace(now.Title)) return "Lyrics: waiting for Windows to report a song.";
        EnsureLyrics(now);
        var result = LyricsSnapshot(now, out bool pending, out bool saved);
        if (pending) return "Lyrics: looking up this song...";
        if (result.HasLines)
            return $"Lyrics: ready - {result.Lines.Count} lines, {(result.Synced ? "timed" : "plain")} ({result.Source}).";
        if (result.Instrumental) return "Lyrics: instrumental track (no words).";
        if (result.Reason == "error") return "Lyrics: service could not be reached. Try Retry or paste lyrics.";
        return "Lyrics: no match for this song. Try Search / paste lyrics.";
    }

    private string BuildDebugJson()
    {
        var now = _monitor.Current;
        var st = _settings();
        var res = LyricsSnapshot(now, out bool pending, out bool saved);
        string lyricsFor;
        lock (_lyricsGate) lyricsFor = _lyricsForTrack;

        var (artBytes, artHash) = _monitor.GetArt();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", Version);
            var sessions = _monitor.SessionDump();
            w.WritePropertyName("sessions");
            w.WriteStartArray();
            foreach (var line in sessions) w.WriteStringValue(line);
            w.WriteEndArray();

            w.WriteString("detectedTitle", now.Title);
            w.WriteString("detectedArtist", now.Artist);
            w.WriteString("detectedAlbum", now.Album);
            w.WriteString("detectedSource", now.SourceLabel);
            w.WriteBoolean("playing", now.Playing);
            w.WriteNumber("position", Math.Round(now.Position, 2));
            w.WriteNumber("positionOffset", Math.Round(st.PositionOffsetSeconds, 2));
            w.WriteNumber("duration", Math.Round(now.Duration, 1));
            w.WriteNumber("playbackRate", now.Rate);
            w.WriteString("clockSource", now.ClockSource);
            w.WriteString("clockMode", now.ClockMode);
            w.WriteNumber("clockRawPosition", Math.Round(now.ClockRawPosition, 2));
            w.WriteNumber("clockSourceAgeSeconds", Math.Round(now.ClockSourceAgeSeconds, 2));
            w.WriteBoolean("clockTimestampTrusted", now.ClockTimestampTrusted);
            w.WriteNumber("clockRevision", now.ClockRevision);
            bool browserLinked = _bridge.TryGet(out var linked, out _, out bool actualQueue);
            w.WriteBoolean("browserLinked", browserLinked);
            w.WriteBoolean("actualQueueAvailable", browserLinked && actualQueue && linked.TrackId == now.TrackId);
            w.WriteNumber("port", Port);
            w.WriteString("artHash", artHash ?? "");
            w.WriteNumber("artBytes", artBytes?.Length ?? 0);
            w.WriteNumber("lyricsLines", res.Lines.Count);
            w.WriteBoolean("lyricsSynced", res.Synced);
            w.WriteBoolean("lyricsFound", res.Found);
            w.WriteString("lyricsReason", res.Reason ?? "");
            w.WriteString("lyricsSource", res.Source ?? "");
            w.WriteString("lyricsQuery", res.Query ?? "");
            w.WriteString("lyricsDetail", res.Detail ?? "");
            w.WriteString("lyricsFor", lyricsFor);
            w.WriteBoolean("lyricsPending", pending);
            w.WriteBoolean("lyricsSaved", saved);
            w.WriteBoolean("lyricsShowSetting", st.ShowLyrics);
            w.WriteString("lyricsStatus", GetLyricsStatus());
            w.WriteString("settingsFile", AppSettings.FilePath);
            w.WriteNumber("queueCount", st.QueueCount);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void EnsureQueue(NowPlaying now, bool force = false)
    {
        var st = _settings();
        if (!force && (!st.ShowQueue || st.QueueCount <= 0)) return;

        string track = now.TrackId;
        long ticket;
        lock (_queueGate)
        {
            if (_queueInFlight == track) return;
            bool fresh = track == _queueForTrack && (DateTime.UtcNow - _queueAt).TotalSeconds < 40;
            if (fresh) return;
            _queueInFlight = track;
            ticket = ++_queueGeneration;
        }

        _ = Task.Run(async () =>
        {
            List<QueueItem> items;
            try
            {
                items = await _yt.GetQueueAsync(now.Title, now.Artist, 8)
                    .ConfigureAwait(false);
            }
            catch { items = new List<QueueItem>(); }
            lock (_queueGate)
            {
                if (ticket != _queueGeneration || _queueInFlight != track) return;
                _queue = items;
                _queueForTrack = track;
                _queueAt = DateTime.UtcNow; // Cache empty results too, to avoid hammering the service.
                _queueInFlight = "";
            }
        });
    }

    private void ApplySettingsPatch(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var st = _settings();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (p.Name == nameof(AppSettings.Port) || p.Name == nameof(AppSettings.SourceHints) ||
                p.Name == nameof(AppSettings.BrowserBridgeKey) || p.Name == nameof(AppSettings.ChatBridgeKey) ||
                p.Name.StartsWith("Request", StringComparison.Ordinal)) continue;
            string camel = p.Name.Length == 0 ? p.Name : char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1);
            JsonElement v;
            if (!root.TryGetProperty(camel, out v) && !root.TryGetProperty(p.Name, out v)) continue;

            if (p.PropertyType == typeof(bool) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                p.SetValue(st, v.GetBoolean());
            else if (p.PropertyType == typeof(double) && v.ValueKind == JsonValueKind.Number)
            {
                double d = v.GetDouble();
                d = p.Name switch
                {
                    nameof(AppSettings.Scale) => Math.Clamp(d, 0.4, 3),
                    nameof(AppSettings.Opacity) => Math.Clamp(d, 0.3, 1),
                    nameof(AppSettings.Corner) => Math.Clamp(d, 0, 1.8),
                    nameof(AppSettings.Blur) => Math.Clamp(d, 0, 2),
                    _ => d
                };
                p.SetValue(st, d);
            }
            else if (p.PropertyType == typeof(int) && v.ValueKind == JsonValueKind.Number)
                p.SetValue(st, v.GetInt32());
            else if (p.PropertyType == typeof(string) && v.ValueKind == JsonValueKind.String)
                p.SetValue(st, v.GetString());
        }
        st.Sanitize();
        st.Save();
    }
}
