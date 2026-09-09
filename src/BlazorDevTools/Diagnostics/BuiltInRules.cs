using BlazorDevTools.Events;

namespace BlazorDevTools.Diagnostics;

internal static class BuiltInRules
{
    public static IEnumerable<IDiagnosticRule> Create() =>
    [
        new RenderStormRule(),
        new RenderCascadeRule(),
        new SlowRenderRule(),
        new SlowEventHandlerRule(),
        new DuplicateHttpRule(),
        new SlowHttpRule(),
        new RepeatedHttpFailureRule(),
        new SlowJsInteropRule(),
        new ExcessiveJsInteropRule(),
        new RepeatedErrorRule(),
    ];

    private static DiagnosticsOptions Opt(IDiagnosticContext ctx) => ctx.GetOptions<DiagnosticsOptions>() ?? new DiagnosticsOptions();

    private static string Ms(double value) => value.ToString("0.#") + " ms";

    /// <summary>Many renders of one component in a short window. Evidence includes the cause breakdown so the developer knows where to look.</summary>
    private sealed class RenderStormRule : IDiagnosticRule
    {
        public string Id => "render.storm";

        public string Title => "Render storm";

        public string Description => "A component rendered many times within a short window.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            var window = TimeSpan.FromMilliseconds(options.RenderStormWindowMs);
            var renders = context.EventsInWindow(DevToolsEventKind.Render, window).Where(e => e.ComponentInstanceId is not null).ToList();
            foreach (var group in renders.GroupBy(e => e.ComponentInstanceId!.Value))
            {
                var count = group.Count();
                if (count < options.RenderStormCount)
                {
                    continue;
                }

                var name = group.First().ComponentName ?? "component";
                var causes = group.GroupBy(e => e.Data?["cause"]?.ToString() ?? "Unknown").OrderByDescending(g => g.Count()).ToList();
                var breakdown = string.Join("; ", causes.Select(c => $"{c.Count()} × {Describe(c.Key, c)}"));
                var totalMs = group.Sum(e => e.DurationMs ?? 0);
                var suggestion = causes[0].Key switch
                {
                    "ParentRender" => "The parent re-renders and re-applies parameters. Override ShouldRender, pass stable (memoized) parameter values, or move fast-changing state out of the parent.",
                    "UiEvent" => "Each event handler triggers a render. Debounce the input or batch updates.",
                    "StateHasChanged" => "Renders come from StateHasChanged outside events: timers, state-container notifications or async continuations. Check the subscription that calls StateHasChanged.",
                    _ => "Inspect the render causes in the Profiler panel.",
                };
                yield return new Diagnostic(
                    Id,
                    DiagnosticSeverity.Warning,
                    $"{name} rendered {count} times in {options.RenderStormWindowMs / 1000.0:0.#} s",
                    $"{breakdown}. Total build time {Ms(totalMs)}.",
                    suggestion,
                    Fingerprint: $"{Id}:{group.Key}",
                    ComponentInstanceId: group.Key,
                    RelatedEventIds: group.Take(20).Select(e => e.Id).ToList());
            }
        }

