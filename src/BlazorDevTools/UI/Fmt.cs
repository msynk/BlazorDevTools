using System.Globalization;
using BlazorDevTools.Events;

namespace BlazorDevTools.UI;

/// <summary>Formatting helpers for the DevTools UI.</summary>
public static class Fmt
{
    public static string Ms(double? ms) => ms is null ? "–" : ms.Value switch
    {
        < 0.01 => "<0.01 ms",
        < 10 => ms.Value.ToString("0.00", CultureInfo.InvariantCulture) + " ms",
        < 1000 => ms.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
        _ => (ms.Value / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s",
    };

    public static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string Rel(DateTimeOffset t, DateTimeOffset start) => "+" + (t - start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";

    public static string Ago(DateTimeOffset? t) => t is null ? "never" : (DateTimeOffset.UtcNow - t.Value) switch
    {
        { TotalSeconds: < 1 } => "just now",
        { TotalSeconds: < 60 } s => ((int)s.TotalSeconds) + " s ago",
        { TotalMinutes: < 60 } m => ((int)m.TotalMinutes) + " min ago",
        var h => ((int)h.TotalHours) + " h ago",
    };

    public static string Bytes(long? bytes) => bytes is null ? "–" : Instrumentation.DevToolsHttpMessageHandler.FormatBytes(bytes.Value);

    public static string Num(double value) => value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);

    public static string KindClass(DevToolsEventKind kind) => "bdt-kind bdt-kind-" + kind.ToString().ToLowerInvariant();

    public static string KindLabel(DevToolsEventKind kind) => kind switch
    {
        DevToolsEventKind.RenderBatch => "batch",
        DevToolsEventKind.UiEvent => "event",
        DevToolsEventKind.StateChange => "state",
        DevToolsEventKind.JsInterop => "js",
        DevToolsEventKind.Navigation => "nav",
        DevToolsEventKind.Diagnostic => "diag",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static string SevClass(DevToolsSeverity severity) => "bdt-sev-" + severity.ToString().ToLowerInvariant();

    public static string Lifetime(Microsoft.Extensions.DependencyInjection.ServiceLifetime lifetime) => lifetime.ToString().ToLowerInvariant();

    public static string ComponentFile(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick > 0)
        {
            name = name[..tick];
        }

        return name + ".razor";
    }
}

/// <summary>Parses the small query language shared by the panels: free text plus key:value tokens.</summary>
public sealed class QueryFilter
{
    private QueryFilter()
    {
    }

    public List<string> Terms { get; } = [];

    public Dictionary<string, string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static QueryFilter Parse(string? query)
    {
        var filter = new QueryFilter();
        if (string.IsNullOrWhiteSpace(query))
        {
            return filter;
        }

        foreach (var part in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            var gt = part.IndexOf('>');
            if (colon > 0 && colon < part.Length - 1)
            {
                filter.Tokens[part[..colon]] = part[(colon + 1)..];
            }
            else if (gt > 0 && gt < part.Length - 1)
            {
                filter.Tokens[part[..gt] + ">"] = part[(gt + 1)..];
            }
            else
            {
                filter.Terms.Add(part);
            }
        }

        return filter;
    }

    public bool MatchesText(params string?[] fields)
    {
        if (Terms.Count == 0)
        {
            return true;
        }

        foreach (var term in Terms)
        {
            var found = false;
            foreach (var field in fields)
            {
                if (field is not null && field.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    public string? Token(string name) => Tokens.TryGetValue(name, out var value) ? value : null;

    public int? IntToken(string name) => int.TryParse(Token(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public long? LongToken(string name) => long.TryParse(Token(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
