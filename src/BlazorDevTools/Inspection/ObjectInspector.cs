using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BlazorDevTools.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorDevTools.Inspection;

public enum NodeKind
{
    Null,
    Primitive,
    String,
    Enum,
    Object,
    Collection,
    Dictionary,
    Redacted,
    Error,
    Special,
    Circular,
}

/// <summary>A lazily expandable view over one value. Children are resolved on demand by re-walking <see cref="Path"/> from the root object.</summary>
public sealed record InspectedNode(string Name, string? TypeName, string Display, NodeKind Kind, string Path, bool HasChildren, int? Count = null)
{
    public bool IsExpandable => HasChildren && Kind is not (NodeKind.Circular or NodeKind.Redacted or NodeKind.Error);
}

/// <summary>
/// Safe, bounded, lazy object inspection. Never mutates values, catches throwing getters, redacts sensitive members,
/// detects cycles along the expansion path and caps strings and collections.
/// </summary>
public sealed class ObjectInspector
{
    private static readonly ConcurrentDictionary<Type, MemberInfo[]> MemberCache = new();
    private readonly Redactor _redactor;
    private readonly IReadOnlyDictionary<Type, DevToolsValueFormatter> _formatters;
    private readonly int _maxItems;
    private readonly int _maxString;
    private readonly int _maxDepth;

    public ObjectInspector(DevToolsOptions options, Redactor redactor, IReadOnlyDictionary<Type, DevToolsValueFormatter>? formatters = null)
    {
        _redactor = redactor;
        _formatters = formatters ?? new Dictionary<Type, DevToolsValueFormatter>();
        _maxItems = Math.Max(1, options.MaxCollectionItems);
        _maxString = Math.Max(16, options.MaxStringLength);
        _maxDepth = Math.Max(1, options.MaxInspectionDepth);
    }

    public InspectedNode Describe(string name, object? value, string path = "", MemberInfo? member = null)
    {
        if (member is not null && _redactor.IsSensitive(member) || member is null && _redactor.IsSensitiveName(name) && value is string)
        {
            return new InspectedNode(name, value?.GetType() is { } rt ? Model.TypeNames.Short(rt) : null, Redactor.RedactedValue, NodeKind.Redacted, path, false);
        }

        return DescribeCore(name, value, path, ancestors: null);
    }

    /// <summary>Resolves the children of the node at <paramref name="path"/> relative to <paramref name="root"/>.</summary>
    public IReadOnlyList<InspectedNode> Children(object? root, string path)
    {
        var ancestors = new List<object>();
        object? current;
        try
        {
            current = Resolve(root, path, ancestors);
        }
        catch (Exception ex)
        {
            return [new InspectedNode("error", null, "«" + ex.GetType().Name + ": " + ex.Message + "»", NodeKind.Error, path, false)];
        }

        if (current is null)
        {
            return [];
        }

        return ChildrenOf(current, path, ancestors);
    }

    private IReadOnlyList<InspectedNode> ChildrenOf(object value, string path, List<object> ancestors)
    {
        var result = new List<InspectedNode>();
        ancestors.Add(value);
        try
        {
            if (value is IDictionary dictionary)
            {
                var i = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (i++ >= _maxItems)
                    {
                        result.Add(new InspectedNode("…", null, $"{dictionary.Count - _maxItems} more items", NodeKind.Special, path, false));
                        break;
                    }

                    var key = FormatKey(entry.Key);
                    result.Add(DescribeCore(key, entry.Value, path + "[" + key + "]", ancestors));
                }

                return result;
            }

            if (value is IEnumerable enumerable and not string)
            {
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= _maxItems)
                    {
                        result.Add(new InspectedNode("…", null, "more items not shown", NodeKind.Special, path, false));
                        break;
                    }

                    result.Add(DescribeCore("[" + i + "]", item, path + "[" + i + "]", ancestors));
                    i++;
                }

