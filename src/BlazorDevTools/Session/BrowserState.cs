namespace BlazorDevTools.Session;

public sealed record StorageEntry(string Key, int Size, string Preview);

/// <summary>Browser-side facts reported by the JS bridge. Only available when the bridge is enabled and the session is interactive.</summary>
public sealed class BrowserState
{
    public bool BridgeAttached { get; internal set; }

    public string? BridgeError { get; internal set; }

    public bool? Online { get; internal set; }

    public string? Visibility { get; internal set; }

    public string? UserAgent { get; internal set; }

    public string? ReconnectState { get; internal set; }

    public int? ReconnectAttempt { get; internal set; }

    public DateTimeOffset? ReconnectStateChangedAt { get; internal set; }

    public int ReconnectCount { get; internal set; }

    public double? JsHeapUsedMb { get; internal set; }

    public double? JsHeapLimitMb { get; internal set; }

    public double? NavigationLoadMs { get; internal set; }

    public double? DomContentLoadedMs { get; internal set; }

    public int? ResourceCount { get; internal set; }

    public int JsErrorCount { get; internal set; }

    public int JsToDotNetCalls { get; internal set; }

    public int UntrackedDotNetToJsCalls { get; internal set; }

    public IReadOnlyList<StorageEntry>? LocalStorage { get; internal set; }

    public IReadOnlyList<StorageEntry>? SessionStorage { get; internal set; }

    public DateTimeOffset? StorageReadAt { get; internal set; }
}
