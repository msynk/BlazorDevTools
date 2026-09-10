using BlazorDevTools.Events;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

/// <summary>Guards the cost of instrumentation. Thresholds are loose (Debug builds, shared CI) but catch order-of-magnitude regressions.</summary>
public class OverheadTests : BunitContext
{
    public OverheadTests()
    {
        Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.MaxRendersPerComponent = 50;
        });
    }

    [Fact]
    public void Per_render_bookkeeping_stays_in_the_microsecond_range()
    {
        var cut = Render<ParentComponent>();
        var session = Services.GetRequiredService<DevToolsSession>();
        for (var i = 0; i < 500; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Refresh());
        }

        var overhead = session.Overhead;
        // 1 initial parent + child render, then 500 parent-only refreshes (child parameters do not change).
        Assert.True(overhead.AppRenderCount >= 502, $"expected at least 502 app renders, got {overhead.AppRenderCount}");
        Assert.True(overhead.InstrumentationPerRenderMicroseconds < 500, $"instrumentation cost {overhead.InstrumentationPerRenderMicroseconds:0} µs per render is too high");
    }

    [Fact]
    public void Buffers_stay_bounded_under_sustained_activity()
    {
        var cut = Render<ParentComponent>();
        var session = Services.GetRequiredService<DevToolsSession>();
        for (var i = 0; i < 2000; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Refresh());
        }

        Assert.True(session.Timeline.Count <= session.Options.MaxEvents);
        var parent = session.Components.All.Single(r => r.Type == typeof(ParentComponent));
        Assert.Equal(50, parent.Renders.Length);
        Assert.Equal(2001, parent.RenderCount);
    }

    [Fact]
    public void Disposed_component_records_are_released()
    {
        var cut = Render<ParentComponent>(ps => ps.Add(p => p.ShowChild, true));
        var session = Services.GetRequiredService<DevToolsSession>();
        for (var i = 0; i < 20; i++)
        {
            cut.Render(ps => ps.Add(p => p.ShowChild, i % 2 == 0));
        }

        session.Components.Refresh(force: true);
        var childRecords = session.Components.All.Where(r => r.Type == typeof(ChildComponent)).ToList();
        Assert.True(childRecords.Count(r => !r.IsDisposed) <= 1);
        Assert.True(session.Components.LiveCount <= 2, "only live components stay in the identity map");
        Assert.All(childRecords.Where(r => r.IsDisposed), r => Assert.Null(r.Component));
    }

    [Fact]
    public void Disabled_devtools_adds_no_activator_or_handlers()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o => o.Enabled = false);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.IComponentActivator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Microsoft.Extensions.Logging.ILoggerProvider));
    }

    [Fact]
    public void Devtools_is_off_by_default_without_a_development_environment()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools();
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<DevToolsRegistry>().IsEnabled);
        Assert.False(provider.GetRequiredService<IDevToolsTimeline>().IsEnabled);
    }
}
