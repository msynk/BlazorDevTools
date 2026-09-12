using BlazorDevTools.Session;
using Microsoft.AspNetCore.Components;

namespace BlazorDevTools.UI;

/// <summary>
/// Built-in tabs snapshot session data in <see cref="ComponentBase.OnParametersSet"/>. Blazor skips
/// <c>SetParametersAsync</c> when a child's direct parameters are unchanged, and these tabs have none, so
/// the host cascades a monotonically increasing <see cref="Revision"/> whenever the open tab should re-read.
/// </summary>
public abstract class DevToolsTab : ComponentBase
{
    [CascadingParameter]
    public DevToolsPanel Host { get; set; } = default!;

    [CascadingParameter(Name = "bdt-rev")]
    public long Revision { get; set; }

    protected DevToolsSession Session => Host.Session;

    protected DevToolsUiState Ui => Host.Ui;
}
