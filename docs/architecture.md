# Architecture

## Goals that shaped the design

1. **No application source changes** for the core experience (tree, renders, events, HTTP, errors).
2. **Nothing faked.** Every number has a documented source; gaps are labeled in the UI (see `capabilities.md`).
3. **Minimal impact on the inspected app**: bounded buffers, a fixed and measured per-render cost (one render sample
   plus one timeline event, no string formatting on the hot path), lazy inspection, a polling UI that re-renders only
   when a version counter changed, and its own cost measured and reported. See [performance.md](performance.md).
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
| `RenderTracker` | Replaces `ComponentBase._renderFragment` with a measuring wrapper (`FieldInfo.SetValue` on the readonly field). | Measures `BuildRenderTree`, detects batch boundaries through `RenderBatchBuilder.UpdatedComponentDiffs.Count`, attributes causes, captures optional field diffs, and patches `[Inject] IJSRuntime` on the first render (the earliest point after the framework injects it). Batch diff measurements that arrive late — on Blazor Server the framework records them when the browser acknowledges the render — are matched back to the batch that produced them through a queue of batches still awaiting one. |
| `BlazorReflection` | Expression-compiled accessors created once; each is null when the member is missing. | The capability report reflects which accessors work. |
| `ActivityObserver` | Static `ActivityListener` on `Microsoft.AspNetCore.Components` and `MeterListener` on the two components meters. `AddBlazorDevTools` also registers the framework's own `ComponentsActivitySource`/`ComponentsMetrics` (public `AddComponentsTracing`/`AddComponentsMetrics`), which only server-side rendering registers on its own. | Events are attached to activities via `Activity.SetCustomProperty`; sessions are resolved with `SessionResolver`. The framework tags its activities on *stop*, so the component type is read from the display name while the handler is still running and corrected from the tags afterwards. Interactions with the DevTools UI itself are recognised and never recorded. |
| `SessionResolver` | Ambient session lookup: explicit ambient scope → the renderer synchronization context (an O(1) map, which is the common case) → `Dispatcher.CheckAccess()` over live sessions → the only live session, **never on Blazor Server**. | Lets process-wide hooks (HTTP handler pool, logger provider, meters) find the right session without per-session registration. The Server exclusion is deliberate: "the only circuit connected right now" is not evidence that background work belongs to that user. |
| `DevToolsHttpMessageHandler` + `DevToolsHttpFilter` | `IHttpMessageHandlerBuilderFilter` appends the handler as the **innermost** `DelegatingHandler` of every `IHttpClientFactory` pipeline, next to the primary handler. | Every attempt a retry/resilience handler makes is a real network request and is recorded as one; sitting outermost would hide retries behind a single record. Handlers are pooled across scopes, hence session resolution at send time. |
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

`DevToolsSession` (scoped) owns: `Timeline` (ring buffer of `DevToolsEvent`, monotonic ids, O(log n) lookup by id),
`ComponentRegistry` (weak identity map + lazily resolved hierarchy + disposal detection + cached tree), `ErrorCenter` (fingerprint dedupe),
`HttpStore`, `JsInteropStore` (records + per-identifier aggregates), `StateStore` (providers, flattened snapshots,
diffs, history), `DiagnosticsEngine`, `CommandRegistry`, `DevToolsUiState`, `OverheadMeter`, `BrowserState`.
Every mutation bumps `Session.Version`; the UI polls the version on a timer (250 ms open, 1 s closed) and re-renders
only on change, so idle DevTools costs a comparison per tick. Opening the panel forces a refresh immediately rather
than waiting for the next tick.

Components are held **weakly**: DevTools must never keep an application component — and through it a subtree, its
services and its renderer — alive. Records are swept on component churn (at most once a second) rather than only when
the UI is open, so a session that never opens DevTools still releases what the application dropped. Disposed records
are kept for `DisposedComponentRetentionSeconds` so the timeline and errors can still name them, and the number of
tracked components is capped by `MaxTrackedComponents` with the truncation reported in the About panel.

### Inspection (`BlazorDevTools.Inspection`)

`ObjectInspector` describes values as `InspectedNode`s and resolves children by re-walking a path from the root, so
nothing is serialized eagerly. It handles cycles along the expansion path, throwing getters, depth/size caps, special
types (delegates, `RenderFragment`, `EventCallback`, tasks, streams, service providers), extension formatters and
redaction (`Redactor`: name patterns + `[DevToolsSensitive]`). `Flatten`/`Diff` produce bounded path→display maps for
state and component-field change detection.

### Diagnostics (`BlazorDevTools.Diagnostics`)

Rules implement `IDiagnosticRule.Evaluate(IDiagnosticContext)` over component snapshots and recent events; findings are
deduplicated by fingerprint, dismissable and mirrored into the timeline once. Built-ins: render storm (with cause
breakdown), renders driven only by the parent (the steady avoidable re-render that no rate threshold catches), render
cascade, slow render, slow event handler, duplicate HTTP, slow HTTP, repeated HTTP failure, slow JS interop, excessive
JS interop, repeated error; the server package adds circuit instability/blocking; the DI
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

## API surface and stability

Three tiers, because a developer tool that breaks its integrators is not infrastructure:

| Tier | What | Promise |
|---|---|---|
| **Contract** — `BlazorDevTools.Abstractions` | `IDevToolsExtension`, `IDiagnosticRule`, `IDevToolsCommand`, `IStateProvider`, `IDevToolsTimeline`, `DevToolsEvent`, `Diagnostic`, `ComponentSnapshot`, the attributes. No Blazor dependency. | Treated as versioned public API. Additions are additive; removals are breaking changes. |
| **Setup** | `AddBlazorDevTools`, `AddBlazorDevToolsServer`, `DevToolsOptions`, `<DevToolsPanel />`. | Stable. Options gain properties; defaults change only with a reason stated in the changelog. |
| **Read models** — `BlazorDevTools.Model`, `BlazorDevTools.Session`, `ObjectInspector`, `Fmt`, `ValueTree` | What the built-in panels read and render. Public so an extension panel can present the same data the same way. | Usable, but shaped by the UI's needs; expect additive change and occasional refinement. |

Razor components other than `DevToolsPanel` are public because the Razor SDK makes them so, not because they are a
supported surface. Depend on `ValueTree`, `InspectorSection` and `Fmt` if they help; treat the panels themselves as
internal.

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
* Scale: a thousand components, a 50 000-deep parent chain, render storms, a full event buffer, repeated create/destroy.
* UI: every panel opens, the component tree selects, the browser bridge reads storage (bUnit with a mocked module).
* Framework diagnostics: the exact activity, meter and tag names of `Microsoft.AspNetCore.Components`, so a rename
  breaks a test instead of silently degrading every render cause to *StateHasChanged*.
* Isolation and security: sessions never see each other, server-side background HTTP is not attributed to a circuit,
  a disabled or non-Development host registers nothing, the DI inspector never reads instance values.
* Extensibility: one third-party-style extension contributing a panel, rule, command, state provider and formatter.
* Non-functional: parallel writers, session churn, bounded buffers, per-render overhead ceiling, disabled-mode footprint,
  plus a reproducible benchmark (`tests/BlazorDevTools.Benchmarks`).
