using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace YtmOverlay;

/// <summary>Lets the user correct a search or paste their own lyrics for the current song.</summary>
public sealed class LyricsEditorForm : Form
{
    private readonly NowPlaying _target;
    private readonly SessionMonitor _monitor;
    private readonly LyricsClient _client;
    private readonly ManualLyricsStore _store;
    private readonly WebServer _server;
    private readonly TextBox _queryTitle = new();
    private readonly TextBox _queryArtist = new();
    private readonly TextBox _words = new();
    private readonly Label _notice = new();
    private readonly Button _search = new();

    public LyricsEditorForm(NowPlaying target, SessionMonitor monitor, LyricsClient client, ManualLyricsStore store, WebServer server)
    {
        _target = target;
        _monitor = monitor;
        _client = client;
        _store = store;
        _server = server;
        Text = "Find or paste lyrics";
        ClientSize = new Size(690, 610);
        MinimumSize = new Size(590, 530);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 18, 22);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9.5f);
        Icon = AppIcon.Create();
        DoubleBuffered = true;

        var targetLabel = new Label
        {
            Text = "SAVING FOR:  " + target.Title + (string.IsNullOrWhiteSpace(target.Artist) ? "" : "  —  " + target.Artist),
            Bounds = new Rectangle(18, 14, 650, 25), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoEllipsis = true, UseMnemonic = false, ForeColor = Color.FromArgb(255, 125, 150), Font = new Font("Segoe UI Semibold", 9.5f)
        };
        Controls.Add(targetLabel);
        Controls.Add(new Label
        {
            Text = "Search with a different title or artist, or just paste your own text below.",
            Bounds = new Rectangle(18, 43, 650, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.FromArgb(165, 165, 178)
        });
        Controls.Add(new Label { Text = "Search title", Bounds = new Rectangle(18, 72, 120, 20), ForeColor = Color.FromArgb(165, 165, 178) });
        Controls.Add(new Label { Text = "Search artist", Bounds = new Rectangle(273, 72, 120, 20), ForeColor = Color.FromArgb(165, 165, 178) });
        _queryTitle.Bounds = new Rectangle(18, 93, 240, 28);
        _queryTitle.Text = target.Title;
        Controls.Add(_queryTitle);
        _queryArtist.Bounds = new Rectangle(273, 93, 215, 28);
        _queryArtist.Text = target.Artist;
        Controls.Add(_queryArtist);
        _search.Text = "Search online";
        _search.Bounds = new Rectangle(503, 91, 150, 31);
        StyleButton(_search, Color.FromArgb(255, 45, 85));
        _search.Click += async (_, _) => await SearchAsync();
        Controls.Add(_search);

        _notice.Text = "Tip: plain text works. To sync lines, paste LRC timestamps such as [00:12.50] First line.";
        _notice.Bounds = new Rectangle(18, 132, 650, 43);
        _notice.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _notice.ForeColor = Color.FromArgb(175, 175, 190);
        Controls.Add(_notice);

        _words.Bounds = new Rectangle(18, 180, 654, 348);
        _words.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _words.Multiline = true;
        _words.ScrollBars = ScrollBars.Vertical;
        _words.AcceptsReturn = true;
        _words.AcceptsTab = false;
        _words.BackColor = Color.FromArgb(30, 30, 36);
        _words.ForeColor = Color.White;
        _words.BorderStyle = BorderStyle.FixedSingle;
        _words.Font = new Font("Consolas", 10f);
        _words.Text = _store.GetText(target.Title, target.Artist);
        Controls.Add(_words);

        var save = new Button { Text = "Save for this song", Bounds = new Rectangle(18, 545, 175, 38), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        StyleButton(save, Color.FromArgb(255, 45, 85));
        save.Click += (_, _) => Save();
        Controls.Add(save);
        var remove = new Button { Text = "Remove saved lyrics", Bounds = new Rectangle(204, 545, 182, 38), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        StyleButton(remove, Color.FromArgb(60, 60, 72));
        remove.Click += (_, _) => Remove();
        Controls.Add(remove);
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(570, 545, 102, 38), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        StyleButton(cancel, Color.FromArgb(60, 60, 72));
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);
    }

    private static void StyleButton(Button b, Color color)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = color;
        b.ForeColor = Color.White;
        b.Cursor = Cursors.Hand;
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        string title = _queryTitle.Text.Trim();
        string artist = _queryArtist.Text.Trim();
        if (title.Length == 0) { _notice.Text = "Enter a title to search for."; return; }
        _search.Enabled = false;
        _notice.Text = "Searching LRCLIB and the backup source...";
        try
        {
            _client.Forget(title, artist);
            var result = await _client.GetAsync(title, artist, "", _target.Duration);
            if (IsDisposed) return;
            if (result.HasLines)
            {
                _words.Text = ToEditableText(result);
                _notice.Text = $"Found {result.Lines.Count} {(result.Synced ? "timed" : "plain")} lines from {result.Source}. Review them, then click Save for this song.";
            }
            else if (result.Instrumental)
                _notice.Text = "This search result is marked instrumental. Try another title or paste your own text.";
            else if (result.Reason == "error")
                _notice.Text = "The lyrics service could not be reached. You can still paste lyrics. " + result.Detail;
            else
                _notice.Text = "No matching lyrics found. Try simpler search words, or paste your own text below.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _notice.Text = "Search failed: " + ex.Message;
        }
        finally { if (!IsDisposed) _search.Enabled = true; }
    }

    private static string ToEditableText(LyricsResult result)
    {
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (result.Synced)
            {
                long hundredths = (long)Math.Round(Math.Max(0, line.Time) * 100);
                long minutes = hundredths / 6000;
                long seconds = (hundredths / 100) % 60;
                long fraction = hundredths % 100;
                sb.Append('[').Append(minutes.ToString("00", CultureInfo.InvariantCulture)).Append(':')
                  .Append(seconds.ToString("00", CultureInfo.InvariantCulture)).Append('.')
                  .Append(fraction.ToString("00", CultureInfo.InvariantCulture)).Append("] ");
            }
            sb.AppendLine(line.Text);
        }
        return sb.ToString();
    }

    private bool ConfirmTargetIfSongChanged()
    {
        var current = _monitor.Current;
        if (current.TrackId == _target.TrackId) return true;
        return MessageBox.Show(this,
            "The playing song changed while this window was open. Save/remove lyrics for the original song?\n\n" +
            _target.Title + " — " + _target.Artist,
            "Song changed", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private void Save()
    {
        if (!ConfirmTargetIfSongChanged()) return;
        try
        {
            _store.Set(_target.Title, _target.Artist, _words.Text);
            _server.RetryLyrics();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save lyrics", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Remove()
    {
        if (!ConfirmTargetIfSongChanged()) return;
        try
        {
            bool removed = _store.Remove(_target.Title, _target.Artist);
            if (!removed) { _notice.Text = "There are no saved lyrics for this song."; return; }
            _words.Clear();
            _server.RetryLyrics();
            _notice.Text = "Saved lyrics removed. Automatic lookup will be used for this song.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not remove lyrics", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
