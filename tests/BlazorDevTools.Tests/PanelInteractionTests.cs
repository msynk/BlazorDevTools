using BlazorDevTools.UI;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

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

    private IRenderedComponent<DevToolsPanel> RenderPanel()
    {
        var module = JSInterop.SetupModule("./_content/BlazorDevTools/devtools.js");
        module.Setup<BrowserSnapshot>("attach", _ => true).SetResult(new BrowserSnapshot { Online = true, Visibility = "visible" });
        module.Setup<string?>("loadPrefs", _ => true).SetResult(null);
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
}
