# Blazor DevTools

An in-app inspection and diagnostics tool for Blazor applications: a live component tree, a render profiler that
explains *why* a component rendered, a unified activity timeline, state inspection with diffs, network and JS interop
tracking, an error center, a DI inspector, circuit diagnostics for Blazor Server, and an evidence-based diagnostics
engine. Built as developer infrastructure with a public extension API, minimal runtime overhead and no changes to
application source.

```
dotnet add package BlazorDevTools          # Blazor WebAssembly / any Blazor host
dotnet add package BlazorDevTools.Server   # adds circuit instrumentation for Blazor Server
```

```csharp
// Program.cs (server)
builder.Services.AddBlazorDevToolsServer();   // or AddBlazorDevTools() in WebAssembly / other hosts
```

In an **Interactive Auto** solution, call `AddBlazorDevToolsServer()` in the server project and
`AddBlazorDevTools()` in the `.Client` project, so DevTools works whichever render mode the page ends up in.
In **Blazor WebAssembly**, add `<MetricsSupport>true</MetricsSupport>` to the project if you want render-diff and
lifecycle timings: the WebAssembly SDK compiles `System.Diagnostics.Metrics` out by default. The About panel says so
when it is missing, and everything else still works without it.

```razor
@* MainLayout.razor (or any interactive component) *@
<DevToolsPanel />
```

That is the whole setup. DevTools is enabled only when the host environment is `Development` (override with
`options.Enabled`), renders nothing during prerendering, and stays dormant in production builds.
Press **Ctrl+Shift+D** or click the badge to open it; **Ctrl+K** opens search and the command palette.

## What it answers

| Question | Where |
|---|---|
| Which components exist, with what parameters, cascading values, state and services? | Components |
| Why did this component render, and how many times / how long? | Components → Rendering, Profiler |
| What happened when I clicked, and what did it cause (renders, HTTP, JS, state)? | Timeline (select an event: *caused by* / *caused*) |
| What changed in my state container and which click did it? | State (history with before/after diffs) |
| Which HTTP requests ran, how long, which failed, what triggered them? | Network |
| Which JS interop calls are slow or excessive, and from which component? | JS Interop |
| Where did this exception come from and what preceded it? | Errors |
| What is registered in DI, with which lifetimes, dependencies and mistakes? | Services |
| Is my circuit blocked, reconnecting, dying? | Circuit (Server), Browser |
| What is DevTools itself costing me? | About → overhead |

Diagnostics rules turn symptoms into causes with evidence, e.g. *"ProductList rendered 87 times in 2 s: 81 × parent
re-render (Products), 6 × UI event (oninput → FilterBar.OnQuery). Consider ShouldRender…"*.

## Honest capability model

DevTools never fakes data. Every piece of information is classified in the About panel and in
[docs/capabilities.md](docs/capabilities.md) as *Available*, *Partially available*, *Requires instrumentation*,
*Requires application integration*, *Requires runtime support* (.NET 10 activities/metrics), *Not technically
reliable* or *Not available*, with the reason.

How the information is obtained, in short:

* **Component instances**: the public `IComponentActivator` hook. No reflection, no source changes.
* **Hierarchy, batches, render timing**: cached reflection accessors over `ComponentBase` / `Renderer` internals
  (public `ComponentState` since .NET 8). Every accessor is optional; when a member is missing the capability is reported
  as unavailable instead of crashing.
* **UI events, navigation, lifecycle and diff timings**: the framework's own `Microsoft.AspNetCore.Components`
  ActivitySource and meters (.NET 10). `AddBlazorDevTools` registers them through the public
  `ComponentsMetricsServiceCollectionExtensions.AddComponentsTracing/AddComponentsMetrics`, because outside
  server-side rendering the framework does not register them itself — without that, WebAssembly could not answer
  *why did this render?* at all. Turn it off with `options.UseFrameworkInstrumentation = false`.
* **HTTP**: `IHttpMessageHandlerBuilderFilter` (all `IHttpClientFactory` clients). Bodies are never captured.
* **JS interop**: `IJSRuntime` injected into components is wrapped on the first render (calls made earlier, from
  `OnInitialized`, are not attributed); `IJSRuntime` is *not* decorated in DI because the framework casts it to its
  concrete type.
* **Errors**: render wrapper + Blazor's own logger categories + browser `error`/`unhandledrejection`.
* **Circuits**: `CircuitHandler` (lifecycle, connection state, inbound message processing time) +
  `components:reconnect-state-changed` in the browser.

## Security

Development-only by default. Member, header, query and storage names matching sensitive patterns (`password`, `token`,
`authorization`, `cookie`, `connectionstring`, …) are redacted; `[DevToolsSensitive]` redacts explicitly;
`[DevToolsIgnore]` hides members or whole components. Request/response bodies and DI instance values are never read.
Circuit ids are truncated. Nothing is sent anywhere: all data stays in the session's memory and is bounded by ring
buffers (`MaxEvents`, `MaxRendersPerComponent`, …).

## Extending

```csharp
public sealed class MyLibraryDevTools : IDevToolsExtension
{
    public string Name => "MyLibrary";
    public void Configure(IDevToolsExtensionBuilder b) => b
        .AddPanel(new DevToolsPanelDescriptor("mylib", "MyLibrary", typeof(MyPanel)))
        .AddStateProvider(sp => new StoreAdapter(sp.GetRequiredService<MyStore>()))
        .AddDiagnosticRule(new MyRule())
        .AddCommand(new MyCommand())
        .AddValueFormatter(typeof(Money), v => ((Money)v).ToString("C"))
        .HideComponentsInNamespace("MyLibrary.Internal");
}

services.AddBlazorDevTools(o => o.Extensions.Add(new MyLibraryDevTools()));
```

Application code can also emit timeline events through `IDevToolsTimeline`, expose state with `IStateProvider`
(or `services.AddDevToolsStateProvider<T>()`), and attribute background work with `session.UseAmbient()`.
The `BlazorDevTools.Server` package is itself an extension (Circuit panel, circuit rules, command).

## Repository layout

```
src/BlazorDevTools.Abstractions   public integration API (events, state providers, rules, commands, panels, capabilities)
src/BlazorDevTools                instrumentation, session stores, diagnostics, inspection, DevTools UI (Razor class library)
src/BlazorDevTools.Server         circuit handler, circuit registry, Circuit panel (reference extension)
Demo/BlazorDevTools.Demo          Blazor Web App (Interactive Auto) with a "Problems to diagnose" index
tests/BlazorDevTools.Tests        xunit + bUnit: instrumentation, tree, rendering, timeline, state, network, interop,
                                  diagnostics, DI graph, concurrency, overhead, memory bounds, scale, panel interaction,
                                  framework-diagnostics attribution, extensibility
tests/BlazorDevTools.Benchmarks   reproducible overhead measurement (dotnet run -c Release)
docs/                             capability matrix, architecture, performance
```

Run the demo: `dotnet run --project Demo/BlazorDevTools.Demo`, then open `/problems`.
Run the tests: `dotnet test`.

## Status

Targets .NET 10. The MVP covers the component tree and inspector, parameters, render tracking and profiler, event
timeline, error inspection, state inspection, network, JS interop, search/commands, DI inspection, circuit diagnostics
and the rules engine. See [docs/architecture.md](docs/architecture.md) for the design and the list of deliberate
non-goals (component ↔ DOM highlighting, request bodies, SignalR payloads).
