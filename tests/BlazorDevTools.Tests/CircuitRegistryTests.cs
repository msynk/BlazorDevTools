using BlazorDevTools.Events;
using BlazorDevTools.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

public class CircuitRegistryTests
{
    [Fact]
    public void Registry_tracks_open_close_and_truncates_ids()
    {
        var registry = new CircuitRegistry();
        var info = registry.Open("abcdefghijklmnop", 1);
        Assert.Equal("abcdefgh…", info.ShortId);
        Assert.Equal(1, registry.ActiveCount);
        Assert.Equal(0, registry.ConnectedCount);

        info.IsConnected = true;
        Assert.Equal(1, registry.ConnectedCount);

        registry.Close("abcdefghijklmnop");
        Assert.Equal(0, registry.ActiveCount);
        Assert.Equal(1, registry.TotalClosed);
        Assert.True(registry.Circuits.Single().IsClosed);
    }

    [Fact]
    public void AddBlazorDevToolsServer_registers_handler_extension_and_panel()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevToolsServer(o => o.Enabled = true);
        Assert.Contains(services, d => d.ServiceType == typeof(CircuitHandler) && d.ImplementationType == typeof(DevToolsCircuitHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(CircuitRegistry));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();
        Assert.Contains("Blazor Server circuits", registry.ExtensionNames);
        var panel = Assert.Single(registry.Panels);
        Assert.Equal("circuit", panel.Id);
        Assert.Equal("Server", panel.RequiredPlatform);
        Assert.Contains(registry.Commands, c => c.Id == "circuit.show");
        Assert.Contains(registry.Rules, r => r.Id == "circuit.unstable");
    }

    [Fact]
    public void Disabled_server_devtools_registers_no_circuit_handler()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevToolsServer(o => o.Enabled = false);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(CircuitHandler));
    }

    [Fact]
    public void A_later_core_registration_can_enable_server_circuit_instrumentation()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevToolsServer(options => options.Enabled = false);
        services.AddBlazorDevTools(options => options.Enabled = true);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(CircuitHandler)
            && descriptor.ImplementationType == typeof(DevToolsCircuitHandler));
    }

    [Fact]
    public void Circuit_instability_rule_fires_on_repeated_disconnects()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.Extensions.Add(new ServerDevToolsExtension()));
        using (scope)
        {
            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Circuit, Category = "circuit", Title = "Connection down" });
            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Circuit, Category = "circuit", Title = "Connection up" });
            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Circuit, Category = "circuit", Title = "Connection down" });
            session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Circuit, Category = "reconnect", Title = "Reconnect show" });
            session.Diagnostics.Evaluate(force: true);

            var finding = Assert.Single(session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "circuit.unstable");
            Assert.Contains("dropped 2 times", finding.Diagnostic.Title);
            Assert.Contains("Reconnect show", finding.Diagnostic.Evidence);
        }
    }
}
