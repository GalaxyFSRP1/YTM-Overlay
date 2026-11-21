using System;

namespace YtmOverlay;

public sealed class NowPlaying
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
    public double Rate { get; set; } = 1;
    public bool Playing { get; set; }
    public string SourceLabel { get; set; } = "";
    public string TrackId { get; set; } = "";
    /// <summary>Relative URL of the cover art as served by this app (e.g. /art/abc).</summary>
    public string Art { get; set; } = "";
    /// <summary>Monotonic discontinuity counter; changes on seeks and state transitions.</summary>
    public long ClockRevision { get; set; }
    public string ClockSource { get; set; } = "Windows";
    public string ClockMode { get; set; } = "waiting";
    public double ClockRawPosition { get; set; }
    public double ClockSourceAgeSeconds { get; set; } = -1;
    public bool ClockTimestampTrusted { get; set; }
}
