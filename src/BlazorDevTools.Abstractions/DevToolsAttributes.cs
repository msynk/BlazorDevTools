namespace BlazorDevTools;

/// <summary>Excludes a component type, or a field/property from inspection.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class DevToolsIgnoreAttribute : Attribute
{
}

/// <summary>Marks a member whose value must never be shown (rendered as «redacted»).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = true)]
public sealed class DevToolsSensitiveAttribute : Attribute
{
}
