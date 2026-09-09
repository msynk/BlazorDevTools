using System.Text.Json;
using BlazorDevTools.Commands;
using BlazorDevTools.Events;
using BlazorDevTools.Extensions;
using BlazorDevTools.Session;
using BlazorDevTools.UI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace BlazorDevTools;

/// <summary>
/// The DevTools overlay. Place it once inside an interactive component (normally the layout). It renders nothing
/// during prerendering or when DevTools is disabled, and it re-renders only when captured data changed.
/// </summary>
public partial class DevToolsPanel : ComponentBase, IDevToolsCommandContext, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PrefsJson = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();
    private IJSObjectReference? _module;
    private DotNetObjectReference<JsBridge>? _bridgeRef;
    private long _lastVersion = -1;
    private long _lastUiVersion = -1;
    private bool _interactive;
    private string _searchText = "";
    private Task? _loop;

    [Inject] public DevToolsSession Session { get; set; } = default!;

    [Inject] public DevToolsOptions Options { get; set; } = default!;

    [Inject] public IServiceProvider Services { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    public DevToolsUiState Ui => Session.Ui;

    public ServiceGraph? Graph => Session.Registry.ServiceGraph;

    public IReadOnlyList<DevToolsPanelDescriptor> ExtensionPanels => Session.Registry.Panels.Where(p => p.RequiredPlatform is null || string.Equals(p.RequiredPlatform, Session.Platform, StringComparison.OrdinalIgnoreCase)).ToList();

    private static readonly (string Id, string Title)[] BuiltInPanels =
    [
        ("components", "Components"),
        ("profiler", "Profiler"),
        ("timeline", "Timeline"),
        ("state", "State"),
        ("network", "Network"),
        ("interop", "JS Interop"),
        ("errors", "Errors"),
        ("services", "Services"),
        ("diagnostics", "Diagnostics"),
        ("browser", "Browser"),
        ("about", "About"),
    ];

    protected override void OnInitialized()
    {
        if (!Session.IsEnabled)
        {
            return;
        }

        _interactive = RendererInfo.IsInteractive;
        Session.Platform ??= RendererInfo.Name;
        Session.IsInteractive = RendererInfo.IsInteractive;
        Session.RenderModeName = AssignedRenderMode?.GetType().Name.Replace("RenderMode", "");
        if (!_interactive)
        {
            return;
        }

        Session.EnsureActivated();
        Navigation.LocationChanged += OnLocationChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !_interactive || !Session.IsEnabled)
        {
            return;
        }

        if (Options.EnableBrowserBridge)
        {
            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/BlazorDevTools/devtools.js");
                _bridgeRef = DotNetObjectReference.Create(new JsBridge(Session, () => InvokeAsync(() => { Toggle(); StateHasChanged(); })));
                var snapshot = await _module.InvokeAsync<BrowserSnapshot>("attach", _bridgeRef, new { shortcut = Options.Ui.ToggleShortcut, trackJsToDotNet = Options.TrackJsInterop });
                ApplySnapshot(snapshot);
                Session.Browser.BridgeAttached = true;
                var prefs = await _module.InvokeAsync<string?>("loadPrefs");
                ApplyPrefs(prefs);
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
            {
                Session.Browser.BridgeError = ex.Message;
            }
        }

        _loop = RefreshLoopAsync(_cts.Token);
        StateHasChanged();
    }

    private async Task RefreshLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(Ui.IsOpen ? Math.Max(50, Options.Ui.RefreshIntervalMs) : 1000, token);
                if (Session.Version != _lastVersion || Ui.Version != _lastUiVersion)
                {
                    await InvokeAsync(() =>
                    {
                        Session.RenderTracker.CloseBatch();
                        if (Ui.IsOpen)
                        {
                            Session.Components.Refresh();
                            Session.Diagnostics.Evaluate();
                        }

                        _lastVersion = Session.Version;
                        _lastUiVersion = Ui.Version;
                        StateHasChanged();
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var last = Session.LastNavigationEventId is { } id ? Session.Timeline.Find(id) : null;
        if (last is not null && System.Diagnostics.Stopwatch.GetElapsedTime(last.StartTicks).TotalMilliseconds < 500 && last.Title.Contains(Navigation.ToBaseRelativePath(e.Location), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Session.LastNavigationEventId = Session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.Navigation,
            Category = "navigation",
            Title = "Navigate to /" + Navigation.ToBaseRelativePath(e.Location),
            Detail = e.IsNavigationIntercepted ? "intercepted link" : "programmatic",
            ParentEventId = Session.LastUiEventId,
        });
    }

    private void ApplySnapshot(BrowserSnapshot? s)
    {
        if (s is null)
        {
            return;
        }

        var b = Session.Browser;
        b.Online = s.Online;
        b.Visibility = s.Visibility;
        b.UserAgent = s.UserAgent;
        b.JsHeapUsedMb = s.JsHeapUsedMb;
        b.JsHeapLimitMb = s.JsHeapLimitMb;
        b.NavigationLoadMs = s.NavigationLoadMs;
        b.DomContentLoadedMs = s.DomContentLoadedMs;
        b.ResourceCount = s.ResourceCount;
    }

    private void ApplyPrefs(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var prefs = JsonSerializer.Deserialize<Prefs>(json, PrefsJson);
            if (prefs is null)
            {
                return;
            }

            if (prefs.Open is { } open)
            {
                Ui.IsOpen = open;
            }

            if (Enum.TryParse<DevToolsDock>(prefs.Dock, true, out var dock))
            {
                Ui.Dock = dock;
            }

            if (Enum.TryParse<DevToolsTheme>(prefs.Theme, true, out var theme))
            {
                Ui.Theme = theme;
            }

            if (Enum.TryParse<PanelSize>(prefs.Size, true, out var size))
            {
                Ui.Size = size;
            }

            if (!string.IsNullOrEmpty(prefs.Panel))
            {
                Ui.ActivePanelId = prefs.Panel;
            }
        }
        catch (JsonException)
        {
        }
    }

    private async Task SavePrefsAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            var prefs = new Prefs { Open = Ui.IsOpen, Dock = Ui.Dock.ToString(), Theme = Ui.Theme.ToString(), Size = Ui.Size.ToString(), Panel = Ui.ActivePanelId };
            await _module.InvokeVoidAsync("savePrefs", JsonSerializer.Serialize(prefs, PrefsJson));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
        }
    }

    private sealed class Prefs
    {
        public bool? Open { get; set; }

        public string? Dock { get; set; }

        public string? Theme { get; set; }

        public string? Size { get; set; }

        public string? Panel { get; set; }
    }

    // ----- IDevToolsCommandContext -----

    public void ShowPanel(string panelId, string? query = null)
    {
        Ui.ActivePanelId = panelId;
        Ui.IsOpen = true;
        if (query is not null)
        {
            Ui.SetQuery(panelId, query);
        }

        Ui.CommandPaletteOpen = false;
        Ui.Touch();
        _ = SavePrefsAsync();
        StateHasChanged();
    }

    public void SelectComponent(long instanceId)
    {
        Ui.SelectedComponentId = instanceId;
        var record = Session.Components.Get(instanceId);
        if (record is not null)
        {
            record.CaptureState = true;
            // expand ancestors so the node is visible
            var parent = record.Parent;
            while (parent is not null)
            {
                Ui.CollapsedTreeNodes.Remove(parent.InstanceId);
                parent = parent.Parent;
            }
        }

        ShowPanel("components");
    }

    public void SelectEvent(long eventId)
    {
        Ui.SelectedEventId = eventId;
        ShowPanel("timeline");
    }

    public void SelectError(long errorId)
    {
        Ui.SelectedErrorId = errorId;
        ShowPanel("errors");
    }

    public void SelectHttp(long requestId)
    {
        Ui.SelectedHttpId = requestId;
        ShowPanel("network");
    }

    public void Notify(string message)
    {
        Ui.Notify(message);
        StateHasChanged();
    }

    public void Refresh() => StateHasChanged();

    public void Toggle()
    {
        Ui.IsOpen = !Ui.IsOpen;
        Ui.Touch();
        _ = SavePrefsAsync();
    }

    public async Task<bool> CopyAsync(string text)
    {
        if (_module is null)
        {
            return false;
        }

        try
        {
            return await _module.InvokeAsync<bool>("copyText", text);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            return false;
        }
    }

    public async Task LoadStorageAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            Session.Browser.LocalStorage = await _module.InvokeAsync<StorageEntry[]>("getStorage", "local");
            Session.Browser.SessionStorage = await _module.InvokeAsync<StorageEntry[]>("getStorage", "session");
            Session.Browser.StorageReadAt = DateTimeOffset.UtcNow;
            ApplySnapshot(await _module.InvokeAsync<BrowserSnapshot>("refresh"));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Session.Browser.BridgeError = ex.Message;
        }

        Ui.Touch();
    }

    private void SetTheme()
    {
        Ui.Theme = Ui.Theme switch { DevToolsTheme.Auto => DevToolsTheme.Dark, DevToolsTheme.Dark => DevToolsTheme.Light, _ => DevToolsTheme.Auto };
        Ui.Touch();
        _ = SavePrefsAsync();
    }

    private void SetDock()
    {
        Ui.Dock = Ui.Dock == DevToolsDock.Bottom ? DevToolsDock.Right : DevToolsDock.Bottom;
        Ui.Touch();
        _ = SavePrefsAsync();
    }

    private void SetSize()
    {
        Ui.Size = Ui.Size switch { PanelSize.Small => PanelSize.Medium, PanelSize.Medium => PanelSize.Large, _ => PanelSize.Small };
        Ui.Touch();
        _ = SavePrefsAsync();
    }

    private void OpenPalette(string? initial = null)
    {
        _searchText = initial ?? "";
        Ui.CommandPaletteOpen = true;
        Ui.Touch();
    }

    private void OnRootKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape" && Ui.CommandPaletteOpen)
        {
            Ui.CommandPaletteOpen = false;
            Ui.Touch();
        }
        else if ((e.CtrlKey || e.MetaKey) && (e.Key == "k" || e.Key == "K"))
        {
            OpenPalette();
        }
    }

    private string ThemeAttribute => Ui.Theme.ToString().ToLowerInvariant();

    private string PanelClass => $"bdt-panel bdt-dock-{Ui.Dock.ToString().ToLowerInvariant()} bdt-size-{Ui.Size.ToString().ToLowerInvariant()}";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_interactive)
        {
            Navigation.LocationChanged -= OnLocationChanged;
        }

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detach");
                await _module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or ObjectDisposedException)
            {
            }
        }

        _bridgeRef?.Dispose();
        _cts.Dispose();
    }
}
