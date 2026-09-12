using Microsoft.Extensions.DependencyInjection;
using BlazorDevTools.Events;

namespace BlazorDevTools.Tests;

public class TimelineTests
{
    [Fact]
    public void Record_assigns_monotonic_ids_and_keeps_only_the_newest_events()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.MaxEvents = 50);
        using (scope)
        {
            var ids = new List<long>();
            for (var i = 0; i < 120; i++)
            {
                ids.Add(session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Custom, Category = "test", Title = "e" + i }));
            }

            Assert.Equal(ids.OrderBy(x => x), ids);
            var snapshot = session.Timeline.Snapshot();
            Assert.Equal(50, snapshot.Length);
            Assert.Equal("e70", snapshot[0].Title);
            Assert.Equal("e119", snapshot[^1].Title);
            Assert.Null(session.Timeline.Find(ids[0]));
            Assert.NotNull(session.Timeline.Find(ids[^1]));
        }
    }

    [Fact]
    public void Complete_sets_duration_detail_and_severity()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var id = session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Http, Category = "http", Title = "GET /x" });
            var beforeCompletion = session.Timeline.Find(id)!;
            session.Timeline.Complete(id, 12.5, "200 OK", DevToolsSeverity.Warning);
            var evt = session.Timeline.Find(id)!;
            Assert.Null(beforeCompletion.DurationMs);
            Assert.Equal(12.5, evt.DurationMs);
            Assert.Equal("200 OK", evt.Detail);
            Assert.Equal(DevToolsSeverity.Warning, evt.Severity);
        }
    }

    [Fact]
    public async Task BeginActivity_measures_until_disposed()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            long id;
            using (session.TimelineApi.BeginActivity("test", "work"))
            {
                await Task.Delay(20);
                id = session.Timeline.Snapshot()[^1].Id;
            }

            var evt = session.Timeline.Find(id)!;
            Assert.NotNull(evt.DurationMs);
            Assert.True(evt.DurationMs >= 15, "duration should cover the awaited delay");
        }
    }

    [Fact]
    public void Disabled_session_records_nothing()
    {
        var collection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        collection.AddBlazorDevTools(o => o.Enabled = false);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(collection);
        var timeline = (IDevToolsTimeline)provider.GetService(typeof(IDevToolsTimeline))!;
        Assert.False(timeline.IsEnabled);
        Assert.Equal(-1, timeline.Record(new DevToolsEvent { Title = "ignored" }));
    }

    [Fact]
    public void Version_changes_on_record_and_clear()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var v0 = session.Version;
            session.Timeline.Record(new DevToolsEvent { Title = "a" });
            var v1 = session.Version;
            session.Timeline.Clear();
            var v2 = session.Version;
            Assert.True(v1 > v0);
            Assert.True(v2 > v1);
            Assert.Equal(0, session.Timeline.Count);
        }
    }
}
