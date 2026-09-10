using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BlazorDevTools.Model;

/// <summary>
/// The structured data of a render timeline event, projected from the <see cref="RenderSample"/> instead of copied
/// into a dictionary.
/// <para>
/// Renders are by far the highest-frequency event in a Blazor application: a dictionary with four boxed values per
/// render dominated DevTools' allocation profile (roughly 1 KB per component render, and the GC pauses that come
/// with it). This keeps the same read-only contract for extensions and the UI at the cost of one small object.
/// </para>
/// </summary>
internal sealed class RenderEventData(RenderSample sample, int renderCount) : IReadOnlyDictionary<string, object?>
{
    private static readonly string[] Keys = ["cause", "batch", "renderCount", "changedState"];

    public IEnumerable<string> Keys2 => Keys;

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values
    {
        get
        {
            foreach (var key in Keys)
            {
                yield return this[key];
            }
        }
    }

    public int Count => Keys.Length;

    public object? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => Array.IndexOf(Keys, key) >= 0;

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        switch (key)
        {
            case "cause":
                value = RenderCauseNames.Of(sample.Cause);
                return true;
            case "batch":
                value = sample.BatchId;
                return true;
            case "renderCount":
                value = renderCount;
                return true;
            case "changedState":
                value = sample.ChangedState;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var key in Keys)
        {
            yield return new KeyValuePair<string, object?>(key, this[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Cached names so attributing a render never calls <c>Enum.ToString</c>, which allocates.</summary>
internal static class RenderCauseNames
{
    private static readonly string[] Names = Enum.GetNames<RenderCause>();

    public static string Of(RenderCause cause)
    {
        var index = (int)cause;
        return (uint)index < (uint)Names.Length ? Names[index] : cause.ToString();
    }
}
