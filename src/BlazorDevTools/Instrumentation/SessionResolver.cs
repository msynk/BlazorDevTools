using BlazorDevTools.Session;

namespace BlazorDevTools.Instrumentation;

/// <summary>
/// Maps ambient execution context to a DevTools session. Blazor runs every session (circuit) on its own
/// <c>Dispatcher</c>; <c>Dispatcher.CheckAccess()</c> identifies the current one. Falls back to the only live session
/// (WebAssembly, tests) and finally to an explicit ambient session set by instrumentation code.
/// </summary>
internal static class SessionResolver
{
    private static readonly object Lock = new();
    private static readonly List<WeakReference<DevToolsSession>> Sessions = [];
    private static readonly AsyncLocal<DevToolsSession?> Ambient = new();
    private static DevToolsSession?[] _cache = [];

    public static void Register(DevToolsSession session)
    {
        lock (Lock)
        {
            Sessions.Add(new WeakReference<DevToolsSession>(session));
            Prune();
        }
    }

    public static void Unregister(DevToolsSession session)
    {
        lock (Lock)
        {
            Sessions.RemoveAll(w => !w.TryGetTarget(out var s) || ReferenceEquals(s, session));
            Prune();
        }
    }

    public static int LiveSessionCount => Volatile.Read(ref _cache).Length;

    public static DevToolsSession? Resolve()
    {
        var ambient = Ambient.Value;
        if (ambient is { IsDisposed: false })
        {
            return ambient;
        }

        var sessions = Volatile.Read(ref _cache);
        DevToolsSession? single = null;
        var liveCount = 0;
        foreach (var session in sessions)
        {
            if (session is null || session.IsDisposed)
            {
                continue;
            }

            liveCount++;
            single = session;
            try
            {
                if (session.Dispatcher?.CheckAccess() == true)
                {
                    return session;
                }
            }
            catch
            {
                // dispatcher of a disposed renderer; ignore.
            }
        }

        return liveCount == 1 ? single : null;
    }

    public static IDisposable PushAmbient(DevToolsSession session)
    {
        var previous = Ambient.Value;
        Ambient.Value = session;
        return new AmbientScope(previous);
    }

    private static void Prune()
    {
        Sessions.RemoveAll(w => !w.TryGetTarget(out var s) || s.IsDisposed);
        var list = new DevToolsSession?[Sessions.Count];
        for (var i = 0; i < Sessions.Count; i++)
        {
            Sessions[i].TryGetTarget(out list[i]);
        }

        Volatile.Write(ref _cache, list);
    }

    private sealed class AmbientScope(DevToolsSession? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
