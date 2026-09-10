using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;

namespace BlazorDevTools.Tests;

public class DiagnosticsRulesTests
{
    private static DevToolsEvent Render(long componentId, string name, string cause, double ms = 1) => new()
    {
        Kind = DevToolsEventKind.Render,
        Category = "render",
        Title = name + " re-rendered",
        Detail = cause == "ParentRender" ? "Parent Dashboard re-rendered (parameters re-applied)" : "Event: onclick → Filter.Set",
        DurationMs = ms,
        ComponentInstanceId = componentId,
        ComponentName = name,
        Data = new Dictionary<string, object?> { ["cause"] = cause },
    };

    [Fact]
    public void Render_storm_reports_cause_breakdown_and_component()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.Diagnostics.RenderStormCount = 10);
        using (scope)
        {
            for (var i = 0; i < 9; i++)
            {
                session.Timeline.Record(Render(7, "ProductList", "ParentRender"));
            }

            for (var i = 0; i < 3; i++)
            {
                session.Timeline.Record(Render(7, "ProductList", "UiEvent"));
            }

            session.Diagnostics.Evaluate(force: true);
            var finding = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.storm");
            Assert.Equal(7, finding.Diagnostic.ComponentInstanceId);
            Assert.Contains("ProductList rendered 12 times", finding.Diagnostic.Title);
            Assert.Contains("9 × parent re-render", finding.Diagnostic.Evidence);
            Assert.Contains("3 × UI event", finding.Diagnostic.Evidence);
            Assert.Contains("ShouldRender", finding.Diagnostic.Suggestion);
        }
    }

    [Fact]
    public void Below_threshold_produces_no_storm_finding()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.Diagnostics.RenderStormCount = 10);
        using (scope)
        {
            for (var i = 0; i < 5; i++)
            {
                session.Timeline.Record(Render(1, "Calm", "UiEvent"));
            }

            session.Diagnostics.Evaluate(force: true);
            Assert.DoesNotContain(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.storm");
        }
    }

    [Fact]
    public void Duplicate_http_requests_are_detected_with_trigger_context()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.Diagnostics.DuplicateHttpCount = 3);
        using (scope)
        {
            var trigger = session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.UiEvent, Category = "ui-event", Title = "oninput → FilterBar.OnQuery" });
            for (var i = 0; i < 4; i++)
            {
                session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Http, Category = "http", Title = "GET /api/search", ParentEventId = trigger, DurationMs = 5 });
            }

            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Http, Category = "http", Title = "GET /api/other", DurationMs = 5 });
            session.Diagnostics.Evaluate(force: true);
            var finding = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "http.duplicate");
            Assert.Contains("GET /api/search", finding.Diagnostic.Title);
            Assert.Contains("oninput → FilterBar.OnQuery", finding.Diagnostic.Evidence);
        }
    }

    [Fact]
    public void Slow_and_excessive_js_interop_are_detected()
    {
        var (session, scope) = TestHelpers.CreateSession(o =>
        {
            o.Diagnostics.SlowJsInteropMs = 100;
            o.Diagnostics.ExcessiveJsInteropCount = 5;
        });
        using (scope)
        {
            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.JsInterop, Category = "js-interop", Title = "→ JS module.slowCall", DurationMs = 400, ComponentName = "Interop" });
            for (var i = 0; i < 6; i++)
            {
                session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.JsInterop, Category = "js-interop", Title = "→ JS module.ping", DurationMs = 1 });
            }

            session.Diagnostics.Evaluate(force: true);
            var findings = session.Diagnostics.Findings;
            Assert.Contains(findings, f => f.Diagnostic.RuleId == "jsinterop.slow" && f.Diagnostic.Evidence.Contains("Interop"));
            Assert.Contains(findings, f => f.Diagnostic.RuleId == "jsinterop.excessive" && f.Diagnostic.Title.Contains("module.ping"));
        }
    }

    [Fact]
    public void Repeated_errors_and_findings_are_deduplicated_across_evaluations()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.Diagnostics.RepeatedErrorCount = 2);
        using (scope)
        {
            session.Errors.Record(new InvalidOperationException("boom"), "render");
            session.Errors.Record(new InvalidOperationException("boom"), "render");
            session.Diagnostics.Evaluate(force: true);
            var first = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "error.repeated");
            session.Errors.Record(new InvalidOperationException("boom"), "render");
            session.Diagnostics.Evaluate(force: true);
            var again = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "error.repeated");
            Assert.Same(first, again);
            Assert.True(again.LastSeen >= again.FirstSeen);
            Assert.Contains("occurred 3 times", again.Diagnostic.Title);
            Assert.Single(session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.Diagnostic && e.Category == "diagnostic:error.repeated");
        }
    }

    [Fact]
    public void Disabled_rules_do_not_run_and_custom_rules_do()
    {
        var custom = new CountingRule();
        var (session, scope) = TestHelpers.CreateSession(o =>
        {
            o.Diagnostics.DisabledRules.Add("render.storm");
            o.Extensions.Add(new RuleExtension(custom));
        });
        using (scope)
        {
            for (var i = 0; i < 100; i++)
            {
                session.Timeline.Record(Render(1, "Storm", "UiEvent"));
            }

            session.Diagnostics.Evaluate(force: true);
            Assert.DoesNotContain(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.storm");
            Assert.Contains(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "test.custom");
            Assert.Equal(1, custom.Evaluations);
        }
    }

    private sealed class RuleExtension(IDiagnosticRule rule) : Extensions.IDevToolsExtension
    {
        public string Name => "test";

        public void Configure(Extensions.IDevToolsExtensionBuilder builder) => builder.AddDiagnosticRule(rule);
    }

    private sealed class CountingRule : IDiagnosticRule
    {
        public int Evaluations;

        public string Id => "test.custom";

        public string Title => "Custom";

        public string Description => "";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            Evaluations++;
            yield return new Diagnostic(Id, DiagnosticSeverity.Info, "custom finding", context.Events.Count + " events");
        }
    }
}
