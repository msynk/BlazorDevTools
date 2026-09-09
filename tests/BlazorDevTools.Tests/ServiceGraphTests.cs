using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

public class ServiceGraphTests
{
    public interface IRepo { }

    public sealed class Repo : IRepo { }

    public sealed class ScopedThing(IRepo repo)
    {
        public IRepo Repo { get; } = repo;
    }

    public sealed class Captor(ScopedThing thing)
    {
        public ScopedThing Thing { get; } = thing;
    }

    public sealed class A(B b)
    {
        public B B { get; } = b;
    }

    public sealed class B(A a)
    {
        public A A { get; } = a;
    }

    [Fact]
    public void Builds_dependencies_and_consumers_from_constructors()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepo, Repo>();
        services.AddScoped<ScopedThing>();
        var graph = ServiceGraph.Build(services);

        var thing = graph.Services.Single(s => s.ServiceType == typeof(ScopedThing));
        var repo = graph.Services.Single(s => s.ServiceType == typeof(IRepo));
        Assert.Single(thing.Dependencies, repo);
        Assert.Single(repo.Consumers, thing);
        Assert.Equal(ServiceLifetime.Scoped, thing.Lifetime);
        Assert.Equal("Repo", repo.ImplementationName);
        Assert.Equal(1, thing.SubtreeSize);
    }

    [Fact]
    public void Captive_dependency_is_reported_as_error()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRepo, Repo>();
        services.AddScoped<ScopedThing>();
        services.AddSingleton<Captor>();
        var findings = ServiceGraph.Build(services).Analyze().ToList();

        var captive = Assert.Single(findings, f => f.RuleId == "di.captive");
        Assert.Contains("Captor captures scoped ScopedThing", captive.Title);
        Assert.Equal(Diagnostics.DiagnosticSeverity.Error, captive.Severity);
    }

    [Fact]
    public void Circular_dependencies_are_detected_once()
    {
        var services = new ServiceCollection();
        services.AddScoped<A>();
        services.AddScoped<B>();
        var graph = ServiceGraph.Build(services);

        var cycle = Assert.Single(graph.FindCycles());
        Assert.Equal(2, cycle.Count);
        var finding = Assert.Single(graph.Analyze(), f => f.RuleId == "di.cycle");
        Assert.Contains("A → B → A", finding.Title);
    }

    [Fact]
    public void Shadowed_registrations_are_flagged_and_unresolved_dependencies_listed()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedThing>(); // IRepo not registered
        services.AddSingleton<IRepo, Repo>();
        services.AddSingleton<IRepo>(new Repo());
        var graph = ServiceGraph.Build(services);

        var repos = graph.Services.Where(s => s.ServiceType == typeof(IRepo)).ToList();
        Assert.True(repos[0].IsShadowed);
        Assert.False(repos[1].IsShadowed);
        Assert.Equal("Instance", repos[1].RegistrationKind);
        Assert.Empty(graph.Services.Single(s => s.ServiceType == typeof(ScopedThing)).UnresolvedDependencies);
    }

    [Fact]
    public void Registry_captures_the_collection_passed_to_AddBlazorDevTools()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedThing>();
        services.AddBlazorDevTools(o => o.Enabled = true);
        services.AddSingleton<IRepo, Repo>(); // registered after: still visible because the collection is enumerated lazily
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();
        Assert.NotNull(registry.ServiceGraph);
        Assert.Contains(registry.ServiceGraph!.Services, s => s.ServiceType == typeof(IRepo));
    }
}
