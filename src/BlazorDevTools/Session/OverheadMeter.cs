using System.Diagnostics;

namespace BlazorDevTools.Session;

/// <summary>Measures the cost of DevTools itself so it can be reported honestly next to application numbers.</summary>
public sealed class OverheadMeter
{
    private long _instrumentationTicks;
    private long _devToolsRenderTicks;
    private long _inspectionTicks;
    private long _diagnosticsTicks;
    private long _treeRefreshTicks;
    private long _uiRenderCount;
    private long _appRenderCount;
    private readonly long _startTicks = Stopwatch.GetTimestamp();

    /// <summary>Time spent inside render wrappers, excluding the wrapped fragment (per-render bookkeeping).</summary>
    public double InstrumentationMs => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _instrumentationTicks)).TotalMilliseconds;

    /// <summary>Time spent rendering DevTools' own components.</summary>
    public double DevToolsRenderMs => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _devToolsRenderTicks)).TotalMilliseconds;

    /// <summary>Time spent flattening/inspecting objects for state diffs.</summary>
    public double InspectionMs => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _inspectionTicks)).TotalMilliseconds;

    public double DiagnosticsMs => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _diagnosticsTicks)).TotalMilliseconds;

    public double TreeRefreshMs => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _treeRefreshTicks)).TotalMilliseconds;

    public long DevToolsRenderCount => Volatile.Read(ref _uiRenderCount);

    public long AppRenderCount => Volatile.Read(ref _appRenderCount);

    public double UptimeSeconds => Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;

    public double TotalMs => InstrumentationMs + DevToolsRenderMs + InspectionMs + DiagnosticsMs + TreeRefreshMs;

    /// <summary>Average per-render bookkeeping cost in microseconds.</summary>
    public double InstrumentationPerRenderMicroseconds => AppRenderCount == 0 ? 0 : InstrumentationMs * 1000 / AppRenderCount;

    internal void AddInstrumentation(long ticks)
    {
        Interlocked.Add(ref _instrumentationTicks, ticks);
        Interlocked.Increment(ref _appRenderCount);
    }

    internal void AddDevToolsRender(long ticks)
    {
        Interlocked.Add(ref _devToolsRenderTicks, ticks);
        Interlocked.Increment(ref _uiRenderCount);
    }

    internal void AddInspection(long ticks) => Interlocked.Add(ref _inspectionTicks, ticks);

    internal void AddDiagnostics(long ticks) => Interlocked.Add(ref _diagnosticsTicks, ticks);

    internal void AddTreeRefresh(long ticks) => Interlocked.Add(ref _treeRefreshTicks, ticks);
}
