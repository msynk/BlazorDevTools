using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using BlazorDevTools.Events;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace BlazorDevTools.Instrumentation;

/// <summary>Bookkeeping for one render batch (one pass of the renderer's queue, ending in one diff sent to the browser).</summary>
public sealed class RenderBatchInfo
{
    public long Id { get; init; }

    public long EventId { get; set; }

    public long StartTicks { get; init; }

    public int Count { get; set; }

    public double TotalMs { get; set; }

    public double? DiffMs { get; set; }

    public int? DiffSize { get; set; }

    /// <summary>Instance ids rendered in this batch; used to attribute a render to an ancestor that rendered first.</summary>
    internal HashSet<long> RenderedInstanceIds { get; } = [];

    public long? TriggerEventId { get; set; }

    /// <summary>True once no further renders may be attached (the renderer moved on, or the diff was measured).</summary>
    public bool Closed { get; set; }
}

/// <summary>
/// Wraps each ComponentBase render fragment to measure BuildRenderTree, count renders, detect batches and attribute
/// causes. Runs on the renderer's dispatcher; the only shared state is the session's buffers.
/// </summary>
internal sealed class RenderTracker
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> JsRuntimeProperties = new();
    private static readonly ConcurrentDictionary<(string Label, string Ancestor), string> ParentCauseDetails = new();
    private const string InitialCauseDetail = "First render";
    private const string DisabledCauseDetail = "Cause tracking disabled";
    private const string StateHasChangedCauseDetail = "StateHasChanged outside an event (async continuation, timer, state notification or explicit call)";
    private readonly DevToolsSession _session;
    private readonly DevToolsOptions _options;
    private readonly Queue<RenderBatchInfo> _awaitingDiff = new();
    private RenderBatchInfo? _lastDiffBatch;
    private long _batchSequence;
    private long _renderSequence;
    private long _lastRenderEndTicks;

    public RenderTracker(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _options = options;
    }

    public RenderBatchInfo? CurrentBatch { get; private set; }

    public long BatchCount => Volatile.Read(ref _batchSequence);

    public void Instrument(ComponentRecord record)
    {
        if (record.Component is not ComponentBase component)
        {
            record.InstrumentationNote = "Not derived from ComponentBase: renders are not measured, only presence in the tree.";
            return;
        }

        if (!BlazorReflection.CanWrapRenderFragment)
        {
            record.InstrumentationNote = "ComponentBase._renderFragment not found on this framework version; render tracking unavailable.";
            return;
        }

        var original = BlazorReflection.GetRenderFragment!(component);
        if (original is null)
        {
            record.InstrumentationNote = "Render fragment was null at activation.";
            return;
        }

        RenderFragment wrapped = builder =>
        {
            var t0 = Stopwatch.GetTimestamp();
            var scope = BeginRender(record, component);
            var t1 = Stopwatch.GetTimestamp();
            try
            {
                original(builder);
            }
            catch (Exception ex)
            {
                var t2 = Stopwatch.GetTimestamp();
                EndRender(record, scope, t1, t2, ex);
                _session.Overhead.AddInstrumentation((t1 - t0) + (Stopwatch.GetTimestamp() - t2));
                throw;
            }

            var t3 = Stopwatch.GetTimestamp();
            EndRender(record, scope, t1, t3, null);
            if (record.IsDevTools)
            {
                _session.Overhead.AddDevToolsRender(t3 - t1);
            }
            else
            {
                _session.Overhead.AddInstrumentation((t1 - t0) + (Stopwatch.GetTimestamp() - t3));
            }
        };

        record.IsInstrumented = BlazorReflection.TrySetRenderFragment(component, wrapped);
        if (!record.IsInstrumented)
        {
            record.InstrumentationNote = "Could not replace the render fragment.";
        }
    }

    private readonly record struct RenderScope(RenderSample? Sample, RenderBatchInfo? Batch);

    private RenderScope BeginRender(ComponentRecord record, ComponentBase component)
    {
        if (!_session.IsEnabled)
        {
            return default;
        }

        if (!record.RendererResolved)
        {
            ResolveRenderer(record, component);
        }

        if (!record.JsRuntimePatched)
        {
            record.JsRuntimePatched = true;
            if (_options.TrackJsInterop && !record.IsDevTools)
            {
                PatchInjectedJsRuntime(record, component);
            }
        }

        if (record.IsDevTools)
        {
            return default;
        }

        var batch = DetectBatch(record);
        var (cause, detail, triggerId) = DetermineCause(record, batch);
        var sample = new RenderSample
        {
            Sequence = Interlocked.Increment(ref _renderSequence),
            At = DateTimeOffset.UtcNow,
            StartTicks = Stopwatch.GetTimestamp(),
            BatchId = batch?.Id ?? 0,
            Cause = cause,
            CauseDetail = detail,
            TriggerEventId = triggerId,
        };

        if (record.CaptureState || _options.CaptureComponentStateOnRender)
        {
            CaptureStateDiff(record, component, sample);
        }

        return new RenderScope(sample, batch);
    }

    private void EndRender(ComponentRecord record, RenderScope scope, long startTicks, long endTicks, Exception? exception)
    {
        if (scope.Sample is null)
        {
            if (exception is not null && record.IsDevTools)
            {
                _session.Errors.Record(exception, "devtools", component: record);
            }

            return;
        }

        var sample = scope.Sample;
        sample.DurationMs = Stopwatch.GetElapsedTime(startTicks, endTicks).TotalMilliseconds;
        sample.Failed = exception is not null;
        record.AddRender(sample);

        if (scope.Batch is { } batch)
        {
            batch.Count++;
            batch.TotalMs += sample.DurationMs;
            batch.RenderedInstanceIds.Add(record.InstanceId);
            batch.TriggerEventId ??= sample.TriggerEventId;
        }

        if (!_options.RecordRenderEvents)
        {
            if (exception is not null)
            {
                _session.Errors.Record(exception, "render", component: record, precedingEventId: sample.TriggerEventId);
            }

            _session.Touch();
            _lastRenderEndTicks = Stopwatch.GetTimestamp();
            return;
        }

        var title = sample.Cause == RenderCause.Initial ? record.FirstRenderTitle : record.ReRenderTitle;
        var evt = new DevToolsEvent
        {
            Kind = DevToolsEventKind.Render,
            Category = "render",
            Title = title,
            Detail = sample.CauseDetail,
            DurationMs = sample.DurationMs,
            Severity = exception is null ? (sample.DurationMs >= _options.Diagnostics.SlowRenderMs ? DevToolsSeverity.Warning : DevToolsSeverity.Info) : DevToolsSeverity.Error,
            ParentEventId = sample.TriggerEventId ?? scope.Batch?.EventId,
            ComponentInstanceId = record.InstanceId,
            ComponentName = record.DisplayName,
            Timestamp = sample.At,
            StartTicks = sample.StartTicks,
            Data = new RenderEventData(sample, record.RenderCount),
        };
        sample.EventId = _session.Timeline.Record(evt);

        if (exception is not null)
        {
            _session.Errors.Record(exception, "render", component: record, precedingEventId: sample.TriggerEventId);
        }

        _lastRenderEndTicks = Stopwatch.GetTimestamp();
    }

    private void ResolveRenderer(ComponentRecord record, ComponentBase component)
    {
        record.RendererResolved = true;
        if (!BlazorReflection.CanResolveRenderer)
        {
            return;
        }

        var renderer = BlazorReflection.ResolveRenderer(component, out var componentId);
        if (renderer is null)
        {
            return;
        }

        record.Renderer = renderer;
        record.ComponentId = componentId;
        _session.Components.NoteRenderer(renderer);

        // Rendering always happens on the renderer's synchronization context; remembering it makes later ambient
        // session lookups (HTTP, logs, metrics) an O(1) dictionary hit instead of a scan over every live session.
        SessionResolver.NoteCurrentContext(_session);

        if (_session.Dispatcher is null)
        {
            try
            {
                _session.Dispatcher = renderer.Dispatcher;
            }
            catch
            {
                // some renderers throw for Dispatcher before initialization; ignore.
            }
        }

        if (_session.Platform is null)
        {
            var info = BlazorReflection.ResolveRendererInfo(component);
            if (info is not null)
            {
                _session.Platform = info.Name;
                _session.IsInteractive = info.IsInteractive;
            }
        }

        _session.Components.ResolveHierarchy(record, DateTimeOffset.UtcNow);
    }

    private RenderBatchInfo? DetectBatch(ComponentRecord record)
    {
        var renderer = record.Renderer;
        var current = CurrentBatch;
        bool newBatch;
        if (renderer is not null && BlazorReflection.CanDetectBatches)
        {
            int diffCount;
            try
            {
                diffCount = BlazorReflection.GetUpdatedDiffCount!(renderer);
            }
            catch
            {
                diffCount = -1;
            }

            // The batch builder's diff list is cleared between batches, so a count of zero means the renderer just
            // started a new pass. A count lower than what we already attributed means a new pass started with renders
            // we could not observe (components that do not derive from ComponentBase).
            newBatch = current is null || current.Closed || diffCount <= 0 || current.Count > diffCount;
        }
        else
        {
            // Fallback heuristic: renders separated by more than 2 ms of idle time belong to different batches.
            newBatch = current is null || current.Closed || Stopwatch.GetElapsedTime(_lastRenderEndTicks).TotalMilliseconds > 2;
        }

        if (!newBatch)
        {
            return current;
        }

        CloseBatch();
        var batch = new RenderBatchInfo { Id = Interlocked.Increment(ref _batchSequence), StartTicks = Stopwatch.GetTimestamp() };
        batch.EventId = _session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.RenderBatch,
            Category = "render-batch",
            Title = "Render batch #" + batch.Id,
            Detail = "in progress",
            ParentEventId = ActivityObserver.CurrentTriggerEvent(_session)?.Id,
            Data = new Dictionary<string, object?> { ["batch"] = batch.Id },
        });
        CurrentBatch = batch;
        return batch;
    }

    /// <summary>Finalizes the current batch event. Called when the next batch starts and by the UI refresh.</summary>
    public void CloseBatch()
    {
        if (CurrentBatch is not { Closed: false } batch)
        {
            return;
        }

        batch.Closed = true;
        if (batch.DiffMs is null)
        {
            _awaitingDiff.Enqueue(batch);
            while (_awaitingDiff.Count > 32)
            {
                _awaitingDiff.Dequeue();
            }
        }

        UpdateBatchEvent(batch);
    }

    private void UpdateBatchEvent(RenderBatchInfo batch)
    {
        var evt = _session.Timeline.Find(batch.EventId);
        if (evt is null)
        {
            return;
        }

        evt.DurationMs = batch.TotalMs;
        evt.Detail = $"{batch.Count} component{(batch.Count == 1 ? "" : "s")} rendered, {batch.TotalMs:0.00} ms building"
            + (batch.DiffMs is { } d ? $", {d:0.00} ms diffing" : "")
            + (batch.DiffSize is { } s ? $", {s} DOM edits" : "");
        if (batch.Count >= _options.Diagnostics.RenderCascadeSize)
        {
            evt.Severity = DevToolsSeverity.Warning;
        }

        if (evt.Data is Dictionary<string, object?> data)
        {
            data["count"] = batch.Count;
            data["totalMs"] = batch.TotalMs;
            data["diffMs"] = batch.DiffMs;
            data["diffSize"] = batch.DiffSize;
        }
    }

    /// <summary>
    /// Called from the <c>aspnetcore.components.render_diff.duration</c> measurement, once per batch.
    /// <para>
    /// The framework records this metric when the batch <em>task</em> completes. In WebAssembly that is synchronous,
    /// so the measurement belongs to the batch still open. On Blazor Server the task completes when the browser
    /// acknowledges the render, which can be after later batches have started — so measurements are matched against
    /// the queue of batches still waiting for one, in the order the renderer produced them.
    /// </para>
    /// </summary>
    internal void RecordBatchDiff(double diffMs)
    {
        var batch = TakeBatchAwaitingDiff() ?? CurrentBatch;
        if (batch is null)
        {
            return;
        }

        _lastDiffBatch = batch;
        batch.DiffMs = diffMs;
        if (ReferenceEquals(batch, CurrentBatch))
        {
            CloseBatch();
        }
        else
        {
            UpdateBatchEvent(batch);
        }
    }

    /// <summary>Called from the <c>aspnetcore.components.render_diff.size</c> measurement, which follows the duration.</summary>
    internal void RecordBatchDiffSize(int diffSize)
    {
        var batch = _lastDiffBatch ?? CurrentBatch;
        if (batch is null)
        {
            return;
        }

        batch.DiffSize = diffSize;
        UpdateBatchEvent(batch);
    }

    private RenderBatchInfo? TakeBatchAwaitingDiff()
    {
        while (_awaitingDiff.TryDequeue(out var batch))
        {
            // Stale entries mean a measurement was never delivered for that batch; do not let it shift every later
            // diff onto the wrong batch.
            if (batch.DiffMs is null && Stopwatch.GetElapsedTime(batch.StartTicks).TotalSeconds < 5)
            {
                return batch;
            }
        }

        return null;
    }

    private (RenderCause Cause, string Detail, long? TriggerEventId) DetermineCause(ComponentRecord record, RenderBatchInfo? batch)
    {
        if (record.RenderCount == 0)
        {
            return (RenderCause.Initial, InitialCauseDetail, ActivityObserver.CurrentTriggerEvent(_session)?.Id);
        }

        if (!_options.TrackRenderCauses)
        {
            return (RenderCause.Unknown, DisabledCauseDetail, null);
        }

        // 1. An ancestor rendered earlier in the same batch: the renderer re-set our parameters.
        if (batch is not null && batch.Count > 0)
        {
            var ancestor = record.Parent;
            var hops = 0;
            while (ancestor is not null && hops++ < 64)
            {
                if (batch.RenderedInstanceIds.Contains(ancestor.InstanceId))
                {
                    var ancestorSample = ancestor.LastRender;
                    var triggerId = ancestorSample?.TriggerEventId;
                    var label = hops == 1 ? "Parent" : "Ancestor";
                    var detail = ParentCauseDetails.GetOrAdd((label, ancestor.DisplayName), static key => $"{key.Label} {key.Ancestor} re-rendered (parameters re-applied)");
                    return (RenderCause.ParentRender, detail, triggerId ?? ancestorSample?.EventId);
                }

                ancestor = ancestor.Parent;
            }
        }

        // 2. A UI event or navigation is executing on this session.
        var trigger = ActivityObserver.CurrentTriggerEvent(_session);
        if (trigger is not null)
        {
            return trigger.Kind == DevToolsEventKind.Navigation
                ? (RenderCause.Navigation, "Navigation: " + trigger.Title, trigger.Id)
                : (RenderCause.UiEvent, "Event: " + trigger.Title, trigger.Id);
        }

        // 3. Anything else is an explicit StateHasChanged / async continuation (timer, HTTP completion, state notification).
        return (RenderCause.StateHasChanged, StateHasChangedCauseDetail, null);
    }

    private void CaptureStateDiff(ComponentRecord record, ComponentBase component, RenderSample sample)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in ComponentStateReader.ReadFields(component))
            {
                snapshot[name] = _session.Inspector.Describe(name, value).Display;
            }

            if (record.LastStateSnapshot is not null)
            {
                var changes = Inspection.ObjectInspector.Diff(record.LastStateSnapshot, snapshot);
                if (changes.Count > 0)
                {
                    sample.ChangedState = changes.Select(c => c.Path + ": " + (c.Before ?? "∅") + " → " + (c.After ?? "∅")).ToArray();
                }
            }

            record.LastStateSnapshot = snapshot;
        }
        catch
        {
            // state capture is best-effort
        }
        finally
        {
            _session.Overhead.AddInspection(Stopwatch.GetElapsedTime(start).Ticks);
        }
    }

    /// <summary>
    /// Replaces the component's <c>[Inject] IJSRuntime</c> with a tracking decorator. This happens on the first render,
    /// which is the earliest point after the framework has performed property injection; calls made from
    /// <c>OnInitialized(Async)</c> before that first render are therefore not attributed to the component.
    /// </summary>
    private void PatchInjectedJsRuntime(ComponentRecord record, ComponentBase component)
    {
        var properties = JsRuntimeProperties.GetOrAdd(component.GetType(), static type =>
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                .Where(p => p.IsDefined(typeof(InjectAttribute), true) && p.CanWrite && p.CanRead
                    && (p.PropertyType == typeof(IJSRuntime) || p.PropertyType == typeof(IJSInProcessRuntime)))
                .ToArray());
        foreach (var property in properties)
        {
            try
            {
                var current = property.GetValue(component);
                if (current is null or TrackingJSRuntime)
                {
                    continue;
                }

                if (current is IJSRuntime js)
                {
                    var wrapper = TrackingJSRuntime.Create(js, _session, record);
                    if (property.PropertyType.IsInstanceOfType(wrapper))
                    {
                        property.SetValue(component, wrapper);
                    }
                }
            }
            catch
            {
                // never let instrumentation break the app
            }
        }
    }
}

