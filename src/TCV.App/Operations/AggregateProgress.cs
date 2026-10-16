using System.Diagnostics;
using TCV.Core.Domain;

namespace TCV.App.Operations;

/// <summary>
/// Rolls per-file byte counts up into one progress figure for a whole operation.
/// Thread-safe, so a parallel copy can report through it from several files at once.
/// </summary>
internal sealed class AggregateProgress
{
    private readonly IProgress<FileProgress>? _outer;
    private readonly long _totalBytes;
    private readonly Stopwatch _sw;
    private long _bytesCopied;
    private readonly object _reportLock = new();
    private long _lastReportTicks;
    private long _lastReportedBytes;
    private static readonly long ReportIntervalTicks = Stopwatch.Frequency / 10; // 100 ms

    public AggregateProgress(IProgress<FileProgress>? outer, long totalBytes, Stopwatch sw)
    {
        _outer = outer;
        _totalBytes = totalBytes;
        _sw = sw;
    }

    public FileRelay CreateFileRelay(string fileName) =>
        new(this, fileName);

    /// <summary>Counts bytes that will never be transferred (a skipped file) as done, so the bar can still reach 100%.</summary>
    public void AddSkipped(long bytes, string fileName)
    {
        if (bytes > 0) Add(bytes, fileName);
    }

    private void Add(long delta, string fileName)
    {
        var total = Interlocked.Add(ref _bytesCopied, delta);
        if (_outer is null) return;
        if (total != _totalBytes && Stopwatch.GetTimestamp() - Volatile.Read(ref _lastReportTicks) < ReportIntervalTicks)
        {
            return;
        }

        // One report at a time, each with the latest sum. Files finishing on several threads used to
        // post their own figures out of order (the bar stepped back), and the one carrying 100% could
        // lose the race to a smaller one and never be sent.
        lock (_reportLock)
        {
            var now = Stopwatch.GetTimestamp();
            var current = Interlocked.Read(ref _bytesCopied);
            if (current <= _lastReportedBytes
                || (current != _totalBytes && now - _lastReportTicks < ReportIntervalTicks))
            {
                return;
            }

            Volatile.Write(ref _lastReportTicks, now);
            _lastReportedBytes = current;
            _outer.Report(new FileProgress(current, _totalBytes, fileName, _sw.Elapsed));
        }
    }

    /// <summary>One file's progress, turned into deltas of the whole.</summary>
    internal sealed class FileRelay : IProgress<FileProgress>
    {
        private readonly AggregateProgress _parent;
        private readonly string _fileName;
        private long _prevBytes;

        public FileRelay(AggregateProgress parent, string fileName)
        {
            _parent = parent;
            _fileName = fileName;
        }

        public void Report(FileProgress value)
        {
            var delta = value.BytesCopied - _prevBytes;
            if (delta > 0)
            {
                _prevBytes = value.BytesCopied;
                _parent.Add(delta, _fileName);
            }
        }

        /// <summary>
        /// The file was given up (skipped after an error): counts what was not reported yet as done.
        /// Adding its whole size would count the part that did arrive twice and push the bar past 100%.
        /// </summary>
        public void SkipRemaining(long fileSize)
        {
            var rest = fileSize - _prevBytes;
            _prevBytes = Math.Max(_prevBytes, fileSize);
            if (rest > 0) _parent.Add(rest, _fileName);
        }
    }
}
