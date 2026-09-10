using BlazorDevTools.Commands;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.Extensions;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.State;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools;

/// <summary>Process-wide DevTools state: extension contributions, the DI service graph and the enabled flag. Singleton.</summary>
public sealed class DevToolsRegistry
{
    private readonly Lazy<ServiceGraph?> _serviceGraph;

    internal DevToolsRegistry(DevToolsOptions options, bool isEnabled, string enabledReason, IServiceCollection? services)
    {
        Options = options;
        IsEnabled = isEnabled;
        EnabledReason = enabledReason;
        StartedAt = DateTimeOffset.UtcNow;
        var builder = new ExtensionBuilder();
        foreach (var extension in options.Extensions)
        {
            try
            {
                extension.Configure(builder);
                ExtensionNames.Add(extension.Name);
            }
            catch (Exception ex)
            {
                ExtensionErrors.Add(extension.Name + ": " + ex.Message);
            }
        }

        Panels = builder.Panels.OrderBy(p => p.Order).ToArray();
        Rules = builder.Rules;
        Commands = builder.Commands;
        StateProviderFactories = builder.StateProviderFactories;
        Formatters = builder.Formatters;
        HiddenNamespaces = builder.HiddenNamespaces;
        _serviceGraph = new Lazy<ServiceGraph?>(() => services is null ? null : ServiceGraph.Build(services), LazyThreadSafetyMode.ExecutionAndPublication);

        if (isEnabled)
        {
            ActivityObserver.EnsureStarted();
        }
    }

    public DevToolsOptions Options { get; }

    public bool IsEnabled { get; }

    /// <summary>Why DevTools is (or is not) running, so a developer never has to guess when the panel does not appear.</summary>
    public string EnabledReason { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<DevToolsPanelDescriptor> Panels { get; }

    public IReadOnlyList<IDiagnosticRule> Rules { get; }

    public IReadOnlyList<IDevToolsCommand> Commands { get; }

    public IReadOnlyList<Func<IServiceProvider, IStateProvider>> StateProviderFactories { get; }

    public IReadOnlyDictionary<Type, DevToolsValueFormatter> Formatters { get; }

    public IReadOnlyList<string> HiddenNamespaces { get; }

    public List<string> ExtensionNames { get; } = [];

    public List<string> ExtensionErrors { get; } = [];

    /// <summary>Snapshot of the DI registrations captured at <c>AddBlazorDevTools</c> time (built lazily on first use).</summary>
    public ServiceGraph? ServiceGraph => _serviceGraph.Value;

    private sealed class ExtensionBuilder : IDevToolsExtensionBuilder
    {
        public List<DevToolsPanelDescriptor> Panels { get; } = [];

        public List<IDiagnosticRule> Rules { get; } = [];

        public List<IDevToolsCommand> Commands { get; } = [];

        public List<Func<IServiceProvider, IStateProvider>> StateProviderFactories { get; } = [];

        public Dictionary<Type, DevToolsValueFormatter> Formatters { get; } = [];

        public List<string> HiddenNamespaces { get; } = [];

        public IDevToolsExtensionBuilder AddPanel(DevToolsPanelDescriptor panel)
        {
            ArgumentNullException.ThrowIfNull(panel);
            if (!typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(panel.ComponentType))
            {
                throw new ArgumentException($"Panel '{panel.Id}' component type {panel.ComponentType.Name} is not a Blazor component.");
            }

            Panels.RemoveAll(p => p.Id == panel.Id);
            Panels.Add(panel);
            return this;
        }

        public IDevToolsExtensionBuilder AddDiagnosticRule(IDiagnosticRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            Rules.Add(rule);
            return this;
        }

        public IDevToolsExtensionBuilder AddCommand(IDevToolsCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            Commands.Add(command);
            return this;
        }

        public IDevToolsExtensionBuilder AddStateProvider(Func<IServiceProvider, IStateProvider> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            StateProviderFactories.Add(factory);
            return this;
        }

        public IDevToolsExtensionBuilder AddValueFormatter(Type type, DevToolsValueFormatter formatter)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(formatter);
            Formatters[type] = formatter;
            return this;
        }

        public IDevToolsExtensionBuilder HideComponentsInNamespace(string namespacePrefix)
        {
            if (!string.IsNullOrWhiteSpace(namespacePrefix))
            {
                HiddenNamespaces.Add(namespacePrefix);
            }

            return this;
        }
    }
}