/// <summary>Reads a component's own fields (state) excluding parameters, injected services and framework plumbing.</summary>
internal static class ComponentStateReader
{
    private static readonly ConcurrentDictionary<Type, (FieldInfo Field, string Name)[]> Cache = new();

    public static IEnumerable<(string Name, object? Value)> ReadFields(object component)
    {
        foreach (var (field, name) in Fields(component.GetType()))
        {
            object? value;
            try
            {
                value = field.GetValue(component);
            }
            catch (Exception ex)
            {
                value = "«threw " + ex.GetType().Name + "»";
            }

            yield return (name, value);
        }
    }

    public static (FieldInfo Field, string Name)[] Fields(Type type) => Cache.GetOrAdd(type, static t =>
    {
        var result = new List<(FieldInfo, string)>();
        var excludedProperties = new HashSet<string>(StringComparer.Ordinal);
        for (var current = t; current is not null && !IsFrameworkType(current); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (property.IsDefined(typeof(ParameterAttribute), true) || property.IsDefined(typeof(CascadingParameterAttribute), true)
                    || property.IsDefined(typeof(InjectAttribute), true) || property.IsDefined(typeof(DevToolsIgnoreAttribute), true)
                    || property.IsDefined(typeof(SupplyParameterFromQueryAttribute), true) || property.IsDefined(typeof(SupplyParameterFromFormAttribute), true))
                {
                    excludedProperties.Add(property.Name);
                }
            }
        }

        for (var current = t; current is not null && !IsFrameworkType(current); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsDefined(typeof(DevToolsIgnoreAttribute), true) || field.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) && !field.Name.EndsWith("k__BackingField", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = field.Name;
                if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                {
                    name = name[1..name.IndexOf('>')];
                    if (excludedProperties.Contains(name))
                    {
                        continue;
                    }
                }

                if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(EventCallback) && name.Contains("__", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add((field, name));
            }
        }

        return result.ToArray();
    });

    private static bool IsFrameworkType(Type type) =>
        type == typeof(object) || type == typeof(ComponentBase) || type == typeof(LayoutComponentBase) || type == typeof(OwningComponentBase)
        || (type.Namespace?.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal) ?? false);
}
