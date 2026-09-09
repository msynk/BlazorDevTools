namespace BlazorDevTools.Commands;

/// <summary>A command shown in the DevTools command palette.</summary>
public interface IDevToolsCommand
{
    string Id { get; }

    string Title { get; }

    /// <summary>Grouping label, e.g. "Components", "Network".</summary>
    string Category { get; }

    /// <summary>Extra words that make the command findable.</summary>
    IReadOnlyList<string> Keywords => Array.Empty<string>();

    /// <summary>True when the command accepts free text after its title, e.g. "Find component ProductList".</summary>
    bool AcceptsArgument => false;

    Task ExecuteAsync(IDevToolsCommandContext context, string? argument);
}

/// <summary>What a command can do to the DevTools UI.</summary>
public interface IDevToolsCommandContext
{
    /// <summary>Activates a panel by id ("components", "profiler", "timeline", "state", "network", "interop", "errors", "services", "diagnostics", "browser", or an extension panel id).</summary>
    void ShowPanel(string panelId, string? query = null);

    void SelectComponent(long instanceId);

    void SelectEvent(long eventId);

    void Notify(string message);

    IServiceProvider Services { get; }
}
