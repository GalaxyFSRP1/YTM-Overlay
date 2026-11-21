using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YtmOverlay;

public sealed class ControlForm : Form
{
    private readonly Func<AppSettings> _get;
    private readonly SessionMonitor _monitor;
    private readonly WebServer _server;
    private readonly LyricsClient _lyricsClient;
    private readonly ManualLyricsStore _manualLyrics;
    private readonly string _url;
    private readonly NotifyIcon _tray;
    private readonly Timer _tick = new();

    private readonly Label _statusTitle = new();
    private readonly Label _statusSub = new();
    private readonly Label _urlBox = new();
    private readonly Label _netStatus = new();
    private readonly Label _lyrStatus = new();
    private readonly Label _lyricsPanelStatus = new();
    private readonly Label _clockValue = new();
    private readonly Label _clockDetail = new();
    private readonly RichTextBox _liveLyrics = new();
    private readonly CheckBox _followLyrics = new();
    private readonly NumericUpDown _clockOffset = new();
    private readonly CheckBox _remaining = new();
    private readonly CheckBox _transparent = new();
    private readonly CheckBox _reduceMotion = new();
    private readonly ComboBox _lyricsAlign = new();
    private string _previewSignature = "";
    private int _previewIndex = -2;
    private readonly List<(int start, int length)> _previewRanges = new();

    private readonly ComboBox _theme = new();
    private readonly ComboBox _size = new();
    private readonly ComboBox _align = new();
    private readonly ComboBox _case = new();
    private readonly TextBox _accent = new();
    private readonly TextBox _accent2 = new();
    private readonly Panel _swatch = new();
    private readonly Panel _swatch2 = new();
    private readonly TrackBar _opacity = new();
    private readonly TrackBar _corner = new();
    private readonly TrackBar _blur = new();
    private readonly Label _opacityVal = new();
    private readonly Label _cornerVal = new();
    private readonly Label _blurVal = new();
    private readonly TextBox _label = new();
    private readonly TextBox _placeholder = new();
    private readonly CheckBox _marquee = new();

    private readonly ComboBox _count = new();
    private readonly CheckBox _queue = new();
    private readonly CheckBox _qArt = new();
    private readonly CheckBox _qNums = new();
    private readonly CheckBox _qArtist = new();
    private readonly CheckBox _progress = new();
    private readonly CheckBox _times = new();
    private readonly CheckBox _album = new();
    private readonly CheckBox _artBox = new();
    private readonly CheckBox _labelBox = new();
    private readonly CheckBox _dim = new();
    private readonly CheckBox _hidePaused = new();

    private readonly CheckBox _lyrics = new();
    private readonly ComboBox _lyrLines = new();
    private readonly CheckBox _karaoke = new();
    private readonly CheckBox _hideInstr = new();
    private readonly NumericUpDown _lyricsOffset = new();

    private bool _loading;
    private DebugForm? _debug;

    private static readonly Color Bg = Color.FromArgb(18, 18, 22);
    private static readonly Color Card = Color.FromArgb(28, 28, 34);
    private static readonly Color Fg = Color.FromArgb(240, 240, 245);
    private static readonly Color Muted = Color.FromArgb(150, 150, 162);

