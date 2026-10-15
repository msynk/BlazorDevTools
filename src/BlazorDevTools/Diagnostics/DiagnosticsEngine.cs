using System.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Session;

namespace BlazorDevTools.Diagnostics;

public sealed class DiagnosticRecord
{
    public required Diagnostic Diagnostic { get; set; }

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; set; }

    public bool Dismissed { get; set; }

    /// <summary>The timeline row of the finding's latest report (first sighting or escalation); 0 when capture was paused.</summary>
    public long TimelineEventId { get; set; }

    /// <summary>The highest severity this finding has been reported with. Only a rise above it is reported again.</summary>
    internal DiagnosticSeverity HighestSeverity { get; set; }
}

internal sealed class DiagnosticContext(DevToolsSession session, IReadOnlyList<ComponentSnapshot> components, DevToolsEvent[] events) : IDiagnosticContext
{
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<ComponentSnapshot> Components => components;

    public IReadOnlyList<DevToolsEvent> Events { get; } = events;

    public string? Platform => session.Platform;

    public IEnumerable<DevToolsEvent> EventsInWindow(DevToolsEventKind kind, TimeSpan window)
    {
        var threshold = Stopwatch.GetTimestamp() - (long)(window.TotalSeconds * Stopwatch.Frequency);
        for (var i = events.Length - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.StartTicks < threshold)
            {
                yield break;
            }

            if (evt.Kind == kind)
            {
                yield return evt;
            }
        }
    }

    private HashSet<long>? _frameworkInternals;

    public bool IsFrameworkInternal(long instanceId)
    {
        _frameworkInternals ??= components.Where(c => c.IsFrameworkInternal).Select(c => c.InstanceId).ToHashSet();
        return _frameworkInternals.Contains(instanceId);
    }

    public T? GetOptions<T>() where T : class => typeof(T) == typeof(DiagnosticsOptions) ? session.Options.Diagnostics as T
        : typeof(T) == typeof(DevToolsOptions) ? session.Options as T
        : null;
}

/// <summary>Runs rules on the UI cadence (at most once per second), deduplicates findings and records new ones in the timeline.</summary>
internal sealed class DiagnosticsEngine
{
    private readonly DevToolsSession _session;
    private readonly DevToolsOptions _options;
    private readonly List<IDiagnosticRule> _rules = [];
    private readonly Dictionary<string, DiagnosticRecord> _findings = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private long _lastRunTicks;
    private long _lastRunVersion = -1;
    private bool _serviceGraphAnalyzed;

    public DiagnosticsEngine(DevToolsSession session, DevToolsOptions options, DevToolsRegistry registry)
    {
        _session = session;
        _options = options;
        _rules.AddRange(BuiltInRules.Create());
        _rules.AddRange(registry.Rules);
    }

    public IReadOnlyList<IDiagnosticRule> Rules => _rules;

    public void AddRule(IDiagnosticRule rule)
    {
        lock (_lock)
        {
            if (_rules.All(r => r.Id != rule.Id))
            {
                _rules.Add(rule);
            }
        }
    }

    public DiagnosticRecord[] Findings
    {
        get { lock (_lock) { return _findings.Values.OrderByDescending(f => f.Diagnostic.Severity).ThenByDescending(f => f.LastSeen).ToArray(); } }
    }

    public int ActiveCount
    {
        get { lock (_lock) { return _findings.Values.Count(f => !f.Dismissed); } }
    }

    public int ActiveWarningsOrErrors
    {
        get { lock (_lock) { return _findings.Values.Count(f => !f.Dismissed && f.Diagnostic.Severity >= DiagnosticSeverity.Warning); } }
    }

