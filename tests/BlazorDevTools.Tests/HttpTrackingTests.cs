using System.Net;
using BlazorDevTools.Events;
using BlazorDevTools.Instrumentation;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDevTools.Tests;

public class HttpTrackingTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task Successful_request_is_recorded_with_status_duration_and_redacted_headers()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var handler = new DevToolsHttpMessageHandler(session)
            {
                InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", System.Text.Encoding.UTF8, "application/json") }),
            };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
            client.DefaultRequestHeaders.Add("X-Trace", "abc");

            var response = await client.GetAsync("https://example.com/api/items?token=abc&page=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var record = Assert.Single(session.Http.Snapshot());
            Assert.Equal("GET", record.Method);
            Assert.Equal(200, record.StatusCode);
            Assert.NotNull(record.DurationMs);
            Assert.Contains("token=" + Inspection.Redactor.RedactedValue, record.Url);
            Assert.Contains("page=1", record.Url);
            Assert.Equal(Inspection.Redactor.RedactedValue, record.RequestHeaders!.Single(h => h.Key == "Authorization").Value);
            Assert.Equal("abc", record.RequestHeaders!.Single(h => h.Key == "X-Trace").Value);
            Assert.Equal(11, record.ResponseBytes);

            var evt = session.Timeline.Find(record.TimelineEventId)!;
            Assert.Equal(DevToolsEventKind.Http, evt.Kind);
            Assert.Equal("GET /api/items?token=" + Inspection.Redactor.RedactedValue + "&page=1", evt.Title);
            Assert.StartsWith("200", evt.Detail);
        }
    }

    [Fact]
    public async Task Failed_status_creates_an_error_record()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var handler = new DevToolsHttpMessageHandler(session) { InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)) };
            using var client = new HttpClient(handler);
            await client.GetAsync("https://example.com/api/flaky");

            var record = Assert.Single(session.Http.Snapshot());
            Assert.True(record.IsFailed);
            Assert.Equal(1, session.Http.FailedCount);
            var error = Assert.Single(session.Errors.Snapshot());
            Assert.Equal("http", error.Source);
            Assert.Contains("500", error.Message);
        }
    }

    [Fact]
    public async Task Exceptions_are_recorded_and_rethrown()
    {
        var (session, scope) = TestHelpers.CreateSession();
        using (scope)
        {
            var handler = new DevToolsHttpMessageHandler(session) { InnerHandler = new ThrowingHandler() };
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.com/down"));

            var record = Assert.Single(session.Http.Snapshot());
            Assert.Contains("connection refused", record.Error);
            Assert.Equal(DevToolsSeverity.Error, session.Timeline.Find(record.TimelineEventId)!.Severity);
        }
    }

    [Fact]
    public async Task Requests_from_http_client_factory_are_tracked_through_the_filter()
    {
        var (session, scope) = TestHelpers.CreateSession(services: s => s.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))));
        using (scope)
        using (session.UseAmbient()) // no renderer here: attribute the async flow explicitly, as background work would
        {
            var factory = (IHttpClientFactory)scope.ServiceProvider.GetService(typeof(IHttpClientFactory))!;
            using var client = factory.CreateClient("api");
            await client.GetAsync("https://example.com/via-factory");
            var record = Assert.Single(session.Http.Snapshot());
            Assert.Equal(204, record.StatusCode);
        }
    }
}
