# Architecture

## Goals that shaped the design

1. **No application source changes** for the core experience (tree, renders, events, HTTP, errors).
2. **Nothing faked.** Every number has a documented source; gaps are labeled in the UI (see `capabilities.md`).
3. **Minimal impact on the inspected app**: bounded buffers, no per-render allocations beyond one sample object, lazy
   inspection, polling UI that re-renders only when a version counter changed, own cost measured.
4. **Session isolation**: one `DevToolsSession` per circuit (Server), per app (WebAssembly), per request (static SSR).
5. **Extensible**: panels, state providers, rules, commands and formatters through `BlazorDevTools.Abstractions`.

## Layers

```
BlazorDevTools.Abstractions        public contracts, no Blazor dependency
BlazorDevTools                     runtime instrumentation · session stores · inspection · diagnostics · UI
BlazorDevTools.Server              circuit instrumentation (reference extension)
```

### Runtime instrumentation (`BlazorDevTools.Instrumentation`)

| Piece | Mechanism | Notes |
|---|---|---|
| `DevToolsComponentActivator` | Public `IComponentActivator` registered in DI (scoped). Wraps a previously registered activator (custom ones, bUnit) as inner. | Registers a `ComponentRecord` and instruments the instance. Types under `[DevToolsIgnore]` or matching ignore predicates are skipped. |
| `RenderTracker` | Replaces `ComponentBase._renderFragment` with a measuring wrapper (`FieldInfo.SetValue` on the readonly field). | Measures `BuildRenderTree`, detects batch boundaries through `RenderBatchBuilder.UpdatedComponentDiffs.Count`, attributes causes, captures optional field diffs, patches `[Inject] IJSRuntime` before the first render. |
| `BlazorReflection` | Expression-compiled accessors created once; each is null when the member is missing. | The capability report reflects which accessors work. |
| `ActivityObserver` | Static `ActivityListener` on `Microsoft.AspNetCore.Components` and `MeterListener` on the components meters. | Events are attached to activities via `Activity.SetCustomProperty`; sessions are resolved with `SessionResolver`. |
| `SessionResolver` | Ambient session lookup: explicit ambient scope → `Dispatcher.CheckAccess()` over live sessions → single live session. | Lets process-wide hooks (HTTP handler pool, logger provider, meters) find the right session without per-session registration. |
| `DevToolsHttpMessageHandler` + `DevToolsHttpFilter` | `IHttpMessageHandlerBuilderFilter` inserts the handler as the outermost `DelegatingHandler` of every `IHttpClientFactory` pipeline. | Handlers are pooled across scopes, hence session resolution at send time. |
| `TrackingJSRuntime` / `TrackingJSObjectReference` | Decorators with in-process variants so `IJSInProcessRuntime` casts keep working. | Applied per component; global DI decoration is unsafe because `CircuitFactory` casts `IJSRuntime` to `RemoteJSRuntime`. |
| `DevToolsLoggerProvider` | `ILoggerProvider` capturing Warning+ from `Microsoft.AspNetCore.Components*`, `Microsoft.JSInterop`, SignalR. | Catches unhandled circuit exceptions, event-handler exceptions and ErrorBoundary logs without touching the renderer. |
| Browser bridge (`devtools.js`) | ES module imported by the panel; `DotNetObjectReference` callbacks. | JS errors, online/visibility, reconnect state, storage inventory on request, JS→.NET call batching, toggle shortcut. |

### Why render causes are trustworthy

A render sample's cause is decided in this order, and the label says which rule matched:

1. First render → *Initial*.
2. An ancestor rendered earlier **in the same batch** → *Parent re-rendered (parameters re-applied)*. This is exactly
   what the renderer does: the parent's diff calls `SetParametersAsync` on the child, which queues the child in the same
   batch.
3. A `HandleEvent`/`Navigate` activity is current on the session → *Event: onclick → Type.Method* / *Navigation*.
4. Otherwise → *StateHasChanged outside an event*: timer, async continuation, state-container notification or explicit
   call. DevTools does not walk the stack to name the caller; the timeline's *Just before* section shows the surrounding
   activity instead.

