using BlazorDevTools.Events;
using BlazorDevTools.Session;
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

    private sealed class ConcurrentState : IStateProvider
    {
        private int _count;

        public string Name => "ConcurrentState";

        public event Action<StateChangeInfo>? Changed;

        public object GetSnapshot() => new { Count = Volatile.Read(ref _count) };

        public void Increment()
        {
            var count = Interlocked.Increment(ref _count);
            Changed?.Invoke(new StateChangeInfo("Increment", count.ToString()));
        }
    }

    private sealed class ObservableState
    {
        private Action? _changed;

        public int SubscriberCount { get; private set; }

        public int Count { get; private set; }

        public event Action Changed
        {
            add
            {
                _changed += value;
                SubscriberCount++;
            }
            remove
            {
                _changed -= value;
                SubscriberCount--;
            }
        }

        public IDisposable Subscribe(Action callback)
        {
            Changed += callback;
            return new CallbackDisposable(() =>
            {
                Changed -= callback;
            });
        }

        public void Increment()
        {
            Count++;
            _changed?.Invoke();
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
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

            session.ClearAll();
            Assert.Equal(0, entry.ChangeCount);
            Assert.Empty(entry.Changes);
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

    [Fact]
    public void Disposable_adapter_subscription_is_released_with_the_scope()
    {
        var observed = new ObservableState();
        var (session, scope) = TestHelpers.CreateSession(services: services =>
        {
            services.AddSingleton(observed);
            services.AddDevToolsStateProviderWithSubscription<ObservableState>(
                "Observed",
                state => new { state.Count },
                (state, notify) => state.Subscribe(notify));
        });

        Assert.Single(session.State.Entries);
        Assert.Equal(1, observed.SubscriberCount);
        observed.Increment();
        Assert.Equal(1, session.State.Entries[0].ChangeCount);

        scope.Dispose();
        Assert.Equal(0, observed.SubscriberCount);
    }

    [Fact]
    public void Action_adapter_for_a_singleton_subscribes_only_once_across_scopes()
    {
        var observed = new ObservableState();
        var services = new ServiceCollection();
        services.AddSingleton(observed);
        services.AddDevToolsStateProvider<ObservableState>("Observed", state => new { state.Count }, (state, notify) => state.Changed += notify);
        services.AddBlazorDevTools(options => options.Enabled = true);
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        firstScope.ServiceProvider.GetRequiredService<DevToolsSession>().EnsureActivated();
        secondScope.ServiceProvider.GetRequiredService<DevToolsSession>().EnsureActivated();

        Assert.Equal(1, observed.SubscriberCount);
    }

    [Fact]
    public void Concurrent_provider_notifications_do_not_lose_change_counts()
    {
        var state = new ConcurrentState();
        var (session, scope) = TestHelpers.CreateSession(services: services => services.AddSingleton<IStateProvider>(state));
        using (scope)
        {
            Parallel.For(0, 100, _ => state.Increment());

            var entry = Assert.Single(session.State.Entries);
            Assert.Equal(100, entry.ChangeCount);
        }
    }
}
