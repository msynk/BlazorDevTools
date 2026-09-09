namespace BlazorDevTools.Capabilities;

/// <summary>Honest availability classification for every piece of information DevTools shows.</summary>
public enum CapabilityStatus
{
    Available,
    Partial,
    RequiresInstrumentation,
    RequiresIntegration,
    RequiresRuntimeSupport,
    NotReliable,
    NotAvailable,
}

public sealed record DevToolsCapability(string Area, string Name, CapabilityStatus Status, string Explanation)
{
    /// <summary>Platforms where the capability applies ("Server", "WebAssembly", "Static", "WebView", "All").</summary>
    public string Platforms { get; init; } = "All";
}