        private static string Describe(string cause, IEnumerable<DevToolsEvent> events) => cause switch
        {
            "ParentRender" => "parent re-render (" + (events.Select(e => e.Detail).FirstOrDefault(d => d is not null)?.Replace(" re-rendered (parameters re-applied)", "") ?? "?") + ")",
            "UiEvent" => "UI event (" + string.Join(", ", events.Select(e => e.Detail?.Replace("Event: ", "")).Where(d => d is not null).Distinct().Take(3)) + ")",
            "StateHasChanged" => "StateHasChanged outside an event",
            "Initial" => "first render",
            "Navigation" => "navigation",
            _ => cause,
        };
    }

    private sealed class RenderCascadeRule : IDiagnosticRule
    {
        public string Id => "render.cascade";

        public string Title => "Render cascade";

        public string Description => "One render batch re-rendered a large number of components.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var batch in context.EventsInWindow(DevToolsEventKind.RenderBatch, TimeSpan.FromSeconds(30)))
            {
                if (batch.Data is null || !batch.Data.TryGetValue("count", out var countObj) || countObj is not int count || count < options.RenderCascadeSize)
                {
                    continue;
                }

                var renders = context.Events.Where(e => e.Kind == DevToolsEventKind.Render && e.ParentEventId == batch.Id).ToList();
                var root = context.Events.FirstOrDefault(e => e.Id == batch.ParentEventId);
                var first = renders.FirstOrDefault();
                yield return new Diagnostic(
                    Id,
                    DiagnosticSeverity.Warning,
                    $"Render batch touched {count} components",
                    (root is not null ? $"Triggered by {root.Title}. " : "") + (first is not null ? $"Started with {first.ComponentName}. " : "") + $"Building took {Ms(batch.DurationMs ?? 0)}.",
                    "A cascade usually means a high-level component (layout, cascading value provider) re-rendered. Narrow what changes: cascade immutable values, or give children ShouldRender/keyed parameters.",
                    Fingerprint: $"{Id}:{batch.Id}",
                    RelatedEventIds: [batch.Id]);
            }
        }
    }

    private sealed class SlowRenderRule : IDiagnosticRule
    {
        public string Id => "render.slow";

        public string Title => "Slow render";

        public string Description => "A component's BuildRenderTree takes long.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var component in context.Components)
            {
                if (component.IsDisposed || component.RenderCount == 0)
                {
                    continue;
                }

                if (component.MaxRenderMs >= options.SlowRenderMs && (component.RenderCount >= 3 ? component.AverageRenderMs >= options.SlowRenderMs / 4 : true))
                {
                    yield return new Diagnostic(
                        Id,
                        DiagnosticSeverity.Warning,
                        $"{component.DisplayName} renders slowly (max {Ms(component.MaxRenderMs)})",
                        $"{component.RenderCount} renders, average {Ms(component.AverageRenderMs)}, total {Ms(component.TotalRenderMs)}. Measured time covers BuildRenderTree only, not DOM diffing.",
                        "Move work out of the render path: compute derived data in OnParametersSet, cache formatted values, virtualize large lists, and avoid LINQ over large collections inside markup.",
                        Fingerprint: $"{Id}:{component.InstanceId}",
                        ComponentInstanceId: component.InstanceId);
                }
            }
        }
    }

    private sealed class SlowEventHandlerRule : IDiagnosticRule
    {
        public string Id => "event.slow";

        public string Title => "Slow event handler";

        public string Description => "An event handler blocked the renderer for long.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var evt in context.EventsInWindow(DevToolsEventKind.UiEvent, TimeSpan.FromSeconds(60)))
            {
                if (evt.DurationMs is { } d && d >= options.SlowEventHandlerMs)
                {
                    yield return new Diagnostic(
                        Id,
                        DiagnosticSeverity.Warning,
                        $"{evt.Title} took {Ms(d)}",
                        "The duration spans the handler and every await inside it; while it runs no other work for this session is processed.",
                        "Move CPU work off the renderer with Task.Run (Server) or split awaited steps so intermediate renders can flush.",
                        Fingerprint: $"{Id}:{evt.Id}",
                        RelatedEventIds: [evt.Id]);
                }
            }
        }
    }

    private sealed class DuplicateHttpRule : IDiagnosticRule
    {
        public string Id => "http.duplicate";

        public string Title => "Duplicate HTTP requests";

        public string Description => "The same request was issued repeatedly within a short window.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            var window = TimeSpan.FromMilliseconds(options.DuplicateHttpWindowMs);
            var requests = context.EventsInWindow(DevToolsEventKind.Http, TimeSpan.FromSeconds(30)).ToList();
            foreach (var group in requests.GroupBy(e => e.Title))
            {
                var ordered = group.OrderBy(e => e.StartTicks).ToList();
                for (var i = 0; i + options.DuplicateHttpCount - 1 < ordered.Count; i++)
                {
                    var last = ordered[i + options.DuplicateHttpCount - 1];
                    if ((last.StartTicks - ordered[i].StartTicks) / (double)System.Diagnostics.Stopwatch.Frequency <= window.TotalSeconds)
                    {
                        var triggers = ordered.Skip(i).Take(options.DuplicateHttpCount).Select(e => context.Events.FirstOrDefault(x => x.Id == e.ParentEventId)?.Title).Where(t => t is not null).Distinct().ToList();
                        yield return new Diagnostic(
                            Id,
                            DiagnosticSeverity.Warning,
                            $"{group.Key} issued {options.DuplicateHttpCount}+ times within {options.DuplicateHttpWindowMs} ms",
                            $"{ordered.Count} requests in the last 30 s." + (triggers.Count > 0 ? " Triggered by: " + string.Join(", ", triggers.Take(3)) + "." : " Not attributable to a UI event (lifecycle or timer)."),
                            "Cache the response, dedupe in-flight requests, or move the call from OnParametersSet to OnInitialized when parameters do not affect it.",
                            Fingerprint: $"{Id}:{group.Key}",
                            RelatedEventIds: ordered.Skip(i).Take(options.DuplicateHttpCount).Select(e => e.Id).ToList());
                        break;
                    }
                }
            }
        }
    }

    private sealed class SlowHttpRule : IDiagnosticRule
    {
        public string Id => "http.slow";

        public string Title => "Slow HTTP request";

        public string Description => "An HTTP request exceeded the slow threshold.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var evt in context.EventsInWindow(DevToolsEventKind.Http, TimeSpan.FromSeconds(60)))
            {
                if (evt.DurationMs is { } d && d >= options.SlowHttpMs)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Info, $"{evt.Title} took {Ms(d)}", evt.Detail ?? "", "Show a loading state, cancel superseded requests, or paginate the endpoint.", Fingerprint: $"{Id}:{evt.Id}", RelatedEventIds: [evt.Id]);
                }
            }
        }
    }

    private sealed class RepeatedHttpFailureRule : IDiagnosticRule
    {
        public string Id => "http.failures";

        public string Title => "Repeated HTTP failures";

        public string Description => "The same request keeps failing.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            foreach (var group in context.EventsInWindow(DevToolsEventKind.Http, TimeSpan.FromMinutes(2)).Where(e => e.Severity == DevToolsSeverity.Error).GroupBy(e => e.Title))
            {
                var count = group.Count();
                if (count >= 3)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Error, $"{group.Key} failed {count} times", string.Join("; ", group.Select(e => e.Detail).Distinct().Take(3)), "Check the endpoint and add retry/backoff only for transient failures.", Fingerprint: $"{Id}:{group.Key}", RelatedEventIds: group.Take(10).Select(e => e.Id).ToList());
                }
            }
        }
    }

    private sealed class SlowJsInteropRule : IDiagnosticRule
    {
        public string Id => "jsinterop.slow";

        public string Title => "Slow JS interop";

        public string Description => "A JS interop call exceeded the slow threshold.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var evt in context.EventsInWindow(DevToolsEventKind.JsInterop, TimeSpan.FromSeconds(60)))
            {
                if (evt.DurationMs is { } d && d >= options.SlowJsInteropMs)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Warning, $"{evt.Title} took {Ms(d)}", (evt.ComponentName is null ? "" : "From " + evt.ComponentName + ". ") + "On Server every interop call is a SignalR round trip.", "Batch several calls into one JS function, or move the work to JS entirely.", Fingerprint: $"{Id}:{evt.Id}", ComponentInstanceId: evt.ComponentInstanceId, RelatedEventIds: [evt.Id]);
                }
            }
        }
    }

    private sealed class ExcessiveJsInteropRule : IDiagnosticRule
    {
        public string Id => "jsinterop.excessive";

        public string Title => "Excessive JS interop";

        public string Description => "The same JS function was called very frequently.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var group in context.EventsInWindow(DevToolsEventKind.JsInterop, TimeSpan.FromSeconds(1)).GroupBy(e => e.Title))
            {
                var count = group.Count();
                if (count >= options.ExcessiveJsInteropCount)
                {
                    var components = group.Select(e => e.ComponentName).Where(n => n is not null).Distinct().ToList();
                    yield return new Diagnostic(Id, DiagnosticSeverity.Warning, $"{group.Key} called {count} times in 1 s", components.Count > 0 ? "From " + string.Join(", ", components) + "." : "Origin not attributable to a component.", "Throttle, batch arguments into one call, or keep the loop in JavaScript.", Fingerprint: $"{Id}:{group.Key}", RelatedEventIds: group.Take(10).Select(e => e.Id).ToList());
                }
            }
        }
    }

    private sealed class RepeatedErrorRule : IDiagnosticRule
    {
        public string Id => "error.repeated";

        public string Title => "Repeated error";

        public string Description => "The same error occurred several times.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var options = Opt(context);
            foreach (var group in context.EventsInWindow(DevToolsEventKind.Error, TimeSpan.FromMinutes(5)).Where(e => !e.Category.StartsWith("log:", StringComparison.Ordinal)).GroupBy(e => e.Title.Replace("(repeat) ", "")))
            {
                var count = group.Count();
                if (count >= options.RepeatedErrorCount)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Error, $"{group.Key} occurred {count} times", group.First().Detail ?? "", "Open the Errors panel to see the preceding events for each occurrence.", Fingerprint: $"{Id}:{group.Key}", ComponentInstanceId: group.First().ComponentInstanceId, RelatedEventIds: group.Take(10).Select(e => e.Id).ToList());
                }
            }
        }
    }
}
