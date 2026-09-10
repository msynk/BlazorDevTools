using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlazorDevTools.Events;
using BlazorDevTools.Session;

namespace BlazorDevTools.Instrumentation;

/// <summary>
/// Listens to the framework's own diagnostics (.NET 10+): the "Microsoft.AspNetCore.Components" ActivitySource
/// (HandleEvent, Navigate) and the components meters (parameter-update durations, batch diff duration and size).
/// This is public, supported instrumentation; no reflection involved. The emitting services are registered by
/// <c>AddBlazorDevTools</c>, because outside server-side rendering the framework does not register them itself.
/// </summary>
internal static class ActivityObserver
{
    public const string SourceName = "Microsoft.AspNetCore.Components";
    public const string MeterName = "Microsoft.AspNetCore.Components";
    public const string LifecycleMeterName = "Microsoft.AspNetCore.Components.Lifecycle";
    public const string HandleEventName = "Microsoft.AspNetCore.Components.HandleEvent";
    public const string NavigateName = "Microsoft.AspNetCore.Components.Navigate";

    // Tag keys emitted by ComponentsActivitySource / ComponentsMetrics in .NET 10.
    internal const string ComponentTypeTag = "aspnetcore.components.type";
    internal const string MethodTag = "code.function.name";
    internal const string AttributeTag = "aspnetcore.components.attribute.name";
    internal const string RouteTag = "aspnetcore.components.route";

    private const string EventProperty = "BlazorDevTools.Event";
    private const string SessionProperty = "BlazorDevTools.Session";

    private static readonly object Lock = new();
    private static ActivityListener? _listener;
    private static MeterListener? _meterListener;
    private static int _handleEventCount;
    private static int _navigateCount;
    private static int _lifecycleMeasurements;
    private static int _batchMeasurements;

    public static bool IsStarted => _listener is not null;

    public static bool HandleEventActivitiesObserved => Volatile.Read(ref _handleEventCount) > 0;

    public static bool NavigateActivitiesObserved => Volatile.Read(ref _navigateCount) > 0;

    public static bool LifecycleMetricsObserved => Volatile.Read(ref _lifecycleMeasurements) > 0;

