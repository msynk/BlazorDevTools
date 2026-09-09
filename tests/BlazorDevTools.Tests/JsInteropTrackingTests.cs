using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using Microsoft.JSInterop;

namespace BlazorDevTools.Tests;

public class JsInteropTrackingTests
{
    [Fact]
    public async Task Calls_are_recorded_with_identifier_duration_and_result_type()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var fake = new FakeJSRuntime { Handler = (id, _) => id == "add" ? 3 : null };
            var tracked = fake.WithDevToolsTracking(session);
            Assert.IsType<TrackingJSRuntime>(tracked);

            var result = await tracked.InvokeAsync<int>("add", 1, 2);
            Assert.Equal(3, result);

            var record = Assert.Single(session.Interop.Snapshot());
            Assert.Equal("add", record.Identifier);
            Assert.Equal(JsInteropDirection.DotNetToJs, record.Direction);
            Assert.Equal(2, record.ArgumentCount);
            Assert.Equal("Int32, Int32", record.ArgumentTypes);
            Assert.Equal("Int32", record.ResultType);
            Assert.True(record.Succeeded);
            Assert.NotNull(record.DurationMs);
            Assert.Equal(DevToolsEventKind.JsInterop, session.Timeline.Find(record.TimelineEventId)!.Kind);
            Assert.Single(session.Interop.Aggregates());
        }
    }

    [Fact]
    public async Task Failures_are_recorded_and_rethrown()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var fake = new FakeJSRuntime { Handler = (_, _) => throw new JSException("nope") };
            var tracked = fake.WithDevToolsTracking(session);
            await Assert.ThrowsAsync<JSException>(async () => await tracked.InvokeVoidAsync("explode"));

            var record = Assert.Single(session.Interop.Snapshot());
            Assert.False(record.Succeeded);
            Assert.Contains("nope", record.Error);
            Assert.Equal("void", record.ResultType);
            var error = Assert.Single(session.Errors.Snapshot());
            Assert.Equal("js-interop", error.Source);
        }
    }

    [Fact]
    public async Task Module_references_are_wrapped_so_module_calls_are_tracked_too()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var module = new FakeJSObjectReference();
            var fake = new FakeJSRuntime { Handler = (id, _) => id == "import" ? module : null };
            var tracked = fake.WithDevToolsTracking(session);

            var imported = await tracked.InvokeAsync<IJSObjectReference>("import", "./x.js");
            Assert.IsType<TrackingJSObjectReference>(imported);
            await imported.InvokeVoidAsync("doWork", 1);

            var calls = session.Interop.Snapshot();
            Assert.Equal(2, calls.Length);
            Assert.Equal("module.doWork", calls[1].Identifier);
            Assert.True(calls[1].IsModuleCall);
            Assert.Single(module.Calls, "doWork");
        }
    }

    [Fact]
    public void Wrapping_is_idempotent_and_disabled_sessions_return_the_original()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var fake = new FakeJSRuntime();
            var once = fake.WithDevToolsTracking(session);
            var twice = once.WithDevToolsTracking(session);
            Assert.Same(fake, ((TrackingJSRuntime)twice).Inner);
        }

        var collection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        collection.AddBlazorDevTools(o => o.Enabled = false);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(collection);
        var disabled = (Session.DevToolsSession)provider.GetService(typeof(Session.DevToolsSession))!;
        var raw = new FakeJSRuntime();
        Assert.Same(raw, raw.WithDevToolsTracking(disabled));
    }
}
