using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorDevTools.Tests;

/// <summary>End-to-end instrumentation tests through a real renderer (bUnit).</summary>
public class ComponentTrackingTests : BunitContext
{
    private readonly FakeJSRuntime _js = new();

    public ComponentTrackingTests()
    {
        Services.AddSingleton<IJSRuntime>(_js);
        Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.CaptureComponentStateOnRender = true;
        });
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void Reflection_accessors_are_available_on_this_framework()
    {
        Assert.Null(BlazorReflection.InitializationError);
        Assert.True(BlazorReflection.CanWrapRenderFragment);
        Assert.True(BlazorReflection.CanResolveRenderer);
        Assert.True(BlazorReflection.CanResolveHierarchy);
        Assert.True(BlazorReflection.CanDetectBatches);
    }

    [Fact]
    public void Components_are_registered_with_hierarchy_and_render_counts()
    {
        var cut = Render<ParentComponent>(ps => ps.Add(p => p.Value, 1));
        Session.Components.Refresh(force: true);

        var parent = Session.Components.All.Single(r => r.Type == typeof(ParentComponent));
        var child = Session.Components.All.Single(r => r.Type == typeof(ChildComponent));
        Assert.True(parent.IsInstrumented);
        Assert.Equal(1, parent.RenderCount);
        Assert.Equal(1, child.RenderCount);
        Assert.Same(parent, child.Parent);
        Assert.NotNull(parent.ComponentId);
        Assert.Equal(RenderCause.Initial, child.LastRender!.Cause);
        Assert.NotNull(Session.Dispatcher);
        Assert.True(parent.LastRender!.DurationMs >= 0);

        var tree = Session.Components.BuildTree(includeHidden: false);
        var root = Assert.Single(tree);
        Assert.Same(parent, root.Record);
        Assert.Single(root.Children);
        Assert.Equal(2, root.SubtreeRenders);
    }

    [Fact]
    public void Child_render_caused_by_parent_is_attributed_and_state_diff_captured()
    {
        var cut = Render<ParentComponent>(ps => ps.Add(p => p.Value, 1));
        cut.InvokeAsync(() => cut.Instance.Increment());

        Session.Components.Refresh(force: true);
        var parent = Session.Components.All.Single(r => r.Type == typeof(ParentComponent));
        var child = Session.Components.All.Single(r => r.Type == typeof(ChildComponent));

        Assert.Equal(2, parent.RenderCount);
        Assert.Equal(2, child.RenderCount);
        Assert.Equal(RenderCause.StateHasChanged, parent.LastRender!.Cause);
        Assert.Equal(RenderCause.ParentRender, child.LastRender!.Cause);
        Assert.Contains("ParentComponent", child.LastRender.CauseDetail);
        Assert.Equal(parent.LastRender.BatchId, child.LastRender.BatchId);
        Assert.Contains(parent.LastRender.ChangedState!, s => s.StartsWith("_counter: 0 → 1"));

        var childEvent = Session.Timeline.Find(child.LastRender.EventId)!;
        Assert.Equal(parent.LastRender.EventId, childEvent.ParentEventId);
    }

    [Fact]
    public void Batches_are_detected_and_closed()
    {
        var cut = Render<ParentComponent>();
        cut.InvokeAsync(() => cut.Instance.Refresh());
        cut.InvokeAsync(() => cut.Instance.Refresh());
        Session.RenderTracker.CloseBatch();

        var batches = Session.Timeline.Snapshot().Where(e => e.Kind == DevToolsEventKind.RenderBatch).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Contains("2 components rendered", batches[0].Detail); // initial: parent + child
        Assert.Contains("1 component rendered", batches[1].Detail); // refresh: child parameters unchanged, only the parent renders
        Assert.All(batches, b => Assert.Contains("ms building", b.Detail));
        Assert.Equal(3, Session.RenderTracker.BatchCount);
    }

    [Fact]
    public void Disposed_components_are_detected_and_pruned_from_the_live_tree()
    {
        var cut = Render<ParentComponent>(ps => ps.Add(p => p.ShowChild, true));
        Session.Components.Refresh(force: true);
        Assert.Single(Session.Components.All, r => r.Type == typeof(ChildComponent) && !r.IsDisposed);

        cut.Render(ps => ps.Add(p => p.ShowChild, false));
        Session.Components.Refresh(force: true);

        var child = Session.Components.All.Single(r => r.Type == typeof(ChildComponent));
        Assert.True(child.IsDisposed);
        Assert.DoesNotContain(Session.Components.BuildTree(false)[0].Children, n => n.Record.Type == typeof(ChildComponent));
        Assert.Contains(Session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.Lifecycle && e.Title == "ChildComponent disposed");
    }

    [Fact]
    public void Injected_js_runtime_is_wrapped_and_calls_are_attributed_to_the_component()
    {
        var cut = Render<ParentComponent>();
        var child = Session.Components.All.Single(r => r.Type == typeof(ChildComponent));
        var instance = Assert.IsType<ChildComponent>(child.Component);

        Assert.IsType<TrackingJSRuntime>(instance.JS);
        cut.InvokeAsync(async () => await instance.JS!.InvokeVoidAsync("hello"));

        var call = Assert.Single(Session.Interop.Snapshot());
        Assert.Equal(child.InstanceId, call.ComponentInstanceId);
        Assert.Equal("ChildComponent", call.ComponentName);
        Assert.Single(_js.Calls, "hello");
    }

    [Fact]
    public void Render_exceptions_are_captured_with_the_component()
    {
        Assert.ThrowsAny<Exception>(() => Render<ThrowingComponent>(ps => ps.Add(p => p.Throw, true)));

        var record = Session.Components.All.Single(r => r.Type == typeof(ThrowingComponent));
        Assert.Equal(1, record.ErrorCount);
        var error = Assert.Single(Session.Errors.Snapshot(), e => e.Source == "render");
        Assert.Equal(record.InstanceId, error.ComponentInstanceId);
        Assert.Equal("boom", error.Message);
        Assert.True(record.LastRender!.Failed);
    }

    [Fact]
    public void DevTools_own_components_are_excluded_from_app_statistics()
    {
        Render<ParentComponent>();
        Assert.DoesNotContain(Session.Components.BuildTree(false), n => n.Record.IsDevTools);
        Assert.True(Session.Overhead.AppRenderCount >= 2);
        Assert.Equal(0, Session.Overhead.DevToolsRenderCount);
    }

    [Fact]
    public void Ignored_components_are_not_tracked()
    {
        Services.GetRequiredService<DevToolsOptions>().IgnoreComponentPredicates.Add(t => t == typeof(ChildComponent));
        Render<ParentComponent>();
        Assert.DoesNotContain(Session.Components.All, r => r.Type == typeof(ChildComponent));
    }

    [Fact]
    public void Session_is_resolved_from_the_renderer_dispatcher()
    {
        var cut = Render<ParentComponent>();
        DevToolsSession? resolved = null;
        cut.InvokeAsync(() => resolved = SessionResolver.Resolve());
        Assert.Same(Session, resolved);
    }

    [Fact]
    public void Disabled_tracking_options_remove_optional_work_without_disabling_render_counts()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IJSRuntime>(new FakeJSRuntime());
        context.Services.AddBlazorDevTools(options =>
        {
            options.Enabled = true;
            options.UseFrameworkInstrumentation = false;
            options.RecordRenderEvents = false;
            options.TrackRenderCauses = false;
            options.TrackJsInterop = false;
        });

        var cut = context.Render<ParentComponent>();
        cut.InvokeAsync(() => cut.Instance.Refresh());
        var session = context.Services.GetRequiredService<DevToolsSession>();
        var parent = session.Components.All.Single(record => record.Type == typeof(ParentComponent));
        var child = session.Components.All.Single(record => record.Type == typeof(ChildComponent));

        Assert.Equal(2, parent.RenderCount);
        Assert.Equal(RenderCause.Unknown, parent.LastRender!.Cause);
        Assert.DoesNotContain(session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.Render);
        Assert.IsNotType<TrackingJSRuntime>(((ChildComponent)child.Component!).JS);
    }
}
