using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YtmOverlay;

/// <summary>Lyrics the user deliberately supplies, saved locally for the exact song/artist.</summary>
public sealed class ManualLyricsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _text = new();
    private readonly Dictionary<string, LyricsResult> _parsed = new();

    public ManualLyricsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppSettings.Dir, "saved-lyrics.json");
        try
        {
            if (File.Exists(_path))
                _text = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch { _text = new(); }
        foreach (var entry in _text)
        {
            var result = Parse(entry.Value ?? "");
            if (result.HasLines) _parsed[entry.Key] = result;
        }
    }

    private static string Key(string title, string artist) =>
        Regex.Replace((title ?? "").Trim(), @"\s+", " ").ToLowerInvariant() + "\u001f" +
        Regex.Replace((artist ?? "").Trim(), @"\s+", " ").ToLowerInvariant();

    public bool TryGet(string title, string artist, out LyricsResult result)
    {
        lock (_gate) return _parsed.TryGetValue(Key(title, artist), out result!);
    }

    public string GetText(string title, string artist)
    {
        lock (_gate) return _text.TryGetValue(Key(title, artist), out var value) ? value : "";
    }

    public void Set(string title, string artist, string text)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Play a song first.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Paste some lyrics first.");
        if (text.Length > 128_000) throw new ArgumentException("Lyrics are too long (maximum 128,000 characters).");
        var parsed = Parse(text);
        if (!parsed.HasLines) throw new ArgumentException("No lyric lines were found in that text.");
        string key = Key(title, artist);
        lock (_gate)
        {
            var updated = new Dictionary<string, string>(_text) { [key] = text };
            Save(updated);
            _text = updated;
            _parsed[key] = parsed;
        }
    }

    public bool Remove(string title, string artist)
    {
        string key = Key(title, artist);
        lock (_gate)
        {
            if (!_text.ContainsKey(key)) return false;
            var updated = new Dictionary<string, string>(_text);
            updated.Remove(key);
            Save(updated);
            _text = updated;
            _parsed.Remove(key);
            return true;
        }
    }

    private void Save(Dictionary<string, string> snapshot)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static LyricsResult Parse(string text)
    {
        var timed = LyricsClient.ParseLrc(text);
        if (timed.Any(l => !string.IsNullOrWhiteSpace(l.Text)))
            return new LyricsResult { Found = true, Synced = true, Lines = timed, Source = "Saved lyrics", Reason = "synced" };
        var plain = LyricsClient.PlainLines(text);
        return new LyricsResult { Found = plain.Count > 0, Synced = false, Lines = plain, Source = "Saved lyrics", Reason = "plain" };
    }
}
