using System;

namespace YtmOverlay;

/// <summary>
/// Projects Windows media-session snapshots onto a monotonic clock. Windows documents
/// Position as valid at LastUpdatedTime, not necessarily at the time we read it.
/// This class has no WinRT dependencies so frozen reports, seeks, pauses and rate
/// changes can be tested deterministically.
/// </summary>
public sealed class TimelineClock
{
    private bool _has;
    private bool _playing;
    private double _basePosition, _baseMonotonic, _rate = 1, _duration;
    private double _lastRaw;
    private DateTimeOffset _lastStamp;
    private bool _lastStampValid, _timestampUnreliable;
    private long _revision;

    public double RawPosition { get; private set; }
    public double SourceAgeSeconds { get; private set; } = -1;
    public bool TimestampTrusted { get; private set; }
    public string Mode { get; private set; } = "waiting";
    public long Revision => _revision;

    public double At(double monotonicSeconds)
    {
        if (!_has) return 0;
        double elapsed = _playing ? Math.Max(0, monotonicSeconds - _baseMonotonic) * _rate : 0;
        return Clamp(_basePosition + elapsed, _duration);
    }

    public void Reset()
    {
        _has = false;
        _playing = false;
        _lastStampValid = false;
        _timestampUnreliable = false;
        RawPosition = 0;
        SourceAgeSeconds = -1;
        TimestampTrusted = false;
        Mode = "waiting";
        ++_revision;
    }

    public void Observe(double rawPosition, double duration, double rate, bool playing,
        DateTimeOffset? updatedAt, DateTimeOffset observedAt, double monotonicSeconds, bool newTrack)
    {
        rawPosition = double.IsFinite(rawPosition) ? Math.Max(0, rawPosition) : 0;
        duration = double.IsFinite(duration) && duration > 0 ? duration : 0;
        rate = double.IsFinite(rate) && rate > 0 ? Math.Clamp(rate, 0.05, 8) : 1;
        rawPosition = Clamp(rawPosition, duration);

        double age = updatedAt.HasValue ? (observedAt - updatedAt.Value).TotalSeconds : double.NaN;
        // Some programs report DateTime.MinValue or a future time. Don't turn a bad
        // timestamp into an enormous seek; instead keep a local monotonic clock.
        double maxAge = duration > 0 ? Math.Max(120, duration * 2 + 30) : 6 * 3600;
        bool trusted = updatedAt.HasValue && updatedAt.Value.Year >= 2001 && double.IsFinite(age)
                       && age >= -2 && age <= maxAge;
        bool sameRaw = _has && Math.Abs(rawPosition - _lastRaw) < 0.01;
        bool stampChanged = _has && trusted && _lastStampValid &&
                            Math.Abs((updatedAt!.Value - _lastStamp).TotalSeconds) > 0.05;
        bool olderStamp = _has && trusted && _lastStampValid &&
                          (updatedAt!.Value - _lastStamp).TotalSeconds < -0.05;
        bool sameStamp = _has && trusted && _lastStampValid && !stampChanged;
        if (newTrack || (stampChanged && !olderStamp)) _timestampUnreliable = false;
        // A position changing while its supposedly authoritative timestamp stays
        // identical violates the Windows contract. In that case treat the raw
        // position as current until the source supplies a new timestamp.
        if (!newTrack && sameStamp && !sameRaw) _timestampUnreliable = true;
        bool project = trusted && !_timestampUnreliable;
        double candidate = rawPosition + (playing && project ? Math.Max(0, age) * rate : 0);
        candidate = Clamp(candidate, duration);
        RawPosition = rawPosition;
        SourceAgeSeconds = double.IsFinite(age) ? age : -1;
        TimestampTrusted = project;
        double value;
        bool ignoredOlder = false;

        if (!_has || newTrack)
        {
            value = candidate;
            Mode = trusted ? "timestamp-projected" : "local-clock";
            ++_revision;
        }
        else
        {
            double expected = At(monotonicSeconds);
            if (olderStamp && playing == _playing)
            {
                // An out-of-order snapshot must not rewind the overlay.
                value = expected;
                ignoredOlder = true;
                Mode = "ignored-older-snapshot";
            }
            else if (playing != _playing)
            {
                // If a player did not send a fresh timeline for pause/resume, freeze
                // or resume the last predicted position instead of replaying old time.
                bool stale = sameRaw;
                value = stale ? expected : (playing ? candidate : rawPosition);
                Mode = stale ? "state-change-held" : "state-change-synced";
                ++_revision;
            }
            else if (sameRaw)
            {
                // Some browsers refresh LastUpdatedTime but leave Position frozen.
                // An unchanged raw value alone is not evidence of a seek.
                value = expected;
                Mode = stampChanged ? "extrapolating-stale-position" : "extrapolating-unchanged-snapshot";
            }
            else if (playing)
            {
                double difference = candidate - expected;
                if (difference >= -1.25 && difference <= 0.35)
                {
                    // Small negative corrections cause the familiar sawtooth timer.
                    // Keep moving smoothly; significant seeks still snap below.
                    value = Math.Max(expected, candidate);
                    Mode = "smoothed";
                }
                else
                {
                    value = candidate;
                    Mode = "seek-or-correction";
                    ++_revision;
                }
            }
            else
            {
                value = rawPosition;
                if (Math.Abs(value - expected) > 0.15) ++_revision;
                Mode = "paused-snapshot";
            }
        }

        _basePosition = Clamp(value, duration);
        _baseMonotonic = monotonicSeconds;
        _duration = duration;
        _rate = rate;
        _playing = playing;
        _has = true;
        if (!ignoredOlder)
        {
            _lastRaw = rawPosition;
            _lastStamp = trusted ? updatedAt!.Value : default;
            _lastStampValid = trusted;
        }
    }

    /// <summary>Maintain a prior clock through a temporary missing timeline.</summary>
    public void ObserveUnavailable(double duration, double rate, bool playing, double monotonicSeconds, bool newTrack)
    {
        duration = double.IsFinite(duration) && duration > 0 ? duration : 0;
        rate = double.IsFinite(rate) && rate > 0 ? Math.Clamp(rate, 0.05, 8) : 1;
        if (!_has || newTrack)
        {
            _basePosition = 0;
            RawPosition = 0;
            ++_revision;
        }
        else
        {
            _basePosition = At(monotonicSeconds);
            if (playing != _playing) ++_revision;
        }
        _baseMonotonic = monotonicSeconds;
        _duration = duration;
        _rate = rate;
        _playing = playing;
        _has = true;
        Mode = "timeline-unavailable";
        TimestampTrusted = false;
        SourceAgeSeconds = -1;
        _lastStampValid = false;
    }

    private static double Clamp(double value, double duration)
    {
        if (!double.IsFinite(value) || value < 0) value = 0;
        return duration > 0 ? Math.Min(value, duration) : value;
    }
}
