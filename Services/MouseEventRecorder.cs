using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace MouseAccelerator.Services;

public sealed class MouseEventRecorder : IDisposable
{
    private const int FlushIntervalMilliseconds = 250;
    private const int MaximumQueuedRows = 50_000;

    private readonly BlockingCollection<string> _rows =
        new(new ConcurrentQueue<string>(), MaximumQueuedRows);
    private readonly Thread _writerThread;
    private readonly string _baseCsvPath;
    private readonly long _sessionStartedAt;
    private string? _createdCsvPath;
    private bool _disposed;

    public MouseEventRecorder()
    {
        SessionTimestampUtc = DateTimeOffset.UtcNow;
        _sessionStartedAt = Stopwatch.GetTimestamp();
        _baseCsvPath = BuildSessionPath(SessionTimestampUtc);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "PrecisionJump.CsvWriter"
        };
        _writerThread.Start();
    }

    public DateTimeOffset SessionTimestampUtc { get; }

    public string CsvPath =>
        Volatile.Read(ref _createdCsvPath) ?? _baseCsvPath;

    public static string CsvDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                "MOUSE_ACCELERATOR_EVENTS_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var fullPath = Path.GetFullPath(overridePath);
                return Path.HasExtension(fullPath)
                    ? Path.GetDirectoryName(fullPath)!
                    : fullPath;
            }

            return Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "MouseAccelerator");
        }
    }

    public void Record(
        Point position,
        string eventName,
        bool jumping,
        int rawDeltaX = 0,
        int rawDeltaY = 0)
    {
        if (_disposed)
        {
            return;
        }

        var elapsedMicroseconds = (long)Math.Round(
            (Stopwatch.GetTimestamp() - _sessionStartedAt)
            * 1_000_000d
            / Stopwatch.Frequency);
        _rows.TryAdd(
            $"{elapsedMicroseconds},{position.X},{position.Y},{eventName},"
            + $"{(jumping ? "true" : "false")},{rawDeltaX},{rawDeltaY}");
    }

    private void WriterLoop()
    {
        var batch = new List<string>(256);
        var lastFlushAt = Stopwatch.GetTimestamp();
        try
        {
            while (!_rows.IsCompleted)
            {
                if (_rows.TryTake(
                    out var row,
                    millisecondsTimeout: 25))
                {
                    batch.Add(row);
                }

                var elapsedSinceFlush =
                    (Stopwatch.GetTimestamp() - lastFlushAt)
                    * 1_000d
                    / Stopwatch.Frequency;
                if (
                    batch.Count >= 256
                    || batch.Count > 0
                    && elapsedSinceFlush >= FlushIntervalMilliseconds
                )
                {
                    WriteBatch(batch);
                    batch.Clear();
                    lastFlushAt = Stopwatch.GetTimestamp();
                }
            }

            while (_rows.TryTake(out var remaining))
            {
                batch.Add(remaining);
                if (batch.Count >= 256)
                {
                    WriteBatch(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                WriteBatch(batch);
            }
        }
        catch
        {
            // Recording is optional and must never affect pointer input.
        }
    }

    private void WriteBatch(List<string> batch)
    {
        using var writer = OpenWriter();
        foreach (var row in batch)
        {
            writer.WriteLine(row);
        }
    }

    private StreamWriter OpenWriter()
    {
        var existingPath = Volatile.Read(ref _createdCsvPath);
        if (existingPath is not null)
        {
            return new StreamWriter(
                new FileStream(
                    existingPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16_384,
                    useAsync: false),
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
        }

        var directory = Path.GetDirectoryName(_baseCsvPath)!;
        Directory.CreateDirectory(directory);

        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var path = suffix == 0
                ? _baseCsvPath
                : Path.Combine(
                    directory,
                    $"{Path.GetFileNameWithoutExtension(_baseCsvPath)}-{suffix}"
                    + Path.GetExtension(_baseCsvPath));
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16_384,
                    useAsync: false);
                Volatile.Write(ref _createdCsvPath, path);
                var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(
                    "t_us,x,y,evt,jumping,raw_dx,raw_dy");
                writer.Flush();
                return writer;
            }
            catch (IOException) when (File.Exists(path))
            {
                // A same-timestamp session already owns this name.
            }
        }

        throw new IOException(
            "Could not allocate a unique mouse event session file.");
    }

    private static string BuildSessionPath(
        DateTimeOffset sessionTimestampUtc)
    {
        var stamp = sessionTimestampUtc.ToString(
            "yyyyMMdd'T'HHmmss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var overridePath = Environment.GetEnvironmentVariable(
            "MOUSE_ACCELERATOR_EVENTS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullPath = Path.GetFullPath(overridePath);
            if (Path.HasExtension(fullPath))
            {
                return Path.Combine(
                    Path.GetDirectoryName(fullPath)!,
                    $"{Path.GetFileNameWithoutExtension(fullPath)}-{stamp}"
                    + Path.GetExtension(fullPath));
            }
        }

        return Path.Combine(
            CsvDirectory,
            $"mouse-events-{stamp}.csv");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rows.CompleteAdding();
        _writerThread.Join(millisecondsTimeout: 2_000);
        _rows.Dispose();
    }
}
