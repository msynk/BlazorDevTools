namespace BlazorDevTools.Model;

/// <summary>An error captured from any source (render, event handler, JS, HTTP, circuit, logger).</summary>
public sealed class ErrorRecord
{
    public required long Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Source { get; init; }

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }

    public string? SourceLocation { get; init; }

    public long? ComponentInstanceId { get; init; }

    public string? ComponentName { get; init; }

    /// <summary>The UI event or activity that immediately preceded the error, when known.</summary>
    public long? PrecedingEventId { get; init; }

    public long TimelineEventId { get; set; }

    /// <summary>Stable key for grouping repeats.</summary>
    public required string Fingerprint { get; init; }

    public int Count { get; internal set; } = 1;

    public DateTimeOffset LastSeen { get; internal set; }

    /// <summary>
    /// "Type: message" of the innermost exception when it differs from the outer one. The exception object itself is
    /// deliberately not retained: its graph can reference components, services and closures, and the error center
    /// keeps hundreds of records for the lifetime of the session.
    /// </summary>
    public string? InnerError { get; init; }
}

public sealed class HttpRecord
{
    internal int StoreGeneration { get; set; }

    internal bool FailureCounted { get; set; }

    public required long Id { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string Method { get; init; }

    public required string Url { get; init; }

    public int? StatusCode { get; set; }

    public string? ReasonPhrase { get; set; }

    public double? DurationMs { get; set; }

    public long? RequestBytes { get; set; }

    public long? ResponseBytes { get; set; }

    public string? Error { get; set; }

    public string? ContentType { get; set; }

    public IReadOnlyList<KeyValuePair<string, string>>? RequestHeaders { get; set; }

    public IReadOnlyList<KeyValuePair<string, string>>? ResponseHeaders { get; set; }

    public long? TriggerEventId { get; init; }

    public string? TriggerDescription { get; init; }

    public long TimelineEventId { get; set; }

    public bool IsFailed => Error is not null || StatusCode >= 400;

    public bool IsPending => DurationMs is null;

    /// <summary>Method + URL without query values, used for duplicate detection.</summary>
    public string Signature => Method + " " + Url;
}

public enum JsInteropDirection
{
    DotNetToJs,
    JsToDotNet,
}

public sealed class JsInteropRecord
{
    public required long Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required JsInteropDirection Direction { get; init; }

    public required string Identifier { get; init; }

    public double? DurationMs { get; set; }

    public bool Succeeded { get; set; } = true;

    public string? Error { get; set; }

    public int ArgumentCount { get; init; }

    public string? ArgumentTypes { get; init; }

    public string? ResultType { get; init; }

    public long? ComponentInstanceId { get; init; }

    public string? ComponentName { get; init; }

    public bool IsModuleCall { get; init; }

    public bool IsSync { get; init; }

    public long TimelineEventId { get; set; }

    /// <summary>High-resolution start timestamp, so the duration never depends on the timeline still holding the event.</summary>
    internal long StartTicks { get; init; }
}

/// <summary>One recorded change of a registered state provider.</summary>
public sealed class StateChangeRecord
{
    public required long Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Provider { get; init; }

    public string? Action { get; init; }

    public string? Detail { get; init; }

    /// <summary>Path → (before, after) for members whose display value changed.</summary>
    public required IReadOnlyList<StateDiffEntry> Changes { get; init; }

    /// <summary>Flattened snapshot after the change (bounded), used by history browsing.</summary>
    public required IReadOnlyDictionary<string, string> Snapshot { get; init; }

    public long? TriggerEventId { get; init; }

    public long TimelineEventId { get; set; }
}

public sealed record StateDiffEntry(string Path, string? Before, string? After)
{
    public string Kind => Before is null ? "added" : After is null ? "removed" : "changed";
}
