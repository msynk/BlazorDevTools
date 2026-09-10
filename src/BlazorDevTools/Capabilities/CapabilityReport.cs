using BlazorDevTools.Instrumentation;
using BlazorDevTools.Session;

namespace BlazorDevTools.Capabilities;

/// <summary>Builds the honest capability matrix for a session, combining static facts with what was actually observed at runtime.</summary>
internal static class CapabilityReport
{
    /// <summary>
    /// Blazor WebAssembly builds with the System.Diagnostics.Metrics feature switch off, which silently turns every
    /// meter into a no-op. Reporting "not yet observed" in that case would be a lie the developer cannot act on.
    /// </summary>
    internal static bool MetricsAreSupported() =>
        !AppContext.TryGetSwitch("System.Diagnostics.Metrics.Meter.IsSupported", out var supported) || supported;

    /// <summary>Explains a missing measurement in terms the developer can act on.</summary>
    internal static string MetricsHint(bool metricsSupported) => metricsSupported
        ? "No aspnetcore.components.render_diff measurements seen yet; they arrive with the first render batch."
        : "System.Diagnostics.Metrics is compiled out of this app: Blazor WebAssembly disables it by default. "
          + "Add <MetricsSupport>true</MetricsSupport> to the WebAssembly project to get diff durations and lifecycle timings.";

