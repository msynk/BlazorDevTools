using System.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Inspection;
using BlazorDevTools.Internal;
using BlazorDevTools.Model;
using BlazorDevTools.State;

namespace BlazorDevTools.Session;

public sealed class StateProviderEntry
{
    internal StateProviderEntry(IStateProvider provider, string origin, int historySize)
    {
        Provider = provider;
        Origin = origin;
        History = new RingBuffer<StateChangeRecord>(historySize);
    }

    public IStateProvider Provider { get; }

    public string Name => Provider.Name;

    public string? Description => Provider.Description;

    /// <summary>Where the provider came from: "DI", "extension", "runtime".</summary>
    public string Origin { get; }

    public int ChangeCount { get; internal set; }

    public DateTimeOffset? LastChangedAt { get; internal set; }

    internal RingBuffer<StateChangeRecord> History { get; }

    internal Dictionary<string, string>? LastSnapshot { get; set; }

    public StateChangeRecord[] Changes => History.ToArray();

    public string? Error { get; internal set; }
}

/// <summary>Tracks registered state providers, records changes and computes before/after diffs.</summary>
internal sealed class StateStore : IStateProviderRegistry, IDisposable
{
    private readonly DevToolsSession _session;
    private readonly DevToolsOptions _options;
    private readonly object _lock = new();
    private readonly List<StateProviderEntry> _entries = [];
    private readonly Dictionary<IStateProvider, Action<StateChangeInfo>> _handlers = new(ReferenceEqualityComparer.Instance);
    private long _nextId;

    public StateStore(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _options = options;
    }

    public IReadOnlyList<IStateProvider> Providers
    {
        get { lock (_lock) { return _entries.Select(e => e.Provider).ToArray(); } }
    }

    public StateProviderEntry[] Entries
    {
        get { lock (_lock) { return _entries.ToArray(); } }
    }

    public IDisposable Register(IStateProvider provider) => Attach(provider, "runtime");

    public IDisposable Attach(IStateProvider provider, string origin)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            if (_handlers.ContainsKey(provider))
            {
                // Already registered (a provider reachable both from DI and from an extension factory). Hand back a
                // no-op token so disposing this registration does not tear down the original one.
                return NullDisposable.Instance;
            }
        }

        var entry = new StateProviderEntry(provider, origin, _options.MaxStateChangesPerProvider);
        lock (_lock)
        {
            if (_handlers.ContainsKey(provider))
            {
                return NullDisposable.Instance;
            }

            _entries.Add(entry);
            Action<StateChangeInfo> handler = info => OnChanged(entry, info);
            _handlers[provider] = handler;
            provider.Changed += handler;
        }

        try
        {
            entry.LastSnapshot = _session.Inspector.Flatten(provider.GetSnapshot());
        }
        catch (Exception ex)
        {
            entry.Error = ex.GetType().Name + ": " + ex.Message;
        }

        _session.Touch();
        return new Unsubscriber(this, provider);
    }

    private void Detach(IStateProvider provider)
    {
        lock (_lock)
        {
            if (_handlers.Remove(provider, out var handler))
            {
                provider.Changed -= handler;
            }

            _entries.RemoveAll(e => ReferenceEquals(e.Provider, provider));
        }

        _session.Touch();
    }

    private void OnChanged(StateProviderEntry entry, StateChangeInfo info)
    {
        if (!_session.IsEnabled)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        Dictionary<string, string> snapshot;
        try
        {
            snapshot = _session.Inspector.Flatten(entry.Provider.GetSnapshot());
            entry.Error = null;
        }
        catch (Exception ex)
        {
            entry.Error = ex.GetType().Name + ": " + ex.Message;
            return;
        }

        var changes = ObjectInspector.Diff(entry.LastSnapshot, snapshot);
        entry.LastSnapshot = snapshot;
        entry.ChangeCount++;
        entry.LastChangedAt = DateTimeOffset.UtcNow;

        var record = new StateChangeRecord
        {
            Id = Interlocked.Increment(ref _nextId),
            At = entry.LastChangedAt.Value,
            Provider = entry.Name,
            Action = info?.Action,
            Detail = info?.Detail,
            Changes = changes,
            Snapshot = snapshot,
            TriggerEventId = _session.CurrentTriggerEventId,
        };
        entry.History.Add(record);

        var title = string.IsNullOrEmpty(info?.Action) ? entry.Name + " changed" : entry.Name + ": " + info!.Action;
        record.TimelineEventId = _session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.StateChange,
            Category = "state:" + entry.Name,
            Title = title,
            Detail = changes.Count == 0 ? "no observable member changes" : string.Join(", ", changes.Take(6).Select(c => c.Path + (c.Kind == "changed" ? ": " + c.Before + " → " + c.After : " (" + c.Kind + ")"))) + (changes.Count > 6 ? $" … +{changes.Count - 6}" : ""),
            ParentEventId = record.TriggerEventId,
            Data = new Dictionary<string, object?> { ["provider"] = entry.Name, ["changeId"] = record.Id, ["changes"] = changes.Count },
        });
        _session.Overhead.AddInspection(Stopwatch.GetElapsedTime(start).Ticks);
        _session.Touch();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (provider, handler) in _handlers)
            {
                try
                {
                    provider.Changed -= handler;
                }
                catch
                {
                    // provider may already be disposed
                }
            }

            _handlers.Clear();
            _entries.Clear();
        }
    }

    private sealed class Unsubscriber(StateStore store, IStateProvider provider) : IDisposable
    {
        public void Dispose() => store.Detach(provider);
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
