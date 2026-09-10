using BlazorDevTools.Commands;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Extensions;
using BlazorDevTools.Session;
using BlazorDevTools.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

/// <summary>
/// Everything a library author is promised by the README, exercised the way they would use it: one extension class
/// that contributes a panel, a rule, a command, a state provider, a value formatter and a hidden namespace, plus
/// timeline events emitted from application code. If this test needs privileged access, the API is wrong.
/// </summary>
public class ExtensibilityTests : BunitContext
{
    public ExtensibilityTests()
    {
        Services.AddSingleton<MoneyStore>();
        Services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.Extensions.Add(new AcmeDevToolsExtension());
        });
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void An_extension_contributes_a_panel_a_rule_a_command_and_a_formatter()
    {
        var registry = Services.GetRequiredService<DevToolsRegistry>();

        Assert.Contains("Acme", registry.ExtensionNames);
        Assert.Empty(registry.ExtensionErrors);
        Assert.Contains(registry.Panels, p => p.Id == "acme" && p.ComponentType == typeof(AcmePanel));
        Assert.Contains(registry.Rules, r => r.Id == "acme.too-much-money");
        Assert.Contains(registry.Commands, c => c.Id == "acme.show");
        Assert.Contains(typeof(Money), registry.Formatters.Keys);
        Assert.Contains("Acme.Internal", registry.HiddenNamespaces);
    }

    [Fact]
    public void The_extension_panel_is_shown_and_can_be_opened_like_a_built_in_one()
    {
        var cut = Render<DevToolsPanel>();
        cut.Find(".bdt-badge").Click();

        var tab = cut.FindAll(".bdt-tab").First(t => t.TextContent.Contains("Acme", StringComparison.Ordinal));
        tab.Click();

        Assert.Contains("Acme panel says hello", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_provider_from_an_extension_is_attached_and_its_changes_are_diffed()
    {
        Session.EnsureActivated();
        var store = Services.GetRequiredService<MoneyStore>();

        store.Set(42);

        var entry = Assert.Single(Session.State.Entries, e => e.Name == "Acme money");
        Assert.Equal("extension", entry.Origin);
        var change = Assert.Single(entry.Changes);
        Assert.Equal("set", change.Action);
        Assert.Contains(change.Changes, c => c.Path == "Amount" && c.After == "42");
        Assert.Contains(Session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.StateChange && e.Title.Contains("Acme money", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_formatter_from_an_extension_is_used_by_the_inspector()
    {
        Assert.Equal("$5.00", Session.Inspector.Describe("price", new Money(5)).Display);
    }

    [Fact]
    public void Application_code_can_emit_its_own_timeline_events_through_the_public_api()
    {
        var timeline = Services.GetRequiredService<IDevToolsTimeline>();

        using (timeline.BeginActivity("acme", "Importing catalogue", "3 files"))
        {
            timeline.Record(new DevToolsEvent { Category = "acme", Title = "File 1 imported" });
        }

        var events = Session.Timeline.Snapshot();
        var activity = Assert.Single(events, e => e.Title == "Importing catalogue");
        Assert.NotNull(activity.DurationMs);
        Assert.Contains(events, e => e.Title == "File 1 imported");
    }

    [Fact]
    public void An_extension_rule_runs_alongside_the_built_in_ones()
    {
        Session.EnsureActivated();
        Services.GetRequiredService<MoneyStore>().Set(1_000_000);
        Session.Diagnostics.Evaluate(force: true);

        Assert.Contains(Session.Diagnostics.Findings, f => f.Diagnostic.RuleId == "acme.too-much-money");
    }

    [Fact]
    public void An_extension_that_throws_while_configuring_is_reported_not_fatal()
    {
        var services = new ServiceCollection();
        services.AddBlazorDevTools(o =>
        {
            o.Enabled = true;
            o.Extensions.Add(new BrokenExtension());
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<DevToolsRegistry>();

        Assert.Contains(registry.ExtensionErrors, e => e.Contains("Broken", StringComparison.Ordinal));
        Assert.True(registry.IsEnabled);
    }
}

public readonly record struct Money(decimal Amount);

public sealed class MoneyStore
{
    public decimal Amount { get; private set; }

    public event Action<string>? Changed;

    public void Set(decimal amount)
    {
        Amount = amount;
        Changed?.Invoke("set");
    }
}

public sealed class MoneyStateProvider : IStateProvider
{
    private readonly MoneyStore _store;

    public MoneyStateProvider(MoneyStore store)
    {
        _store = store;
        _store.Changed += action => Changed?.Invoke(new StateChangeInfo(action));
    }

    public string Name => "Acme money";

    public string? Description => "The Acme sample store";

    public object? GetSnapshot() => new { _store.Amount };

    public event Action<StateChangeInfo>? Changed;
}

public sealed class AcmeDevToolsExtension : IDevToolsExtension
{
    public string Name => "Acme";

    public void Configure(IDevToolsExtensionBuilder builder) => builder
        .AddPanel(new DevToolsPanelDescriptor("acme", "Acme", typeof(AcmePanel)))
        .AddStateProvider(sp => new MoneyStateProvider(sp.GetRequiredService<MoneyStore>()))
        .AddDiagnosticRule(new TooMuchMoneyRule())
        .AddCommand(new ShowAcmeCommand())
        .AddValueFormatter(typeof(Money), v => ((Money)v).Amount.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-US")))
        .HideComponentsInNamespace("Acme.Internal");

    private sealed class TooMuchMoneyRule : IDiagnosticRule
    {
        public string Id => "acme.too-much-money";

        public string Title => "Suspiciously large amount";

        public string Description => "The Acme store holds an implausible amount.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            foreach (var change in context.Events.Where(e => e.Kind == DevToolsEventKind.StateChange && e.Category == "state:Acme money"))
            {
                if (change.Detail?.Contains("1000000", StringComparison.Ordinal) == true)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Warning, "Acme store holds 1,000,000", change.Detail, "Check the import.", Fingerprint: Id);
                }
            }
        }
    }

    private sealed class ShowAcmeCommand : IDevToolsCommand
    {
        public string Id => "acme.show";

        public string Title => "Show Acme";

        public string Category => "Acme";

        public IReadOnlyList<string> Keywords => ["acme"];

        public Task ExecuteAsync(IDevToolsCommandContext context, string? argument)
        {
            context.ShowPanel("acme");
            return Task.CompletedTask;
        }
    }
}

public sealed class BrokenExtension : IDevToolsExtension
{
    public string Name => "Broken";

    public void Configure(IDevToolsExtensionBuilder builder) => throw new InvalidOperationException("nope");
}

public class AcmePanel : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddContent(1, "Acme panel says hello");
        builder.CloseElement();
    }
}
