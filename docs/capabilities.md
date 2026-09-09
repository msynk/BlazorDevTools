# Capability matrix

What Blazor DevTools can observe, how, and where it stops. The same matrix is rendered live in the About panel with
the runtime-verified status of each row (for example, whether HandleEvent activities have actually been observed on
this host). Nothing in the UI is approximated without saying so.

Legend: **Available** — works out of the box · **Partial** — works with stated gaps · **Instrumentation** — obtained by
DevTools instrumenting framework internals (dev-only reflection or wrappers) · **Integration** — the application or
library must call an API · **Runtime** — depends on framework support (.NET 10 activities/metrics) · **Unreliable** —
deliberately not shown as fact · **N/A** — not obtainable.

## Components

| Information | Status | How / why |
|---|---|---|
| Component instances (all render modes) | Available | Public `IComponentActivator` hook wraps any existing activator; every instance the renderer creates is registered. |
| Parent/child hierarchy | Instrumentation | `Renderer._componentStateByComponent` → public `ComponentState.LogicalParentComponentState` (.NET 8+). Cached expression-compiled accessor; reported unavailable if the member disappears. |
| Parameters, cascading values, `[SupplyParameterFrom*]` | Available | Read on demand from attributed properties; lazy inspection; redaction by name/attribute. |
| Component state (private fields) | Partial | Read on demand; diffed per render only for the selected component (or all with `CaptureComponentStateOnRender`). Mutations that do not lead to a render are not observed. |
| Injected services with lifetime | Available | `[Inject]` properties + captured DI registrations. |
| Source location | Partial | Inferred `TypeName.razor`; exact paths only inside exception stack traces (needs PDBs). |
| Disposal | Partial | Detected when the renderer no longer knows the instance (checked on refresh), so timing is approximate. |
| Component ↔ DOM element highlighting | N/A | Blazor exposes no component-to-DOM map to .NET; not implemented rather than guessed. |

## Rendering

| Information | Status | How / why |
|---|---|---|
| Render count, `BuildRenderTree` duration per instance | Instrumentation | The private `_renderFragment` of each `ComponentBase` is replaced with a measuring wrapper at creation. Components implementing `IComponent` directly are listed but not measured (shown as *not measured*). |
| Render batches (one DOM diff) | Instrumentation | Batch boundary = `RenderBatchBuilder.UpdatedComponentDiffs.Count == 0` at render start. Fallback: idle-time heuristic (flagged *Unreliable*). |
| Batch diff duration and DOM edit count | Runtime | `aspnetcore.components.render_diff.duration/size` meters (.NET 10, needs `IMeterFactory`). |
| Cause: parent re-render | Available* | An ancestor rendered earlier in the same batch (renderer re-applied parameters). *Needs hierarchy + batches. |
| Cause: UI event (with attribute, component, method) | Runtime | `Microsoft.AspNetCore.Components.HandleEvent` activity; DevTools adds an `ActivityListener`. Before the first activity is seen the About panel says so. |
| Cause: navigation | Partial | `NavigationManager.LocationChanged` always; `Navigate` activity when emitted. |
| Cause: explicit `StateHasChanged` / async continuation | Partial | Everything not attributable above. The caller is not identified (no stack walk by design). |
| `SetParametersAsync` cost (OnInitialized/OnParametersSet) | Runtime | `aspnetcore.components.update_parameters.duration`, per component type. |
| Event handler duration | Runtime | Activity duration; also `handle_event.duration` meter. |

## Events and timeline

| Information | Status |
|---|---|
| Unified timeline (renders, batches, events, navigation, state, HTTP, JS, errors, circuit, browser, diagnostics, custom) | Available |
| Cause/effect links (event → renders/HTTP/JS/state; parent render → child render) | Available where the cause is known (see above) |
| Bounded history with configurable size | Available (`MaxEvents`, ring buffer, oldest dropped) |
| Custom events from libraries | Integration (`IDevToolsTimeline`) |

## State

