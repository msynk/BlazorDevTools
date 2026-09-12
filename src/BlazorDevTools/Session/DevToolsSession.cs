using System.Collections.Concurrent;
using BlazorDevTools.Capabilities;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Extensions;
using BlazorDevTools.Inspection;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Session;

/// <summary>Per-type lifecycle numbers observed through the framework's meters (.NET 10+): SetParametersAsync durations.</summary>
public sealed class LifecycleTypeMetrics
{
    public required string TypeName { get; init; }

    public int ParameterUpdates;

    public double ParameterUpdateMs;

    public double MaxParameterUpdateMs;

    public int EventHandlers;

    public double EventHandlerMs;
}

/// <summary>
/// Everything DevTools knows about one Blazor session: a circuit on the server, the app in WebAssembly, a request in
/// static rendering. Scoped service; created lazily when the first component is activated.
/// </summary>
public sealed class DevToolsSession : IDisposable
{
    private static long _nextId;
    private long _version;
    private bool _activated;

    public DevToolsSession(IServiceProvider services, DevToolsOptions options, DevToolsRegistry registry)
    {
        Id = Interlocked.Increment(ref _nextId);
        Services = services;
        Options = options;
        Registry = registry;
        IsEnabled = registry.IsEnabled;
        Redactor = new Redactor(options.SensitiveNamePatterns);
        Inspector = new ObjectInspector(options, Redactor, registry.Formatters);
        Overhead = new OverheadMeter();
        Timeline = new Timeline(this, options.MaxEvents);
        Components = new ComponentRegistry(this, options);
        Errors = new ErrorCenter(this, options);
        Http = new HttpStore(this, options);
        Interop = new JsInteropStore(this, options);
        State = new StateStore(this, options);
        Diagnostics = new DiagnosticsEngine(this, options, registry);
        Commands = new CommandRegistry();
        RenderTracker = new RenderTracker(this, options);
        Ui = new DevToolsUiState
        {
            IsOpen = options.Ui.OpenByDefault,
            Dock = options.Ui.Dock,
            Theme = options.Ui.Theme,
            LiveUpdates = options.Ui.LiveUpdates,
        };
        Browser = new BrowserState();
        StartedAt = DateTimeOffset.UtcNow;

        if (IsEnabled)
        {
            SessionResolver.Register(this);
        }
    }

    public long Id { get; }

    public DateTimeOffset StartedAt { get; }

    public bool IsEnabled { get; }

    public bool IsDisposed { get; private set; }

    public DevToolsOptions Options { get; }

    public DevToolsRegistry Registry { get; }

    internal IServiceProvider Services { get; }

    public Redactor Redactor { get; }

    public ObjectInspector Inspector { get; }

    public OverheadMeter Overhead { get; }

    internal Timeline Timeline { get; }

    internal ComponentRegistry Components { get; }

    internal ErrorCenter Errors { get; }

    internal HttpStore Http { get; }

    internal JsInteropStore Interop { get; }

    internal StateStore State { get; }

    internal DiagnosticsEngine Diagnostics { get; }

    internal CommandRegistry Commands { get; }

    internal RenderTracker RenderTracker { get; }

    public DevToolsUiState Ui { get; }

    public BrowserState Browser { get; }

    /// <summary>Public timeline API for extensions.</summary>
    public IDevToolsTimeline TimelineApi => Timeline;

    public IStateProviderRegistry StateProviders => State;

    /// <summary>The renderer dispatcher owning this session; used to attribute ambient activity (HTTP, logs, metrics) to the session.</summary>
    public Dispatcher? Dispatcher { get; internal set; }

    /// <summary>"Server", "WebAssembly", "Static" or "WebView" as reported by RendererInfo; null until a component reports it.</summary>
    public string? Platform { get; internal set; }

    public bool IsInteractive { get; internal set; }

    public string? RenderModeName { get; internal set; }

    /// <summary>Monotonic version incremented on any change; the UI polls it to decide whether to re-render.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Set once the renderer synchronization context has been mapped to this session.</summary>
    internal bool SynchronizationContextNoted { get; set; }

    internal long? LastUiEventId { get; set; }

    internal long? LastNavigationEventId { get; set; }

    /// <summary>The UI event (or navigation) currently executing on this session, resolved from Activity context.</summary>
    internal long? CurrentTriggerEventId => ActivityObserver.CurrentTriggerEvent(this)?.Id;

    internal ConcurrentDictionary<string, LifecycleTypeMetrics> TypeMetrics { get; } = new(StringComparer.Ordinal);

    internal void Touch() => Interlocked.Increment(ref _version);

    /// <summary>Wires DI-registered state providers, extension providers and commands. Called by the host component (not the constructor) to avoid DI cycles.</summary>
    internal void EnsureActivated()
    {
        if (_activated || !IsEnabled)
        {
            return;
        }

        _activated = true;
        try
        {
            foreach (var provider in Services.GetServices<IStateProvider>())
            {
                State.Attach(provider, "DI");
            }

            foreach (var factory in Registry.StateProviderFactories)
            {
                try
                {
                    State.Attach(factory(Services), "extension");
                }
                catch (Exception ex)
                {
                    Errors.Record(ex, "devtools", "A state provider factory threw.");
                }
            }

            Commands.AddRange(BlazorDevTools.Commands.BuiltInCommands.Create());
            Commands.AddRange(Registry.Commands);
            Commands.AddRange(Services.GetServices<Commands.IDevToolsCommand>());
        }
        catch (Exception ex)
        {
            Errors.Record(ex, "devtools", "DevTools activation failed.");
        }

        Touch();
    }

    /// <summary>Whether the framework diagnostics services this session depends on are present in its container.</summary>
    public (bool ActivitySource, bool Metrics) FrameworkInstrumentation => ActivityObserver.ResolveFrameworkServices(Services);

    public IReadOnlyList<DevToolsCapability> Capabilities => CapabilityReport.Build(this);

    /// <summary>
    /// Attributes ambient activity on the current async flow (HTTP requests, framework logs, metrics) to this session
    /// until the scope is disposed. Use it in background work (Task.Run, hosted services) that runs outside the
    /// renderer's dispatcher, where the session cannot be inferred.
    /// </summary>
    public IDisposable UseAmbient() => SessionResolver.PushAmbient(this);

    public void ClearAll()
    {
        Timeline.Clear();
        Errors.Clear();
        Http.Clear();
        Interop.Clear();
        State.ClearHistory();
        Components.ClearDisposed();
        Diagnostics.Clear();
        TypeMetrics.Clear();
        Touch();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        SessionResolver.Unregister(this);
        State.Dispose();
    }
}
