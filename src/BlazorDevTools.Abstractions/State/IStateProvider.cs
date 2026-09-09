namespace BlazorDevTools.State;

/// <summary>
/// Exposes an application state container to the State Inspector. Implement this in your own state class or in an
/// adapter for a state-management library, then register it with <see cref="IStateProviderRegistry"/> or as an
/// <c>IStateProvider</c> service.
/// </summary>
public interface IStateProvider
{
    /// <summary>Display name, e.g. "CartState".</summary>
    string Name { get; }

    /// <summary>Optional description of where the state lives (singleton service, scoped store, ...).</summary>
    string? Description => null;

    /// <summary>
    /// Returns the object to inspect. It is inspected lazily and never mutated. Returning a snapshot copy is not
    /// required; returning the live object is fine because DevTools only reads public members.
    /// </summary>
    object? GetSnapshot();

    /// <summary>Raised when state changed. DevTools records a state-change event and computes a before/after diff.</summary>
    event Action<StateChangeInfo>? Changed;
}

/// <summary>Optional metadata about a state change.</summary>
public sealed record StateChangeInfo(string? Action = null, string? Detail = null)
{
    public static readonly StateChangeInfo Empty = new();
}

public interface IStateProviderRegistry
{
    /// <summary>Registers a provider for the current DevTools session. Dispose the result to unregister.</summary>
    IDisposable Register(IStateProvider provider);

    IReadOnlyList<IStateProvider> Providers { get; }
}