| Information | Status | How / why |
|---|---|---|
| Registered state containers, snapshots, change history, before/after diffs | Integration | Implement `IStateProvider` or register an adapter (`AddDevToolsStateProvider`). Diffs compare bounded flattened snapshots (depth 3, 25 items per collection). |
| Cascading values | Available | From `[CascadingParameter]` properties. |
| Time travel (restoring state) | N/A | Deliberately not implemented: restoring arbitrary object graphs is unsafe. History browsing of snapshots is available. |

## Network

| Information | Status | How / why |
|---|---|---|
| Requests via `IHttpClientFactory`: method, URL, status, duration, sizes, headers | Available | `IHttpMessageHandlerBuilderFilter` adds a `DelegatingHandler` to every pipeline. Sensitive headers/query values redacted. |
| Hand-built `HttpClient` | Integration | Add `DevToolsHttpMessageHandler` manually. |
| Bodies | N/A | Never captured. |
| Originating UI event | Partial | Linked when a HandleEvent activity is executing; lifecycle/timer requests show no trigger. |
| Browser-level waterfall, WebSocket frames | N/A | Use the browser's DevTools. |

## JS interop

| Information | Status | How / why |
|---|---|---|
| .NET → JS from components (identifier, duration, args types, result, errors, component) | Instrumentation | `[Inject] IJSRuntime` replaced with a tracking wrapper before the first render (after `OnInitialized`). Module references obtained through it are wrapped too. |
| .NET → JS from services | Integration | `jsRuntime.WithDevToolsTracking(session)`. Global decoration is unsafe (framework casts `IJSRuntime` to `RemoteJSRuntime`/`WebAssemblyJSRuntime`). |
| JS → .NET | Instrumentation | `DotNet.invokeMethodAsync/invokeMethod` wrapped by the browser bridge; reported in 500 ms batches. |

## Errors

| Information | Status |
|---|---|
| Render exceptions with component | Available (render wrapper) |
| Event handler / circuit / ErrorBoundary / JS interop failures | Available (Blazor's own logger categories at Warning+) |
| JS errors, unhandled promise rejections | Instrumentation (browser bridge) |
| HTTP failures | Available |
| Preceding UI event and last activity | Available |
| Source location | Partial (from stack trace when PDBs are present) |

## DI

| Information | Status | How / why |
|---|---|---|
| Registrations, lifetimes, implementation, keyed | Available | `IServiceCollection` captured at `AddBlazorDevTools` time (enumerated lazily, so later registrations appear). |
| Constructor dependencies, consumers, cycles, captive dependencies, large graphs | Available | Static analysis of implementation constructors. Factory/instance registrations have no visible dependencies. |
| Runtime resolutions | N/A | The container has no resolution events. |
| Instance values | N/A | Never read (configuration/secrets). |

## Circuit (Blazor Server)

| Information | Status | How / why |
|---|---|---|
| Lifecycle, connection up/down, disconnect count, duration | Instrumentation | `CircuitHandler` registered by `AddBlazorDevToolsServer`. |
| Inbound message count and processing time (slow messages flagged) | Instrumentation | `CircuitHandler.CreateInboundActivityHandler`. |
| Active circuits in the process (aggregates only) | Available | Singleton registry; ids truncated; other circuits' data stays private. |
| Reconnect attempts | Instrumentation | `components:reconnect-state-changed` DOM event. |
| Message sizes, latency, payloads | N/A | Not exposed by the framework. |

## Browser

| Information | Status |
|---|---|
| Online/offline, visibility, user agent, JS heap (Chromium), navigation timing, resource count | Instrumentation (bridge) |
| localStorage / sessionStorage inventory (on request, redacted previews) | Instrumentation (bridge) |
| IndexedDB, generic DOM events | N/A by design (no Blazor-specific value; use browser DevTools) |

## DevTools itself

| Information | Status |
|---|---|
| Overhead: bookkeeping per render, UI render time, inspection, diagnostics, tree refresh | Available (About panel) |
| Session isolation | Available (one session per circuit/app; static listeners route by dispatcher) |
