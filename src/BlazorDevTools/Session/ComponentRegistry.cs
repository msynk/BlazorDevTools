using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

/// <summary>
/// All component instances known to a session, with lazily resolved hierarchy and disposal detection.
/// <para>
/// Components are referenced weakly (see <see cref="ComponentRecord.Component"/>) and records are swept on a
/// schedule that does not depend on the DevTools UI being open, so tracking never keeps an application object
/// alive and never grows without bound.
/// </para>
/// </summary>
internal sealed class ComponentRegistry
{
    private readonly DevToolsSession _session;
    private readonly DevToolsOptions _options;
    private readonly ConditionalWeakTable<IComponent, ComponentRecord> _byComponent = new();
    private readonly ConcurrentDictionary<long, ComponentRecord> _byId = new();
    private static long _nextInstanceId;
    private long _lastRefreshVersion = -1;
    private long _lastRefreshTicks;
    private long _lastSweepTicks = Stopwatch.GetTimestamp();
    private ComponentTreeNode[]? _treeCache;
    private bool _treeCacheIncludesHidden;
    private WeakReference<Renderer>? _renderer;

    public ComponentRegistry(DevToolsSession session, DevToolsOptions options)
    {
        _session = session;
        _options = options;
    }

    /// <summary>Records currently kept, including recently disposed ones still shown in the UI.</summary>
    public int TrackedCount => _byId.Count;

    /// <summary>Components that are still attached to a renderer.</summary>
    public int LiveCount => _byId.Values.Count(r => !r.IsDisposed);

    /// <summary>True when <see cref="DevToolsOptions.MaxTrackedComponents"/> was hit and new components are no longer tracked.</summary>
    public bool IsTruncated { get; private set; }

    public Renderer? Renderer => _renderer is not null && _renderer.TryGetTarget(out var renderer) ? renderer : null;

    public ComponentRecord? Register(IComponent component, Type type, bool isDevTools, bool isHidden)
    {
        MaybeSweep();
        if (_byId.Count >= _options.MaxTrackedComponents)
        {
            IsTruncated = true;
            return null;
        }

        var record = new ComponentRecord(Interlocked.Increment(ref _nextInstanceId), component, type, _options.MaxRendersPerComponent, isDevTools, isHidden);
        _byComponent.AddOrUpdate(component, record);
        _byId[record.InstanceId] = record;
        _session.Touch();
        return record;
    }

    public bool TryGet(IComponent component, out ComponentRecord record) => _byComponent.TryGetValue(component, out record!);

    public ComponentRecord? Get(long instanceId) => _byId.TryGetValue(instanceId, out var record) ? record : null;

    public IEnumerable<ComponentRecord> All => _byId.Values;

    internal void NoteRenderer(Renderer renderer)
    {
        _renderer ??= new WeakReference<Renderer>(renderer);
    }

    /// <summary>
    /// Prunes on component churn so long-running sessions stay bounded even when the DevTools panel is never opened.
    /// Runs on the renderer's dispatcher (it is called from component activation), at most once per second.
    /// </summary>
    private void MaybeSweep()
    {
        if (Stopwatch.GetElapsedTime(_lastSweepTicks).TotalSeconds < 1)
        {
            return;
        }

        _lastSweepTicks = Stopwatch.GetTimestamp();
        Refresh(force: true);
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
        _lastSweepTicks = start;
        _treeCache = null;

        var now = DateTimeOffset.UtcNow;
        var retention = TimeSpan.FromSeconds(_options.DisposedComponentRetentionSeconds);
        foreach (var record in _byId.Values)
        {
            if (record.IsDisposed)
            {
                if (now - record.DisposedAt > retention)
                {
                    _byId.TryRemove(record.InstanceId, out _);
                }

                continue;
            }

            if (!record.IsAlive)
            {
                // The application dropped the instance and the GC reclaimed it before we noticed: it is gone.
                MarkDisposed(record, now, collected: true);
                continue;
            }

            ResolveHierarchy(record, now);
        }

        _session.Overhead.AddTreeRefresh(Stopwatch.GetElapsedTime(start).Ticks);
    }

