using System.Net;
using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorDevTools.Tests;

/// <summary>
/// DevTools runs inside the application it inspects, so a leak between sessions is a leak between users, and a leak
/// into production is a leak of everything. These tests pin the boundaries rather than trusting them.
/// </summary>
public class IsolationAndSecurityTests
{
    [Fact]
    public void Sessions_never_see_each_other_activity()
    {
        var (a, scopeA) = TestHelpers.CreateSession();
        var (b, scopeB) = TestHelpers.CreateSession();
        using (scopeA)
        using (scopeB)
        {
            a.Timeline.Record(new DevToolsEvent { Title = "secret of A" });
            a.Errors.Record(new InvalidOperationException("A failed"), "test");
            a.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false);

            Assert.Empty(b.Timeline.Snapshot());
            Assert.Empty(b.Errors.Snapshot());
            Assert.Equal(0, b.Components.TrackedCount);
            Assert.NotEqual(a.Id, b.Id);
        }
    }

    [Fact]
    public async Task Http_activity_is_not_attributed_to_an_unrelated_server_session()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            // A circuit exists, but the request runs on a thread pool thread with no dispatcher: on a server this is
            // background work, and guessing that it belongs to "the circuit that happens to be connected" would put
            // one user's server activity in another user's DevTools.
            session.Platform = "Server";

            using var handler = new DevToolsHttpMessageHandler(new DevToolsOptions()) { InnerHandler = new StubHandler() };
            using var client = new HttpClient(handler);
            await Task.Run(() => client.GetAsync("https://example.com/background"));

            Assert.Equal(0, session.Http.Count);
        }
    }

    [Fact]
    public async Task Http_activity_is_attributed_when_the_session_owns_it()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            using var handler = new DevToolsHttpMessageHandler(session) { InnerHandler = new StubHandler() };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Authorization", "Bearer super-secret");
            client.DefaultRequestHeaders.Add("X-Trace", "keep-me");

            await client.GetAsync("https://api.example.com/orders?apiKey=leak&page=2");

            var record = Assert.Single(session.Http.Snapshot());
            Assert.DoesNotContain("leak", record.Url, StringComparison.Ordinal);
            Assert.Contains("page=2", record.Url, StringComparison.Ordinal);
            Assert.Equal(Inspection.Redactor.RedactedValue, record.RequestHeaders!.Single(h => h.Key == "Authorization").Value);
            Assert.Equal("keep-me", record.RequestHeaders!.Single(h => h.Key == "X-Trace").Value);
        }
    }

    [Fact]
    public void A_disabled_devtools_records_nothing_at_all()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o => o.Enabled = false);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DevToolsSession>();

        session.Timeline.Record(new DevToolsEvent { Title = "should not be kept" });

        Assert.False(session.IsEnabled);
        Assert.Empty(session.Timeline.Snapshot());
        Assert.False(provider.GetRequiredService<DevToolsRegistry>().IsEnabled);
        Assert.Contains("Enabled was set to false", provider.GetRequiredService<DevToolsRegistry>().EnabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_production_environment_keeps_devtools_off_and_says_why()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new StubEnvironment("Production"));
        services.AddBlazorDevTools();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();

        Assert.False(registry.IsEnabled);
        Assert.Contains("Production", registry.EnabledReason, StringComparison.Ordinal);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.IComponentActivator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILoggerProvider));
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "Microsoft.AspNetCore.Components.ComponentsActivitySource");
    }

    [Fact]
    public void A_development_environment_turns_devtools_on_and_says_why()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new StubEnvironment("Development"));
        services.AddBlazorDevTools();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();

        Assert.True(registry.IsEnabled);
        Assert.Contains("Development", registry.EnabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_di_inspector_never_reads_instance_values()
    {
        var secret = new SecretHolder();
        var services = new ServiceCollection();
        services.AddSingleton(secret);
        services.AddBlazorDevTools(o => o.Enabled = true);

        using var provider = services.BuildServiceProvider();
        var graph = provider.GetRequiredService<DevToolsRegistry>().ServiceGraph!;

        var info = Assert.Single(graph.Services, s => s.ServiceType == typeof(SecretHolder));
        Assert.Equal("Instance", info.RegistrationKind);
        Assert.Equal(0, secret.Reads);
    }

    private sealed class SecretHolder
    {
        public int Reads { get; private set; }

        public string ConnectionString
        {
            get
            {
                Reads++;
                return "Server=prod;Password=hunter2";
            }
        }
    }

    private sealed class StubEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
