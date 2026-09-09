using BlazorDevTools.Commands;
using BlazorDevTools.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Extensions;

namespace BlazorDevTools.Server;

/// <summary>Contributes the Circuit panel, the circuit-instability rule and the "Show active circuits" command. Also serves as the reference extension.</summary>
public sealed class ServerDevToolsExtension : IDevToolsExtension
{
    public string Name => "Blazor Server circuits";

    public void Configure(IDevToolsExtensionBuilder builder)
    {
        builder
            .AddPanel(new DevToolsPanelDescriptor("circuit", "Circuit", typeof(UI.CircuitPanel), Order: 50) { RequiredPlatform = "Server" })
            .AddDiagnosticRule(new CircuitInstabilityRule())
            .AddCommand(new ShowCircuitsCommand());
    }

    private sealed class CircuitInstabilityRule : IDiagnosticRule
    {
        public string Id => "circuit.unstable";

        public string Title => "Circuit instability";

        public string Description => "The connection dropped repeatedly within a short time.";

        public IEnumerable<Diagnostic> Evaluate(IDiagnosticContext context)
        {
            var downs = context.EventsInWindow(DevToolsEventKind.Circuit, TimeSpan.FromMinutes(2)).Where(e => e.Title == "Connection down").ToList();
            if (downs.Count >= 2)
            {
                var browserSide = context.Events.Where(e => e.Category == "reconnect").Select(e => e.Title).Distinct().Take(4).ToList();
                yield return new Diagnostic(
                    Id,
                    DiagnosticSeverity.Warning,
                    $"Connection dropped {downs.Count} times in 2 minutes",
                    "Server-side connection-down events: " + string.Join(", ", downs.Select(d => d.Timestamp.ToLocalTime().ToString("HH:mm:ss"))) + (browserSide.Count > 0 ? ". Browser reported: " + string.Join(", ", browserSide) : "."),
                    "Check proxies/load balancers for WebSocket timeouts, long-running synchronous handlers (see Slow inbound message events) and client network conditions. Consider CircuitOptions.DisconnectedCircuitRetentionPeriod.",
                    Fingerprint: Id + ":" + downs[0].Id,
                    RelatedEventIds: downs.Select(d => d.Id).ToList());
            }

            var slow = context.EventsInWindow(DevToolsEventKind.Circuit, TimeSpan.FromMinutes(1)).Where(e => e.Category == "circuit-inbound").ToList();
            if (slow.Count >= 3)
            {
                yield return new Diagnostic(
                    "circuit.blocked",
                    DiagnosticSeverity.Warning,
                    $"{slow.Count} inbound messages blocked the circuit for ≥ {(context.GetOptions<DiagnosticsOptions>()?.SlowEventHandlerMs ?? 100):0} ms each",
                    "Longest: " + Ms(slow.Max(e => e.DurationMs ?? 0)) + ". While a message is processed, the circuit cannot handle other input or send renders, which the browser perceives as freezes and eventually as disconnects.",
                    "Move CPU-bound work to Task.Run and avoid blocking waits (.Result, .Wait()) in handlers.",
                    Fingerprint: "circuit.blocked:" + slow[0].Id,
                    RelatedEventIds: slow.Select(e => e.Id).ToList());
            }
        }

        private static string Ms(double v) => v.ToString("0.#") + " ms";
    }

    private sealed class ShowCircuitsCommand : IDevToolsCommand
    {
        public string Id => "circuit.show";

        public string Title => "Show active circuits";

        public string Category => "Circuit";

        public IReadOnlyList<string> Keywords => ["signalr", "connection", "reconnect", "server"];

        public Task ExecuteAsync(IDevToolsCommandContext context, string? argument)
        {
            context.ShowPanel("circuit");
            return Task.CompletedTask;
        }
    }
}
