# Changelog

All notable changes to Blazor DevTools. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
the project uses [Semantic Versioning](https://semver.org/). Until 1.0 the public surface described in
[docs/architecture.md](docs/architecture.md) can still change in minor versions, and every such change is listed here.

## [Unreleased]

### Added

- **Forced re-render detection.** While a component is selected in the inspector (or `CaptureComponentStateOnRender`
  is on), its parameters and cascading values are compared between renders, two levels deep and including up to 25
  collection items. A render forced by the parent whose observable parameter values did not change is marked *forced*
  in the inspector's render list, counted in *Rendering → Forced re-renders*, and exposed to rules and extensions as
  `changedParameters` / `parametersUnchanged` in the render event data (`RenderSample.ChangedParameters`,
  `RenderSample.IsWastedRender`, `ComponentRecord.ParentRendersWithUnchangedParameters`,
  `ComponentRecord.RendersWithComparedParameters`, `ComponentSnapshot.ParentRendersWithUnchangedParameters`). Render
  fragments are compared by instance, and a fragment that reads component state as it runs is never judged unchanged,
  so new child content is never called forced. The *Renders driven only by the parent* rule escalates to a warning
  when every compared render was forced, and says how to obtain the evidence when none was compared.
- **Pause / resume capture** (`DevToolsSession.PauseCapture()` / `ResumeCapture()`, header button, `capture.toggle`
  command). While paused nothing is recorded — renders, timeline, HTTP, JS interop, state changes — so what is on
  screen cannot be evicted by the application; errors are still collected, including failed HTTP requests and JS
  interop calls. Renders that happen while paused still
  count as "has rendered", so the first recorded render afterwards is not mislabeled as the initial one, and the
  resume marker says how many were skipped.
- **Profiler baseline** (`ComponentRegistry.MarkBaseline()`, *Reset* button, `profiler.reset` command). Render counts
  and times restart from zero for every component while the raw totals keep running; the profiler and the inspector
  show the *since reset* figures on request. Reset → interact once → read what rendered.
- **Profiler grouped by type.** One row per component type with instance count, total renders, total/avg/max time
  and the cause breakdown across all instances; clicking a type row drills down to its instances.
- **Session export.** `SessionExporter.ToJson` writes everything a session captured (components, timeline, errors,
  HTTP, JS interop, state history, diagnostics, capabilities, overhead, options) as `blazor-devtools/1` JSON, and
  `SessionExporter.ToMarkdownReport` produces a short Markdown summary for issues. Both are available from the About
  panel and the command palette (`session.export`, `session.report`) and contain only what the panels show, with the
  same redaction. `IDevToolsCommandContext` gained `CopyTextAsync` and `DownloadTextAsync` (default implementations
  return `false`) so extension commands can use the browser bridge too.
- The component tree is an accessible tree (`role="tree"`/`treeitem`, `aria-level`, `aria-expanded`,
  `aria-selected`) and shows the render total of a collapsed subtree.
- Repository infrastructure: GitHub Actions CI (build with warnings as errors, .NET and browser-bridge tests on
  Linux and Windows, pack, benchmark smoke run), `Directory.Build.props`, central package management
  (`Directory.Packages.props`), `.editorconfig`, `.gitattributes`, this changelog and `CONTRIBUTING.md`.

### Fixed

- A finding whose severity rises (for example the parent-driven rule turning into a warning once parameter comparison
  proves the renders wasted) now gets its own timeline row, marked `escalatedFrom`, and reappears if the weaker
  version was dismissed. It used to overwrite the existing finding silently.
- `ComponentRecord.RendersByCause(cause, sinceBaseline)` counts renders per cause for the whole session. The
  inspector's *Why did it render?* table and the profiler's grouped *Causes* column used to count only the samples
  still in the per-instance ring (`MaxRendersPerComponent`), so they disagreed with the render totals beside them.
- `[DevToolsSensitive]` on a component **field** was not honoured by the per-render state diff: only the name
  patterns were applied, so an attributed field with an innocuous name showed its before/after values in *changed
  before last render* and in the render event data. The member is now passed to the inspector, so the attribute
  redacts there as everywhere else.

### Changed

- `ServiceGraph.Analyze()` results are computed once per graph (`ServiceGraph.Findings`); the Services panel used to
  re-run cycle detection on every refresh while a service was selected. The panel also caches the injected property
  types per component type instead of reflecting over every live component on every refresh.
- `DevToolsPanel.ExtensionPanels` is cached per platform instead of allocating on every access.
- Removed unused internals (`RingBuffer.TryUpdateByKey`, `RingBuffer.Resize`, `RenderEventData.Keys2`).

## [0.1.0]

Initial release: component tree and inspector, render profiler with causes, unified timeline, state inspection with
diffs, network and JS interop tracking, error center, DI inspector, circuit diagnostics for Blazor Server, diagnostics
rules, command palette, capability report and overhead measurement.
