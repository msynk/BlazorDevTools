using BlazorDevTools.UI;
using BlazorDevTools.Session;
using BlazorDevTools.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;

namespace BlazorDevTools.Tests;

/// <summary>
/// The DevTools UI is itself a Blazor application, and nothing else in the suite clicks its controls. A panel whose
/// buttons quietly do nothing looks exactly like a panel that works.
/// </summary>
public class PanelInteractionTests : BunitContext
{
    public PanelInteractionTests()
    {
        Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.Ui.OpenByDefault = true;
        });
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    private IRenderedComponent<DevToolsPanel> RenderPanel(string? prefs = null)
    {
        var module = JSInterop.SetupModule("./_content/BlazorDevTools/devtools.js");
        module.Setup<BrowserSnapshot>("attach", _ => true).SetResult(new BrowserSnapshot { Online = true, Visibility = "visible" });
        module.Setup<string?>("loadPrefs", _ => true).SetResult(prefs);
        module.SetupVoid("savePrefs", _ => true).SetVoidResult();
        module.Setup<StorageEntry[]>("getStorage", _ => true)
            .SetResult([new StorageEntry("demo:auth_token", 42, "«redacted»", true), new StorageEntry("theme", 4, "dark")]);
        module.Setup<BrowserSnapshot>("refresh", _ => true).SetResult(new BrowserSnapshot { Online = true });
        return Render<DevToolsPanel>();
    }

    [Fact]
    public void Every_built_in_panel_can_be_opened_from_its_tab()
    {
        var cut = RenderPanel();
        var tabs = cut.FindAll(".bdt-tab").Select(t => t.TextContent.Trim().Split('\n')[0].Trim()).ToList();
        Assert.Contains("Components", tabs);

        foreach (var tab in tabs)
        {
            cut.FindAll(".bdt-tab").First(t => t.TextContent.Trim().StartsWith(tab, StringComparison.Ordinal)).Click();
            Assert.NotEmpty(cut.FindAll(".bdt-body"));
            Assert.DoesNotContain(cut.FindAll(".bdt-empty"), e => e.TextContent.Contains("Unknown panel", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_browser_panel_reads_storage_when_its_button_is_clicked()
    {
        var cut = RenderPanel();
        cut.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Browser", StringComparison.Ordinal)).Click();

        var button = cut.FindAll(".bdt-body button").First(b => b.TextContent.Contains("localStorage", StringComparison.Ordinal));
        button.Click();

        Assert.NotNull(Session.Browser.LocalStorage);
        Assert.Null(Session.Browser.StorageError);
        Assert.Contains(Session.Browser.LocalStorage!, e => e.Key == "demo:auth_token" && e.Redacted);
        Assert.Contains("demo:auth_token", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("«redacted»", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_can_be_closed_and_reopened_from_the_badge()
    {
        var cut = RenderPanel();
        cut.FindAll(".bdt-tools button").Last().Click();

        var badge = cut.Find(".bdt-badge");
        Assert.Empty(cut.FindAll(".bdt-panel"));

        badge.Click();
        Assert.NotEmpty(cut.FindAll(".bdt-panel"));
    }

    [Fact]
    public void An_unavailable_saved_panel_is_ignored()
    {
        var cut = RenderPanel("""{"panel":"circuit","open":true}""");

        Assert.Equal("components", Session.Ui.ActivePanelId);
        Assert.DoesNotContain("Unknown panel", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_updates_the_open_tab_without_switching_panels()
    {
        var cut = RenderPanel();
        cut.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Network", StringComparison.Ordinal)).Click();
        Assert.Contains("No HTTP requests captured", cut.Markup, StringComparison.Ordinal);

        Session.Http.Add(new HttpRecord
        {
            Id = Session.Http.NextId(),
            StartedAt = DateTimeOffset.UtcNow,
            Method = "GET",
            Url = "https://example.test/live-refresh",
            StatusCode = 200,
        });
        Assert.DoesNotContain("live-refresh", cut.Markup, StringComparison.Ordinal);

        cut.Find("button[aria-label='Refresh captured data']").Click();
        Assert.Contains("live-refresh", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_updates_can_be_turned_off_and_the_open_tab_stays_stale_until_refresh()
    {
        var cut = RenderPanel();
        cut.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Network", StringComparison.Ordinal)).Click();
        cut.Find("input[aria-label='Live updates']").Change(false);
        Assert.False(Session.Ui.LiveUpdates);

        Session.Http.Add(new HttpRecord
        {
            Id = Session.Http.NextId(),
            StartedAt = DateTimeOffset.UtcNow,
            Method = "GET",
            Url = "https://example.test/live-off",
            StatusCode = 204,
        });
        cut.Render();
        Assert.DoesNotContain("live-off", cut.Markup, StringComparison.Ordinal);

        cut.Find("button[aria-label='Refresh captured data']").Click();
        Assert.Contains("live-off", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_preference_is_restored()
    {
        var cut = RenderPanel("""{"open":true,"live":false}""");
        Assert.False(Session.Ui.LiveUpdates);
        Assert.False(cut.Find("input[aria-label='Live updates']").HasAttribute("checked"));
    }

    /// <summary>
    /// The panels render framework components an application renders too — <c>Virtualize</c>, <c>CascadingValue</c>,
    /// <c>DynamicComponent</c> — so nothing about their type says they are DevTools'. Before ownership was inherited
    /// from the parent, opening a tab wrote its own renders, render batches and <c>Virtualize.init</c> interop calls
    /// into the very panels the developer was reading.
    /// </summary>
    [Fact]
    public void DevTools_own_ui_never_appears_as_application_activity()
    {
        var cut = RenderPanel();
        Session.Http.Add(new HttpRecord
        {
            Id = Session.Http.NextId(),
            StartedAt = DateTimeOffset.UtcNow,
            Method = "GET",
            Url = "https://example.test/thing",
            StatusCode = 200,
        });

        foreach (var tab in new[] { "Network", "Timeline", "Profiler", "Components" })
        {
            cut.FindAll(".bdt-tab").First(t => t.TextContent.Contains(tab, StringComparison.Ordinal)).Click();
        }

        cut.Find("button[aria-label='Refresh captured data']").Click();

        Assert.All(Session.Components.All, record => Assert.True(
            record.IsDevTools,
            $"{record.TypeName} is rendered by the DevTools panel but is tracked as application activity."));
        Assert.Empty(Session.Timeline.Snapshot());
        Assert.Empty(Session.Interop.Snapshot());
        Assert.Empty(Session.Components.BuildTree(includeHidden: true));
    }

    /// <summary>The inverse: the propagation must not swallow the application's own use of the same framework components.</summary>
    [Fact]
    public void Application_components_under_a_cascading_value_are_still_application_activity()
    {
        RenderPanel();
        var cut = Render<AppHost>();

        var child = Assert.Single(Session.Components.All, r => r.Type == typeof(ChildComponent));
        Assert.False(child.IsDevTools);
        Assert.Contains(Session.Timeline.Snapshot(), e => e.ComponentName == child.DisplayName);
        Assert.NotNull(cut);
    }

    [Fact]
    public void Inspector_section_reacts_when_collapsed_parameter_changes()
    {
        var cut = Render<InspectorSection>(parameters => parameters
            .Add(p => p.Title, "Errors")
            .Add(p => p.Collapsed, true)
            .AddChildContent("details"));

        Assert.DoesNotContain("details", cut.Markup, StringComparison.Ordinal);
        cut.Render(parameters => parameters
            .Add(p => p.Title, "Errors")
            .Add(p => p.Collapsed, false)
            .AddChildContent("details"));

        Assert.Contains("details", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".bdt-section-title").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Selecting_a_component_through_the_host_stops_capturing_the_previous_one()
    {
        var cut = RenderPanel();
        var first = Session.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false)!;
        var second = Session.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false)!;

        cut.InvokeAsync(() => cut.Instance.SelectComponent(first.InstanceId));
        Assert.True(first.CaptureState);
        cut.InvokeAsync(() => cut.Instance.SelectComponent(second.InstanceId));

        Assert.False(first.CaptureState);
        Assert.True(second.CaptureState);
    }

    [Fact]
    public void Browser_bridge_can_be_disabled_without_preventing_the_panel_from_rendering()
    {
        using var context = new BunitContext();
        context.Services.AddBlazorDevTools(options =>
        {
            options.Enabled = true;
            options.EnableBrowserBridge = false;
            options.Ui.OpenByDefault = true;
        });
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/BlazorDevTools/devtools.js");
        module.Setup<string?>("loadPrefs", _ => true).SetResult("""{"theme":"Dark"}""");
        context.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("WebAssembly", isInteractive: true));

        var cut = context.Render<DevToolsPanel>();
        var session = context.Services.GetRequiredService<DevToolsSession>();

        Assert.NotEmpty(cut.FindAll(".bdt-panel"));
        Assert.False(session.Browser.BridgeAttached);
        Assert.Equal(DevToolsTheme.Dark, session.Ui.Theme);
        Assert.DoesNotContain(module.Invocations, invocation => invocation.Identifier == "attach");
        cut.InvokeAsync(cut.Instance.Toggle);
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "savePrefs");
    }

    [Fact]
    public void Interop_filter_treats_colon_identifiers_as_search_text()
    {
        var cut = RenderPanel();
        var record = new JsInteropRecord
        {
            Id = Session.Interop.NextId(),
            At = DateTimeOffset.UtcNow,
            Direction = JsInteropDirection.DotNetToJs,
            Identifier = "module:invoke",
            ComponentName = "CartWidget",
        };
        Session.Interop.Add(record);
        Session.Interop.Complete(record, 1, succeeded: true, error: null);
        cut.FindAll(".bdt-tab").Single(tab => tab.TextContent.Trim() == "JS Interop").Click();

        cut.Find("input[aria-label='Filter JS interop calls']").Input("module:invoke");

        Assert.Contains("module:invoke", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("No JS interop calls captured", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='Filter JS interop calls']").Input("CartWidget");
        Assert.Contains("module:invoke", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>An application component nested in the framework components the panels also use.</summary>
    private sealed class AppHost : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<int>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<int>.Value), 7);
            builder.AddComponentParameter(2, nameof(CascadingValue<int>.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<ChildComponent>(0);
                b.AddComponentParameter(1, nameof(ChildComponent.Text), "app");
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    [Fact]
    public void Programmatic_navigation_is_not_parented_to_an_old_ui_event()
    {
        RenderPanel();
        Session.LastUiEventId = Session.Timeline.Record(new Events.DevToolsEvent
        {
            Kind = Events.DevToolsEventKind.UiEvent,
            Title = "old click",
        });

        Services.GetRequiredService<NavigationManager>().NavigateTo("/later");

        var navigation = Session.Timeline.Snapshot().Last(e => e.Kind == Events.DevToolsEventKind.Navigation);
        Assert.Null(navigation.ParentEventId);
    }
}
