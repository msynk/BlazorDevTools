using BlazorDevTools.Session;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Commands;

internal static class BuiltInCommands
{
    public static IEnumerable<IDevToolsCommand> Create() =>
    [
        new DelegateCommand("components.find", "Find component", "Components", (ctx, arg) => ctx.ShowPanel("components", arg), acceptsArgument: true, keywords: ["search", "tree", "locate"]),
        new DelegateCommand("profiler.slow", "Show slow renders", "Profiler", (ctx, _) => ctx.ShowPanel("profiler", "sort:max"), keywords: ["expensive", "performance"]),
        new DelegateCommand("profiler.renders-over", "Show components rendering more than", "Profiler", (ctx, arg) => ctx.ShowPanel("profiler", "renders>" + (int.TryParse(arg?.Trim(), out var n) ? n : 20)), acceptsArgument: true, keywords: ["render count", "storm", "times"]),
        new DelegateCommand("profiler.show", "Show profiler", "Profiler", (ctx, _) => ctx.ShowPanel("profiler")),
        new DelegateCommand("timeline.show", "Show timeline", "Timeline", (ctx, arg) => ctx.ShowPanel("timeline", arg), acceptsArgument: true, keywords: ["events", "activity", "history"]),
        new DelegateCommand("errors.show", "Show errors", "Errors", (ctx, _) => ctx.ShowPanel("errors"), keywords: ["exceptions", "failures"]),
        new DelegateCommand("network.failures", "Show recent network failures", "Network", (ctx, _) => ctx.ShowPanel("network", "status:failed"), keywords: ["http", "requests", "500", "404"]),
        new DelegateCommand("network.show", "Show network", "Network", (ctx, arg) => ctx.ShowPanel("network", arg), acceptsArgument: true, keywords: ["http", "requests"]),
        new DelegateCommand("state.show", "Show state", "State", (ctx, _) => ctx.ShowPanel("state"), keywords: ["store", "providers"]),
        new DelegateCommand("interop.show", "Show JS interop", "Interop", (ctx, _) => ctx.ShowPanel("interop"), keywords: ["javascript", "invoke"]),
        new DelegateCommand("services.show", "Show services", "Services", (ctx, arg) => ctx.ShowPanel("services", arg), acceptsArgument: true, keywords: ["di", "dependency injection", "container", "lifetime"]),
        new DelegateCommand("diagnostics.show", "Show diagnostics", "Diagnostics", (ctx, _) => ctx.ShowPanel("diagnostics"), keywords: ["warnings", "rules", "problems"]),
        new DelegateCommand("diagnostics.run", "Run diagnostics now", "Diagnostics", (ctx, _) =>
        {
            ctx.Services.GetRequiredService<DevToolsSession>().Diagnostics.Evaluate(force: true);
            ctx.ShowPanel("diagnostics");
        }),
        new DelegateCommand("browser.show", "Show browser activity", "Browser", (ctx, _) => ctx.ShowPanel("browser"), keywords: ["storage", "online", "reconnect"]),
        new DelegateCommand("about.show", "Show capabilities and overhead", "DevTools", (ctx, _) => ctx.ShowPanel("about"), keywords: ["about", "capabilities", "overhead", "cost"]),
        new DelegateCommand("timeline.clear", "Clear timeline", "Timeline", (ctx, _) =>
        {
            ctx.Services.GetRequiredService<DevToolsSession>().Timeline.Clear();
            ctx.Notify("Timeline cleared");
        }),
        new DelegateCommand("all.clear", "Clear captured event histories", "DevTools", (ctx, _) =>
        {
            ctx.Services.GetRequiredService<DevToolsSession>().ClearAll();
            ctx.Notify("Captured event histories cleared");
        }, keywords: ["reset"]),
        new DelegateCommand("ui.refresh", "Refresh captured data now", "DevTools", (ctx, _) =>
        {
            ctx.RefreshView();
            ctx.Notify("Refreshed");
        }, keywords: ["reload", "update", "live"]),
        new DelegateCommand("ui.live", "Toggle live updates", "DevTools", (ctx, _) =>
        {
            var ui = ctx.Services.GetRequiredService<DevToolsSession>().Ui;
            ui.LiveUpdates = !ui.LiveUpdates;
            ui.Touch();
            ctx.PersistPreferences();
            ctx.Notify(ui.LiveUpdates ? "Live updates on" : "Live updates off");
        }, keywords: ["realtime", "polling", "auto"]),
        new DelegateCommand("ui.theme", "Toggle theme", "DevTools", (ctx, _) =>
        {
            var ui = ctx.Services.GetRequiredService<DevToolsSession>().Ui;
            ui.Theme = ui.Theme switch
            {
                DevToolsTheme.Auto => DevToolsTheme.Dark,
                DevToolsTheme.Dark => DevToolsTheme.Light,
                _ => DevToolsTheme.Auto,
            };
            ui.Touch();
            ctx.PersistPreferences();
        }, keywords: ["auto", "dark", "light"]),
        new DelegateCommand("ui.dock", "Toggle dock position", "DevTools", (ctx, _) =>
        {
            var ui = ctx.Services.GetRequiredService<DevToolsSession>().Ui;
            ui.Dock = ui.Dock == DevToolsDock.Bottom ? DevToolsDock.Right : DevToolsDock.Bottom;
            ui.Touch();
            ctx.PersistPreferences();
        }, keywords: ["bottom", "right", "side"]),
        new DelegateCommand("ui.close", "Close DevTools", "DevTools", (ctx, _) =>
        {
            var ui = ctx.Services.GetRequiredService<DevToolsSession>().Ui;
            ui.IsOpen = false;
            ui.Touch();
            ctx.PersistPreferences();
        }, keywords: ["hide"]),
    ];

    private sealed class DelegateCommand(string id, string title, string category, Action<IDevToolsCommandContext, string?> action, bool acceptsArgument = false, string[]? keywords = null) : IDevToolsCommand
    {
        public string Id => id;

        public string Title => title;

        public string Category => category;

        public IReadOnlyList<string> Keywords => keywords ?? [];

        public bool AcceptsArgument => acceptsArgument;

        public Task ExecuteAsync(IDevToolsCommandContext context, string? argument)
        {
            action(context, argument);
            return Task.CompletedTask;
        }
    }
}
