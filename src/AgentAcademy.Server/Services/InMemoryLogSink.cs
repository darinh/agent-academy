using System.Collections.Concurrent;
using System.Globalization;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Thread-safe ring buffer that stores recent log entries in memory.
/// Registered as a singleton; the matching <see cref="InMemoryLogProvider"/>
/// writes to it and <c>TailLogsHandler</c> reads from it.
/// </summary>
public sealed class InMemoryLogStore
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;

    public InMemoryLogStore(int capacity = 500)
    {
        _capacity = capacity;
    }

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);

        // Soft cap — may briefly exceed capacity under concurrency, which is fine
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns the most recent <paramref name="count"/> entries,
    /// optionally filtered by a case-insensitive substring match on the message.
    /// </summary>
    public IReadOnlyList<LogEntry> Tail(int count = 100, string? filter = null)
    {
        var snapshot = _entries.ToArray();
        IEnumerable<LogEntry> filtered = snapshot;

        if (!string.IsNullOrWhiteSpace(filter))
            filtered = filtered.Where(e =>
                e.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.Category?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Exception?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));

        return filtered.TakeLast(count).ToList();
    }
}

public sealed record LogEntry(
    string Timestamp,
    string Level,
    string? Category,
    string Message,
    string? Exception = null);

/// <summary>
/// Logger provider that writes to the shared <see cref="InMemoryLogStore"/>.
/// </summary>
public sealed class InMemoryLogProvider : ILoggerProvider
{
    private readonly InMemoryLogStore _store;

    public InMemoryLogProvider(InMemoryLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(_store, categoryName);

    public void Dispose() { }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly InMemoryLogStore _store;
        private readonly string _category;

        public InMemoryLogger(InMemoryLogStore store, string category)
        {
            _store = store;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            _store.Add(new LogEntry(
                Timestamp: DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Level: logLevel.ToString(),
                Category: _category,
                Message: formatter(state, exception),
                Exception: exception?.ToString()));
        }
    }
}
