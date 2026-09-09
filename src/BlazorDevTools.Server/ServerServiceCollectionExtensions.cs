using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorDevTools;

public static class ServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Blazor DevTools plus Blazor Server circuit instrumentation (circuit lifecycle, connection state,
    /// inbound message processing, active circuit overview). Use instead of <c>AddBlazorDevTools</c> in server apps.
    /// </summary>
    public static IServiceCollection AddBlazorDevToolsServer(this IServiceCollection services, Action<DevToolsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Server.CircuitRegistry>();
        services.AddBlazorDevTools(options =>
        {
            configure?.Invoke(options);
            if (!options.Extensions.OfType<Server.ServerDevToolsExtension>().Any())
            {
                options.Extensions.Add(new Server.ServerDevToolsExtension());
            }
        });

        var registry = services.LastOrDefault(d => d.ServiceType == typeof(DevToolsRegistry))?.ImplementationInstance as DevToolsRegistry;
        if (registry is { IsEnabled: true })
        {
            services.AddScoped<CircuitHandler, Server.DevToolsCircuitHandler>();
        }

        return services;
    }
}