                return result;
            }

            foreach (var member in GetMembers(value.GetType()))
            {
                var childPath = path.Length == 0 ? member.Name : path + "." + member.Name;
                if (_redactor.IsSensitive(member))
                {
                    result.Add(new InspectedNode(member.Name, Model.TypeNames.Short(MemberType(member)), Redactor.RedactedValue, NodeKind.Redacted, childPath, false));
                    continue;
                }

                object? childValue;
                try
                {
                    childValue = GetValue(member, value);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    result.Add(new InspectedNode(member.Name, Model.TypeNames.Short(MemberType(member)), "«threw " + inner.GetType().Name + "»", NodeKind.Error, childPath, false));
                    continue;
                }

                result.Add(DescribeCore(member.Name, childValue, childPath, ancestors));
            }

            return result;
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    private InspectedNode DescribeCore(string name, object? value, string path, List<object>? ancestors)
    {
        if (value is null)
        {
            return new InspectedNode(name, null, "null", NodeKind.Null, path, false);
        }

        var type = value.GetType();
        var typeName = Model.TypeNames.Short(type);

        if (ancestors is not null && !type.IsValueType && value is not string)
        {
            foreach (var ancestor in ancestors)
            {
                if (ReferenceEquals(ancestor, value))
                {
                    return new InspectedNode(name, typeName, "«circular reference»", NodeKind.Circular, path, false);
                }
            }
        }

        if (ancestors is not null && ancestors.Count >= _maxDepth)
        {
            return new InspectedNode(name, typeName, "«max depth»", NodeKind.Special, path, false);
        }

        if (TryFormatWithExtension(value, out var formatted))
        {
            return new InspectedNode(name, typeName, formatted!, NodeKind.Special, path, false);
        }

        if (value is string s)
        {
            return new InspectedNode(name, "string", Quote(s), NodeKind.String, path, false);
        }

        if (type.IsPrimitive || value is decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid or Uri or Version)
        {
            return new InspectedNode(name, typeName, FormatPrimitive(value), NodeKind.Primitive, path, false);
        }

        if (type.IsEnum)
        {
            return new InspectedNode(name, typeName, value.ToString() ?? "", NodeKind.Enum, path, false);
        }

        if (TrySpecial(value, type, out var special))
        {
            return new InspectedNode(name, typeName, special!, NodeKind.Special, path, false);
        }

        if (value is IDictionary dictionary)
        {
            return new InspectedNode(name, typeName, $"{typeName} ({dictionary.Count} entries)", NodeKind.Dictionary, path, dictionary.Count > 0, dictionary.Count);
        }

        if (value is IEnumerable enumerable)
        {
            var count = TryCount(enumerable);
            var display = count is null ? typeName + " (enumerable)" : $"{typeName} ({count} items)";
            return new InspectedNode(name, typeName, display, NodeKind.Collection, path, count is null or > 0, count);
        }

        var summary = Summarize(value, type);
        return new InspectedNode(name, typeName, summary, NodeKind.Object, path, GetMembers(type).Length > 0);
    }

    private bool TryFormatWithExtension(object value, out string? formatted)
    {
        formatted = null;
        if (_formatters.Count == 0)
        {
            return false;
        }

        for (var t = value.GetType(); t is not null; t = t.BaseType)
        {
            if (_formatters.TryGetValue(t, out var formatter))
            {
                formatted = formatter(value);
                return formatted is not null;
            }
        }

        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (_formatters.TryGetValue(iface, out var formatter))
            {
                formatted = formatter(value);
                return formatted is not null;
            }
        }

        return false;
    }

    private bool TrySpecial(object value, Type type, out string? display)
    {
        display = value switch
        {
            RenderFragment => "RenderFragment",
            Delegate d => "delegate " + (d.Method.DeclaringType is { } dt ? Model.TypeNames.Short(dt) + "." : "") + d.Method.Name,
            EventCallback ec => ec.HasDelegate ? "EventCallback (bound)" : "EventCallback (empty)",
            Type t => "typeof(" + Model.TypeNames.Short(t) + ")",
            Task task => "Task: " + task.Status,
            Exception ex => ex.GetType().Name + ": " + ex.Message,
            JsonElement je => je.ValueKind == JsonValueKind.String ? Quote(je.GetString() ?? "") : je.ToString(),
            ElementReference er => "ElementReference " + (string.IsNullOrEmpty(er.Id) ? "(unset)" : er.Id),
            IJSObjectReference => "IJSObjectReference",
            IJSRuntime => "IJSRuntime",
            IServiceProvider => "IServiceProvider",
            CancellationToken ct => "CancellationToken (" + (ct.IsCancellationRequested ? "cancelled" : "active") + ")",
            CancellationTokenSource cts => "CancellationTokenSource (" + (cts.IsCancellationRequested ? "cancelled" : "active") + ")",
            Stream st => Model.TypeNames.Short(st.GetType()) + (st.CanSeek ? $" ({st.Length} bytes)" : ""),
            _ => null,
        };

        if (display is null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EventCallback<>))
        {
            display = "EventCallback";
        }

        if (display is null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RenderFragment<>))
        {
            display = "RenderFragment<T>";
        }

        return display is not null;
    }

    private string Summarize(object value, Type type)
    {
        if (type.IsValueType && type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType == type)
        {
            return SafeToString(value);
        }

        var toStringDeclaringType = type.GetMethod("ToString", Type.EmptyTypes)?.DeclaringType;
        if (toStringDeclaringType is not null && toStringDeclaringType != typeof(object) && toStringDeclaringType != typeof(ValueType))
        {
            // records and user ToString overrides can leak sensitive members; show them only when short.
            var str = SafeToString(value);
            if (str.Length <= 120 && !_redactor.IsSensitiveName(str))
            {
                return str;
            }
        }

        return Model.TypeNames.Short(type);
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return "«ToString threw " + ex.GetType().Name + "»";
        }
    }

    private string Quote(string s)
    {
        if (s.Length > _maxString)
        {
            s = s[.._maxString] + "… (+" + (s.Length - _maxString).ToString(CultureInfo.InvariantCulture) + " chars)";
        }

        return "\"" + s.Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    private static string FormatPrimitive(object value) => value switch
    {
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string FormatKey(object? key) => key switch
    {
        null => "null",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString() ?? "?",
    };

    private static int? TryCount(IEnumerable enumerable) => enumerable switch
    {
        ICollection c => c.Count,
        _ => enumerable.GetType().GetProperty("Count")?.GetValue(enumerable) as int?,
    };

    private static MemberInfo[] GetMembers(Type type) => MemberCache.GetOrAdd(type, static t =>
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(DevToolsIgnoreAttribute), true))
            .Where(p => p.GetMethod?.IsPublic == true)
            .Cast<MemberInfo>();
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsDefined(typeof(DevToolsIgnoreAttribute), true))
            .Cast<MemberInfo>();
        return props.Concat(fields).ToArray();
    });

    private static Type MemberType(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

    private static object? GetValue(MemberInfo member, object target) => member is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)member).GetValue(target);

    private object? Resolve(object? root, string path, List<object> ancestors)
    {
        var current = root;
        if (string.IsNullOrEmpty(path))
        {
            return current;
        }

        foreach (var segment in ParsePath(path))
        {
            if (current is null)
            {
                return null;
            }

            ancestors.Add(current);
            if (segment.IsIndex)
            {
                current = Index(current, segment.Value);
            }
            else
            {
                var member = GetMembers(current.GetType()).FirstOrDefault(m => m.Name == segment.Value)
                    ?? throw new InvalidOperationException($"Member '{segment.Value}' not found on {current.GetType().Name}.");
                if (_redactor.IsSensitive(member))
                {
                    return null;
                }

                current = GetValue(member, current);
            }
        }

        return current;
    }

    private static object? Index(object target, string key)
    {
        if (target is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (FormatKey(entry.Key) == key)
                {
                    return entry.Value;
                }
            }

            return null;
        }

        if (target is IEnumerable enumerable && int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                if (i++ == index)
                {
                    return item;
                }
            }
        }

        return null;
    }

    private readonly record struct PathSegment(string Value, bool IsIndex);

    private static IEnumerable<PathSegment> ParsePath(string path)
    {
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                continue;
            }

            if (path[i] == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0)
                {
                    end = path.Length;
                }

                yield return new PathSegment(path[(i + 1)..end], true);
                i = end + 1;
                continue;
            }

            var next = i;
            while (next < path.Length && path[next] != '.' && path[next] != '[')
            {
                next++;
            }

            yield return new PathSegment(path[i..next], false);
            i = next;
        }
    }

    /// <summary>
    /// Produces a bounded path → display map used for change detection (state diffs, component field diffs).
    /// Delegates, render fragments, services and streams are summarized, never traversed.
    /// </summary>
    public Dictionary<string, string> Flatten(object? root, int maxDepth = 3, int maxItems = 25, int maxEntries = 400)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var ancestors = new List<object>();
        FlattenInto(result, "", root, 0, maxDepth, maxItems, maxEntries, ancestors, null);
        return result;
    }

    private void FlattenInto(Dictionary<string, string> result, string path, object? value, int depth, int maxDepth, int maxItems, int maxEntries, List<object> ancestors, MemberInfo? member)
    {
        if (result.Count >= maxEntries)
        {
            return;
        }

        var node = member is null ? DescribeCore(path, value, path, ancestors) : Describe(member.Name, value, path, member);
        if (path.Length > 0)
        {
            result[path] = node.Display;
        }

        if (value is null || depth >= maxDepth || !node.IsExpandable || node.Kind is NodeKind.Special)
        {
            return;
        }

        ancestors.Add(value);
        try
        {
            if (value is IDictionary dictionary)
            {
                var i = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (i++ >= maxItems)
                    {
                        break;
                    }

                    var key = FormatKey(entry.Key);
                    FlattenInto(result, path + "[" + key + "]", entry.Value, depth + 1, maxDepth, maxItems, maxEntries, ancestors, null);
                }
            }
            else if (value is IEnumerable enumerable and not string)
            {
                var i = 0;
                foreach (var item in enumerable)
                {
                    if (i >= maxItems)
                    {
                        break;
                    }

                    FlattenInto(result, path + "[" + i + "]", item, depth + 1, maxDepth, maxItems, maxEntries, ancestors, null);
                    i++;
                }
            }
            else
            {
                foreach (var m in GetMembers(value.GetType()))
                {
                    var childPath = path.Length == 0 ? m.Name : path + "." + m.Name;
                    object? child;
                    try
                    {
                        child = _redactor.IsSensitive(m) ? Redactor.RedactedValue : GetValue(m, value);
                    }
                    catch
                    {
                        result[childPath] = "«threw»";
                        continue;
                    }

                    FlattenInto(result, childPath, child, depth + 1, maxDepth, maxItems, maxEntries, ancestors, m);
                }
            }
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    /// <summary>Computes changed, added and removed entries between two flattened snapshots.</summary>
    public static List<Model.StateDiffEntry> Diff(IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string> after)
    {
        var changes = new List<Model.StateDiffEntry>();
        before ??= new Dictionary<string, string>();
        foreach (var (path, newValue) in after)
        {
            if (!before.TryGetValue(path, out var oldValue))
            {
                changes.Add(new Model.StateDiffEntry(path, null, newValue));
            }
            else if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new Model.StateDiffEntry(path, oldValue, newValue));
            }
        }

        foreach (var (path, oldValue) in before)
        {
            if (!after.ContainsKey(path))
            {
                changes.Add(new Model.StateDiffEntry(path, oldValue, null));
            }
        }

        // Collapse parent + child changes so "Items" and "Items[0].Name" do not both show; keep the most specific ones.
        changes.RemoveAll(c => changes.Any(other => other != c && other.Path.StartsWith(c.Path, StringComparison.Ordinal) && other.Path.Length > c.Path.Length && (other.Path[c.Path.Length] is '.' or '[')));
        return changes;
    }
}
