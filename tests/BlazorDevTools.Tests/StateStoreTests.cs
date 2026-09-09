using BlazorDevTools.Events;
using BlazorDevTools.State;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

public class StateStoreTests
{
    public sealed class CounterState : IStateProvider
    {
        public int Count { get; private set; }

        public List<string> Log { get; } = [];

        public string Password { get; set; } = "secret";

        public string Name => "CounterState";

        public event Action<StateChangeInfo>? Changed;

        public object? GetSnapshot() => this;

        public void Increment()
        {
            Count++;
            Log.Add("inc");
            Changed?.Invoke(new StateChangeInfo("Increment", Count.ToString()));
        }
    }

    [Fact]
    public void Changes_produce_diffs_history_and_timeline_events()
    {
        var (session, scope) = TestHelpers.CreateSession(services: s => s.AddDevToolsStateProvider<CounterState>());
        using (scope)
        {
            var state = scope.ServiceProvider.GetRequiredService<CounterState>();
            var entry = Assert.Single(session.State.Entries);
            Assert.Equal("DI", entry.Origin);

            state.Increment();
            state.Increment();

            Assert.Equal(2, entry.ChangeCount);
            var changes = entry.Changes;
            Assert.Equal(2, changes.Length);
            var last = changes[1];
            Assert.Equal("Increment", last.Action);
            Assert.Contains(last.Changes, c => c.Path == "Count" && c.Before == "1" && c.After == "2");
            Assert.Contains(last.Changes, c => c.Path == "Log[1]" && c.Kind == "added");
            Assert.Equal(Inspection.Redactor.RedactedValue, last.Snapshot["Password"]);

            var evt = session.Timeline.Find(last.TimelineEventId)!;
            Assert.Equal(DevToolsEventKind.StateChange, evt.Kind);
            Assert.Equal("CounterState: Increment", evt.Title);
            Assert.Contains("Count: 1 → 2", evt.Detail);
        }
    }

    [Fact]
    public void Runtime_registration_can_be_undone()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var state = new CounterState();
            var registration = session.StateProviders.Register(state);
            Assert.Single(session.StateProviders.Providers);
            registration.Dispose();
            Assert.Empty(session.StateProviders.Providers);
            state.Increment();
            Assert.DoesNotContain(session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.StateChange);
        }
    }

    [Fact]
    public void Adapter_registration_exposes_existing_services()
    {
        var (session, scope) = TestHelpers.CreateSession(services: s =>
        {
            s.AddScoped<CounterState>();
            s.AddDevToolsStateProvider<CounterState>("Adapted", c => new { c.Count }, (c, notify) => c.Changed += _ => notify());
        });
        using (scope)
        {
            var entry = Assert.Single(session.State.Entries);
            Assert.Equal("Adapted", entry.Name);
            scope.ServiceProvider.GetRequiredService<CounterState>().Increment();
            Assert.Equal(1, entry.ChangeCount);
            Assert.Contains(entry.Changes[0].Changes, c => c.Path == "Count" && c.After == "1");
        }
    }
}
