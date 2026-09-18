using System.Net;
using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorDevTools.Tests;

/// <summary>
/// Pausing exists so a developer can read a buffer without the application overwriting it. Every capture path must
/// respect the flag, the application must keep working exactly as before, and errors must never be dropped.
/// </summary>
public class CaptureControlTests : BunitContext
{
    private readonly FakeJSRuntime _js = new();

    public CaptureControlTests()
    {
        Services.AddSingleton<IJSRuntime>(_js);
        Services.AddBlazorDevTools(o => o.Enabled = true);
    }

    private DevToolsSession Session => Services.GetRequiredService<DevToolsSession>();

    [Fact]
    public void Paused_renders_are_not_recorded_but_still_count_as_having_rendered()
    {
        var cut = Render<ParentComponent>();
        var parent = Session.Components.All.Single(r => r.Type == typeof(ParentComponent));
        var eventsBefore = Session.Timeline.Count;

        Session.PauseCapture();
        Assert.False(Session.IsCapturing);
        cut.InvokeAsync(() => cut.Instance.Refresh());
        cut.InvokeAsync(() => cut.Instance.Refresh());

        Assert.Equal(1, parent.RenderCount);
        Assert.Equal(2, parent.SkippedRenders);
        Assert.Equal(3, cut.Instance.RenderCalls);
        // Exactly one new row: the pause marker itself.
        Assert.Equal(eventsBefore + 1, Session.Timeline.Count);
        Assert.Equal("Capture paused", Session.Timeline.Snapshot()[^1].Title);

        Session.ResumeCapture();
        Assert.True(Session.IsCapturing);
        var resumed = Session.Timeline.Snapshot()[^1];
        Assert.Equal("Capture resumed", resumed.Title);
        Assert.Contains("2 component renders happened meanwhile", resumed.Detail, StringComparison.Ordinal);

        cut.InvokeAsync(() => cut.Instance.Refresh());
        Assert.Equal(2, parent.RenderCount);
        // Not "Initial": the instance rendered while paused, and that must not be forgotten.
        Assert.Equal(RenderCause.StateHasChanged, parent.LastRender!.Cause);
    }

    [Fact]
    public void Pause_and_resume_are_idempotent()
    {
        Session.PauseCapture();
        Session.PauseCapture();
        Session.ResumeCapture();
        Session.ResumeCapture();

        var markers = Session.Timeline.Snapshot().Where(e => e.Category == "devtools").Select(e => e.Title).ToList();
        Assert.Equal(["Capture paused", "Capture resumed"], markers);
        Assert.Null(Session.PausedAt);
    }

    [Fact]
    public async Task Http_requests_pass_through_untouched_while_paused()
    {
        var handler = new DevToolsHttpMessageHandler(Session) { InnerHandler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)) };
        using var client = new HttpClient(handler);

        Session.PauseCapture();
        var response = await client.GetAsync("https://example.test/paused");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Session.Http.Snapshot());

        Session.ResumeCapture();
        await client.GetAsync("https://example.test/resumed");
        Assert.Contains("resumed", Assert.Single(Session.Http.Snapshot()).Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Js_interop_calls_go_through_but_are_not_recorded_while_paused()
    {
        Render<ParentComponent>();
        var child = Session.Components.All.Single(r => r.Type == typeof(ChildComponent));
        var runtime = ((ChildComponent)child.Component!).JS!;

        Session.PauseCapture();
        runtime.InvokeVoidAsync("whilePaused").GetAwaiter().GetResult();
        Assert.Single(_js.Calls, "whilePaused");
        Assert.Empty(Session.Interop.Snapshot());

        Session.ResumeCapture();
        runtime.InvokeVoidAsync("afterResume").GetAwaiter().GetResult();
        Assert.Equal("afterResume", Assert.Single(Session.Interop.Snapshot()).Identifier);
    }

    [Fact]
    public void Errors_are_still_collected_while_paused_and_say_they_are_not_in_the_timeline()
    {
        Session.PauseCapture();
        var error = Session.Errors.Record(new InvalidOperationException("still important"), "test");

        Assert.Single(Session.Errors.Snapshot());
        Assert.Equal(-1, error.TimelineEventId);
        Assert.DoesNotContain(Session.Timeline.Snapshot(), e => e.Kind == DevToolsEventKind.Error);
    }

    [Fact]
    public void State_changes_while_paused_are_skipped_and_the_next_diff_spans_the_pause()
    {
        var state = new StateStoreTests.CounterState();
        Session.State.Attach(state, "test");

        state.Increment();
        Session.PauseCapture();
        state.Increment();
        state.Increment();
        Session.ResumeCapture();
        state.Increment();

        var entry = Assert.Single(Session.State.Entries);
        Assert.Equal(2, entry.ChangeCount);
        var afterResume = entry.Changes[^1];
        Assert.Contains(afterResume.Changes, c => c.Path == "Count" && c.Before == "1" && c.After == "4");
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
