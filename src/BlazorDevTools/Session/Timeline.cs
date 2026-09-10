using System.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Internal;

namespace BlazorDevTools.Session;

/// <summary>Bounded, thread-safe unified activity timeline for one session.</summary>
internal sealed class Timeline : IDevToolsTimeline
{
    private readonly DevToolsSession _session;
    private readonly RingBuffer<DevToolsEvent> _events;
    private readonly object _idLock = new();
    private long _nextId;

    public Timeline(DevToolsSession session, int capacity)
    {
        _session = session;
        _events = new RingBuffer<DevToolsEvent>(capacity);
    }

    public bool IsEnabled => _session.IsEnabled;

    public int Count => _events.Count;

    public long Version => _events.Version;

    public long Record(DevToolsEvent devToolsEvent)
    {
        if (!_session.IsEnabled)
        {
            return -1;
        }

        long id;
        lock (_idLock)
        {
            // id assignment and insertion happen together so the buffer stays ordered by id under concurrent writers.
            id = ++_nextId;
            devToolsEvent.Id = id;
            _events.Add(devToolsEvent);
        }

        _session.Touch();
        return id;
    }

    public void Complete(long eventId, double durationMs, string? detail = null, DevToolsSeverity? severity = null)
    {
        if (eventId <= 0)
        {
            return;
        }

        var evt = Find(eventId);
        if (evt is null)
        {
            return;
        }

        evt.DurationMs = durationMs;
        if (detail is not null)
        {
            evt.Detail = detail;
        }

        if (severity is not null)
        {
            evt.Severity = severity.Value;
        }

        _session.Touch();
    }

    public IDisposable BeginActivity(string category, string title, string? detail = null, DevToolsEventKind kind = DevToolsEventKind.Custom)
    {
        var evt = new DevToolsEvent { Kind = kind, Category = category, Title = title, Detail = detail };
        var id = Record(evt);
        return new ActivityScope(this, id, evt.StartTicks);
    }

    public DevToolsEvent? Find(long id)
    {
        if (id <= 0)
        {
            return null;
        }

        return _events.FindByKey(static e => e.Id, id);
    }

    public DevToolsEvent[] Snapshot() => _events.ToArray();

    public void Clear()
    {
        _events.Clear();
        _session.Touch();
    }

    private sealed class ActivityScope(Timeline timeline, long id, long startTicks) : IDisposable
    {
        public void Dispose() => timeline.Complete(id, Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds);
    }
}