### Session stores (`BlazorDevTools.Session`)

`DevToolsSession` (scoped) owns: `Timeline` (ring buffer of `DevToolsEvent`, monotonic ids), `ComponentRegistry`
(identity map + lazily resolved hierarchy + disposal detection + cached tree), `ErrorCenter` (fingerprint dedupe),
`HttpStore`, `JsInteropStore` (records + per-identifier aggregates), `StateStore` (providers, flattened snapshots,
diffs, history), `DiagnosticsEngine`, `CommandRegistry`, `DevToolsUiState`, `OverheadMeter`, `BrowserState`.
Every mutation bumps `Session.Version`; the UI polls the version on a timer (250 ms open, 1 s closed) and re-renders
only on change, so idle DevTools costs a comparison per tick.

### Inspection (`BlazorDevTools.Inspection`)

`ObjectInspector` describes values as `InspectedNode`s and resolves children by re-walking a path from the root, so
nothing is serialized eagerly. It handles cycles along the expansion path, throwing getters, depth/size caps, special
types (delegates, `RenderFragment`, `EventCallback`, tasks, streams, service providers), extension formatters and
redaction (`Redactor`: name patterns + `[DevToolsSensitive]`). `Flatten`/`Diff` produce bounded path→display maps for
state and component-field change detection.

### Diagnostics (`BlazorDevTools.Diagnostics`)

Rules implement `IDiagnosticRule.Evaluate(IDiagnosticContext)` over component snapshots and recent events; findings are
deduplicated by fingerprint, counted, dismissable and mirrored into the timeline once. Built-ins: render storm (with
cause breakdown), render cascade, slow render, slow event handler, duplicate HTTP, slow HTTP, repeated HTTP failure,
slow JS interop, excessive JS interop, repeated error; the server package adds circuit instability/blocking; the DI
graph adds captive dependency, cycle, transient-disposable and large-constructor findings. Thresholds live in
`DiagnosticsOptions`; rules can be disabled by id.

### UI (`BlazorDevTools.UI`)

`DevToolsPanel` is the only public component. It renders nothing during prerendering or when disabled, cascades itself
as the `IDevToolsCommandContext` to panels, hosts the refresh loop and the browser bridge, and persists preferences in
`localStorage`. Panels are plain Blazor components reading the session directly; lists use `Virtualize`; the tree and
command palette are keyboard-navigable; theme follows `prefers-color-scheme` unless overridden. Extension panels are
rendered with `DynamicComponent` and can be restricted to a platform (`RequiredPlatform = "Server"`).

### Multi-circuit behaviour (Server)

Everything per circuit is scoped. Process-wide pieces (activity/meter listeners, HTTP handler pool, logger provider,
`CircuitRegistry`) attribute work to a session through `SessionResolver`; when the session cannot be inferred (background
threads with several live circuits) the data is dropped rather than misattributed, and `session.UseAmbient()` lets
application code attribute such work explicitly. Other circuits are visible only as aggregates (truncated id, state,
counts).

## Deliberate non-goals

* Component ↔ DOM highlighting (no reliable mapping from .NET).
* Request/response bodies, DI instance values, full circuit ids (security).
* Time-travel state restoration (unsafe for arbitrary graphs); snapshot history is provided instead.
* Recreating browser DevTools panels (network waterfall, DOM, WebSocket frames).

## Testing strategy

* Unit: ring buffer, timeline, inspector (cycles, redaction, caps, diffs), redactor, command matching, DI graph analysis,
  diagnostics rules with synthetic events, HTTP handler with stub pipelines, JS interop wrappers with fakes, state store.
* Instrumentation through a real renderer (bUnit): registration, hierarchy, render counts, causes, batches, state diffs,
  disposal detection, JS runtime patching, render exceptions, session resolution from the dispatcher.
* Non-functional: parallel writers, session churn, bounded buffers, per-render overhead ceiling, disabled-mode footprint.
