using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;
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