    public void Evaluate(bool force = false)
    {
        if (!_session.IsEnabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && (Stopwatch.GetElapsedTime(_lastRunTicks).TotalMilliseconds < 1000 || _lastRunVersion == _session.Version))
        {
            return;
        }

        _lastRunTicks = now;
        _lastRunVersion = _session.Version;

        var components = _session.Components.All.Where(c => !c.IsDevTools).Select(c => c.ToSnapshot()).ToList();
        var events = _session.Timeline.Snapshot();
        var context = new DiagnosticContext(_session, components, events);
        var produced = new List<Diagnostic>();

        IDiagnosticRule[] rules;
        lock (_lock)
        {
            rules = _rules.ToArray();
        }

        foreach (var rule in rules)
        {
            if (_options.Diagnostics.DisabledRules.Contains(rule.Id))
            {
                continue;
            }

            try
            {
                produced.AddRange(rule.Evaluate(context));
            }
            catch (Exception ex)
            {
                produced.Add(new Diagnostic("devtools.rule-failed", DiagnosticSeverity.Info, $"Rule {rule.Id} threw", ex.GetType().Name + ": " + ex.Message, Fingerprint: "rule-failed:" + rule.Id));
            }
        }

        if (!_serviceGraphAnalyzed && _session.Registry.ServiceGraph is { } graph)
        {
            _serviceGraphAnalyzed = true;
            produced.AddRange(graph.Analyze());
        }

        Merge(produced);
        _session.Overhead.AddDiagnostics(Stopwatch.GetElapsedTime(now).Ticks);
    }

    private void Merge(List<Diagnostic> produced)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        lock (_lock)
        {
            foreach (var diagnostic in produced)
            {
                var key = diagnostic.EffectiveFingerprint;
                if (_findings.TryGetValue(key, out var existing))
                {
                    existing.Diagnostic = diagnostic;
                    existing.LastSeen = now;
                    if (diagnostic.Severity > existing.HighestSeverity)
                    {
                        // A stronger verdict on a known finding is news (the parent-driven rule becomes a warning once
                        // parameter comparison proves the renders wasted): it gets its own timeline row, and dismissing
                        // the weaker version does not hide it. Only a new high is reported, so a finding that moves
                        // back and forth between two severities does not flood the timeline.
                        var previous = existing.HighestSeverity;
                        existing.HighestSeverity = diagnostic.Severity;
                        existing.Dismissed = false;
                        RecordTimeline(existing, key, escalatedFrom: previous);
                        changed = true;
                    }

                    continue;
                }

                var record = new DiagnosticRecord { Diagnostic = diagnostic, FirstSeen = now, LastSeen = now, HighestSeverity = diagnostic.Severity };
                _findings[key] = record;
                changed = true;
                RecordTimeline(record, key, escalatedFrom: null);
            }
        }

        if (changed)
        {
            _session.Touch();
        }
    }

    private void RecordTimeline(DiagnosticRecord record, string fingerprint, DiagnosticSeverity? escalatedFrom)
    {
        var diagnostic = record.Diagnostic;
        var data = new Dictionary<string, object?> { ["fingerprint"] = fingerprint };
        if (escalatedFrom is { } previous)
        {
            data["escalatedFrom"] = previous.ToString();
        }

        var id = _session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.Diagnostic,
            Category = "diagnostic:" + diagnostic.RuleId,
            Title = escalatedFrom is { } from ? $"{diagnostic.Title} (escalated from {from})" : diagnostic.Title,
            Detail = diagnostic.Evidence,
            Severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => DevToolsSeverity.Error,
                DiagnosticSeverity.Warning => DevToolsSeverity.Warning,
                _ => DevToolsSeverity.Info,
            },
            ComponentInstanceId = diagnostic.ComponentInstanceId,
            Data = data,
        });

        // Paused: no row was written, so there is nothing to link to.
        if (id > 0)
        {
            record.TimelineEventId = id;
        }
    }

    public void Dismiss(string fingerprint)
    {
        lock (_lock)
        {
            if (_findings.TryGetValue(fingerprint, out var record))
            {
                record.Dismissed = true;
            }
        }

        _session.Touch();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _findings.Clear();
            _serviceGraphAnalyzed = false;
        }

        _session.Touch();
    }
}
