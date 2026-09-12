using BlazorDevTools.Session;

namespace BlazorDevTools.Tests;

public class ErrorCenterTests
{
    [Fact]
    public void Repeats_collapse_only_within_the_same_component()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var firstComponent = session.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false)!;
            var secondComponent = session.Components.Register(new ChildComponent(), typeof(ChildComponent), isDevTools: false, isHidden: false)!;

            session.Errors.Record(new InvalidOperationException("boom"), "render", component: firstComponent);
            session.Errors.Record(new InvalidOperationException("boom"), "render", component: firstComponent);
            session.Errors.Record(new InvalidOperationException("boom"), "render", component: secondComponent);

            var errors = session.Errors.Snapshot();
            Assert.Equal(2, errors.Length);
            Assert.Equal(2, errors.Single(e => e.ComponentInstanceId == firstComponent.InstanceId).Count);
            Assert.Equal(1, errors.Single(e => e.ComponentInstanceId == secondComponent.InstanceId).Count);
        }
    }

    [Fact]
    public void Clear_all_resets_error_and_http_aggregate_counts()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            session.Errors.Record(new InvalidOperationException("boom"), "test");
            var request = new Model.HttpRecord
            {
                Id = session.Http.NextId(),
                StartedAt = DateTimeOffset.UtcNow,
                Method = "GET",
                Url = "https://example.test/fail",
                StatusCode = 500,
            };
            session.Http.Add(request);
            session.Http.MarkFailed(request);

            Assert.Equal(1, session.Errors.TotalOccurrences);
            Assert.Equal(1, session.Http.FailedCount);
            session.ClearAll();

            Assert.Equal(0, session.Errors.TotalOccurrences);
            Assert.Equal(0, session.Http.FailedCount);
            Assert.Empty(session.Errors.Snapshot());
            Assert.Empty(session.Http.Snapshot());

            session.Http.MarkFailed(request);
            Assert.Equal(0, session.Http.FailedCount);
        }
    }

    [Fact]
    public void Errors_outside_an_active_event_are_not_linked_to_an_old_click()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            session.LastUiEventId = session.Timeline.Record(new Events.DevToolsEvent
            {
                Kind = Events.DevToolsEventKind.UiEvent,
                Title = "old click",
            });

            var error = session.Errors.Record(new InvalidOperationException("later failure"), "test");

            Assert.Null(error.PrecedingEventId);
        }
    }

    [Fact]
    public void Aggregate_counts_follow_ring_buffer_eviction()
    {
        var (session, scope) = TestHelpers.CreateSession(options =>
        {
            options.MaxErrors = 2;
            options.MaxHttpRequests = 2;
        });
        using (scope)
        {
            session.Errors.Record(new InvalidOperationException("repeated"), "test");
            session.Errors.Record(new InvalidOperationException("repeated"), "test");
            session.Errors.Record(new InvalidOperationException("second"), "test");
            session.Errors.Record(new InvalidOperationException("third"), "test");

            Assert.Equal(2, session.Errors.Count);
            Assert.Equal(2, session.Errors.TotalOccurrences);

            var failed = Request(session, 500);
            session.Http.Add(failed);
            session.Http.MarkFailed(failed);
            session.Http.Add(Request(session, 200));
            session.Http.Add(Request(session, 200));

            Assert.Equal(2, session.Http.Count);
            Assert.Equal(0, session.Http.FailedCount);

            session.Http.Clear();
            var pending = Request(session, 200);
            pending.StatusCode = null;
            session.Http.Add(pending);
            pending.StatusCode = 500;
            session.Http.Add(Request(session, 200));
            session.Http.Add(Request(session, 200));
            session.Http.MarkFailed(pending);

            Assert.Equal(0, session.Http.FailedCount);
        }
    }

    private static Model.HttpRecord Request(DevToolsSession session, int statusCode) => new()
    {
        Id = session.Http.NextId(),
        StartedAt = DateTimeOffset.UtcNow,
        Method = "GET",
        Url = "https://example.test/" + statusCode,
        StatusCode = statusCode,
    };
}
