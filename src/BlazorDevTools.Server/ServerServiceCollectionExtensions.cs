using BlazorDevTools;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorDevToolsServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Blazor DevTools plus Blazor Server circuit instrumentation (circuit lifecycle, connection state,
    /// inbound message processing, active circuit overview). Call this in the server project; Interactive Auto apps
    /// must also call <c>AddBlazorDevTools</c> in the client project so the WebAssembly render path is instrumented.
    /// </summary>
    public static IServiceCollection AddBlazorDevToolsServer(this IServiceCollection services, Action<DevToolsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BlazorDevTools.Server.CircuitRegistry>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDevToolsServiceRegistration)
            && descriptor.ImplementationInstance is ServerServiceRegistration))
        {
            services.AddSingleton<IDevToolsServiceRegistration>(ServerServiceRegistration.Instance);
        }

        services.AddBlazorDevTools(options =>
        {
            configure?.Invoke(options);
            if (!options.Extensions.OfType<BlazorDevTools.Server.ServerDevToolsExtension>().Any())
            {
                options.Extensions.Add(new BlazorDevTools.Server.ServerDevToolsExtension());
            }
        });

        return services;
    }

    private sealed class ServerServiceRegistration : IDevToolsServiceRegistration
    {
        public static readonly ServerServiceRegistration Instance = new();

        public void Apply(IServiceCollection services, bool enabled)
        {
            var existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(CircuitHandler)
                && descriptor.ImplementationType == typeof(BlazorDevTools.Server.DevToolsCircuitHandler));
            if (enabled && existing is null)
            {
                services.AddScoped<CircuitHandler, BlazorDevTools.Server.DevToolsCircuitHandler>();
            }
            else if (!enabled && existing is not null)
            {
                services.Remove(existing);
            }
        }
    }
}
