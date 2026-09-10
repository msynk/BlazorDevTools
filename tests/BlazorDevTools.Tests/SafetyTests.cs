using System.Collections;
using BlazorDevTools.Inspection;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorDevTools.Tests;

/// <summary>
/// Guards the properties that make DevTools safe to leave running in a real application: it must not leak secrets,
/// must not execute application logic while inspecting, and must not corrupt the values it passes through.
/// </summary>
public class SafetyTests
{
    private sealed class Credentials
    {
        public string User { get; set; } = "ada";

        public byte[] ApiKeyBytes { get; set; } = [1, 2, 3];

        public int PasswordAttempts { get; set; } = 3;
    }

    private sealed record Login(string Email, string Password);

    private sealed class LazySequence : IEnumerable<int>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<int> GetEnumerator()
        {
            Enumerations++;
            yield return 1;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Node
    {
        public string Name { get; set; } = "root";

        public Node? Self { get; set; }
    }

    private static ObjectInspector NewInspector() => new(new DevToolsOptions(), Redactor.Default);

    [Fact]
    public void Sensitive_names_are_redacted_whatever_the_value_type()
    {
        var inspector = NewInspector();
        var children = inspector.Children(new Credentials(), "");

        Assert.Equal("ada", children.Single(c => c.Name == "User").Display.Trim('"'));
        // A key does not stop being a secret because it is stored as bytes rather than as a string.
        Assert.Equal(Redactor.RedactedValue, children.Single(c => c.Name == "ApiKeyBytes").Display);
        Assert.Equal(Redactor.RedactedValue, children.Single(c => c.Name == "PasswordAttempts").Display);
    }

    [Fact]
    public void Records_do_not_leak_sensitive_members_through_their_generated_ToString()
    {
        var inspector = NewInspector();
        var node = inspector.Describe("login", new Login("ada@example.com", "hunter2"));

        Assert.DoesNotContain("hunter2", node.Display, StringComparison.Ordinal);
        Assert.Equal("Login", node.Display);
    }

    [Fact]
    public void Flatten_does_not_enumerate_lazy_sequences()
    {
        var sequence = new LazySequence();
        var flat = NewInspector().Flatten(new { Items = sequence });

        Assert.Equal(0, sequence.Enumerations);
        Assert.Contains(flat.Keys, k => k == "Items");
    }

    [Fact]
    public void Flatten_handles_self_referencing_objects()
    {
        var node = new Node();
        node.Self = node;

        var flat = NewInspector().Flatten(node);

        Assert.Equal("\"root\"", flat["Name"]);
        Assert.Contains("circular", flat["Self"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Urls_hide_embedded_credentials_and_sensitive_query_values()
    {
        var redactor = Redactor.Default;

        Assert.Equal("https://«redacted»@api.example.com/v1", redactor.RedactUrl("https://ada:hunter2@api.example.com/v1"));
        Assert.Equal("https://api.example.com/v1?page=2&token=«redacted»", redactor.RedactUrl("https://api.example.com/v1?page=2&token=abc123"));
        Assert.Equal("https://api.example.com/v1?page=2", redactor.RedactUrl("https://api.example.com/v1?page=2"));
    }

    [Fact]
    public void Diff_keeps_only_the_most_specific_changed_paths()
    {
        var before = new Dictionary<string, string> { ["Items"] = "List (1 items)", ["Items[0].Name"] = "a", ["Other"] = "1" };
        var after = new Dictionary<string, string> { ["Items"] = "List (2 items)", ["Items[0].Name"] = "b", ["Other"] = "1" };

        var changes = ObjectInspector.Diff(before, after);

        var change = Assert.Single(changes);
        Assert.Equal("Items[0].Name", change.Path);
    }

    [Fact]
    public async Task Module_references_are_unwrapped_before_they_reach_the_framework()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var inner = new FakeJSRuntime();
            var module = new FakeJSObjectReference();
            inner.Handler = (_, _) => module;
            var tracked = inner.WithDevToolsTracking(session);

            var wrapped = await tracked.InvokeAsync<IJSObjectReference>("import", ["./x.js"]);
            Assert.IsType<TrackingJSObjectReference>(wrapped);

            await tracked.InvokeVoidAsync("use", wrapped);

            // The framework serializes an IJSObjectReference by its concrete type; handing it a wrapper silently
            // produces a plain JSON object and the JS side receives garbage.
            Assert.Same(module, Assert.Single(inner.LastArgs!));
        }
    }

    [Fact]
    public async Task Module_reference_wrapping_can_be_turned_off()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.TrackJsModuleReferences = false);
        using (scope)
        {
            var inner = new FakeJSRuntime();
            var module = new FakeJSObjectReference();
            inner.Handler = (_, _) => module;

            var result = await inner.WithDevToolsTracking(session).InvokeAsync<IJSObjectReference>("import", ["./x.js"]);

            Assert.Same(module, result);
        }
    }

    [Fact]
    public async Task Js_interop_duration_survives_the_timeline_buffer_wrapping_around()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.MaxEvents = 2);
        using (scope)
        {
            var inner = new FakeJSRuntime { Handler = (_, _) => null };
            var tracked = inner.WithDevToolsTracking(session);

            await tracked.InvokeVoidAsync("a");
            await tracked.InvokeVoidAsync("b");
            await tracked.InvokeVoidAsync("c");

            Assert.All(session.Interop.Snapshot(), call => Assert.NotNull(call.DurationMs));
        }
    }

    [Fact]
    public void Server_sessions_are_never_resolved_by_being_the_only_one()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            session.Platform = "WebAssembly";
            Assert.Same(session, SessionResolver.Resolve());

            // On a server, "the only circuit right now" is not evidence that ambient work belongs to that user.
            session.Platform = "Server";
            Assert.Null(SessionResolver.Resolve());
        }
    }

    [Fact]
    public void Component_tracking_stops_at_the_configured_budget()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.MaxTrackedComponents = 2);
        using (scope)
        {
            for (var i = 0; i < 5; i++)
            {
                session.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false);
            }

            Assert.Equal(2, session.Components.TrackedCount);
            Assert.True(session.Components.IsTruncated);
        }
    }

    [Fact]
    public void Second_registration_merges_configuration_instead_of_being_ignored()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o => o.Enabled = true);
        services.AddBlazorDevTools(o => o.MaxEvents = 17);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<DevToolsOptions>();
        Assert.Equal(17, options.MaxEvents);
        Assert.True(provider.GetRequiredService<DevToolsRegistry>().IsEnabled);
    }

    [Fact]
    public void Server_package_contributes_its_panel_even_when_core_devtools_was_added_first()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o => o.Enabled = true);
        services.AddBlazorDevToolsServer();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();
        Assert.Contains(registry.Panels, p => p.Id == "circuit");
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler));
    }
}
