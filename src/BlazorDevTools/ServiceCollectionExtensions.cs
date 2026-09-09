using BlazorDevTools.Events;
using BlazorDevTools.Extensions;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Session;
using BlazorDevTools.State;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BlazorDevTools;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Blazor DevTools. Enabled only in Development unless <see cref="DevToolsOptions.Enabled"/> is set.
    /// Place <c>&lt;DevToolsPanel /&gt;</c> inside an interactive component (typically the layout) to show the UI.
    /// </summary>
    public static IServiceCollection AddBlazorDevTools(this IServiceCollection services, Action<DevToolsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DevToolsOptions();
        configure?.Invoke(options);
        var enabled = options.Enabled ?? IsDevelopment(services);

        if (services.Any(d => d.ServiceType == typeof(DevToolsRegistry)))
        {
            // Already registered: only merge extensions from a second call.
            return services;
        }

        var registry = new DevToolsRegistry(options, enabled, services);
        services.AddSingleton(options);
        services.AddSingleton(registry);

        if (!enabled)
        {
            services.AddScoped<DevToolsSession>();
            services.AddScoped<IDevToolsTimeline>(sp => sp.GetRequiredService<DevToolsSession>().TimelineApi);
            services.AddScoped<IStateProviderRegistry>(sp => sp.GetRequiredService<DevToolsSession>().StateProviders);
            return services;
        }

        services.AddScoped<DevToolsSession>();
        services.AddScoped<IDevToolsTimeline>(sp => sp.GetRequiredService<DevToolsSession>().TimelineApi);
        services.AddScoped<IStateProviderRegistry>(sp => sp.GetRequiredService<DevToolsSession>().StateProviders);

        WrapExistingActivator(services);
        services.AddScoped<IComponentActivator, DevToolsComponentActivator>();

        services.AddSingleton<IHttpMessageHandlerBuilderFilter, DevToolsHttpFilter>();
        services.AddSingleton<ILoggerProvider, DevToolsLoggerProvider>();

        return services;
    }

    /// <summary>Registers an extension. Must be called before <see cref="AddBlazorDevTools"/> takes effect, i.e. pass it through options instead when possible.</summary>
    public static IServiceCollection AddBlazorDevToolsExtension<TExtension>(this IServiceCollection services, Action<DevToolsOptions>? configure = null) where TExtension : IDevToolsExtension, new()
        => services.AddBlazorDevTools(o =>
        {
            o.Extensions.Add(new TExtension());
            configure?.Invoke(o);
        });

    /// <summary>Registers a state provider so the State panel shows it. The provider type must also be resolvable (it is registered as scoped if not already).</summary>
    public static IServiceCollection AddDevToolsStateProvider<TProvider>(this IServiceCollection services) where TProvider : class, IStateProvider
    {
        services.TryAddScoped<TProvider>();
        services.AddScoped<IStateProvider>(sp => sp.GetRequiredService<TProvider>());
        return services;
    }

    /// <summary>Exposes an already registered service as a state provider through an adapter.</summary>
    public static IServiceCollection AddDevToolsStateProvider<TService>(this IServiceCollection services, string name, Func<TService, object?> snapshot, Action<TService, Action>? subscribe = null) where TService : class
    {
        services.AddScoped<IStateProvider>(sp => new DelegateStateProvider<TService>(sp.GetRequiredService<TService>(), name, snapshot, subscribe));
        return services;
    }

    private static void WrapExistingActivator(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IComponentActivator) && !d.IsKeyedService);
        if (existing is null)
        {
            return;
        }

        services.Remove(existing);
        ServiceDescriptor holder;
        if (existing.ImplementationInstance is IComponentActivator instance)
        {
            holder = new ServiceDescriptor(typeof(InnerComponentActivator), new InnerComponentActivator(instance));
        }
        else if (existing.ImplementationFactory is { } factory)
        {
            holder = new ServiceDescriptor(typeof(InnerComponentActivator), sp => new InnerComponentActivator((IComponentActivator)factory(sp)), existing.Lifetime);
        }
        else if (existing.ImplementationType is { } type)
        {
            holder = new ServiceDescriptor(typeof(InnerComponentActivator), sp => new InnerComponentActivator((IComponentActivator)ActivatorUtilities.CreateInstance(sp, type)), existing.Lifetime);
        }
        else
        {
            return;
        }

        services.Add(holder);
    }

    private static bool IsDevelopment(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IHostEnvironment) && descriptor.ImplementationInstance is IHostEnvironment env)
            {
                return env.IsDevelopment();
            }

            if (descriptor.ServiceType.FullName == "Microsoft.AspNetCore.Components.WebAssembly.Hosting.IWebAssemblyHostEnvironment" && descriptor.ImplementationInstance is { } wasmEnv)
            {
                var environment = wasmEnv.GetType().GetProperty("Environment")?.GetValue(wasmEnv) as string;
                return string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
            }
        }

        // No environment information (tests, custom hosts): stay off unless explicitly enabled.
        return false;
    }

    private sealed class DelegateStateProvider<TService> : IStateProvider where TService : class
    {
        private readonly TService _service;
        private readonly Func<TService, object?> _snapshot;

        public DelegateStateProvider(TService service, string name, Func<TService, object?> snapshot, Action<TService, Action>? subscribe)
        {
            _service = service;
            _snapshot = snapshot;
            Name = name;
            subscribe?.Invoke(service, () => Changed?.Invoke(StateChangeInfo.Empty));
        }

        public string Name { get; }

        public string? Description => Model.TypeNames.Short(typeof(TService));

        public object? GetSnapshot() => _snapshot(_service);

        public event Action<StateChangeInfo>? Changed;
    }
}
