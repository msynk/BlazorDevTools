using System.Reflection;

namespace BlazorDevTools.Inspection;

/// <summary>Decides which member, header and query names are sensitive. Case-insensitive substring match plus <see cref="DevToolsSensitiveAttribute"/>.</summary>
public sealed class Redactor
{
    public const string RedactedValue = "«redacted»";

    private readonly string[] _patterns;

    public Redactor(IEnumerable<string> patterns)
    {
        _patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
    }

    public static Redactor Default { get; } = new(new DevToolsOptions().SensitiveNamePatterns);

    public bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var pattern in _patterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsSensitive(MemberInfo member)
    {
        if (member.IsDefined(typeof(DevToolsSensitiveAttribute), inherit: true))
        {
            return true;
        }

        return IsSensitiveName(member.Name);
    }

    /// <summary>Redacts query-string values whose key looks sensitive; keeps everything else intact.</summary>
    public string RedactUrl(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return url;
        }

        var parts = url[(q + 1)..].Split('&');
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            var key = eq < 0 ? parts[i] : parts[i][..eq];
            if (IsSensitiveName(Uri.UnescapeDataString(key)))
            {
                parts[i] = key + "=" + RedactedValue;
                changed = true;
            }
        }

        return changed ? url[..(q + 1)] + string.Join('&', parts) : url;
    }
}
