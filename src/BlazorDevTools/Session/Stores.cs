using System.Collections.Concurrent;
using BlazorDevTools.Internal;
using BlazorDevTools.Model;

namespace BlazorDevTools.Session;

internal sealed class HttpStore
{
    private readonly DevToolsSession _session;
    private readonly RingBuffer<HttpRecord> _requests;
    private long _nextId;
    private int _failed;

    public HttpStore(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _requests = new RingBuffer<HttpRecord>(options.MaxHttpRequests);
    }

    public int Count => _requests.Count;

    public int FailedCount => Volatile.Read(ref _failed);

    public long NextId() => Interlocked.Increment(ref _nextId);

    public void Add(HttpRecord record)
    {
        _requests.Add(record);
        _session.Touch();
    }

    public void MarkFailed() => Interlocked.Increment(ref _failed);

    public HttpRecord[] Snapshot() => _requests.ToArray();

    public HttpRecord? Find(long id) => _requests.FindLast(r => r.Id == id);

    public void Clear()
    {
        _requests.Clear();
        _session.Touch();
    }

    public void Touch() => _session.Touch();
}

public sealed class JsInteropAggregate
{
    public required string Identifier { get; init; }

    public int Count;

    public double TotalMs;

    public double MaxMs;

    public int Failures;

    public JsInteropDirection Direction { get; init; }
}

internal sealed class JsInteropStore
{
    private readonly DevToolsSession _session;
    private readonly RingBuffer<JsInteropRecord> _calls;
    private readonly ConcurrentDictionary<string, JsInteropAggregate> _aggregates = new(StringComparer.Ordinal);
    private long _nextId;

    public JsInteropStore(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _calls = new RingBuffer<JsInteropRecord>(options.MaxJsInteropCalls);
    }

    public int Count => _calls.Count;

    public long NextId() => Interlocked.Increment(ref _nextId);

    public void Add(JsInteropRecord record)
    {
        _calls.Add(record);
        _session.Touch();
    }

    public void Complete(JsInteropRecord record, double durationMs, bool succeeded, string? error)
    {
        record.DurationMs = durationMs;
        record.Succeeded = succeeded;
        record.Error = error;
        var key = (record.Direction == JsInteropDirection.JsToDotNet ? "←" : "→") + record.Identifier;
        var agg = _aggregates.GetOrAdd(key, _ => new JsInteropAggregate { Identifier = record.Identifier, Direction = record.Direction });
        lock (agg)
        {
            agg.Count++;
            agg.TotalMs += durationMs;
            if (durationMs > agg.MaxMs)
            {
                agg.MaxMs = durationMs;
            }

            if (!succeeded)
            {
                agg.Failures++;
            }
        }

        _session.Touch();
    }

    public JsInteropRecord[] Snapshot() => _calls.ToArray();

    public JsInteropAggregate[] Aggregates() => _aggregates.Values.ToArray();

    public void Clear()
    {
        _calls.Clear();
        _aggregates.Clear();
        _session.Touch();
    }
}
