using BlazorDevTools.Events;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Microsoft.JSInterop;

namespace BlazorDevTools.UI;

public sealed class JsErrorPayload
{
    public string? Message { get; set; }

    public string? Source { get; set; }

    public string? Stack { get; set; }

    public string? Type { get; set; }
}

public sealed class JsCallEntry
{
    public string? Identifier { get; set; }

    public int Args { get; set; }

    public bool Sync { get; set; }

    public double DurationMs { get; set; }

    public string? Error { get; set; }

    public long At { get; set; }
}

public sealed class BrowserSnapshot
{
    public bool? Online { get; set; }

    public string? Visibility { get; set; }

    public string? UserAgent { get; set; }

    public double? JsHeapUsedMb { get; set; }

    public double? JsHeapLimitMb { get; set; }

    public double? NavigationLoadMs { get; set; }

    public double? DomContentLoadedMs { get; set; }

    public int? ResourceCount { get; set; }
}

/// <summary>Receives browser-side reports from devtools.js. One instance per DevTools panel (per session).</summary>
public sealed class JsBridge(DevToolsSession session, Action toggle)
{
    [JSInvokable]
    public void OnJsError(JsErrorPayload payload)
    {
        session.Browser.JsErrorCount++;
        session.Errors.Record(null, "javascript", payload.Message ?? "JavaScript error", exceptionType: payload.Type ?? "Error", stackTrace: payload.Stack ?? payload.Source);
    }

    [JSInvokable]
    public void OnBrowserEvent(string type, string? detail)
    {
        switch (type)
        {
            case "online":
                session.Browser.Online = true;
                break;
            case "offline":
                session.Browser.Online = false;
                break;
            case "visibility":
                session.Browser.Visibility = detail;
                break;
        }

        session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.Browser,
            Category = "browser",
            Title = type == "visibility" ? "Page " + detail : "Browser " + type,
            Severity = type == "offline" ? DevToolsSeverity.Warning : DevToolsSeverity.Info,
        });
    }

    [JSInvokable]
    public void OnReconnectState(string state, int attempt, int secondsToNext)
    {
        var browser = session.Browser;
        browser.ReconnectState = state;
        browser.ReconnectAttempt = attempt;
        browser.ReconnectStateChangedAt = DateTimeOffset.UtcNow;
        if (state == "show")
        {
            browser.ReconnectCount++;
        }

        session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.Circuit,
            Category = "reconnect",
            Title = "Reconnect " + state + (attempt > 0 ? " (attempt " + attempt + ")" : ""),
            Detail = secondsToNext > 0 ? "next attempt in " + secondsToNext + " s" : null,
            Severity = state is "failed" or "rejected" or "resume-failed" ? DevToolsSeverity.Error : state is "show" or "retrying" or "paused" ? DevToolsSeverity.Warning : DevToolsSeverity.Info,
        });
    }

    [JSInvokable]
    public void ReportJsToDotNet(JsCallEntry[] batch)
    {
        foreach (var entry in batch)
        {
            session.Browser.JsToDotNetCalls++;
            var record = new JsInteropRecord
            {
                Id = session.Interop.NextId(),
                At = DateTimeOffset.FromUnixTimeMilliseconds(entry.At),
                Direction = JsInteropDirection.JsToDotNet,
                Identifier = entry.Identifier ?? "?",
                ArgumentCount = entry.Args,
                IsSync = entry.Sync,
            };
            record.TimelineEventId = session.Timeline.Record(new DevToolsEvent
            {
                Kind = DevToolsEventKind.JsInterop,
                Category = "js-interop",
                Title = "← .NET " + record.Identifier,
                Detail = (entry.Sync ? "sync" : "async") + ", " + entry.Args + " args" + (entry.Error is null ? "" : ", failed: " + entry.Error),
                Timestamp = record.At,
                DurationMs = entry.DurationMs,
                Severity = entry.Error is null ? DevToolsSeverity.Info : DevToolsSeverity.Error,
                Data = new Dictionary<string, object?> { ["callId"] = record.Id },
            });
            session.Interop.Add(record);
            session.Interop.Complete(record, entry.DurationMs, entry.Error is null, entry.Error);
        }
    }

    [JSInvokable]
    public void OnToggleShortcut() => toggle();
}
