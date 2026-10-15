using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Export;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

/// <summary>
/// "Why did this render?" has a follow-up question: "did it need to?". A parent that recreates a list, a lambda or an
/// anonymous object per render forces every child to re-render even when nothing observable changed, and Blazor cannot
/// tell (it only short-circuits primitive parameters). The inspector compares parameter values between captured
/// renders so the developer can.
/// </summary>
public class WastedRenderTests : BunitContext
{
    public WastedRenderTests() => Services.AddBlazorDevTools(o =>
    {
        o.Enabled = true;
        o.CaptureComponentStateOnRender = true;
    });

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void A_parent_driven_render_with_identical_parameter_values_is_marked_as_forced()
    {
        var cut = Render<ListParent>();
        cut.InvokeAsync(() => cut.Instance.Bump());
        cut.InvokeAsync(() => cut.Instance.Bump());

        var child = Session.Components.All.Single(r => r.Type == typeof(ListChild));
        Assert.Equal(3, child.RenderCount);
        var last = child.LastRender!;
        Assert.Equal(RenderCause.ParentRender, last.Cause);
        Assert.True(last.ParametersCompared);
        Assert.Empty(last.ChangedParameters!);
        Assert.True(last.IsWastedRender);
        // The first render has nothing to compare against and is never counted.
        Assert.Equal(2, child.ParentRendersWithUnchangedParameters);

        var evt = Session.Timeline.Find(last.EventId)!;
        Assert.True(evt.TryGetData("parametersUnchanged", out var unchanged) && unchanged is true);
    }

    [Fact]
    public void A_parameter_whose_contents_changed_is_reported_with_the_path_and_values()
    {
        var cut = Render<ListParent>();
        cut.InvokeAsync(() => cut.Instance.Add("c"));

        var child = Session.Components.All.Single(r => r.Type == typeof(ListChild));
        var last = child.LastRender!;
        Assert.Equal(RenderCause.ParentRender, last.Cause);
        Assert.False(last.IsWastedRender);
        // The most specific path wins (the parent "Items" entry collapses into it), same as state-provider diffs.
        Assert.Equal("Items[2]: ∅ → \"c\"", Assert.Single(last.ChangedParameters!));
        Assert.Equal(0, child.ParentRendersWithUnchangedParameters);
    }

