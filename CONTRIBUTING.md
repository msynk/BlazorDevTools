# Contributing

Thanks for looking into Blazor DevTools. This page is short on purpose: the design constraints matter more than the
process.

## Principles that every change must respect

1. **Never fake data.** If something cannot be observed reliably, show that it cannot (see `CapabilityReport`), do
   not approximate it. Every number in the UI has a documented source.
2. **Never observe yourself.** DevTools' own components, events and interop calls must not appear as application
   activity. `PanelInteractionTests.DevTools_own_ui_never_appears_as_application_activity` guards this.
3. **Never leak secrets.** Redaction happens at capture time (`Redactor`, `[DevToolsSensitive]`, browser-side storage
   redaction). Anything that displays or exports data must go through the same paths — exports may never show more
   than the panels do.
4. **Bounded and cheap.** Every buffer is a ring buffer; the per-render hot path (`RenderTracker.BeginRender` /
   `EndRender`) formats no strings and allocates one sample plus one event. Run the benchmark before and after touching
   it: `dotnet run -c Release --project tests/BlazorDevTools.Benchmarks`.
5. **Optional reflection.** Every accessor over Blazor internals lives in `BlazorReflection`, is null when the member
   is missing, and degrades to a reported capability gap rather than an exception.

## Working on the code

```
dotnet build BlazorDevTools.slnx -c Release      # warnings are errors
npm test                                         # .NET tests + browser-bridge tests, the same gate CI runs
dotnet run --project Demo/BlazorDevTools.Demo    # then open /problems
```

- Package versions live in `Directory.Packages.props`; shared build settings in `Directory.Build.props`.
- Code style is in `.editorconfig`: file-scoped namespaces, braces always, `_camelCase` private fields, comments that
  explain *why* rather than *what*.
- Public API tiers are described in [docs/architecture.md](docs/architecture.md#api-surface-and-stability). Anything
  in `BlazorDevTools.Abstractions` is a contract: additions only, never removals or signature changes without a major
  version.
- Add a line to [CHANGELOG.md](CHANGELOG.md) under *Unreleased* for anything a user or an integrator could notice.

## Tests

Every behaviour has a test at the level where it can fail for real:

- unit tests for stores, inspection, redaction, rules and matching;
- bUnit tests through a real renderer for instrumentation (hierarchy, causes, batches, diffs, disposal, JS wrapping);
- bUnit tests that click the DevTools UI itself (`PanelInteractionTests`, `ProfilerTests`);
- Node tests for the browser bridge (`tests/BlazorDevTools.BrowserTests`), which have no dependencies on purpose.

A test that asserts on the exact activity, meter and tag names of `Microsoft.AspNetCore.Components` is deliberate: a
framework rename should break a test, not silently degrade every render cause to *StateHasChanged*.

## Reporting a problem

Open DevTools in the affected app, reproduce, then use *About → Copy report (Markdown)* or *Export session (JSON)* and
attach the result. Both contain only what the panels show, with the same redaction, but read them before posting.