    public static bool BatchMetricsObserved => Volatile.Read(ref _batchMeasurements) > 0;

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
                ShouldListenTo = static source => source.Name == SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = OnActivityStarted,
                ActivityStopped = OnActivityStopped,
            };
            ActivitySource.AddActivityListener(_listener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == MeterName || instrument.Meter.Name == LifecycleMeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>(static (i, v, tags, s) => OnMeasurement(i, v, tags));
            _meterListener.SetMeasurementEventCallback<long>(static (i, v, tags, s) => OnMeasurement(i, v, tags));
            _meterListener.SetMeasurementEventCallback<int>(static (i, v, tags, s) => OnMeasurement(i, v, tags));
            _meterListener.Start();
        }
    }

    /// <summary>Stops listening. Used by tests; production keeps the listeners for the process lifetime.</summary>
    internal static void Stop()
    {
        lock (Lock)
        {
            _listener?.Dispose();
            _listener = null;
            _meterListener?.Dispose();
            _meterListener = null;
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
            return ReferenceEquals(existing, IgnoredEvent) ? null : existing;
        }

        var isEvent = activity.OperationName == HandleEventName;
        // The framework sets DisplayName before starting the activity but adds its tags only when stopping it, so the
        // display name is the only source of the component type while the handler is still running.
        var tags = new Dictionary<string, object?>(StringComparer.Ordinal);
        var (componentType, method, attribute, route) = ReadTags(activity, tags);
        var fromDisplayName = ParseDisplayName(activity.DisplayName);
        componentType ??= fromDisplayName.ComponentType;
        method ??= fromDisplayName.Method;
        attribute ??= fromDisplayName.Attribute;

        // Interacting with the DevTools panel is not application activity; recording it would bury the developer's
        // own events under the clicks they made to look for them.
        if (componentType is not null && IsDevToolsOwnComponent(componentType))
        {
            activity.SetCustomProperty(EventProperty, IgnoredEvent);
            return null;
        }

        var shortType = componentType is null ? null : Shorten(componentType);
        var title = isEvent
            ? (attribute ?? "event") + " → " + (shortType ?? "?") + "." + (method ?? "?")
            : string.IsNullOrEmpty(route) ? DefaultNavigationTitle(activity) : "Navigate to " + route;

        var evt = new DevToolsEvent
        {
            Kind = isEvent ? DevToolsEventKind.UiEvent : DevToolsEventKind.Navigation,
            Category = isEvent ? "ui-event" : "navigation",
            Title = title,
            Detail = "in progress",
            Timestamp = activity.StartTimeUtc,
            ComponentName = shortType,
            // Only parent a navigation to a UI event that is still on the Activity stack (a link click), never to an
            // unrelated event that merely happened to be the most recent one.
            ParentEventId = isEvent ? null : FindAncestorEventId(activity, session),
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

    /// <summary>Marker stored on activities DevTools deliberately does not record, so they are not adopted later.</summary>
    private static readonly DevToolsEvent IgnoredEvent = new() { Title = "(devtools)" };

    /// <summary>
    /// Reports whether the framework's diagnostics services are actually present in the renderer's container.
    /// Without this, a missing measurement is indistinguishable from a measurement that has not happened yet.
    /// </summary>
    internal static (bool ActivitySource, bool Metrics) ResolveFrameworkServices(IServiceProvider services)
    {
        return (Resolve("Microsoft.AspNetCore.Components.ComponentsActivitySource"), Resolve("Microsoft.AspNetCore.Components.ComponentsMetrics"));

        bool Resolve(string typeName)
        {
            try
            {
                var type = Type.GetType(typeName + ", Microsoft.AspNetCore.Components", throwOnError: false);
                return type is not null && services.GetService(type) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Recovers the component type, method and attribute from the activity display name
    /// ("Event onclick -&gt; MyApp.Pages.Counter.IncrementCount"), which is the only thing the framework fills in
    /// before the activity starts.
    /// </summary>
    internal static (string? ComponentType, string? Method, string? Attribute) ParseDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return (null, null, null);
        }

        var arrow = displayName.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return (null, null, null);
        }

        var head = displayName[..arrow];
        var target = displayName[(arrow + 4)..];
        var space = head.LastIndexOf(' ');
        var attribute = space >= 0 ? head[(space + 1)..] : null;
        var dot = target.LastIndexOf('.');
        return dot <= 0
            ? (target.Length == 0 ? null : target, null, attribute)
            : (target[..dot], target[(dot + 1)..], attribute);
    }

    /// <summary>
    /// Namespaces of the DevTools UI itself. Deliberately exact: an application (or a test) is free to live under a
    /// namespace that merely starts with "BlazorDevTools", and its events are ordinary application activity.
    /// </summary>
    private static readonly string[] OwnUiNamespaces = ["BlazorDevTools.UI.", "BlazorDevTools.Server.UI."];

    private static bool IsDevToolsOwnComponent(string componentType)
    {
        if (componentType == "BlazorDevTools.DevToolsPanel")
        {
            return true;
        }

        foreach (var prefix in OwnUiNamespaces)
        {
            if (componentType.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DefaultNavigationTitle(Activity activity) =>
        string.IsNullOrEmpty(activity.DisplayName) || activity.DisplayName == activity.OperationName ? "Navigation" : activity.DisplayName;

    private static (string? ComponentType, string? Method, string? Attribute, string? Route) ReadTags(Activity activity, Dictionary<string, object?> tags)
    {
        string? componentType = null, method = null, attribute = null, route = null;
        foreach (var tag in activity.TagObjects)
        {
            tags[tag.Key] = tag.Value;
            var value = tag.Value?.ToString();
            switch (tag.Key)
            {
                case ComponentTypeTag:
                    componentType = value;
                    break;
                case MethodTag:
                    method = value;
                    break;
                case AttributeTag:
                    attribute = value;
                    break;
                case RouteTag:
                    route = value;
                    break;
            }
        }

        return (componentType, method, attribute, route);
    }

    private static long? FindAncestorEventId(Activity activity, DevToolsSession session)
    {
        var parent = activity.Parent;
        var hops = 0;
        while (parent is not null && hops++ < 16)
        {
            if (parent.GetCustomProperty(EventProperty) is DevToolsEvent evt && ReferenceEquals(parent.GetCustomProperty(SessionProperty), session))
            {
                return evt.Id;
            }

            parent = parent.Parent;
        }

        return null;
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
        if (evt is null || ReferenceEquals(evt, IgnoredEvent))
        {
            return;
        }

        // Tags only exist now; back-fill the component type and the structured data bag recorded at start.
        var componentType = ReadTags(activity, evt.Data as Dictionary<string, object?> ?? []).ComponentType;
        if (componentType is not null)
        {
            evt.ComponentName = Shorten(componentType);
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
            var metrics = session.TypeMetrics.GetOrAdd(typeName, static name => new LifecycleTypeMetrics { TypeName = name });
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
                if (activity.GetCustomProperty(EventProperty) is DevToolsEvent evt)
                {
                    return ReferenceEquals(evt, IgnoredEvent) || !ReferenceEquals(activity.GetCustomProperty(SessionProperty), session) ? null : evt;
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

    private static void OnMeasurement<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
    {
        // Cheap dispatch first: the parameter-update histogram fires once per component per parameter update.
        switch (instrument.Name)
        {
            case "aspnetcore.components.update_parameters.duration":
            {
                Interlocked.Increment(ref _lifecycleMeasurements);
                var session = SessionResolver.Resolve();
                if (session is null || !session.IsEnabled)
                {
                    return;
                }

                string? type = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == ComponentTypeTag)
                    {
                        type = tag.Value?.ToString();
                    }
                }

                if (type is null)
                {
                    return;
                }

                var shortType = Shorten(type);
                var metrics = session.TypeMetrics.GetOrAdd(shortType, static n => new LifecycleTypeMetrics { TypeName = n });
                var ms = ToDouble(value) * 1000;
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
            {
                Interlocked.Increment(ref _batchMeasurements);
                SessionResolver.Resolve()?.RenderTracker.RecordBatchDiff(ToDouble(value) * 1000);
                break;
            }

            case "aspnetcore.components.render_diff.size":
            {
                Interlocked.Increment(ref _batchMeasurements);
                SessionResolver.Resolve()?.RenderTracker.RecordBatchDiffSize((int)ToDouble(value));
                break;
            }
        }
    }

    private static double ToDouble<T>(T value) where T : struct => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => 0,
    };

    /// <summary>Turns an assembly-qualified or namespaced type name into a short display name.</summary>
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