    public ControlForm(Func<AppSettings> get, SessionMonitor monitor, WebServer server, LyricsClient lyricsClient,
        ManualLyricsStore manualLyrics, string url, bool queueOnline)
    {
        _get = get;
        _monitor = monitor;
        _server = server;
        _lyricsClient = lyricsClient;
        _manualLyrics = manualLyrics;
        _url = url;

        Text = "YouTube Music Overlay";
        ClientSize = new Size(524, 668);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        Icon = AppIcon.Create();

        BuildUi(queueOnline);

        _tray = new NotifyIcon { Icon = AppIcon.Create(), Visible = true, Text = "YouTube Music Overlay" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open control panel", null, (_, _) => ShowPanel());
        menu.Items.Add("Open overlay preview", null, (_, _) => OpenUrl());
        menu.Items.Add("Copy OBS Browser Source URL", null, (_, _) => CopyUrl());
        menu.Items.Add("Copy lyrics-only URL", null, (_, _) => CopyLyricsUrl());
        menu.Items.Add("Copy queue-only URL", null, (_, _) => CopyQueueUrl());
        menu.Items.Add("Connect YouTube Music browser", null, (_, _) => OpenBrowserLink());
        menu.Items.Add("Open song requests", null, (_, _) => OpenRequests());
        menu.Items.Add("Test sample lyrics", null, (_, _) => OpenLyricsTest());
        menu.Items.Add("What am I seeing?", null, (_, _) => OpenDebug());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowPanel();

        _tick.Interval = 800;
        _tick.Tick += (_, _) => RefreshStatus();
        _tick.Start();

        LoadFromSettings();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ UI

    private void BuildUi(bool online)
    {
        var header = new Panel { Bounds = new Rectangle(0, 0, ClientSize.Width, 74), BackColor = Color.FromArgb(22, 22, 28) };
        header.Paint += (_, e) =>
        {
            using var b = new LinearGradientBrush(new Rectangle(0, 66, header.Width, 8),
                Color.FromArgb(255, 45, 85), Color.FromArgb(124, 92, 255), 0f);
            e.Graphics.FillRectangle(b, new Rectangle(0, 66, header.Width, 8));
        };
        Controls.Add(header);
        header.Controls.Add(new PictureBox
        {
            Bounds = new Rectangle(18, 14, 42, 42), SizeMode = PictureBoxSizeMode.Zoom, Image = AppIcon.Draw(64)
        });
        header.Controls.Add(new Label
        {
            Text = "YouTube Music Overlay", Font = new Font("Segoe UI Semibold", 13.5f), ForeColor = Fg,
            AutoSize = true, Location = new Point(70, 13)
        });
        header.Controls.Add(new Label
        {
            Text = "v0.0.0 BETA  \u2022  for OBS Studio  \u2022  requests, lyrics and queue",
            Font = new Font("Segoe UI", 8.6f), ForeColor = Muted, AutoSize = true, Location = new Point(72, 40)
        });

        var tabs = new TabControl
        {
            Bounds = new Rectangle(12, 82, ClientSize.Width - 24, 520),
            Font = new Font("Segoe UI", 9f),
            Padding = new Point(14, 5)
        };
        Controls.Add(tabs);

        var tSetup = new TabPage("Setup") { BackColor = Bg, ForeColor = Fg };
        var tLook = new TabPage("Look") { BackColor = Bg, ForeColor = Fg };
        var tContent = new TabPage("Content") { BackColor = Bg, ForeColor = Fg };
        var tLyrics = new TabPage("Lyrics") { BackColor = Bg, ForeColor = Fg };
        var tSync = new TabPage("Sync") { BackColor = Bg, ForeColor = Fg };
        var tRequests = new TabPage("Requests") { BackColor = Bg, ForeColor = Fg };
        tabs.TabPages.AddRange(new[] { tSetup, tLook, tContent, tLyrics, tSync, tRequests });

        BuildSetupTab(tSetup, online);
        BuildLookTab(tLook);
        BuildContentTab(tContent);
        BuildLyricsTab(tLyrics);
        BuildSyncTab(tSync);
        BuildRequestsTab(tRequests);

        // footer
        var close = FlatButton("Close to tray", new Rectangle(14, 614, 128, 36), Color.FromArgb(48, 48, 60));
        close.Click += (_, _) => { Hide(); _tray.ShowBalloonTip(1400, "Still running", "The overlay keeps updating. Right-click the tray icon to exit.", ToolTipIcon.Info); };
        Controls.Add(close);

        var prev = FlatButton("Preview overlay", new Rectangle(150, 614, 130, 36), Color.FromArgb(255, 45, 85));
        prev.Click += (_, _) => OpenUrl();
        Controls.Add(prev);

        var dbg = FlatButton("What am I seeing?", new Rectangle(288, 614, 148, 36), Color.FromArgb(48, 48, 60));
        dbg.Click += (_, _) => OpenDebug();
        Controls.Add(dbg);

        var folder = new LinkLabel
        {
            Text = "settings folder", Bounds = new Rectangle(444, 624, 90, 20),
            LinkColor = Muted, ActiveLinkColor = Fg, Font = new Font("Segoe UI", 8.2f), LinkBehavior = LinkBehavior.HoverUnderline
        };
        folder.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo(AppSettings.Dir) { UseShellExecute = true }); } catch { } };
        Controls.Add(folder);
    }

    private void BuildSetupTab(TabPage t, bool online)
    {
        var card = new Panel { Bounds = new Rectangle(12, 12, ClientSize.Width - 58, 96), BackColor = Card };
        t.Controls.Add(card);

        _statusTitle.Text = "Waiting for music\u2026";
        _statusTitle.Font = new Font("Segoe UI Semibold", 11f);
        _statusTitle.ForeColor = Fg;
        _statusTitle.AutoEllipsis = true;
        _statusTitle.UseMnemonic = false;
        _statusTitle.Bounds = new Rectangle(16, 12, card.Width - 32, 22);
        card.Controls.Add(_statusTitle);

        _statusSub.Text = "Start a song in YouTube Music";
        _statusSub.ForeColor = Muted;
        _statusSub.AutoEllipsis = true;
        _statusSub.UseMnemonic = false;
        _statusSub.Bounds = new Rectangle(16, 36, card.Width - 32, 20);
        card.Controls.Add(_statusSub);

        _netStatus.Text = online
            ? "\u2713 YouTube Music DNS is reachable (lyrics checked separately)"
            : "\u26a0 YouTube Music DNS unavailable \u2014 check your connection";
        _netStatus.ForeColor = online ? Color.FromArgb(120, 220, 150) : Color.FromArgb(255, 180, 90);
        _netStatus.Font = new Font("Segoe UI", 8.4f);
        _netStatus.Bounds = new Rectangle(16, 60, card.Width - 32, 18);
        card.Controls.Add(_netStatus);

        _lyrStatus.Text = "";
        _lyrStatus.ForeColor = Muted;
        _lyrStatus.Font = new Font("Segoe UI", 8.4f);
        _lyrStatus.Bounds = new Rectangle(16, 76, card.Width - 32, 18);
        _lyrStatus.AutoEllipsis = true;
        card.Controls.Add(_lyrStatus);

        t.Controls.Add(Section("ADD THIS URL IN OBS", 118));

        var urlPanel = new Panel { Bounds = new Rectangle(12, 138, ClientSize.Width - 58, 40), BackColor = Color.FromArgb(12, 12, 16) };
        t.Controls.Add(urlPanel);
        _urlBox.Text = _url;
        _urlBox.Font = new Font("Consolas", 10f);
        _urlBox.ForeColor = Color.FromArgb(140, 220, 255);
        _urlBox.Bounds = new Rectangle(12, 10, urlPanel.Width - 108, 20);
        _urlBox.AutoSize = false;
        urlPanel.Controls.Add(_urlBox);
        var copy = FlatButton("Copy URL", new Rectangle(urlPanel.Width - 90, 7, 82, 26), Color.FromArgb(255, 45, 85));
        copy.Click += (_, _) => CopyUrl();
        urlPanel.Controls.Add(copy);

        t.Controls.Add(new Label
        {
            Text = "OBS \u2192 Sources \u2192 + \u2192 Browser \u2192 paste that URL.\r\n" +
                   "Size: 1100 x 240 with lyrics on, 900 x 260 with lyrics off (queue only).\r\n" +
                   "Untick \u201cShutdown source when not visible\u201d. Custom CSS can stay empty.\r\n\r\n" +
                   "Add ?demo=1 to the URL to see sample text, queue and lyrics without playing anything.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.6f),
            Bounds = new Rectangle(14, 186, ClientSize.Width - 62, 88)
        });

        t.Controls.Add(Section("MUSIC SOURCE", 282));
        t.Controls.Add(new Label
        {
            Text = "Works with music.youtube.com in your browser via Windows media sessions.\r\n" +
                   "Optional browser link adds the real queue and a direct playback clock.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.6f),
            Bounds = new Rectangle(14, 302, ClientSize.Width - 62, 40)
        });

        var restart = FlatButton("Open YT Music", new Rectangle(14, 348, 140, 34), Color.FromArgb(64, 64, 78));
        restart.Click += (_, _) => LaunchYtMusic();
        t.Controls.Add(restart);

        var link = FlatButton("Connect browser", new Rectangle(164, 348, 160, 34), Color.FromArgb(255, 45, 85));
        link.Click += (_, _) => OpenBrowserLink();
        t.Controls.Add(link);
        var dbg = FlatButton("Diagnose", new Rectangle(334, 348, 100, 34), Color.FromArgb(64, 64, 78));
        dbg.Click += (_, _) => OpenDebug();
        t.Controls.Add(dbg);
        t.Controls.Add(new Label
        {
            Text = "Without the optional browser link, the queue is public suggestions,\r\nnot necessarily your actual Up next list.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.5f),
            Bounds = new Rectangle(14, 395, ClientSize.Width - 62, 44)
        });
    }

    private void BuildRequestsTab(TabPage t)
    {
        t.Controls.Add(Section("VIEWER SONG REQUESTS  •  BETA", 14));
        t.Controls.Add(new Label
        {
            Text = "Let viewers request songs from Twitch, YouTube Live and Kick.\r\n" +
                   "Choose approval or automatic handling in the request dashboard.",
            ForeColor = Fg, Font = new Font("Segoe UI", 9f), Bounds = new Rectangle(14, 38, 444, 45)
        });
        var dashboard = FlatButton("Open request dashboard", new Rectangle(14, 96, 210, 36), Color.FromArgb(255, 45, 85));
        dashboard.Click += (_, _) => OpenRequests();
        t.Controls.Add(dashboard);
        var chats = FlatButton("Connect chats", new Rectangle(234, 96, 142, 36), Color.FromArgb(64, 64, 78));
        chats.Click += (_, _) => OpenChatLink();
        t.Controls.Add(chats);
        t.Controls.Add(new Label
        {
            Text = "1. Open your live chat in a browser and connect its chat bookmark.\r\n" +
                   "2. In the dashboard, turn requests on and choose a mode.\r\n" +
                   "3. Viewers type !sr followed by a song or YouTube video link.\r\n" +
                   "4. Review requests, or let automatic mode accept them.",
            ForeColor = Muted, Font = new Font("Segoe UI", 9f), Bounds = new Rectangle(14, 148, 444, 90)
        });
        t.Controls.Add(Section("SEND TO THE MUSIC PLAYER", 254));
        t.Controls.Add(new Label
        {
            Text = "Accepted requests are a separate list by default. To try adding\r\n" +
                   "them to YouTube Music, connect its browser tab, start a song,\r\n" +
                   "and enable experimental player insertion in the dashboard.\r\n" +
                   "Text searches use the first result; verify rights before streaming.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.7f), Bounds = new Rectangle(14, 278, 444, 78)
        });
        var copy = FlatButton("Copy request OBS URL", new Rectangle(14, 370, 205, 34), Color.FromArgb(64, 64, 78));
        copy.Click += (_, _) => { try { Clipboard.SetText(_url + "request-overlay"); MessageBox.Show(this, "Requests-only OBS URL copied.", "Copied"); } catch { } };
        t.Controls.Add(copy);
        t.Controls.Add(new Label
        {
            Text = "Approved requests only • Suggested OBS size: 650 × 400",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.3f), Bounds = new Rectangle(14, 416, 444, 24)
        });
    }

    private void BuildLookTab(TabPage t)
    {
        t.Controls.Add(Section("THEME", 10, 150));
        _theme.DropDownStyle = ComboBoxStyle.DropDownList;
        _theme.Bounds = new Rectangle(14, 30, 150, 26);
        _theme.Items.AddRange(new object[] { "Glass", "Minimal", "Neon", "Light" });
        _theme.SelectedIndexChanged += (_, _) => Push(nameof(AppSettings.Theme), _theme.SelectedItem!.ToString()!.ToLowerInvariant());
        t.Controls.Add(_theme);

        t.Controls.Add(Field("Size", 182, 10, 145));
        _size.DropDownStyle = ComboBoxStyle.DropDownList;
        _size.Bounds = new Rectangle(182, 30, 150, 26);
        _size.Items.AddRange(new object[] { "Small (75%)", "Medium (100%)", "Large (130%)", "Huge (170%)" });
        _size.SelectedIndexChanged += (_, _) =>
        {
            var map = new[] { 0.75, 1.0, 1.3, 1.7 };
            Push(nameof(AppSettings.Scale), map[Math.Max(0, _size.SelectedIndex)]);
        };
        t.Controls.Add(_size);

        t.Controls.Add(Field("Position", 350, 10, 125));
        _align.DropDownStyle = ComboBoxStyle.DropDownList;
        _align.Bounds = new Rectangle(350, 30, 132, 26);
        _align.Items.AddRange(new object[] { "Centred", "Bottom centre", "Bottom left", "Bottom right", "Top left", "Top right" });
        _align.SelectedIndexChanged += (_, _) =>
        {
            var map = new[] { "center", "bottomcenter", "bottomleft", "bottomright", "topleft", "topright" };
            Push(nameof(AppSettings.Align), map[Math.Max(0, _align.SelectedIndex)]);
        };
        t.Controls.Add(_align);

        t.Controls.Add(Section("COLOURS", 68));
        t.Controls.Add(Field("Accent", 14, 86, 145));
        _accent.Bounds = new Rectangle(14, 104, 96, 26);
        _accent.Font = new Font("Consolas", 9.5f);
        _accent.TextChanged += (_, _) => AccentChanged(_accent, _swatch, nameof(AppSettings.Accent));
        t.Controls.Add(_accent);
        _swatch.Bounds = new Rectangle(116, 104, 26, 26);
        _swatch.BorderStyle = BorderStyle.FixedSingle;
        _swatch.Cursor = Cursors.Hand;
        _swatch.Click += (_, _) => PickColour(_accent);
        t.Controls.Add(_swatch);

        t.Controls.Add(Field("Second colour (progress / neon)", 170, 86));
        _accent2.Bounds = new Rectangle(170, 104, 96, 26);
        _accent2.Font = new Font("Consolas", 9.5f);
        _accent2.TextChanged += (_, _) => AccentChanged(_accent2, _swatch2, nameof(AppSettings.Accent2));
        t.Controls.Add(_accent2);
        _swatch2.Bounds = new Rectangle(272, 104, 26, 26);
        _swatch2.BorderStyle = BorderStyle.FixedSingle;
        _swatch2.Cursor = Cursors.Hand;
        _swatch2.Click += (_, _) => PickColour(_accent2);
        t.Controls.Add(_swatch2);

        var presets = new[] { "#ff2d55", "#22d3ee", "#22c55e", "#f59e0b", "#7c3aed", "#ec4899", "#38bdf8", "#e5e7eb" };
        int x = 316;
        foreach (var hex in presets)
        {
            var sw = new Panel { Bounds = new Rectangle(x, 104, 18, 26), BackColor = SafeColor(hex), Cursor = Cursors.Hand };
            string captured = hex;
            sw.Click += (_, _) => { _accent.Text = captured; };
            t.Controls.Add(sw);
            x += 20;
        }

        t.Controls.Add(Section("FINISH", 146));
        t.Controls.Add(Field("Opacity", 14, 164));
        _opacity.Bounds = new Rectangle(14, 182, 200, 30);
        _opacity.Minimum = 30; _opacity.Maximum = 100; _opacity.TickFrequency = 10;
        _opacity.ValueChanged += (_, _) =>
        {
            _opacityVal.Text = _opacity.Value + "%";
            if (!_loading) Push(nameof(AppSettings.Opacity), _opacity.Value / 100.0);
        };
        t.Controls.Add(_opacity);
        _opacityVal.Bounds = new Rectangle(218, 186, 60, 20); _opacityVal.ForeColor = Muted;
        t.Controls.Add(_opacityVal);

        t.Controls.Add(Field("Corner roundness", 14, 220));
        _corner.Bounds = new Rectangle(14, 238, 200, 30);
        _corner.Minimum = 0; _corner.Maximum = 18; _corner.TickFrequency = 3;
        _corner.ValueChanged += (_, _) =>
        {
            _cornerVal.Text = (_corner.Value / 10.0).ToString("0.0") + "x";
            if (!_loading) Push(nameof(AppSettings.Corner), _corner.Value / 10.0);
        };
        t.Controls.Add(_corner);
        _cornerVal.Bounds = new Rectangle(218, 242, 60, 20); _cornerVal.ForeColor = Muted;
        t.Controls.Add(_cornerVal);

        t.Controls.Add(Field("Glass blur", 14, 276));
        _blur.Bounds = new Rectangle(14, 294, 200, 30);
        _blur.Minimum = 0; _blur.Maximum = 20; _blur.TickFrequency = 4;
        _blur.ValueChanged += (_, _) =>
        {
            _blurVal.Text = (_blur.Value / 10.0).ToString("0.0") + "x";
            if (!_loading) Push(nameof(AppSettings.Blur), _blur.Value / 10.0);
        };
        t.Controls.Add(_blur);
        _blurVal.Bounds = new Rectangle(218, 298, 60, 20); _blurVal.ForeColor = Muted;
        t.Controls.Add(_blurVal);

        t.Controls.Add(Section("TEXT", 336));
        _marquee.Text = "Scroll long titles";
        _marquee.ForeColor = Fg;
        _marquee.AutoSize = true;
        _marquee.Location = new Point(14, 356);
        _marquee.CheckedChanged += (_, _) => Push(nameof(AppSettings.TitleMarquee), _marquee.Checked);
        t.Controls.Add(_marquee);

        t.Controls.Add(Field("Capitalisation", 200, 354));
        _case.DropDownStyle = ComboBoxStyle.DropDownList;
        _case.Bounds = new Rectangle(200, 372, 120, 26);
        _case.Items.AddRange(new object[] { "Normal", "UPPERCASE" });
        _case.SelectedIndexChanged += (_, _) => Push(nameof(AppSettings.TextCase), _case.SelectedIndex == 1 ? "upper" : "normal");
        t.Controls.Add(_case);

        t.Controls.Add(Field("Small label text", 14, 408));
        _label.Bounds = new Rectangle(14, 426, 200, 26);
        _label.BorderStyle = BorderStyle.FixedSingle;
        _label.TextChanged += (_, _) => Push(nameof(AppSettings.Label), _label.Text);
        t.Controls.Add(_label);

        t.Controls.Add(Field("Text when nothing plays", 230, 408));
        _placeholder.Bounds = new Rectangle(230, 426, 200, 26);
        _placeholder.BorderStyle = BorderStyle.FixedSingle;
        _placeholder.TextChanged += (_, _) => Push(nameof(AppSettings.Placeholder), _placeholder.Text);
        t.Controls.Add(_placeholder);
    }

    private void BuildContentTab(TabPage t)
    {
        t.Controls.Add(Section("CARDS SHOWN", 10));
        AddCheck(t, _queue, "Show the queue", 28, nameof(AppSettings.ShowQueue));
        t.Controls.Add(Field("Queue rows", 300, 26));
        _count.DropDownStyle = ComboBoxStyle.DropDownList;
        _count.Bounds = new Rectangle(300, 44, 120, 26);
        _count.Items.AddRange(new object[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" });
        _count.SelectedIndexChanged += (_, _) => Push(nameof(AppSettings.QueueCount), int.Parse(_count.SelectedItem!.ToString()!));
        t.Controls.Add(_count);

        t.Controls.Add(Section("INSIDE THE QUEUE", 84));
        AddCheck(t, _qArt, "Cover art", 102, nameof(AppSettings.ShowQueueArt));
        AddCheck(t, _qNums, "Row numbers", 130, nameof(AppSettings.ShowQueueNumbers));
        AddCheck(t, _qArtist, "Artist names", 158, nameof(AppSettings.ShowQueueArtist));

        t.Controls.Add(Section("NOW PLAYING CARD", 196));
        AddCheck(t, _progress, "Progress bar", 214, nameof(AppSettings.ShowProgress));
        AddCheck(t, _times, "Time stamps", 242, nameof(AppSettings.ShowTimes));
        AddCheck(t, _album, "Album name", 270, nameof(AppSettings.ShowAlbum));
        AddCheck(t, _artBox, "Cover art", 298, nameof(AppSettings.ShowArt));

        t.Controls.Add(Section("BEHAVIOUR", 336));
        AddCheck(t, _labelBox, "Small label above title", 354, nameof(AppSettings.ShowLabel));
        AddCheck(t, _dim, "Dim when paused", 382, nameof(AppSettings.DimWhenPaused));
        AddCheck(t, _hidePaused, "Hide overlay when paused", 410, nameof(AppSettings.HideWhenPaused));
        var queueUrl = FlatButton("Copy queue URL", new Rectangle(276, 407, 148, 32), Color.FromArgb(64, 64, 78));
        queueUrl.Click += (_, _) => CopyQueueUrl();
        t.Controls.Add(queueUrl);
    }

    private void BuildLyricsTab(TabPage t)
    {
        t.Controls.Add(Section("LYRICS & VISIBILITY", 8));
        AddCheck(t, _lyrics, "Show lyrics in the main overlay", 28, nameof(AppSettings.ShowLyrics));
        var repair = FlatButton("Enable + repair", new Rectangle(278, 24, 155, 32), Color.FromArgb(255, 45, 85));
        repair.Click += (_, _) => RepairLyrics();
        t.Controls.Add(repair);

        _lyricsPanelStatus.Text = "Lyrics: waiting for a song.";
        _lyricsPanelStatus.ForeColor = Color.FromArgb(180, 220, 200);
        _lyricsPanelStatus.Font = new Font("Segoe UI", 8.7f);
        _lyricsPanelStatus.Bounds = new Rectangle(14, 62, ClientSize.Width - 62, 32);
        t.Controls.Add(_lyricsPanelStatus);

        t.Controls.Add(Field("Lines on screen", 14, 97, 125));
        _lyrLines.DropDownStyle = ComboBoxStyle.DropDownList;
        _lyrLines.Bounds = new Rectangle(14, 116, 112, 26);
        _lyrLines.Items.AddRange(new object[] { "1", "2", "3", "4", "5", "6" });
        _lyrLines.SelectedIndexChanged += (_, _) => Push(nameof(AppSettings.LyricsLines), int.Parse(_lyrLines.SelectedItem!.ToString()!));
        t.Controls.Add(_lyrLines);

        t.Controls.Add(Field("Lyric offset (seconds)", 157, 97, 132));
        _lyricsOffset.Bounds = new Rectangle(157, 116, 112, 26);
        _lyricsOffset.Minimum = -30; _lyricsOffset.Maximum = 30; _lyricsOffset.DecimalPlaces = 1; _lyricsOffset.Increment = 0.1m;
        _lyricsOffset.ValueChanged += (_, _) => Push(nameof(AppSettings.LyricsOffsetSeconds), (double)_lyricsOffset.Value);
        t.Controls.Add(_lyricsOffset);
        t.Controls.Add(new Label
        {
            Text = "Positive = lyrics earlier", ForeColor = Muted,
            Font = new Font("Segoe UI", 8.2f), Bounds = new Rectangle(157, 145, 170, 20)
        });
        t.Controls.Add(Field("Text alignment", 294, 97, 140));
        _lyricsAlign.DropDownStyle = ComboBoxStyle.DropDownList;
        _lyricsAlign.Bounds = new Rectangle(294, 116, 132, 26);
        _lyricsAlign.Items.AddRange(new object[] { "Left", "Centered" });
        _lyricsAlign.SelectedIndexChanged += (_, _) => Push(nameof(AppSettings.LyricsAlign), _lyricsAlign.SelectedIndex == 1 ? "center" : "left");
        t.Controls.Add(_lyricsAlign);

        AddCheck(t, _karaoke, "Highlight the line being sung", 167, nameof(AppSettings.LyricsKaraoke));
        AddCheck(t, _hideInstr, "Hide main card for instrumentals", 194, nameof(AppSettings.LyricsHideInstrumental));
        t.Controls.Add(Section("LYRICS SOURCE & REPAIR", 228));
        var copy = FlatButton("Copy lyrics URL", new Rectangle(14, 247, 155, 32), Color.FromArgb(64, 64, 78));
        copy.Click += (_, _) => CopyLyricsUrl();
        t.Controls.Add(copy);
        var test = FlatButton("Test sample", new Rectangle(179, 247, 120, 32), Color.FromArgb(64, 64, 78));
        test.Click += (_, _) => OpenLyricsTest();
        t.Controls.Add(test);
        var retry = FlatButton("Retry", new Rectangle(309, 247, 105, 32), Color.FromArgb(64, 64, 78));
        retry.Click += (_, _) => RetryLyrics();
        t.Controls.Add(retry);
        var edit = FlatButton("Search / paste lyrics", new Rectangle(14, 287, 195, 32), Color.FromArgb(64, 64, 78));
        edit.Click += (_, _) => EditLyrics();
        t.Controls.Add(edit);
        var debug = FlatButton("Details", new Rectangle(219, 287, 100, 32), Color.FromArgb(64, 64, 78));
        debug.Click += (_, _) => OpenDebug();
        t.Controls.Add(debug);

        t.Controls.Add(Section("LIVE LYRICS  •  UNCHECK FOLLOW TO SCROLL", 331, 310));
        _followLyrics.Text = "Follow line";
        _followLyrics.ForeColor = Fg;
        _followLyrics.AutoSize = true;
        _followLyrics.Location = new Point(334, 326);
        _followLyrics.CheckedChanged += (_, _) => { _previewIndex = -2; Push(nameof(AppSettings.FollowLyricsInApp), _followLyrics.Checked); };
        t.Controls.Add(_followLyrics);
        _liveLyrics.Bounds = new Rectangle(14, 353, ClientSize.Width - 62, 114);
        _liveLyrics.ReadOnly = true;
        _liveLyrics.DetectUrls = false;
        _liveLyrics.BorderStyle = BorderStyle.FixedSingle;
        _liveLyrics.ScrollBars = RichTextBoxScrollBars.Vertical;
        _liveLyrics.BackColor = Color.FromArgb(11, 11, 15);
        _liveLyrics.ForeColor = Color.FromArgb(185, 190, 204);
        _liveLyrics.Font = new Font("Segoe UI", 9.5f);
        _liveLyrics.Text = "Waiting for a song. Automatically fetched or saved lyrics will appear here.";
        t.Controls.Add(_liveLyrics);
    }

    private void BuildSyncTab(TabPage t)
    {
        t.Controls.Add(Section("PLAYBACK CLOCK", 12));
        _clockValue.Text = "--:-- / --:--";
        _clockValue.Font = new Font("Segoe UI Semibold", 18f);
        _clockValue.ForeColor = Fg;
        _clockValue.Bounds = new Rectangle(14, 32, 360, 38);
        t.Controls.Add(_clockValue);
        _clockDetail.Text = "Waiting for a song";
        _clockDetail.ForeColor = Muted;
        _clockDetail.Font = new Font("Segoe UI", 8.5f);
        _clockDetail.Bounds = new Rectangle(14, 74, ClientSize.Width - 62, 35);
        t.Controls.Add(_clockDetail);
        t.Controls.Add(Section("MANUAL CLOCK ADJUSTMENT", 118));
        t.Controls.Add(Field("Seconds (positive moves forward)", 14, 139, 230));
        _clockOffset.Bounds = new Rectangle(14, 160, 112, 27);
        _clockOffset.Minimum = -30; _clockOffset.Maximum = 30; _clockOffset.DecimalPlaces = 1; _clockOffset.Increment = 0.1m;
        _clockOffset.ValueChanged += (_, _) => Push(nameof(AppSettings.PositionOffsetSeconds), (double)_clockOffset.Value);
        t.Controls.Add(_clockOffset);
        var reset = FlatButton("Reset to 0", new Rectangle(142, 157, 120, 32), Color.FromArgb(64, 64, 78));
        reset.Click += (_, _) => _clockOffset.Value = 0;
        t.Controls.Add(reset);
        t.Controls.Add(new Label
        {
            Text = "Changes the song timer and timed lyrics together.\r\nFor lyrics only, use the separate offset on the Lyrics tab.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.5f),
            Bounds = new Rectangle(14, 195, ClientSize.Width - 62, 42)
        });
        AddCheck(t, _remaining, "Show remaining time instead of duration", 244, nameof(AppSettings.ShowRemainingTime));
        t.Controls.Add(Section("MORE OVERLAY OPTIONS", 280));
        AddCheck(t, _transparent, "Transparent panels (text and artwork only)", 300, nameof(AppSettings.TransparentBackground));
        AddCheck(t, _reduceMotion, "Reduce animations and scrolling", 330, nameof(AppSettings.ReduceMotion));
        t.Controls.Add(new Label
        {
            Text = "Independent OBS sources let you place the queue and lyrics\r\nanywhere in your scene. The browser link is optional.",
            ForeColor = Muted, Font = new Font("Segoe UI", 8.5f),
            Bounds = new Rectangle(14, 369, ClientSize.Width - 62, 40)
        });
        var queue = FlatButton("Copy queue-only URL", new Rectangle(14, 416, 190, 34), Color.FromArgb(64, 64, 78));
        queue.Click += (_, _) => CopyQueueUrl();
        t.Controls.Add(queue);
        var browser = FlatButton("Connect browser", new Rectangle(214, 416, 166, 34), Color.FromArgb(64, 64, 78));
        browser.Click += (_, _) => OpenBrowserLink();
        t.Controls.Add(browser);
    }

    private void AddCheck(TabPage t, CheckBox box, string text, int y, string prop)
    {
        box.Text = text;
        box.ForeColor = Fg;
        box.AutoSize = true;
        box.Location = new Point(16, y);
        box.CheckedChanged += (_, _) => Push(prop, box.Checked);
        t.Controls.Add(box);
    }

    private static Label Section(string text, int y, int width = 420) => new()
    {
        Text = text, ForeColor = Color.FromArgb(120, 120, 138),
        Font = new Font("Segoe UI", 8f, FontStyle.Bold), UseMnemonic = false,
        Bounds = new Rectangle(14, y, width, 16), AutoSize = false
    };

    private static Label Field(string text, int x, int y, int width = 200) => new()
    {
        Text = text, ForeColor = Muted, Font = new Font("Segoe UI", 8.3f),
        Bounds = new Rectangle(x, y, width, 18), AutoSize = false
    };

    private static Button FlatButton(string text, Rectangle bounds, Color back)
    {
        var b = new Button
        {
            Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Fg,
            Font = new Font("Segoe UI Semibold", 9f), Cursor = Cursors.Hand, TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.18f);
        return b;
    }

    // ------------------------------------------------------------------ settings plumbing

    private void Push(string prop, object value)
    {
        if (_loading) return;
        try
        {
            var st = _get();
            typeof(AppSettings).GetProperty(prop)?.SetValue(st, value);
            st.Sanitize();
            st.Save();
        }
        catch { }
    }

    private void AccentChanged(TextBox box, Panel swatch, string prop)
    {
        if (_loading) return;
        try
        {
            var c = ColorTranslator.FromHtml(box.Text.Trim());
            box.ForeColor = Fg;
            swatch.BackColor = c;
            Push(prop, "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"));
        }
        catch { box.ForeColor = Color.FromArgb(255, 120, 120); }
    }

    private void PickColour(TextBox box)
    {
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
        try { dlg.Color = ColorTranslator.FromHtml(box.Text.Trim()); } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = "#" + dlg.Color.R.ToString("x2") + dlg.Color.G.ToString("x2") + dlg.Color.B.ToString("x2");
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var st = _get();
        _theme.SelectedIndex = st.Theme switch { "minimal" => 1, "neon" => 2, "light" => 3, _ => 0 };
        _size.SelectedIndex = st.Scale switch { < 0.9 => 0, < 1.15 => 1, < 1.5 => 2, _ => 3 };
        _align.SelectedIndex = st.Align switch
        {
            "bottomcenter" => 1, "bottomleft" => 2, "bottomright" => 3, "topleft" => 4, "topright" => 5, _ => 0
        };
        _case.SelectedIndex = st.TextCase == "upper" ? 1 : 0;
        _accent.Text = st.Accent;
        _accent2.Text = st.Accent2;
        _swatch.BackColor = SafeColor(st.Accent);
        _swatch2.BackColor = SafeColor(st.Accent2);
        _opacity.Value = (int)Math.Round(Math.Clamp(st.Opacity, 0.3, 1) * 100);
        _opacityVal.Text = _opacity.Value + "%";
        _corner.Value = (int)Math.Round(Math.Clamp(st.Corner, 0, 1.8) * 10);
        _cornerVal.Text = (st.Corner).ToString("0.0") + "x";
        _blur.Value = (int)Math.Round(Math.Clamp(st.Blur, 0, 2) * 10);
        _blurVal.Text = (st.Blur).ToString("0.0") + "x";
        _label.Text = st.Label;
        _placeholder.Text = st.Placeholder;
        _marquee.Checked = st.TitleMarquee;
        _count.SelectedItem = Math.Clamp(st.QueueCount, 0, 8).ToString();
        _queue.Checked = st.ShowQueue;
        _qArt.Checked = st.ShowQueueArt;
        _qNums.Checked = st.ShowQueueNumbers;
        _qArtist.Checked = st.ShowQueueArtist;
        _progress.Checked = st.ShowProgress;
        _times.Checked = st.ShowTimes;
        _album.Checked = st.ShowAlbum;
        _artBox.Checked = st.ShowArt;
        _labelBox.Checked = st.ShowLabel;
        _dim.Checked = st.DimWhenPaused;
        _hidePaused.Checked = st.HideWhenPaused;
        _lyrics.Checked = st.ShowLyrics;
        _lyrLines.SelectedItem = Math.Clamp(st.LyricsLines, 1, 6).ToString();
        _karaoke.Checked = st.LyricsKaraoke;
        _hideInstr.Checked = st.LyricsHideInstrumental;
        _lyricsOffset.Value = (decimal)Math.Clamp(st.LyricsOffsetSeconds, -30, 30);
        _lyricsAlign.SelectedIndex = st.LyricsAlign == "center" ? 1 : 0;
        _followLyrics.Checked = st.FollowLyricsInApp;
        _clockOffset.Value = (decimal)Math.Clamp(st.PositionOffsetSeconds, -30, 30);
        _remaining.Checked = st.ShowRemainingTime;
        _transparent.Checked = st.TransparentBackground;
        _reduceMotion.Checked = st.ReduceMotion;
        _loading = false;
    }

    private static Color SafeColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(255, 45, 85); }
    }

    // ------------------------------------------------------------------ actions

    private void ShowPanel()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RepairLyrics()
    {
        var st = _get();
        st.ShowLyrics = true;
        st.LyricsSource = "lrclib";
        st.LyricsHideInstrumental = false;
        st.Sanitize();
        st.Save();
        _loading = true;
        _lyrics.Checked = true;
        _hideInstr.Checked = false;
        _loading = false;
        _server.RetryLyrics();
        RefreshStatus();
    }

    private void CopyLyricsUrl()
    {
        try
        {
            Clipboard.SetText(_url + "lyrics");
            MessageBox.Show(this,
                "Lyrics-only URL copied!\n\nIn OBS, add a NEW Browser source and paste the URL. " +
                "Try width 800 and height 180. This separate source works even if lyrics are turned off in the main overlay.\n\n" +
                "URL: " + _url + "lyrics",
                "Lyrics source", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not copy the URL: " + ex.Message); }
    }

    private void CopyQueueUrl()
    {
        try
        {
            Clipboard.SetText(_url + "queue");
            MessageBox.Show(this,
                "Queue-only URL copied!\n\nAdd it as another OBS Browser source (try 650 x 350). " +
                "With the optional browser link this is your actual Up next queue; otherwise it shows public suggestions.\n\n" +
                "URL: " + _url + "queue",
                "Queue source", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not copy the URL: " + ex.Message); }
    }

    private void OpenBrowserLink()
    {
        try { Process.Start(new ProcessStartInfo(_url + "connect") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open browser setup: " + ex.Message); }
    }

    private void OpenRequests()
    {
        try { Process.Start(new ProcessStartInfo(_url + "requests") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open requests: " + ex.Message); }
    }

    private void OpenChatLink()
    {
        try { Process.Start(new ProcessStartInfo(_url + "chat-connect") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open chat setup: " + ex.Message); }
    }

    private void OpenLyricsTest()
    {
        try { Process.Start(new ProcessStartInfo(_url + "lyrics?demo=1") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Could not open the test: " + ex.Message); }
    }

    private void RetryLyrics()
    {
        var now = _monitor.Current;
        if (string.IsNullOrWhiteSpace(now.Title))
        {
            MessageBox.Show(this, "Play a song first. If the Setup tab still says nothing is playing, check Windows media integration in your music app.",
                "No song detected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_manualLyrics.TryGet(now.Title, now.Artist, out _))
        {
            MessageBox.Show(this, "Saved lyrics take priority for this song. To return to automatic lookup, open Search / paste lyrics and choose Remove saved lyrics.",
                "Saved lyrics in use", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _server.RetryLyrics();
        RefreshStatus();
    }

    private void EditLyrics()
    {
        var now = _monitor.Current;
        if (string.IsNullOrWhiteSpace(now.Title))
        {
            MessageBox.Show(this, "Play a song first so these lyrics can be saved for the correct track. " +
                "Use Test sample lyrics to check the box without playing anything.",
                "No song detected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new LyricsEditorForm(now, _monitor, _lyricsClient, _manualLyrics, _server);
        dialog.ShowDialog(this);
        RefreshStatus();
    }

    private void CopyUrl()
    {
        try { Clipboard.SetText(_url); _statusSub.Text = "URL copied to clipboard"; } catch { }
    }

    private void OpenUrl()
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); } catch { }
    }

    private void OpenDebug()
    {
        if (_debug is { IsDisposed: false })
        {
            _debug.Show();
            _debug.Activate();
            return;
        }
        _debug = new DebugForm(_url, _monitor);
        _debug.Show(this);
    }

    private static void LaunchYtMusic()
    {
        try { Process.Start(new ProcessStartInfo("https://music.youtube.com") { UseShellExecute = true }); }
        catch { }
    }

    private void RefreshStatus()
    {
        var now = _monitor.Current;
        string lyricStatus = _server.GetLyricsStatus();
        _lyrStatus.Text = lyricStatus;
        _lyricsPanelStatus.Text = lyricStatus;
        RefreshClock(now);
        RefreshLyricsPreview();
        if (string.IsNullOrWhiteSpace(now.Title))
        {
            _statusTitle.Text = string.IsNullOrWhiteSpace(_get().Placeholder) ? "Waiting for music" : _get().Placeholder;
            _statusSub.Text = "Open YouTube Music and press play";
            _tray.Text = "YouTube Music Overlay \u2014 idle";
            return;
        }
        _statusTitle.Text = now.Title;
        string source = string.IsNullOrWhiteSpace(now.SourceLabel) ? "" : $"  \u2022  via {now.SourceLabel}";
        _statusSub.Text = (string.IsNullOrEmpty(now.Artist)
            ? (now.Playing ? "Playing" : "Paused")
            : $"{now.Artist}  \u2022  {(now.Playing ? "playing" : "paused")}") + source;

        var t = "YT Music Overlay \u2014 " + now.Title;
        _tray.Text = t.Length > 62 ? t.Substring(0, 62) : t;
    }

    private static string ClockText(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        long whole = (long)Math.Min(seconds, 359999);
        long hours = whole / 3600, minutes = (whole / 60) % 60, secs = whole % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{whole / 60}:{secs:00}";
    }

    private void RefreshClock(NowPlaying now)
    {
        if (string.IsNullOrWhiteSpace(now.Title))
        {
            _clockValue.Text = "--:-- / --:--";
            _clockDetail.Text = "Waiting for a song. Start playback in your browser.";
            return;
        }
        double adjusted = Math.Max(0, now.Position + _get().PositionOffsetSeconds);
        if (now.Duration > 0) adjusted = Math.Min(adjusted, now.Duration);
        _clockValue.Text = ClockText(adjusted) + " / " + (now.Duration > 0 ? ClockText(now.Duration) : "--:--");
        string age = now.ClockSourceAgeSeconds >= 0 ? $"  •  sample age {now.ClockSourceAgeSeconds:0.0}s" : "";
        _clockDetail.Text = now.ClockSource + "  •  " + now.ClockMode + age;
    }

    private void SetPreviewText(string text)
    {
        _liveLyrics.Text = text;
        // RichTextBox may reset character colors on assignment, especially under Wine.
        _liveLyrics.SelectAll();
        _liveLyrics.SelectionColor = Color.FromArgb(185, 190, 204);
        _liveLyrics.SelectionBackColor = _liveLyrics.BackColor;
        _liveLyrics.Select(0, 0);
    }

    private void RefreshLyricsPreview()
    {
        var (now, result, pending, _) = _server.GetLyricsPreview();
        if (!result.HasLines)
        {
            string message = string.IsNullOrWhiteSpace(now.Title) ? "Waiting for a song. Play music to see lyrics here." :
                pending || result.Reason == "pending" ? "Looking up lyrics for this song..." :
                result.Instrumental ? "This track is marked instrumental." :
                result.Reason == "error" ? "Could not reach the lyrics service. Use Retry or Search / paste lyrics." :
                "No automatic lyrics found. Use Search / paste lyrics to add your own.";
            string sig = "message|" + now.TrackId + "|" + message;
            if (_previewSignature != sig)
            {
                _previewSignature = sig; _previewIndex = -2; _previewRanges.Clear();
                SetPreviewText(message);
            }
            return;
        }

        int index = -1;
        if (result.Synced)
        {
            double position = Math.Max(0, now.Position + _get().PositionOffsetSeconds);
            if (now.Duration > 0) position = Math.Min(position, now.Duration);
            position += _get().LyricsOffsetSeconds + 0.12;
            for (int i = 0; i < result.Lines.Count; i++)
            {
                if (result.Lines[i].Time <= position) index = i; else break;
            }
        }
        // Follow mode uses a small, rolling window with the current line at the
        // top. This is reliable even when a RichEdit control won't scroll an
        // unfocused caret; unchecking Follow displays the full scrollable text.
        bool follow = result.Synced && _followLyrics.Checked;
        int startLine = follow && index >= 0 ? index : 0;
        int endLine = follow ? Math.Min(result.Lines.Count, startLine + 6) : result.Lines.Count;
        var hash = new HashCode();
        hash.Add(now.TrackId); hash.Add(result.Synced); hash.Add(result.Source); hash.Add(follow); hash.Add(startLine);
        foreach (var line in result.Lines) { hash.Add(line.Time); hash.Add(line.Text); }
        string signature = now.TrackId + "|" + hash.ToHashCode().ToString();
        if (_previewSignature != signature)
        {
            _previewSignature = signature; _previewIndex = -2; _previewRanges.Clear();
            var text = new StringBuilder();
            for (int i = startLine; i < endLine; i++)
            {
                if (text.Length > 0) text.Append('\n');
                string value = string.IsNullOrWhiteSpace(result.Lines[i].Text) ? "♪" : result.Lines[i].Text;
                int start = text.Length;
                text.Append(value);
                _previewRanges.Add((start, value.Length));
            }
            SetPreviewText(text.ToString());
        }
        if (!follow || index < 0 || index == _previewIndex || _previewRanges.Count == 0) return;
        _previewIndex = index;
        var active = _previewRanges[0];
        _liveLyrics.Select(active.start, active.length);
        _liveLyrics.SelectionColor = Color.White;
        _liveLyrics.SelectionBackColor = Color.FromArgb(94, 42, 65);
        _liveLyrics.Select(0, 0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1400, "Still running", "The overlay keeps updating. Right-click the tray icon to exit.", ToolTipIcon.Info);
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }
}

internal static class AppIcon
{
    public static Icon Create()
    {
        try
        {
            using var bmp = Draw(64);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return SystemIcons.Application; }
    }

    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size),
            Color.FromArgb(255, 55, 95), Color.FromArgb(120, 70, 240), 45f);
        using var path = RoundRect(new Rectangle(0, 0, size, size), size / 4);
        g.FillPath(bg, path);
        using var ring = new Pen(Color.White, Math.Max(2f, size * 0.055f));
        float cx = size * 0.5f, cy = size * 0.5f, r = size * 0.185f;
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(ring, cx, cy + r, cx, cy - r * 0.15f);
        g.FillEllipse(Brushes.White, cx - r * 0.85f, cy + r * 0.35f, r * 0.55f, r * 0.55f);
        return bmp;
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