    public static IReadOnlyList<DevToolsCapability> Build(DevToolsSession session)
    {
        var platform = session.Platform ?? "unknown";
        var isServer = platform == "Server";
        var reflectionOk = BlazorReflection.CanWrapRenderFragment;
        var hierarchyOk = BlazorReflection.CanResolveHierarchy;
        var batchesOk = BlazorReflection.CanDetectBatches;
        var eventsObserved = ActivityObserver.HandleEventActivitiesObserved;
        var navObserved = ActivityObserver.NavigateActivitiesObserved;
        var lifecycleObserved = ActivityObserver.LifecycleMetricsObserved;
        var batchMetricsObserved = ActivityObserver.BatchMetricsObserved;
        var bridge = session.Browser.BridgeAttached;
        var metricsSupported = MetricsAreSupported();
        var metricsHint = MetricsHint(metricsSupported);

        return
        [
            new("Components", "Component instances", CapabilityStatus.Available, "Every component is created through the public IComponentActivator hook, so all instances are known without touching application code."),
            new("Components", "Parent/child hierarchy", hierarchyOk ? CapabilityStatus.Available : CapabilityStatus.NotAvailable, hierarchyOk ? "Resolved from the renderer's ComponentState (public since .NET 8) through a cached reflection accessor." : "The renderer's component state map could not be accessed on this framework version."),
            new("Components", "Parameters and cascading values", CapabilityStatus.Available, "Read from [Parameter]/[CascadingParameter] properties on demand; values are inspected lazily and sensitive names are redacted."),
            new("Components", "Component state (fields)", CapabilityStatus.Partial, "Fields are read on demand and diffed per render for the selected component only. Changes made without a render are not observed."),
            new("Components", "Injected services", CapabilityStatus.Available, "[Inject] properties are listed with their registered lifetime."),
            new("Components", "Source location", CapabilityStatus.Partial, "Inferred from the type name (Foo.razor); exact file paths need PDBs and are only shown in exception stack traces."),
            new("Components", "Disposal", CapabilityStatus.Partial, "Detected when the renderer forgets the instance (checked on each refresh), so timing is approximate."),
            new("Rendering", "Render count and BuildRenderTree duration", reflectionOk ? CapabilityStatus.RequiresInstrumentation : CapabilityStatus.NotAvailable, reflectionOk ? "The private render fragment of each ComponentBase is wrapped at creation; components that implement IComponent directly are not measured." : "ComponentBase internals changed; render measurement is unavailable."),
            new("Rendering", "Render batches", batchesOk ? CapabilityStatus.RequiresInstrumentation : CapabilityStatus.NotReliable, batchesOk ? "Batch boundaries come from the renderer's batch builder; a batch is one diff sent to the browser." : "Falling back to idle-time heuristics for batch boundaries."),
            new("Rendering", "Batch diff duration and DOM edit count", batchMetricsObserved ? CapabilityStatus.Available : metricsSupported ? CapabilityStatus.RequiresRuntimeSupport : CapabilityStatus.NotAvailable, batchMetricsObserved ? "Observed through the aspnetcore.components.render_diff meters (.NET 10). On Blazor Server the measurement arrives when the browser acknowledges the render, so it is matched back to the batch it belongs to." : metricsHint),
            new("Rendering", "Render cause: parent re-render", hierarchyOk && batchesOk ? CapabilityStatus.Available : CapabilityStatus.NotReliable, "Attributed when an ancestor rendered earlier in the same batch (the renderer re-applies parameters)."),
            new("Rendering", "Render cause: UI event", eventsObserved ? CapabilityStatus.Available : CapabilityStatus.RequiresRuntimeSupport, eventsObserved ? "Observed through the Microsoft.AspNetCore.Components.HandleEvent activity (.NET 10). AddBlazorDevTools registers the framework ActivitySource itself, which is what makes this work outside server-side rendering." : "No HandleEvent activities observed yet: interact with the app once. Requires .NET 10 and DevToolsOptions.UseFrameworkInstrumentation; until an event is seen, renders inside events are labeled StateHasChanged."),
            new("Rendering", "Render cause: explicit StateHasChanged", CapabilityStatus.Partial, "Anything not attributable to a parent, event or navigation is reported as StateHasChanged (timer, async continuation, state notification). The caller is not identifiable without a stack walk."),
            new("Rendering", "Lifecycle method durations (SetParametersAsync)", lifecycleObserved ? CapabilityStatus.Available : metricsSupported ? CapabilityStatus.RequiresRuntimeSupport : CapabilityStatus.NotAvailable, lifecycleObserved ? "Per component type, from aspnetcore.components.update_parameters.duration (.NET 10)." : metricsHint),
            new("Events", "UI events with handler duration", eventsObserved ? CapabilityStatus.Available : CapabilityStatus.RequiresRuntimeSupport, "From the framework ActivitySource; includes component type, method and attribute name."),
            new("Events", "Navigation", navObserved || session.LastNavigationEventId is not null ? CapabilityStatus.Available : CapabilityStatus.Partial, "From NavigationManager.LocationChanged and the Navigate activity when available."),
            new("State", "Registered state providers", CapabilityStatus.RequiresIntegration, "Implement IStateProvider (or register an adapter) to get snapshots, change history and before/after diffs."),
            new("State", "Cascading values", CapabilityStatus.Available, "Read from [CascadingParameter] properties of the selected component."),
            new("Network", "HTTP requests via IHttpClientFactory", CapabilityStatus.Available, "A DelegatingHandler is added to every factory pipeline through IHttpMessageHandlerBuilderFilter. Hand-built HttpClient instances need DevToolsHttpMessageHandler added manually."),
            new("Network", "Request/response bodies", CapabilityStatus.NotAvailable, "Never captured: bodies routinely contain credentials and personal data."),
            new("Network", "Originating component", CapabilityStatus.Partial, "Requests are linked to the UI event that issued them when one is executing; requests from lifecycle methods show no trigger."),
            new("JS interop", ".NET → JS calls from components", CapabilityStatus.RequiresInstrumentation, "IJSRuntime injected into tracked components is wrapped on the first render, the earliest point after the framework injects it: calls made from OnInitialized/OnInitializedAsync happen before that and are not attributed. Module references obtained through a wrapped runtime are wrapped too."),
            new("JS interop", ".NET → JS calls from services", CapabilityStatus.RequiresIntegration, "Services must call jsRuntime.WithDevToolsTracking(session). Decorating IJSRuntime globally is unsafe because the framework casts it to its concrete type."),
            new("JS interop", "JS → .NET calls", bridge ? CapabilityStatus.Available : CapabilityStatus.RequiresInstrumentation, "DotNet.invokeMethodAsync/invokeMethod are wrapped by the browser bridge and reported in batches."),
            new("Errors", "Render and event exceptions", CapabilityStatus.Available, "Render exceptions are caught in the wrapper; event exceptions and circuit failures arrive through Blazor's own logger categories."),
            new("Errors", "JS errors and unhandled promise rejections", bridge ? CapabilityStatus.Available : CapabilityStatus.RequiresInstrumentation, "window.onerror / unhandledrejection via the browser bridge."),
            new("Circuit", "Circuit lifecycle, connection state, inbound message count", isServer ? CapabilityStatus.RequiresInstrumentation : CapabilityStatus.NotAvailable, "Blazor Server only, through a CircuitHandler registered by AddBlazorDevToolsServer().") { Platforms = "Server" },
            new("Circuit", "Reconnect attempts", bridge && isServer ? CapabilityStatus.Available : CapabilityStatus.RequiresInstrumentation, "From the components:reconnect-state-changed browser event.") { Platforms = "Server" },
            new("Circuit", "SignalR message sizes and latency", CapabilityStatus.NotAvailable, "Not exposed by the framework; use SignalR logging or browser DevTools WS frames.") { Platforms = "Server" },
            new("DI", "Registrations, lifetimes, constructor dependencies", CapabilityStatus.Available, "From the IServiceCollection captured at AddBlazorDevTools time. Factory and instance registrations show no dependencies."),
            new("DI", "Runtime resolutions and consumers", CapabilityStatus.NotAvailable, "The container does not expose resolution events; consumers are inferred from constructors and [Inject] properties."),
            new("Browser", "Online/offline, visibility, storage keys, memory, timing", bridge ? CapabilityStatus.Available : CapabilityStatus.RequiresInstrumentation, "Reported by the browser bridge. Storage is read only on request, and values whose key looks sensitive or whose content looks like a token are redacted in the browser, so DevTools never receives them."),
            new("Browser", "Component ↔ DOM element mapping", CapabilityStatus.NotAvailable, "Blazor does not expose a component-to-DOM map to .NET code; highlighting elements is not implemented rather than approximated."),
            new("DevTools", "Overhead measurement", CapabilityStatus.Available, "Instrumentation time, DevTools UI render time, inspection and diagnostics time are tracked separately from application numbers."),
        ];
    }
}
