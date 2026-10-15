using BlazorDevTools.Commands;
using BlazorDevTools.Session;
using BlazorDevTools.UI;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorDevTools.Tests;

/// <summary>
/// The profiler workflow is "reset, do one thing, read what rendered", and on list-heavy pages "what did all the rows
/// cost together". Neither is possible with per-instance totals since page load.
/// </summary>
public class ProfilerTests : BunitContext
{
    public ProfilerTests()
    {
        Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.Ui.OpenByDefault = true;
        });
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void Baseline_restarts_the_since_figures_without_touching_raw_counters()
    {
        var cut = Render<ParentComponent>();
        cut.InvokeAsync(() => cut.Instance.Refresh());
        var parent = Session.Components.All.Single(r => r.Type == typeof(ParentComponent));
        Assert.Equal(2, parent.RenderCount);
        Assert.Equal(2, parent.RendersSinceBaseline);

        Session.Components.MarkBaseline();
        Assert.NotNull(Session.Components.BaselineAt);
        Assert.Equal(0, parent.RendersSinceBaseline);
        Assert.Equal(0, parent.RenderMsSinceBaseline, precision: 6);
        Assert.Equal(0, parent.MaxRenderMsSinceBaseline);
        Assert.Equal(2, parent.RenderCount);

        cut.InvokeAsync(() => cut.Instance.Refresh());
        Assert.Equal(1, parent.RendersSinceBaseline);
        Assert.Equal(3, parent.RenderCount);
        Assert.True(parent.MaxRenderMsSinceBaseline <= parent.MaxRenderMs);
        Assert.Equal(parent.LastRenderMs, parent.RenderMsSinceBaseline, precision: 6);
    }

    [Fact]
    public void Components_created_after_the_baseline_count_in_full()
    {
        Session.Components.MarkBaseline();
        var cut = Render<ParentComponent>();
        var parent = Session.Components.All.Single(r => r.Type == typeof(ParentComponent));

        Assert.Null(parent.BaselineAt);
        Assert.Equal(1, parent.RendersSinceBaseline);
    }

    [Fact]
    public void Profiler_groups_instances_by_type_and_drills_back_down()
    {
        var panel = RenderPanel();
        Render<TwoChildren>();
        panel.Find("button[aria-label='Refresh captured data']").Click();
        panel.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Profiler", StringComparison.Ordinal)).Click();

        panel.Find("input[aria-label='Group by type']").Change(true);
        var rows = panel.FindAll("tbody tr.bdt-row");
        var grouped = Assert.Single(rows, r => r.TextContent.Contains("ChildComponent", StringComparison.Ordinal));
        var cells = grouped.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToList();
        Assert.Equal("2", cells[1]); // instances
        Assert.Equal("2", cells[2]); // renders
        Assert.Contains("2 initial", cells[6], StringComparison.Ordinal);

        grouped.Click();
        Assert.Equal("ChildComponent", Session.Ui.GetQuery("profiler"));
        Assert.Equal(2, panel.FindAll("tbody tr.bdt-row").Count(r => r.TextContent.Contains("ChildComponent", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reset_button_shows_only_renders_since_the_reset()
    {
        var panel = RenderPanel();
        var cut = Render<ParentComponent>();
        panel.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Profiler", StringComparison.Ordinal)).Click();
        panel.Find("button[aria-label='Refresh captured data']").Click();
        Assert.Contains(panel.FindAll("tbody tr.bdt-row"), r => r.TextContent.Contains("ParentComponent", StringComparison.Ordinal));

        panel.Find("button[aria-label='Reset profiler baseline']").Click();
        Assert.True(panel.Find("input[aria-label='Since baseline']").HasAttribute("checked"));
        Assert.Contains("Nothing rendered since the baseline", panel.Markup, StringComparison.Ordinal);

        cut.InvokeAsync(() => cut.Instance.Refresh());
        panel.Find("button[aria-label='Refresh captured data']").Click();
        var row = Assert.Single(panel.FindAll("tbody tr.bdt-row"), r => r.TextContent.Contains("ParentComponent", StringComparison.Ordinal));
        Assert.Equal("1", row.QuerySelectorAll("td")[1].TextContent.Trim());
    }

    [Fact]
    public void Header_button_pauses_and_resumes_capture()
    {
        var panel = RenderPanel();
        var button = panel.Find("button[aria-label='Pause capture']");
        button.Click();

        Assert.False(Session.IsCapturing);
        Assert.Equal("true", panel.Find("button[aria-label='Resume capture']").GetAttribute("aria-pressed"));

        panel.Find("button[aria-label='Resume capture']").Click();
        Assert.True(Session.IsCapturing);
        Assert.NotNull(panel.Find("button[aria-label='Pause capture']"));
    }

    [Fact]
    public void Component_tree_is_an_accessible_tree_with_subtree_totals_on_collapsed_nodes()
    {
        var panel = RenderPanel();
        Render<ParentComponent>();
        panel.Find("button[aria-label='Refresh captured data']").Click();

        var tree = panel.Find("[role='tree']");
        var items = tree.QuerySelectorAll("[role='treeitem']");
        Assert.Equal(2, items.Length);
        var parentRow = items[0];
        Assert.Equal("1", parentRow.GetAttribute("aria-level"));
        Assert.Equal("true", parentRow.GetAttribute("aria-expanded"));
        Assert.Equal("2", items[1].GetAttribute("aria-level"));
        Assert.Null(items[1].GetAttribute("aria-expanded"));

        parentRow.QuerySelector(".bdt-caret")!.Click();
        parentRow = panel.Find("[role='treeitem']");
        Assert.Equal("false", parentRow.GetAttribute("aria-expanded"));
        Assert.Contains("⊕ 2 ×", parentRow.QuerySelector(".bdt-subtree")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_and_report_commands_use_the_command_context_bridge()
    {
        RenderPanel();
        Render<ParentComponent>();
        var commands = new CommandRegistry();
        commands.AddRange(BlazorDevTools.Commands.BuiltInCommands.Create());
        var context = new RecordingContext(Services);

        await commands.Find("session.export")!.ExecuteAsync(context, null);
        await commands.Find("session.report")!.ExecuteAsync(context, null);
        await commands.Find("capture.toggle")!.ExecuteAsync(context, null);
        await commands.Find("profiler.reset")!.ExecuteAsync(context, null);

        Assert.EndsWith(".json", context.DownloadedFileName, StringComparison.Ordinal);
        Assert.Contains("\"format\"", context.DownloadedContent, StringComparison.Ordinal);
        // The DevTools panel is rendered in this test; none of its own components may leak into the export.
        Assert.DoesNotContain("BlazorDevTools.UI.", context.DownloadedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("\"BlazorDevTools.DevToolsPanel\"", context.DownloadedContent, StringComparison.Ordinal);
        Assert.Contains("\"ParentComponent\"", context.DownloadedContent, StringComparison.Ordinal);
        Assert.StartsWith("# Blazor DevTools report", context.CopiedText, StringComparison.Ordinal);
        Assert.False(Session.IsCapturing);
        Assert.NotNull(Session.Components.BaselineAt);
        Assert.Contains(context.Notifications, n => n.StartsWith("Export downloaded", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ring keeps 100 samples per instance. Fractions and cause breakdowns computed from it disagreed with the
    /// session-long render counters next to them: "250 of 100 compared renders", or a Causes column that stopped at 100.
    /// </summary>
    [Fact]
    public void Inspector_and_grouped_profiler_figures_add_up_beyond_the_sample_ring()
    {
        var panel = RenderPanel();
        var cut = Render<WastedRenderTests.ListParent>();
        var child = Session.Components.All.Single(r => r.Type == typeof(WastedRenderTests.ListChild));
        panel.InvokeAsync(() => panel.Instance.SelectComponent(child.InstanceId));

        // The first render after selecting starts the capture; the 120 after it are compared, and all are forced.
        for (var i = 0; i < 121; i++)
        {
            cut.InvokeAsync(() => cut.Instance.Bump());
        }

        Assert.Equal(100, child.Renders.Length);
        panel.Find("button[aria-label='Refresh captured data']").Click();
        Assert.Contains("of 120 compared renders were forced", panel.Markup, StringComparison.Ordinal);
        var parentCause = Assert.Single(panel.FindAll("tr"), r => r.QuerySelector("td")?.TextContent == "ParentRender");
        Assert.Equal("121", parentCause.QuerySelectorAll("td")[1].TextContent.Trim());

        panel.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Profiler", StringComparison.Ordinal)).Click();
        panel.Find("input[aria-label='Group by type']").Change(true);
        var grouped = Assert.Single(panel.FindAll("tbody tr.bdt-row"), r => r.TextContent.Contains("ListChild", StringComparison.Ordinal));
        var cells = grouped.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToList();
        Assert.Equal("122", cells[2]);
        Assert.Equal("121 parent · 1 initial", cells[6]);
    }

    /// <summary>A navigation while paused is not in the timeline, so it must not be remembered as one.</summary>
    [Fact]
    public void Navigation_while_paused_leaves_no_event_id_behind()
    {
        RenderPanel();
        var navigation = Services.GetRequiredService<NavigationManager>();

        Session.PauseCapture();
        navigation.NavigateTo("/paused");
        Assert.Null(Session.LastNavigationEventId);

        Session.ResumeCapture();
        navigation.NavigateTo("/resumed");
        var recorded = Session.Timeline.Find(Session.LastNavigationEventId!.Value);
        Assert.Equal("Navigate to /resumed", recorded!.Title);
    }

    private IRenderedComponent<DevToolsPanel> RenderPanel()
    {
        var module = JSInterop.SetupModule("./_content/BlazorDevTools/devtools.js");
        module.Setup<BrowserSnapshot>("attach", _ => true).SetResult(new BrowserSnapshot { Online = true });
        module.Setup<string?>("loadPrefs", _ => true).SetResult(null);
        module.SetupVoid("savePrefs", _ => true).SetVoidResult();
        return Render<DevToolsPanel>();
    }

    private sealed class TwoChildren : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ChildComponent>(0);
            builder.AddComponentParameter(1, nameof(ChildComponent.Text), "one");
            builder.CloseComponent();
            builder.OpenComponent<ChildComponent>(2);
            builder.AddComponentParameter(3, nameof(ChildComponent.Text), "two");
            builder.CloseComponent();
        }
    }

    private sealed class RecordingContext(IServiceProvider services) : IDevToolsCommandContext
    {
        public IServiceProvider Services => services;

        public string? DownloadedFileName { get; private set; }

        public string? DownloadedContent { get; private set; }

        public string? CopiedText { get; private set; }

        public List<string> Notifications { get; } = [];

        public void ShowPanel(string panelId, string? query = null) { }

        public void SelectComponent(long instanceId) { }

        public void SelectEvent(long eventId) { }

        public void Notify(string message) => Notifications.Add(message);

        public Task<bool> CopyTextAsync(string text)
        {
            CopiedText = text;
            return Task.FromResult(true);
        }

        public Task<bool> DownloadTextAsync(string fileName, string content, string contentType = "text/plain")
        {
            DownloadedFileName = fileName;
            DownloadedContent = content;
            return Task.FromResult(true);
        }
    }
}