    internal void ResolveHierarchy(ComponentRecord record, DateTimeOffset now)
    {
        var component = record.Component;
        if (component is null)
        {
            MarkDisposed(record, now, collected: true);
            return;
        }

        if (!record.RendererResolved && component is ComponentBase cb && BlazorReflection.CanResolveRenderer)
        {
            var renderer = BlazorReflection.ResolveRenderer(cb, out var componentId);
            if (renderer is not null)
            {
                record.Renderer = renderer;
                record.ComponentId = componentId;
                record.RendererResolved = true;
                NoteRenderer(renderer);
            }
        }

        var effectiveRenderer = record.Renderer ?? Renderer;
        if (effectiveRenderer is null || !BlazorReflection.CanResolveHierarchy)
        {
            return;
        }

        var state = BlazorReflection.ResolveState(effectiveRenderer, component);
        if (state is null)
        {
            // Not attached yet (just created) or already disposed by the renderer.
            var age = now - record.CreatedAt;
            if (record.RenderCount > 0 || age > TimeSpan.FromSeconds(5))
            {
                MarkDisposed(record, now, collected: false);
            }

            return;
        }

        record.ComponentId ??= state.ComponentId;
        if (!record.ParentResolved)
        {
            var parentState = state.LogicalParentComponentState ?? state.ParentComponentState;
            if (parentState is null)
            {
                record.ParentResolved = true;
            }
            else if (_byComponent.TryGetValue(parentState.Component, out var parent))
            {
                record.Parent = parent;
                record.ParentResolved = true;
            }
            // A renderer can expose the child before its parent has been registered by our activator.
            // Leave the link unresolved so a later refresh can repair the tree.
        }
    }

    private void MarkDisposed(ComponentRecord record, DateTimeOffset now, bool collected)
    {
        record.IsDisposed = true;
        record.DisposedAt = now;
        if (record.Component is { } component)
        {
            _byComponent.Remove(component);
        }

        record.ReleaseReferences();
        if (record.IsDevTools || record.IsHidden)
        {
            return;
        }

        _session.Timeline.Record(new Events.DevToolsEvent
        {
            Kind = Events.DevToolsEventKind.Lifecycle,
            Category = "lifecycle",
            Title = record.DisplayName + " disposed",
            Detail = collected ? "instance was garbage collected" : null,
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
            var hops = 0;
            while (parent is not null && !nodes.ContainsKey(parent.InstanceId) && hops++ < 1024)
            {
                parent = parent.Parent;
            }

            if (parent is null || !nodes.TryGetValue(parent.InstanceId, out var parentNode) || ReferenceEquals(parentNode, node))
            {
                roots.Add(node);
            }
            else
            {
                parentNode.Children.Add(node);
            }
        }

        foreach (var root in roots)
        {
            Aggregate(root);
        }

        _treeCache = roots.ToArray();
        _treeCacheIncludesHidden = includeHidden;
        return _treeCache;
    }

    /// <summary>
    /// Iterative post-order aggregation. Recursion would overflow the stack on deeply nested or recursive component
    /// trees, which are exactly the trees a developer opens DevTools to understand.
    /// </summary>
    private static void Aggregate(ComponentTreeNode root)
    {
        var order = new List<ComponentTreeNode>();
        var stack = new Stack<ComponentTreeNode>();
        root.Depth = 0;
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            node.SubtreeRenders = node.Record.RenderCount;
            node.SubtreeRenderMs = node.Record.TotalRenderMs;
            foreach (var child in node.Children)
            {
                child.Depth = node.Depth + 1;
                stack.Push(child);
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            foreach (var child in node.Children)
            {
                node.SubtreeRenders += child.SubtreeRenders;
                node.SubtreeRenderMs += child.SubtreeRenderMs;
            }
        }
    }

    /// <summary>Drops the history of components that are already gone. Live components keep their statistics.</summary>
    public void ClearDisposed()
    {
        foreach (var record in _byId.Values)
        {
            if (record.IsDisposed)
            {
                _byId.TryRemove(record.InstanceId, out _);
            }
        }

        // Once an instance was omitted at the cap we cannot know when that untracked instance is disposed.
        // Keep this warning sticky for the session rather than implying the registry is complete again.
        _treeCache = null;
        _session.Touch();
    }
}
