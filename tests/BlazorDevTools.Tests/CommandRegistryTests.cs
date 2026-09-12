using BlazorDevTools.Session;
using BlazorDevTools.Commands;

namespace BlazorDevTools.Tests;

public class CommandRegistryTests
{
    private static CommandRegistry Create()
    {
        var registry = new CommandRegistry();
        registry.AddRange(Commands.BuiltInCommands.Create());
        return registry;
    }

    [Fact]
    public void Exact_and_prefix_matches_rank_first()
    {
        var registry = Create();
        var results = registry.Search("> show errors");
        Assert.Equal("errors.show", results[0].Command.Id);

        results = registry.Search("show slow");
        Assert.Equal("profiler.slow", results[0].Command.Id);
    }

    [Fact]
    public void Argument_commands_capture_trailing_text()
    {
        var registry = Create();
        var results = registry.Search("Find component ProductList");
        Assert.Equal("components.find", results[0].Command.Id);
        Assert.Equal("ProductList", results[0].Argument);

        results = registry.Search("find ProductList");
        Assert.Equal("components.find", results[0].Command.Id);
        Assert.Equal("ProductList", results[0].Argument);

        results = registry.Search("show components rendering more than 40");
        Assert.Equal("profiler.renders-over", results[0].Command.Id);
        Assert.Equal("40", results[0].Argument);
    }

    [Fact]
    public void Keywords_and_subsequences_still_match()
    {
        var registry = Create();
        Assert.Contains(registry.Search("http"), r => r.Command.Id == "network.show");
        Assert.Contains(registry.Search("di"), r => r.Command.Id == "services.show");
        Assert.True(CommandRegistry.IsSubsequence("sce", "Show capabilities and overhead"));
        Assert.Empty(registry.Search("zzzzqqq"));
    }

    [Fact]
    public void Empty_query_lists_everything_grouped_by_category()
    {
        var registry = Create();
        var results = registry.Search("", limit: 100);
        Assert.Equal(registry.Commands.Count, results.Count);
        Assert.Equal(results.Select(r => r.Command.Category).OrderBy(c => c), results.Select(r => r.Command.Category));
    }

    [Fact]
    public async Task Theme_command_cycles_through_auto_dark_and_light()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var command = Assert.Single(Create().Commands, c => c.Id == "ui.theme");
            var context = new CommandContext(scope.ServiceProvider);

            Assert.Equal(DevToolsTheme.Auto, session.Ui.Theme);
            await command.ExecuteAsync(context, null);
            Assert.Equal(DevToolsTheme.Dark, session.Ui.Theme);
            await command.ExecuteAsync(context, null);
            Assert.Equal(DevToolsTheme.Light, session.Ui.Theme);
            await command.ExecuteAsync(context, null);
            Assert.Equal(DevToolsTheme.Auto, session.Ui.Theme);
            Assert.Equal(3, context.PersistPreferencesCalls);

            session.Ui.IsOpen = true;
            var close = Create().Commands.Single(c => c.Id == "ui.close");
            await close.ExecuteAsync(context, null);
            Assert.False(session.Ui.IsOpen);
            Assert.Equal(4, context.PersistPreferencesCalls);
        }
    }

    private sealed class CommandContext(IServiceProvider services) : IDevToolsCommandContext
    {
        public IServiceProvider Services => services;

        public int ShowPanelCalls { get; private set; }

        public void ShowPanel(string panelId, string? query = null) => ShowPanelCalls++;

        public int PersistPreferencesCalls { get; private set; }

        public void PersistPreferences() => PersistPreferencesCalls++;

        public void SelectComponent(long instanceId) { }

        public void SelectEvent(long eventId) { }

        public void Notify(string message) { }
    }
}
