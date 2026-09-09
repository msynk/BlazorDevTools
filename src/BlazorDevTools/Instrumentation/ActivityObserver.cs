using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlazorDevTools.Events;
using BlazorDevTools.Session;

namespace BlazorDevTools.Instrumentation;

/// <summary>
/// Listens to the framework's own diagnostics (.NET 10+): the "Microsoft.AspNetCore.Components" ActivitySource
/// (HandleEvent, Navigate) and the components meters (parameter-update durations, batch diff durations, circuits).
/// This is public, supported instrumentation; no reflection involved. When the host does not emit them the
/// corresponding capabilities are reported as unavailable.
/// </summary>
internal static class ActivityObserver
{
    public const string SourceName = "Microsoft.AspNetCore.Components";
    public const string HandleEventName = "Microsoft.AspNetCore.Components.HandleEvent";
    public const string NavigateName = "Microsoft.AspNetCore.Components.Navigate";
    private const string EventProperty = "BlazorDevTools.Event";
    private const string SessionProperty = "BlazorDevTools.Session";

    private static readonly object Lock = new();
    private static ActivityListener? _listener;
    private static MeterListener? _meterListener;
    private static int _handleEventCount;
    private static int _navigateCount;
    private static int _lifecycleMeasurements;
    private static int _batchMeasurements;
    private static int _circuitMeasurements;

    public static bool IsStarted => _listener is not null;

    public static bool HandleEventActivitiesObserved => Volatile.Read(ref _handleEventCount) > 0;

    public static bool NavigateActivitiesObserved => Volatile.Read(ref _navigateCount) > 0;

    public static bool LifecycleMetricsObserved => Volatile.Read(ref _lifecycleMeasurements) > 0;

    public static bool BatchMetricsObserved => Volatile.Read(ref _batchMeasurements) > 0;

    public static bool CircuitMetricsObserved => Volatile.Read(ref _circuitMeasurements) > 0;

