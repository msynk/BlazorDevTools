using System.Diagnostics;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;

namespace BlazorDevTools.Model;

/// <summary>Why a component rendered. Determined from Activity context and render-batch analysis; see <see cref="RenderSample.CauseDetail"/>.</summary>
public enum RenderCause
{
    Unknown,
    Initial,
    ParentRender,
    UiEvent,
    Navigation,
    StateHasChanged,
}

/// <summary>One execution of a component's render fragment (BuildRenderTree). Diffing time is tracked per batch, not per component.</summary>
public sealed class RenderSample
{
    public required long Sequence { get; init; }

    public required DateTimeOffset At { get; init; }

    public required long StartTicks { get; init; }

    public double DurationMs { get; set; }

    public long BatchId { get; init; }

    public RenderCause Cause { get; init; }

    public string? CauseDetail { get; init; }

    /// <summary>Timeline event id of this render.</summary>
    public long EventId { get; set; }

    /// <summary>Timeline event that triggered the render (UI event, navigation, parent render) when known.</summary>
    public long? TriggerEventId { get; init; }

    /// <summary>Component fields that changed since the previous render, when state capture is enabled for the component.</summary>
    public IReadOnlyList<string>? ChangedState { get; set; }

    public bool Failed { get; set; }
}

/// <summary>Live bookkeeping for one component instance. Mutated only on the renderer's dispatcher; read from anywhere.</summary>
public sealed class ComponentRecord
{
    private readonly RingBuffer<RenderSample> _renders;
    private readonly WeakReference<IComponent> _component;
    private int _renderCount;
    private int _errorCount;

    internal ComponentRecord(long instanceId, IComponent component, Type type, int maxRenders, bool isDevTools, bool isHidden)
    {
        InstanceId = instanceId;
        _component = new WeakReference<IComponent>(component);
        Type = type;
        TypeName = type.FullName ?? type.Name;
        DisplayName = TypeNames.Short(type);
        FirstRenderTitle = DisplayName + " rendered (first)";
        ReRenderTitle = DisplayName + " re-rendered";
        IsDevTools = isDevTools;
        IsHidden = isHidden;
        CreatedAt = DateTimeOffset.UtcNow;
        CreatedTicks = Stopwatch.GetTimestamp();
        _renders = new RingBuffer<RenderSample>(maxRenders);
    }

    public long InstanceId { get; }

    /// <summary>
    /// The tracked instance. Held weakly: DevTools must never keep a component (and through it a whole subtree,
    /// its services and its renderer) alive after the application dropped it. Returns null once collected.
    /// </summary>
    public IComponent? Component => _component.TryGetTarget(out var component) ? component : null;

    /// <summary>True while the instance is still reachable; false once the GC reclaimed it.</summary>
    public bool IsAlive => _component.TryGetTarget(out _);

    public Type Type { get; }

    public string TypeName { get; }

    public string DisplayName { get; }

    /// <summary>Pre-built timeline titles. Renders are the hottest event in the system; formatting one string per
    /// render is measurable in both time and GC pressure.</summary>
    internal string FirstRenderTitle { get; }

    internal string ReRenderTitle { get; }

    /// <summary>
    /// True for components that belong to DevTools itself; their cost is counted as overhead, not app activity.
    /// <para>
    /// Set from the type at activation and again from the parent once the hierarchy resolves: the panels render
    /// framework components (<c>Virtualize</c>, <c>CascadingValue</c>, <c>DynamicComponent</c>) that an application
    /// renders too, so the type alone cannot tell whose they are — only the position in the tree can.
    /// </para>
    /// </summary>
    public bool IsDevTools { get; private set; }

    /// <summary>Marks this record — and therefore its own descendants, which resolve their parent later — as DevTools' own.</summary>
    internal void MarkDevToolsOwned() => IsDevTools = true;

    /// <summary>Hidden from the tree (framework/library internals) but still tracked so parent chains stay intact.</summary>
    public bool IsHidden { get; }

    public DateTimeOffset CreatedAt { get; }

    public long CreatedTicks { get; }

    /// <summary>True when the render fragment was wrapped (ComponentBase-derived components only).</summary>
    public bool IsInstrumented { get; internal set; }

    public string? InstrumentationNote { get; internal set; }

    public int RenderCount => Volatile.Read(ref _renderCount);

    public double TotalRenderMs { get; private set; }

    public double LastRenderMs { get; private set; }

    public double MaxRenderMs { get; private set; }

    public DateTimeOffset? LastRenderAt { get; private set; }

    public long LastRenderTicks { get; private set; }

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public bool IsDisposed { get; internal set; }

    public DateTimeOffset? DisposedAt { get; internal set; }

    /// <summary>Renderer-assigned component id, resolved lazily.</summary>
    public int? ComponentId { get; internal set; }

    public ComponentRecord? Parent { get; internal set; }

    public long? ParentInstanceId => Parent?.InstanceId;

    internal bool ParentResolved { get; set; }

    private WeakReference<Renderer>? _renderer;

    /// <summary>The renderer that owns the component, held weakly for the same reason as <see cref="Component"/>.</summary>
    internal Renderer? Renderer
    {
        get => _renderer is not null && _renderer.TryGetTarget(out var renderer) ? renderer : null;
        set => _renderer = value is null ? null : new WeakReference<Renderer>(value);
    }

    internal bool RendererResolved { get; set; }

    internal bool JsRuntimePatched { get; set; }

    /// <summary>When true, fields are snapshotted before every render so the inspector can show what changed.</summary>
    public bool CaptureState { get; set; }

    internal Dictionary<string, string>? LastStateSnapshot { get; set; }

    public RenderSample? LastRender => _renders.Last;

    public RenderSample[] Renders => _renders.ToArray();

    /// <summary>Releases everything that could keep application objects alive once the component is gone.</summary>
    internal void ReleaseReferences()
    {
        _component.SetTarget(null!);
        _renderer = null;
        LastStateSnapshot = null;
    }

    internal void AddRender(RenderSample sample)
    {
        Interlocked.Increment(ref _renderCount);
        TotalRenderMs += sample.DurationMs;
        LastRenderMs = sample.DurationMs;
        if (sample.DurationMs > MaxRenderMs)
        {
            MaxRenderMs = sample.DurationMs;
        }

        LastRenderAt = sample.At;
        LastRenderTicks = sample.StartTicks;
        _renders.Add(sample);
    }

    internal void AddError() => Interlocked.Increment(ref _errorCount);

    public int Depth
    {
        get
        {
            var depth = 0;
            var current = Parent;
            while (current is not null && depth < 100_000)
            {
                depth++;
                current = current.Parent;
            }

            return depth;
        }
    }

    public ComponentSnapshot ToSnapshot() => new(
        InstanceId, TypeName, DisplayName, ParentInstanceId, Depth, RenderCount, TotalRenderMs, LastRenderMs, MaxRenderMs,
        CreatedAt, LastRenderAt, IsDisposed, ErrorCount, IsHidden);

    public override string ToString() => $"{DisplayName}#{InstanceId}";
}

internal static class TypeNames
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Cache = new();

    public static string Short(Type type) => Cache.GetOrAdd(type, static t => Format(t));

    private static string Format(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name[..tick];
            }

            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Format)) + ">";
        }

        if (t.IsArray)
        {
            return Format(t.GetElementType()!) + "[]";
        }

        return t.Name;
    }
}