    /// <summary>
    /// Names here deliberately match none of the sensitive name patterns, so only the attribute stands between the
    /// value and the diff. Before the member was passed to the inspector, field diffs honoured the patterns only.
    /// </summary>
    [Fact]
    public void Sensitive_fields_and_parameters_never_appear_in_diffs_in_plain_text()
    {
        var cut = Render<SecretHost>();
        cut.InvokeAsync(() => cut.Instance.Rotate());

        var host = Session.Components.All.Single(r => r.Type == typeof(SecretHost));
        var child = Session.Components.All.Single(r => r.Type == typeof(SecretChild));

        // The unmarked sibling proves the field diff ran; the marked field must be invisible in it.
        var changed = host.LastRender!.ChangedState!;
        Assert.Contains(changed, c => c.StartsWith("_note: \"n1\" → \"n2\"", StringComparison.Ordinal));
        Assert.DoesNotContain(changed, c => c.StartsWith("_apiPhrase", StringComparison.Ordinal));
        Assert.DoesNotContain(changed, c => c.Contains("plain-", StringComparison.Ordinal));

        // The child's [DevToolsSensitive] parameter changed too; redacted values compare equal, which is the safe
        // direction: over-redaction never leaks, and the "forced" verdict is documented as observable-only.
        Assert.True(child.LastRender!.ParametersCompared);
        Assert.DoesNotContain(child.LastRender.ChangedParameters ?? [], c => c.Contains("plain-", StringComparison.Ordinal));
        Assert.Equal(Inspection.Redactor.RedactedValue, child.LastParameterSnapshot!["Passphrase"]);
        Assert.DoesNotContain(child.LastParameterSnapshot.Keys, k => k.StartsWith("Passphrase.", StringComparison.Ordinal));
        Assert.DoesNotContain(Session.Timeline.Snapshot(), e => e.Detail?.Contains("plain-", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_parent_driven_rule_escalates_when_every_compared_render_was_forced()
    {
        var cut = Render<ListParent>();
        for (var i = 0; i < 12; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Bump());
        }

        Session.Diagnostics.Evaluate(force: true);

        var finding = Assert.Single(Session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.parent-driven" && f.Diagnostic.Title.StartsWith("ListChild", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Warning, finding.Diagnostic.Severity);
        Assert.Contains("identical to the previous render", finding.Diagnostic.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_capture_the_rule_stays_informational_and_says_how_to_get_evidence()
    {
        using var context = new BunitContext();
        context.Services.AddBlazorDevTools(o => o.Enabled = true);
        var cut = context.Render<ListParent>();
        for (var i = 0; i < 12; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Bump());
        }

        var session = context.Services.GetRequiredService<DevToolsSession>();
        session.Diagnostics.Evaluate(force: true);

        var finding = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.parent-driven" && f.Diagnostic.Title.StartsWith("ListChild", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Info, finding.Diagnostic.Severity);
        Assert.Contains("Select the component in the inspector", finding.Diagnostic.Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>&lt;Child&gt;@count&lt;/Child&gt;</c>: every fragment displays as "RenderFragment", so comparing displays
    /// called new content identical, counted the render as forced and escalated the rule to a warning whose
    /// ShouldRender advice would freeze the count on screen.
    /// </summary>
    [Fact]
    public void New_child_content_is_a_changed_parameter_and_keeps_the_rule_informational()
    {
        var cut = Render<ContentParent>();
        for (var i = 0; i < 12; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Bump());
        }

        var child = Session.Components.All.Single(r => r.Type == typeof(ContentChild));
        var last = child.LastRender!;
        Assert.Equal(RenderCause.ParentRender, last.Cause);
        Assert.False(last.IsWastedRender);
        Assert.Equal("ChildContent: new RenderFragment instance (its output cannot be compared)", Assert.Single(last.ChangedParameters!));
        Assert.Equal(0, child.ParentRendersWithUnchangedParameters);
        Assert.Equal(12, child.RendersWithComparedParameters);
        Assert.Equal("12", cut.Markup.Trim());

        Session.Diagnostics.Evaluate(force: true);
        var finding = Assert.Single(Session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "render.parent-driven" && f.Diagnostic.Title.StartsWith("ContentChild", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Info, finding.Diagnostic.Severity);
        Assert.Contains("0 of 12 compared renders received identical parameter values", finding.Diagnostic.Evidence, StringComparison.Ordinal);
    }

    /// <summary>Non-capturing child content compiles to one cached delegate: the same instance renders the same output.</summary>
    [Fact]
    public void Static_child_content_is_the_same_instance_and_can_be_a_forced_render()
    {
        var cut = Render<ContentParent>(ps => ps.Add(p => p.Mode, "static"));
        cut.InvokeAsync(() => cut.Instance.Bump());
        cut.InvokeAsync(() => cut.Instance.Bump());

        var child = Session.Components.All.Single(r => r.Type == typeof(ContentChild));
        Assert.True(child.LastRender!.IsWastedRender);
        Assert.Equal(2, child.ParentRendersWithUnchangedParameters);
    }

    /// <summary>A fragment cached in a field is the same instance every time, yet renders the owner's current state.</summary>
    [Fact]
    public void A_cached_fragment_that_reads_component_state_is_never_judged_unchanged()
    {
        var cut = Render<ContentParent>(ps => ps.Add(p => p.Mode, "cached"));
        cut.InvokeAsync(() => cut.Instance.Bump());
        cut.InvokeAsync(() => cut.Instance.Bump());

        var child = Session.Components.All.Single(r => r.Type == typeof(ContentChild));
        var last = child.LastRender!;
        Assert.False(last.IsWastedRender);
        Assert.Equal("ChildContent: RenderFragment reads component state when it runs (its output cannot be compared)", Assert.Single(last.ChangedParameters!));
        Assert.Equal(0, child.ParentRendersWithUnchangedParameters);
        Assert.Equal("2", cut.Markup.Trim());
    }

    /// <summary>
    /// The forced count used to be shown as a fraction of the samples still in the ring buffer, so it could read
    /// "250 of 100 compared renders". Both sides are now session-long counters, as is the per-cause breakdown.
    /// </summary>
    [Fact]
    public void Forced_compared_and_per_cause_counts_are_not_capped_by_the_sample_ring()
    {
        using var context = new BunitContext();
        context.Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.CaptureComponentStateOnRender = true;
            o.MaxRendersPerComponent = 5;
        });
        var cut = context.Render<ListParent>();
        for (var i = 0; i < 12; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Bump());
        }

        var session = context.Services.GetRequiredService<DevToolsSession>();
        var child = session.Components.All.Single(r => r.Type == typeof(ListChild));
        Assert.Equal(5, child.Renders.Length);
        Assert.Equal(13, child.RenderCount);
        Assert.Equal(12, child.RendersWithComparedParameters);
        Assert.Equal(12, child.ParentRendersWithUnchangedParameters);
        Assert.Equal(12, child.RendersByCause(RenderCause.ParentRender));
        Assert.Equal(1, child.RendersByCause(RenderCause.Initial));
        Assert.Contains("| 12 of 12 compared |", SessionExporter.ToMarkdownReport(session), StringComparison.Ordinal);

        session.Components.MarkBaseline();
        cut.InvokeAsync(() => cut.Instance.Bump());
        Assert.Equal(1, child.RendersByCause(RenderCause.ParentRender, sinceBaseline: true));
        Assert.Equal(0, child.RendersByCause(RenderCause.Initial, sinceBaseline: true));
    }

    /// <summary>Passes child content in three shapes: rebuilt per render, static, or cached in a field.</summary>
    public sealed class ContentParent : ComponentBase
    {
        private RenderFragment? _cached;
        private int _count;

        [Parameter] public string Mode { get; set; } = "rebuilt";

        public void Bump()
        {
            _count++;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderFragment content = Mode switch
            {
                "static" => b => b.AddContent(0, "static"),
                "cached" => _cached ??= b => b.AddContent(0, _count),
                _ => b => b.AddContent(0, _count),
            };
            builder.OpenComponent<ContentChild>(0);
            builder.AddComponentParameter(1, nameof(ContentChild.ChildContent), content);
            builder.CloseComponent();
        }
    }

    public sealed class ContentChild : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }

    /// <summary>Recreates the list on every render, which is exactly what forces <see cref="ListChild"/> to re-render.</summary>
    public sealed class ListParent : ComponentBase
    {
        private readonly List<string> _items = ["a", "b"];
        private int _counter;

        public void Bump()
        {
            _counter++;
            StateHasChanged();
        }

        public void Add(string item)
        {
            _items.Add(item);
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ListChild>(0);
            builder.AddComponentParameter(1, nameof(ListChild.Items), new List<string>(_items));
            builder.CloseComponent();
            builder.AddContent(2, _counter);
        }
    }

    public sealed class ListChild : ComponentBase
    {
        [Parameter] public List<string> Items { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, string.Join(",", Items));
    }

    public sealed class SecretHost : ComponentBase
    {
        [DevToolsSensitive] private string _apiPhrase = "plain-1";
        private string _note = "n1";

        public void Rotate()
        {
            _apiPhrase = "plain-2";
            _note = "n2";
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SecretChild>(0);
            builder.AddComponentParameter(1, nameof(SecretChild.Passphrase), new Phrase(_apiPhrase));
            builder.CloseComponent();
            builder.AddContent(2, _note);
        }
    }

    public sealed record Phrase(string Value);

    public sealed class SecretChild : ComponentBase
    {
        [Parameter, DevToolsSensitive] public Phrase? Passphrase { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Passphrase?.Value.Length ?? 0);
    }
}
