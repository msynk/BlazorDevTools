using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

/// <summary>
/// Covers the framework diagnostics DevTools depends on for its headline question ("why did this render?").
/// These assertions pin the exact activity and tag names of Microsoft.AspNetCore.Components: getting one of them
/// wrong silently degrades every render cause to "StateHasChanged" and every event title to "?.?", which is
/// exactly the failure mode a purely internal test suite cannot see.
/// </summary>
public class ActivityAttributionTests : BunitContext
{
    public ActivityAttributionTests()
    {
        Services.AddBlazorDevTools(o => o.Enabled = true);
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void AddBlazorDevTools_registers_the_framework_activity_source_and_meters()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o => o.Enabled = true);
        Assert.Contains(services, d => d.ServiceType.FullName == "Microsoft.AspNetCore.Components.ComponentsActivitySource");
        Assert.Contains(services, d => d.ServiceType.FullName == "Microsoft.AspNetCore.Components.ComponentsMetrics");
    }

    [Fact]
    public void Framework_instrumentation_can_be_turned_off()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.UseFrameworkInstrumentation = false;
        });
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "Microsoft.AspNetCore.Components.ComponentsActivitySource");
    }

    [Fact]
    public void Ui_event_is_recorded_with_component_type_attribute_and_method()
    {
        var cut = Render<ClickableComponent>();
        cut.Find("button").Click();

        var uiEvent = Assert.Single(Session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.UiEvent);

        // Regression guard: the tag keys are aspnetcore.components.type / code.function.name /
        // aspnetcore.components.attribute.name, and the framework's own DisplayName already reads well.
        Assert.Contains("onclick", uiEvent.Title, StringComparison.Ordinal);
        Assert.Contains("ClickableComponent", uiEvent.Title, StringComparison.Ordinal);
        Assert.Contains("OnClick", uiEvent.Title, StringComparison.Ordinal);
        Assert.Equal("ClickableComponent", uiEvent.ComponentName);
        Assert.DoesNotContain("?.?", uiEvent.Title, StringComparison.Ordinal);
        Assert.NotNull(uiEvent.DurationMs);
        Assert.Equal("completed", uiEvent.Detail);
        Assert.True(ActivityObserver.HandleEventActivitiesObserved);
    }

    [Fact]
    public void Render_caused_by_a_click_is_attributed_to_that_click()
    {
        var cut = Render<ClickableComponent>();
        cut.Find("button").Click();

        var record = Session.Components.All.Single(r => r.Type == typeof(ClickableComponent));
        var render = record.LastRender!;
        Assert.Equal(RenderCause.UiEvent, render.Cause);

        var uiEvent = Session.Timeline.Snapshot().Single(e => e.Kind == DevToolsEventKind.UiEvent);
        Assert.Equal(uiEvent.Id, render.TriggerEventId);
        Assert.Equal(uiEvent.Id, Session.Timeline.Find(render.EventId)!.ParentEventId);
    }

    [Fact]
    public void Event_handler_durations_are_aggregated_per_component_type()
    {
        var cut = Render<ClickableComponent>();
        cut.Find("button").Click();
        cut.Find("button").Click();

        Assert.True(Session.TypeMetrics.TryGetValue("ClickableComponent", out var metrics));
        Assert.Equal(2, metrics!.EventHandlers);
    }

    [Fact]
    public void Render_diff_metrics_close_the_batch_with_a_measured_diff()
    {
        Render<ClickableComponent>();

        var batch = Session.Timeline.Snapshot().First(e => e.Kind == DevToolsEventKind.RenderBatch);
        Assert.True(ActivityObserver.BatchMetricsObserved, "aspnetcore.components.render_diff.* must be observed");
        Assert.Contains("ms diffing", batch.Detail);
        Assert.Contains("DOM edits", batch.Detail);
    }

    [Fact]
    public void Parameter_update_metrics_are_recorded_per_component_type()
    {
        Render<ClickableComponent>();

        Assert.True(ActivityObserver.LifecycleMetricsObserved);
        Assert.True(Session.TypeMetrics.TryGetValue("ClickableComponent", out var metrics));
        Assert.True(metrics!.ParameterUpdates > 0);
    }
}

public class ClickableComponent : ComponentBase
{
    private int _count;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OnClick));
        builder.AddContent(2, _count);
        builder.CloseElement();
    }

    private void OnClick() => _count++;
}
