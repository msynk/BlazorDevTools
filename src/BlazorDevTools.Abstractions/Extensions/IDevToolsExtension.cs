using BlazorDevTools.Commands;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.State;

namespace BlazorDevTools.Extensions;

/// <summary>Describes an additional panel. <see cref="ComponentType"/> must be a Blazor component; it receives no parameters and can inject DevTools services.</summary>
public sealed record DevToolsPanelDescriptor(string Id, string Title, Type ComponentType, int Order = 100)
{
    /// <summary>Optional platform restriction; when set the panel is only shown when the renderer name matches (e.g. "Server").</summary>
    public string? RequiredPlatform { get; init; }
}

/// <summary>Formats values of a type for display in inspectors. Return null to fall back to default formatting.</summary>
public delegate string? DevToolsValueFormatter(object value);

/// <summary>Collects everything an extension contributes. Obtained through <see cref="IDevToolsExtension.Configure"/>.</summary>
public interface IDevToolsExtensionBuilder
{
    IDevToolsExtensionBuilder AddPanel(DevToolsPanelDescriptor panel);

    IDevToolsExtensionBuilder AddDiagnosticRule(IDiagnosticRule rule);

    IDevToolsExtensionBuilder AddCommand(IDevToolsCommand command);

    /// <summary>Registers a state provider factory. It is invoked once per DevTools session (per circuit on the server).</summary>
    IDevToolsExtensionBuilder AddStateProvider(Func<IServiceProvider, IStateProvider> factory);

    IDevToolsExtensionBuilder AddValueFormatter(Type type, DevToolsValueFormatter formatter);

    /// <summary>Marks a namespace prefix whose components are hidden from the component tree (library internals).</summary>
    IDevToolsExtensionBuilder HideComponentsInNamespace(string namespacePrefix);
}

/// <summary>
/// Implemented by libraries that integrate with Blazor DevTools. Register with
/// <c>services.AddBlazorDevTools(o =&gt; o.Extensions.Add(new MyExtension()))</c> or <c>services.AddBlazorDevToolsExtension&lt;T&gt;()</c>.
/// </summary>
public interface IDevToolsExtension
{
    string Name { get; }

    void Configure(IDevToolsExtensionBuilder builder);
}
