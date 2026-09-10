using System.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Microsoft.Extensions.Http;

namespace BlazorDevTools.Instrumentation;

/// <summary>Adds <see cref="DevToolsHttpMessageHandler"/> to every <c>IHttpClientFactory</c> pipeline.</summary>
internal sealed class DevToolsHttpFilter(DevToolsOptions options, DevToolsRegistry registry) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (registry.IsEnabled && options.TrackHttp)
        {
            // Innermost, i.e. closest to the primary handler: every attempt a resilience/retry handler makes is a real
            // network request and is recorded as one. Sitting outermost would hide retries behind a single record,
            // which is exactly the pattern "why is this request repeating?" needs to show.
            builder.AdditionalHandlers.Add(new DevToolsHttpMessageHandler(options));
        }
    };
}

/// <summary>
/// Records HTTP requests (method, URL, status, duration, sizes, headers with redaction; never bodies) into the
/// session that issued them. Can also be added manually to a hand-built <c>HttpClient</c>.
/// </summary>
public sealed class DevToolsHttpMessageHandler : DelegatingHandler
{
    private static readonly HashSet<string> AlwaysRedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token", "X-CSRF-Token", "X-XSRF-Token",
    };

    private readonly DevToolsOptions _options;
    private readonly DevToolsSession? _explicitSession;

    public DevToolsHttpMessageHandler(DevToolsOptions options)
    {
        _options = options;
    }

    /// <summary>Creates a handler bound to a specific session (use in WebAssembly for hand-built clients).</summary>
    public DevToolsHttpMessageHandler(DevToolsSession session) : this(session.Options)
    {
        _explicitSession = session;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var session = _explicitSession ?? SessionResolver.Resolve();
        if (session is null || !session.IsEnabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var trigger = ActivityObserver.CurrentTriggerEvent(session);
        var url = session.Redactor.RedactUrl(request.RequestUri?.ToString() ?? "");
        var record = new HttpRecord
        {
            Id = session.Http.NextId(),
            StartedAt = DateTimeOffset.UtcNow,
            Method = request.Method.Method,
            Url = url,
            RequestBytes = request.Content?.Headers.ContentLength,
            RequestHeaders = _options.CaptureHttpHeaders ? CollectHeaders(session, request.Headers, request.Content?.Headers) : null,
            TriggerEventId = trigger?.Id,
            TriggerDescription = trigger?.Title,
        };
        record.TimelineEventId = session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.Http,
            Category = "http",
            Title = record.Method + " " + StripOrigin(url),
            Detail = "pending",
            ParentEventId = trigger?.Id,
            Data = new Dictionary<string, object?> { ["requestId"] = record.Id, ["url"] = url },
        });
        session.Http.Add(record);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            record.DurationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            record.StatusCode = (int)response.StatusCode;
            record.ReasonPhrase = response.ReasonPhrase;
            record.ResponseBytes = response.Content?.Headers.ContentLength;
            record.ContentType = response.Content?.Headers.ContentType?.ToString();
            record.ResponseHeaders = _options.CaptureHttpHeaders ? CollectHeaders(session, response.Headers, response.Content?.Headers) : null;
            var failed = record.StatusCode >= 400;
            if (failed)
            {
                session.Http.MarkFailed();
            }

            var severity = failed ? DevToolsSeverity.Error : record.DurationMs >= _options.Diagnostics.SlowHttpMs ? DevToolsSeverity.Warning : DevToolsSeverity.Info;
            session.Timeline.Complete(record.TimelineEventId, record.DurationMs.Value, $"{record.StatusCode} {record.ReasonPhrase}" + (record.ResponseBytes is { } b ? $", {FormatBytes(b)}" : ""), severity);
            if (failed)
            {
                session.Errors.Record(null, "http", $"{record.Method} {StripOrigin(url)} returned {record.StatusCode} {record.ReasonPhrase}", precedingEventId: trigger?.Id, exceptionType: "HttpStatus" + record.StatusCode);
            }

            session.Http.Touch();
            return response;
        }
        catch (Exception ex)
        {
            record.DurationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            record.Error = ex.GetType().Name + ": " + ex.Message;
            session.Http.MarkFailed();
            session.Timeline.Complete(record.TimelineEventId, record.DurationMs.Value, record.Error, DevToolsSeverity.Error);
            if (ex is not OperationCanceledException)
            {
                session.Errors.Record(ex, "http", precedingEventId: trigger?.Id);
            }

            session.Http.Touch();
            throw;
        }
    }

    private static List<KeyValuePair<string, string>> CollectHeaders(DevToolsSession session, System.Net.Http.Headers.HttpHeaders headers, System.Net.Http.Headers.HttpHeaders? contentHeaders)
    {
        var list = new List<KeyValuePair<string, string>>();
        Add(headers);
        if (contentHeaders is not null)
        {
            Add(contentHeaders);
        }

        return list;

        void Add(System.Net.Http.Headers.HttpHeaders source)
        {
            foreach (var header in source)
            {
                var value = AlwaysRedactedHeaders.Contains(header.Key) || session.Redactor.IsSensitiveName(header.Key)
                    ? Inspection.Redactor.RedactedValue
                    : string.Join(", ", header.Value);
                list.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }
    }

    internal static string StripOrigin(string url)
    {
        // Manual strip instead of Uri.PathAndQuery so redaction markers are not percent-encoded.
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var pathStart = url.IndexOf('/', schemeEnd + 3);
        return pathStart < 0 ? "/" : url[pathStart..];
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB",
    };
}
