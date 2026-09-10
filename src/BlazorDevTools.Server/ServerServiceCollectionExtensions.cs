using BlazorDevTools;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorDevToolsServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Blazor DevTools plus Blazor Server circuit instrumentation (circuit lifecycle, connection state,
    /// inbound message processing, active circuit overview). Use instead of <c>AddBlazorDevTools</c> in server apps.
    /// </summary>
    public static IServiceCollection AddBlazorDevToolsServer(this IServiceCollection services, Action<DevToolsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BlazorDevTools.Server.CircuitRegistry>();
        services.AddBlazorDevTools(options =>
        {
            configure?.Invoke(options);
            if (!options.Extensions.OfType<BlazorDevTools.Server.ServerDevToolsExtension>().Any())
            {
                options.Extensions.Add(new BlazorDevTools.Server.ServerDevToolsExtension());
            }
        });

        // AddBlazorDevTools has already resolved whether DevTools runs (and merges configuration when it was called
        // before), so the circuit handler is only registered when it will actually do something.
        var options = services.FirstOrDefault(d => d.ServiceType == typeof(DevToolsOptions))?.ImplementationInstance as DevToolsOptions;
        if (options is { ResolvedEnabled: true } && !services.Any(d => d.ServiceType == typeof(CircuitHandler) && d.ImplementationType == typeof(BlazorDevTools.Server.DevToolsCircuitHandler)))
        {
            services.AddScoped<CircuitHandler, BlazorDevTools.Server.DevToolsCircuitHandler>();
        }

        return services;
    }
}
