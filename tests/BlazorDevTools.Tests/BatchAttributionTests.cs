using System.Diagnostics;
using BlazorDevTools.Capabilities;
using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

/// <summary>
/// Batch bookkeeping and the framework diagnostics it depends on. These cover the cases that only show up in a real
/// host: measurements that arrive after the batch they describe, and a runtime where meters are compiled out.
/// Framework instrumentation is off here so the assertions are about DevTools' own logic, not about whichever
/// measurements the test renderer happened to emit.
/// </summary>
public class BatchAttributionTests : BunitContext
{
    public BatchAttributionTests() => Services.AddBlazorDevTools(o =>
    {
        o.Enabled = true;
        o.UseFrameworkInstrumentation = false;
    });

    private Session.DevToolsSession Session => Services.GetRequiredService<Session.DevToolsSession>();

    [Fact]
    public void A_late_diff_measurement_is_matched_to_the_batch_it_belongs_to()
    {
        var cut = Render<ParentComponent>();
        var tracker = Session.RenderTracker;

        // Blazor Server records the diff duration when the browser acknowledges the render, which can be after the
        // next batch has already started. Attributing it to whatever batch is current would report nonsense.
        var first = tracker.CurrentBatch!;
        tracker.CloseBatch();
        cut.InvokeAsync(() => cut.Instance.Refresh());
        var second = tracker.CurrentBatch!;
        Assert.NotSame(first, second);

        tracker.RecordBatchDiff(3.5);
        tracker.RecordBatchDiffSize(7);

        Assert.Equal(3.5, first.DiffMs);
        Assert.Equal(7, first.DiffSize);
        Assert.Null(second.DiffMs);
        var detail = Session.Timeline.Find(first.EventId)!.Detail!;
        Assert.Contains("3.50 ms diffing", detail, StringComparison.Ordinal);
        Assert.Contains("7 DOM edits", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prompt_diff_measurement_closes_the_current_batch()
    {
        Render<ParentComponent>();
        var tracker = Session.RenderTracker;
        var batch = tracker.CurrentBatch!;

        tracker.RecordBatchDiff(1.25);

        Assert.True(batch.Closed);
        Assert.Equal(1.25, batch.DiffMs);
    }

    [Fact]
    public void Renders_never_join_a_batch_that_was_already_closed()
    {
        var cut = Render<ParentComponent>();
        var tracker = Session.RenderTracker;
        var first = tracker.CurrentBatch!;
        tracker.CloseBatch();
        var counted = first.Count;

        cut.InvokeAsync(() => cut.Instance.Refresh());

        Assert.NotSame(first, tracker.CurrentBatch);
        Assert.Equal(counted, first.Count);
    }

    [Fact]
    public void Devtools_own_interactions_are_not_recorded_as_application_events()
    {
        Render<PanelLikeComponent>();
        var before = Session.Timeline.Snapshot().Count(e => e.Kind == DevToolsEventKind.UiEvent);

        Emit("Event onclick -> BlazorDevTools.UI.TimelinePanel.Select");
        Emit("Event onclick -> BlazorDevTools.DevToolsPanel.Toggle");
        Assert.Equal(before, Session.Timeline.Snapshot().Count(e => e.Kind == DevToolsEventKind.UiEvent));

        // An application namespace that merely starts with the same word is still application activity.
        Emit("Event onclick -> BlazorDevToolsDemo.Pages.Home.Go");
        Assert.Equal(before + 1, Session.Timeline.Snapshot().Count(e => e.Kind == DevToolsEventKind.UiEvent));
    }

    /// <summary>Mimics ComponentsActivitySource: display name set before the activity starts, tags only on stop.</summary>
    private static void Emit(string displayName)
    {
        using var source = new ActivitySource(ActivityObserver.SourceName);
        var activity = source.CreateActivity(ActivityObserver.HandleEventName, ActivityKind.Client);
        if (activity is null)
        {
            Assert.Fail("no listener is attached to the components ActivitySource");
        }

        activity.DisplayName = displayName;
        activity.Start();
        activity.Stop();
    }

    [Theory]
    [InlineData("Event onclick -> MyApp.Pages.Counter.IncrementCount", "MyApp.Pages.Counter", "IncrementCount", "onclick")]
    [InlineData("Event oninput -> Lib.Filter.OnQuery", "Lib.Filter", "OnQuery", "oninput")]
    [InlineData("Route /counter -> MyApp.Pages.Counter", "MyApp.Pages", "Counter", "/counter")]
    [InlineData("something else", null, null, null)]
    public void Display_names_are_parsed_back_into_their_parts(string displayName, string? type, string? method, string? attribute)
    {
        var parsed = ActivityObserver.ParseDisplayName(displayName);

        Assert.Equal(type, parsed.ComponentType);
        Assert.Equal(method, parsed.Method);
        Assert.Equal(attribute, parsed.Attribute);
    }

    [Fact]
    public void Missing_metrics_support_is_explained_instead_of_being_reported_as_pending()
    {
        Assert.True(CapabilityReport.MetricsAreSupported(), "the test host supports metrics");
        Assert.DoesNotContain("MetricsSupport", CapabilityReport.MetricsHint(metricsSupported: true), StringComparison.Ordinal);

        AppContext.SetSwitch("System.Diagnostics.Metrics.Meter.IsSupported", false);
        try
        {
            Assert.False(CapabilityReport.MetricsAreSupported());
            Assert.Contains("MetricsSupport", CapabilityReport.MetricsHint(metricsSupported: false), StringComparison.Ordinal);
        }
        finally
        {
            AppContext.SetSwitch("System.Diagnostics.Metrics.Meter.IsSupported", true);
        }
    }
}

public class PanelLikeComponent : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => { }));
        builder.CloseElement();
    }
}
