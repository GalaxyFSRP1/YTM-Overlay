using System;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YtmOverlay;

/// <summary>
/// "What am I seeing?" - shows which Windows media sessions exist, what the app picked,
/// and what the overlay API is returning. This is the fastest way to diagnose silence.
/// </summary>
public sealed class DebugForm : Form
{
    private readonly string _url;
    private readonly SessionMonitor _monitor;
    private readonly TextBox _text = new();
    private readonly Timer _timer = new();
    private readonly Label _verdict = new();
    private bool _refreshing;

    public DebugForm(string url, SessionMonitor monitor)
    {
        _url = url;
        _monitor = monitor;

        Text = "What am I seeing?";
        ClientSize = new Size(660, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 18, 22);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        Icon = AppIcon.Create();
        DoubleBuffered = true;

        _verdict.Bounds = new Rectangle(14, 12, ClientSize.Width - 28, 40);
        _verdict.Font = new Font("Segoe UI", 9.4f);
        _verdict.ForeColor = Color.FromArgb(120, 220, 150);
        Controls.Add(_verdict);

        _text.Bounds = new Rectangle(14, 58, ClientSize.Width - 28, ClientSize.Height - 118);
        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.BackColor = Color.FromArgb(12, 12, 16);
        _text.ForeColor = Color.FromArgb(200, 210, 220);
        _text.Font = new Font("Consolas", 9f);
        _text.BorderStyle = BorderStyle.None;
        _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_text);

        var copy = new Button
        {
            Text = "Copy details", Bounds = new Rectangle(14, ClientSize.Height - 50, 130, 34),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 70, 82), ForeColor = Color.White
        };
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); copy.Text = "Copied!"; } catch { } };
        Controls.Add(copy);
        Controls.Add(new Label
        {
            Text = "Includes detected app and song info.", ForeColor = Color.FromArgb(150, 150, 162),
            Bounds = new Rectangle(158, ClientSize.Height - 43, 360, 24), Anchor = AnchorStyles.Left | AnchorStyles.Bottom
        });

        _timer.Interval = 1500;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Shown += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        try
        {
            string api = "";
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) })
            {
                try { api = await http.GetStringAsync(_url + "api/debug"); }
                catch (Exception ex) { api = "{\"error\":\"" + ex.GetType().Name + "\"}"; }
            }

            string pretty = api;
            try
            {
                using var doc = JsonDocument.Parse(api);
                pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("WINDOWS MEDIA SESSIONS  (>> = the one the overlay is following)");
            sb.AppendLine(new string('-', 74));
            var sessions = await Task.Run(() => _monitor.SessionDump());
            if (IsDisposed) return;
            foreach (var line in sessions) sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("DIAGNOSTICS  " + _url + "api/debug");
            sb.AppendLine(new string('-', 74));
            sb.AppendLine(pretty);

            string body = sb.ToString();
            if (_text.Text != body)
            {
                int scroll = _text.GetPositionFromCharIndex(0).Y;
                _text.Text = body;
            }

            bool hasSong = false;
            try
            {
                using var state = JsonDocument.Parse(api);
                hasSong = state.RootElement.TryGetProperty("detectedTitle", out var title) &&
                          title.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.GetString());
            }
            catch { }
            if (hasSong)
            {
                _verdict.Text = "\u2713 A song is being detected. If OBS still looks empty, check the Browser source URL and that the source is visible.";
                _verdict.ForeColor = Color.FromArgb(120, 220, 150);
            }
            else
            {
                _verdict.Text = "\u26a0 No song detected yet. Press play in YouTube Music \u2014 Windows only reports a session once playback starts.";
                _verdict.ForeColor = Color.FromArgb(255, 190, 100);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _verdict.Text = "Could not read state: " + ex.Message;
                _verdict.ForeColor = Color.FromArgb(255, 120, 120);
            }
        }
        finally { _refreshing = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