    public static void EnsureStarted()
    {
        lock (Lock)
        {
            if (_listener is not null)
            {
                return;
            }

            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = OnActivityStarted,
                ActivityStopped = OnActivityStopped,
            };
            ActivitySource.AddActivityListener(_listener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => OnMeasurement(instrument, value, tags, state));
            _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, state) => OnMeasurement(instrument, value, tags, state));
            _meterListener.Start();
        }
    }

    private static void OnActivityStarted(Activity activity)
    {
        var isEvent = activity.OperationName == HandleEventName;
        var isNavigate = activity.OperationName == NavigateName;
        if (!isEvent && !isNavigate)
        {
            return;
        }

        if (isEvent)
        {
            Interlocked.Increment(ref _handleEventCount);
        }
        else
        {
            Interlocked.Increment(ref _navigateCount);
        }

        var session = SessionResolver.Resolve();
        if (session is null || !session.IsEnabled)
        {
            return;
        }

        Adopt(activity, session);
    }

    private static DevToolsEvent? Adopt(Activity activity, DevToolsSession session)
    {
        if (activity.GetCustomProperty(EventProperty) is DevToolsEvent existing)
        {
            return existing;
        }

        var isEvent = activity.OperationName == HandleEventName;
        string? componentType = null, method = null, attribute = null, route = null;
        var tags = new Dictionary<string, object?>();
        foreach (var tag in activity.TagObjects)
        {
            tags[tag.Key] = tag.Value;
            var key = tag.Key;
            var value = tag.Value?.ToString();
            if (key.EndsWith("component.type", StringComparison.OrdinalIgnoreCase))
            {
                componentType = value;
            }
            else if (key.EndsWith("component.method", StringComparison.OrdinalIgnoreCase) || key.EndsWith("method", StringComparison.OrdinalIgnoreCase))
            {
                method = value;
            }
            else if (key.EndsWith("attribute.name", StringComparison.OrdinalIgnoreCase))
            {
                attribute = value;
            }
            else if (key.EndsWith("route", StringComparison.OrdinalIgnoreCase))
            {
                route = value;
            }
        }

        var shortType = componentType is null ? null : Model.TypeNames.Short(TryLoadType(componentType) ?? typeof(object)) is var st && st != "Object" ? st : Shorten(componentType);
        string title;
        if (isEvent)
        {
            title = (attribute ?? "event") + " → " + (shortType ?? "?") + "." + (method ?? "?");
        }
        else
        {
            title = string.IsNullOrEmpty(route) ? (activity.DisplayName.Length > 0 ? activity.DisplayName : "Navigation") : "Navigate to " + route;
        }

        var evt = new DevToolsEvent
        {
            Kind = isEvent ? DevToolsEventKind.UiEvent : DevToolsEventKind.Navigation,
            Category = isEvent ? "ui-event" : "navigation",
            Title = title,
            Detail = "in progress",
            Timestamp = activity.StartTimeUtc,
            ComponentName = shortType,
            ParentEventId = isEvent ? null : session.LastUiEventId,
            Data = tags,
        };
        var id = session.Timeline.Record(evt);
        activity.SetCustomProperty(EventProperty, evt);
        activity.SetCustomProperty(SessionProperty, session);
        if (isEvent)
        {
            session.LastUiEventId = id;
        }
        else
        {
            session.LastNavigationEventId = id;
        }

        return evt;
    }

    private static void OnActivityStopped(Activity activity)
    {
        if (activity.OperationName != HandleEventName && activity.OperationName != NavigateName)
        {
            return;
        }

        var session = activity.GetCustomProperty(SessionProperty) as DevToolsSession ?? SessionResolver.Resolve();
        if (session is null || !session.IsEnabled)
        {
            return;
        }

        var evt = activity.GetCustomProperty(EventProperty) as DevToolsEvent ?? Adopt(activity, session);
        if (evt is null)
        {
            return;
        }

        var duration = activity.Duration.TotalMilliseconds;
        var failed = activity.Status == ActivityStatusCode.Error;
        string? errorType = null;
        foreach (var tag in activity.Tags)
        {
            if (tag.Key == "error.type")
            {
                errorType = tag.Value;
            }
        }

        var detail = failed ? "failed" + (errorType is null ? "" : " with " + errorType) : "completed";
        var severity = failed ? DevToolsSeverity.Error : duration >= session.Options.Diagnostics.SlowEventHandlerMs ? DevToolsSeverity.Warning : DevToolsSeverity.Info;
        session.Timeline.Complete(evt.Id, duration, detail, severity);

        if (evt.Kind == DevToolsEventKind.UiEvent && evt.ComponentName is { } typeName)
        {
            var metrics = session.TypeMetrics.GetOrAdd(typeName, _ => new LifecycleTypeMetrics { TypeName = typeName });
            lock (metrics)
            {
                metrics.EventHandlers++;
                metrics.EventHandlerMs += duration;
            }
        }
    }

    /// <summary>Returns the UI event or navigation executing right now on the given session (walks Activity.Current).</summary>
    public static DevToolsEvent? CurrentTriggerEvent(DevToolsSession session)
    {
        var activity = Activity.Current;
        var hops = 0;
        while (activity is not null && hops++ < 32)
        {
            if (activity.OperationName == HandleEventName || activity.OperationName == NavigateName)
            {
                if (activity.GetCustomProperty(EventProperty) is DevToolsEvent evt && ReferenceEquals(activity.GetCustomProperty(SessionProperty), session))
                {
                    return evt;
                }

                if (activity.GetCustomProperty(EventProperty) is null && !activity.IsStopped)
                {
                    return Adopt(activity, session);
                }
            }

            activity = activity.Parent;
        }

        return null;
    }

    private static void OnMeasurement<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) where T : struct
    {
        var seconds = Convert.ToDouble(value);
        var name = instrument.Name;
        if (name.StartsWith("aspnetcore.components.circuit", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _circuitMeasurements);
            return;
        }

        var session = SessionResolver.Resolve();
        if (session is null || !session.IsEnabled)
        {
            return;
        }

        switch (name)
        {
            case "aspnetcore.components.update_parameters.duration":
            {
                Interlocked.Increment(ref _lifecycleMeasurements);
                string? type = null;
                foreach (var tag in tags)
                {
                    if (tag.Key.EndsWith("component.type", StringComparison.Ordinal))
                    {
                        type = tag.Value?.ToString();
                    }
                }

                if (type is null)
                {
                    return;
                }

                var shortType = Shorten(type);
                var metrics = session.TypeMetrics.GetOrAdd(shortType, _ => new LifecycleTypeMetrics { TypeName = shortType });
                var ms = seconds * 1000;
                lock (metrics)
                {
                    metrics.ParameterUpdates++;
                    metrics.ParameterUpdateMs += ms;
                    if (ms > metrics.MaxParameterUpdateMs)
                    {
                        metrics.MaxParameterUpdateMs = ms;
                    }
                }

                break;
            }

            case "aspnetcore.components.render_diff.duration":
                Interlocked.Increment(ref _batchMeasurements);
                session.RenderTracker.RecordBatchDiff(seconds * 1000, null);
                break;

            case "aspnetcore.components.render_diff.size":
                Interlocked.Increment(ref _batchMeasurements);
                if (session.RenderTracker.CurrentBatch is { } batch)
                {
                    batch.DiffSize = (int)seconds;
                }

                break;
        }
    }

    private static Type? TryLoadType(string fullName)
    {
        try
        {
            return Type.GetType(fullName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    internal static string Shorten(string typeName)
    {
        var comma = typeName.IndexOf(',');
        if (comma > 0)
        {
            typeName = typeName[..comma];
        }

        var tick = typeName.IndexOf('`');
        if (tick > 0)
        {
            typeName = typeName[..tick];
        }

        var dot = typeName.LastIndexOf('.');
        return dot < 0 ? typeName : typeName[(dot + 1)..];
    }
}
