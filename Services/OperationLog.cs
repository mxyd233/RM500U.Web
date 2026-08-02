using System.Collections.Concurrent;
using RM500U.Web.Models;

namespace RM500U.Web.Services;

public sealed class OperationLog
{
    private const int MaximumEntries = 400;
    private readonly ConcurrentQueue<OperationLogEntry> _entries = new();

    public void Add(string level, string source, string message, string? detail = null)
    {
        _entries.Enqueue(new OperationLogEntry(DateTimeOffset.UtcNow, level, source, message, detail));
        while (_entries.Count > MaximumEntries)
            _entries.TryDequeue(out _);
    }

    public IReadOnlyList<OperationLogEntry> Snapshot() => _entries.Reverse().ToArray();

    public void Clear() => _entries.Clear();
}
