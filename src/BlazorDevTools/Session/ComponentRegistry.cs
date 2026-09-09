using System.Collections.Concurrent;
using System.Diagnostics;
using BlazorDevTools.Instrumentation;
using BlazorDevTools.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;

namespace BlazorDevTools.Session;

public sealed class ComponentTreeNode
{
    public required ComponentRecord Record { get; init; }

    public int Depth { get; set; }

    public List<ComponentTreeNode> Children { get; } = [];

    /// <summary>Total renders of this node and all descendants.</summary>
    public int SubtreeRenders { get; set; }

    public double SubtreeRenderMs { get; set; }
}

/// <summary>All component instances known to a session, with lazily resolved hierarchy and disposal detection.</summary>
internal sealed class ComponentRegistry
{
    private readonly DevToolsSession _session;
    private readonly DevToolsOptions _options;
    private readonly ConcurrentDictionary<IComponent, ComponentRecord> _byComponent = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<long, ComponentRecord> _byId = new();
    private static long _nextInstanceId;
    private long _lastRefreshVersion = -1;
    private long _lastRefreshTicks;
    private ComponentTreeNode[]? _treeCache;
    private bool _treeCacheIncludesHidden;

    public ComponentRegistry(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _options = options;
    }

    public int TrackedCount => _byComponent.Count;

    public Renderer? Renderer { get; private set; }

    public ComponentRecord Register(IComponent component, Type type, bool isDevTools, bool isHidden)
    {
        var record = new ComponentRecord(Interlocked.Increment(ref _nextInstanceId), component, type, _options.MaxRendersPerComponent, isDevTools, isHidden);
        _byComponent[component] = record;
        _byId[record.InstanceId] = record;
        _session.Touch();
        return record;
    }

    public bool TryGet(IComponent component, out ComponentRecord record) => _byComponent.TryGetValue(component, out record!);

    public ComponentRecord? Get(long instanceId) => _byId.TryGetValue(instanceId, out var record) ? record : null;

    public IEnumerable<ComponentRecord> All => _byId.Values;

    internal void NoteRenderer(Renderer renderer)
    {
        Renderer ??= renderer;
    }

    /// <summary>Resolves component ids, parents and disposal state. Cheap enough for the UI refresh cadence; skipped when nothing changed.</summary>
    public void Refresh(bool force = false)
    {
        var version = _session.Version;
        if (!force && version == _lastRefreshVersion && Stopwatch.GetElapsedTime(_lastRefreshTicks).TotalSeconds < 2)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        _lastRefreshVersion = version;
        _lastRefreshTicks = start;
        _treeCache = null;

        var now = DateTimeOffset.UtcNow;
        foreach (var record in _byId.Values)
        {
            if (record.IsDisposed)
            {
                if (now - record.DisposedAt > TimeSpan.FromSeconds(60))
                {
                    _byId.TryRemove(record.InstanceId, out _);
                }

                continue;
            }

            ResolveHierarchy(record, now);
        }

        _session.Overhead.AddTreeRefresh(Stopwatch.GetElapsedTime(start).Ticks);
    }

    internal void ResolveHierarchy(ComponentRecord record, DateTimeOffset now)
    {
        if (!record.RendererResolved && record.Component is ComponentBase cb && BlazorReflection.CanResolveRenderer)
        {
            var renderer = BlazorReflection.ResolveRenderer(cb, out var componentId);
            if (renderer is not null)
            {
                record.Renderer = renderer;
                record.ComponentId = componentId;
                record.RendererResolved = true;
                Renderer ??= renderer;
            }
        }

        var effectiveRenderer = record.Renderer ?? Renderer;
        if (effectiveRenderer is null || !BlazorReflection.CanResolveHierarchy)
        {
            return;
        }

        var state = BlazorReflection.ResolveState(effectiveRenderer, record.Component);
        if (state is null)
        {
            // Not attached yet (just created) or already disposed by the renderer.
            var age = now - record.CreatedAt;
            if (record.RenderCount > 0 || age > TimeSpan.FromSeconds(5))
            {
                MarkDisposed(record, now);
            }

            return;
        }

        record.ComponentId ??= state.ComponentId;
        if (!record.ParentResolved)
        {
            var parentState = state.LogicalParentComponentState ?? state.ParentComponentState;
            if (parentState is not null && _byComponent.TryGetValue(parentState.Component, out var parent))
            {
                record.Parent = parent;
            }

            record.ParentResolved = true;
        }
    }

    private void MarkDisposed(ComponentRecord record, DateTimeOffset now)
    {
        record.IsDisposed = true;
        record.DisposedAt = now;
        _byComponent.TryRemove(record.Component, out _);
        _session.Timeline.Record(new Events.DevToolsEvent
        {
            Kind = Events.DevToolsEventKind.Lifecycle,
            Category = "lifecycle",
            Title = record.DisplayName + " disposed",
            ComponentInstanceId = record.InstanceId,
            ComponentName = record.DisplayName,
        });
    }

    /// <summary>Builds the visible tree (roots first). Hidden components are collapsed: their children attach to the nearest visible ancestor.</summary>
    public ComponentTreeNode[] BuildTree(bool includeHidden)
    {
        if (_treeCache is not null && _treeCacheIncludesHidden == includeHidden)
        {
            return _treeCache;
        }

        var nodes = new Dictionary<long, ComponentTreeNode>();
        var live = _byId.Values.Where(r => !r.IsDisposed && !r.IsDevTools && (includeHidden || !r.IsHidden)).OrderBy(r => r.ComponentId ?? int.MaxValue).ThenBy(r => r.InstanceId).ToList();
        foreach (var record in live)
        {
            nodes[record.InstanceId] = new ComponentTreeNode { Record = record };
        }

        var roots = new List<ComponentTreeNode>();
        foreach (var record in live)
        {
            var node = nodes[record.InstanceId];
            var parent = record.Parent;
            while (parent is not null && !nodes.ContainsKey(parent.InstanceId))
            {
                parent = parent.Parent;
            }

            if (parent is null)
            {
                roots.Add(node);
            }
            else
            {
                nodes[parent.InstanceId].Children.Add(node);
            }
        }

        foreach (var root in roots)
        {
            Aggregate(root, 0);
        }

        _treeCache = roots.ToArray();
        _treeCacheIncludesHidden = includeHidden;
        return _treeCache;
    }

    private static void Aggregate(ComponentTreeNode node, int depth)
    {
        node.Depth = depth;
        node.SubtreeRenders = node.Record.RenderCount;
        node.SubtreeRenderMs = node.Record.TotalRenderMs;
        foreach (var child in node.Children)
        {
            Aggregate(child, depth + 1);
            node.SubtreeRenders += child.SubtreeRenders;
            node.SubtreeRenderMs += child.SubtreeRenderMs;
        }
    }

    public void Clear()
    {
        foreach (var record in _byId.Values.Where(r => r.IsDisposed).ToList())
        {
            _byId.TryRemove(record.InstanceId, out _);
        }

        _treeCache = null;
        _session.Touch();
    }
}
