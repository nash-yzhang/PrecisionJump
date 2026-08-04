using System.Diagnostics;

namespace MouseAccelerator.Services;

internal enum MatchProgress
{
    None,
    Prefix,
    Complete
}

internal sealed class SequenceMatcher
{
    private int _position;
    private long _lastKeyAt;

    public MatchProgress Consume(
        int virtualKey,
        IReadOnlyList<int> expected,
        TimeSpan timeout,
        long? nowTicks = null)
    {
        if (expected.Count == 0)
        {
            return MatchProgress.None;
        }

        if (_position >= expected.Count)
        {
            Reset();
        }

        var now = nowTicks ?? Stopwatch.GetTimestamp();
        var elapsed = (now - _lastKeyAt) / (double)Stopwatch.Frequency;
        if (_position > 0 && elapsed > timeout.TotalSeconds)
        {
            _position = 0;
        }

        if (virtualKey == expected[_position])
        {
            _position++;
            _lastKeyAt = now;
        }
        else if (virtualKey == expected[0])
        {
            _position = 1;
            _lastKeyAt = now;
        }
        else
        {
            Reset();
            return MatchProgress.None;
        }

        if (_position != expected.Count)
        {
            return MatchProgress.Prefix;
        }

        Reset();
        return MatchProgress.Complete;
    }

    public void Reset()
    {
        _position = 0;
        _lastKeyAt = 0;
    }
}
