namespace BlazorDevTools.Events;

/// <summary>
/// Entry point for libraries and applications that want to add their own activity to the DevTools timeline.
/// Resolve it from DI (scoped). Recording is cheap and bounded: when the buffer is full the oldest entries are dropped.
/// </summary>
public interface IDevToolsTimeline
{
    /// <summary>Records an event and returns its id. Returns -1 when DevTools is disabled.</summary>
    long Record(DevToolsEvent devToolsEvent);

    /// <summary>Marks a previously recorded event as completed, setting its duration and optional detail/severity.</summary>
    void Complete(long eventId, double durationMs, string? detail = null, DevToolsSeverity? severity = null);

    /// <summary>Records a custom event that started now and finishes when the returned scope is disposed.</summary>
    IDisposable BeginActivity(string category, string title, string? detail = null, DevToolsEventKind kind = DevToolsEventKind.Custom);

    /// <summary>True when DevTools is enabled for this session; use it to skip building expensive event payloads.</summary>
    bool IsEnabled { get; }
}
