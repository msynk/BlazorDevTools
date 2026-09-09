using System.Diagnostics;
using BlazorDevTools.Events;
using BlazorDevTools.Model;
using BlazorDevTools.Session;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;

namespace BlazorDevTools.Instrumentation;

/// <summary>
/// Decorates an <see cref="IJSRuntime"/> to record .NET → JS calls with duration, result and originating component.
/// Applied automatically to <c>[Inject] IJSRuntime</c> properties of tracked components; services can opt in with
/// <see cref="DevToolsJSRuntimeExtensions.WithDevToolsTracking"/>.
/// </summary>
public class TrackingJSRuntime : IJSRuntime
{
    internal TrackingJSRuntime(IJSRuntime inner, DevToolsSession session, ComponentRecord? component)
    {
        Inner = inner;
        Session = session;
        Component = component;
    }

    public IJSRuntime Inner { get; }

    internal DevToolsSession Session { get; }

    internal ComponentRecord? Component { get; }

    internal static TrackingJSRuntime Create(IJSRuntime inner, DevToolsSession session, ComponentRecord? component)
    {
        while (inner is TrackingJSRuntime tracking)
        {
            inner = tracking.Inner;
        }

        return inner is IJSInProcessRuntime inProcess
            ? new TrackingInProcessJSRuntime(inProcess, session, component)
            : new TrackingJSRuntime(inner, session, component);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var record = Begin<TValue>(identifier, args, isModule: false, isSync: false);
        try
        {
            var result = await Inner.InvokeAsync<TValue>(identifier, cancellationToken, args).ConfigureAwait(false);
            End(record, null);
            return Wrap(result);
        }
        catch (Exception ex)
        {
            End(record, ex);
            throw;
        }
    }

    internal TValue Wrap<TValue>(TValue result)
    {
        if (result is IJSObjectReference reference && result is not TrackingJSObjectReference)
        {
            object wrapped = reference is IJSInProcessObjectReference inProcess
                ? new TrackingInProcessJSObjectReference(inProcess, this)
                : new TrackingJSObjectReference(reference, this);
            if (wrapped is TValue typed)
            {
                return typed;
            }
        }

        return result;
    }

    internal JsInteropRecord Begin<TValue>(string identifier, object?[]? args, bool isModule, bool isSync)
    {
        var record = new JsInteropRecord
        {
            Id = Session.Interop.NextId(),
            At = DateTimeOffset.UtcNow,
            Direction = JsInteropDirection.DotNetToJs,
            Identifier = identifier,
            ArgumentCount = args?.Length ?? 0,
            ArgumentTypes = args is { Length: > 0 } ? string.Join(", ", args.Select(a => a is null ? "null" : TypeNames.Short(a.GetType()))) : null,
            ResultType = typeof(TValue) == typeof(IJSVoidResult) ? "void" : TypeNames.Short(typeof(TValue)),
            ComponentInstanceId = Component?.InstanceId,
            ComponentName = Component?.DisplayName,
            IsModuleCall = isModule,
            IsSync = isSync,
        };
        record.TimelineEventId = Session.Timeline.Record(new DevToolsEvent
        {
            Kind = DevToolsEventKind.JsInterop,
            Category = "js-interop",
            Title = "→ JS " + identifier,
            Detail = (isSync ? "sync" : "async") + (record.ArgumentTypes is null ? ", no arguments" : ", args: " + record.ArgumentTypes) + ", returns " + record.ResultType,
            ComponentInstanceId = Component?.InstanceId,
            ComponentName = Component?.DisplayName,
            ParentEventId = ActivityObserver.CurrentTriggerEvent(Session)?.Id,
            Data = new Dictionary<string, object?> { ["callId"] = record.Id },
        });
        Session.Interop.Add(record);
        return record;
    }

    internal void End(JsInteropRecord record, Exception? exception)
    {
        var elapsed = Stopwatch.GetElapsedTime(Session.Timeline.Find(record.TimelineEventId)?.StartTicks ?? Stopwatch.GetTimestamp()).TotalMilliseconds;
        var error = exception is null ? null : exception.GetType().Name + ": " + exception.Message;
        Session.Interop.Complete(record, elapsed, exception is null, error);
        var severity = exception is not null ? DevToolsSeverity.Error : elapsed >= Session.Options.Diagnostics.SlowJsInteropMs ? DevToolsSeverity.Warning : DevToolsSeverity.Info;
        Session.Timeline.Complete(record.TimelineEventId, elapsed, exception is null ? null : "failed: " + error, severity);
        if (exception is not null and not OperationCanceledException)
        {
            Session.Errors.Record(exception, "js-interop", component: Component);
        }
    }
}

public sealed class TrackingInProcessJSRuntime : TrackingJSRuntime, IJSInProcessRuntime
{
    private readonly IJSInProcessRuntime _inner;

    internal TrackingInProcessJSRuntime(IJSInProcessRuntime inner, DevToolsSession session, ComponentRecord? component) : base(inner, session, component)
    {
        _inner = inner;
    }

    public TResult Invoke<TResult>(string identifier, params object?[]? args)
    {
        var record = Begin<TResult>(identifier, args, isModule: false, isSync: true);
        try
        {
            var result = _inner.Invoke<TResult>(identifier, args);
            End(record, null);
            return Wrap(result);
        }
        catch (Exception ex)
        {
            End(record, ex);
            throw;
        }
    }
}

public class TrackingJSObjectReference : IJSObjectReference
{
    internal TrackingJSObjectReference(IJSObjectReference inner, TrackingJSRuntime runtime)
    {
        Inner = inner;
        Runtime = runtime;
    }

    public IJSObjectReference Inner { get; }

    internal TrackingJSRuntime Runtime { get; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var record = Runtime.Begin<TValue>("module." + identifier, args, isModule: true, isSync: false);
        try
        {
            var result = await Inner.InvokeAsync<TValue>(identifier, cancellationToken, args).ConfigureAwait(false);
            Runtime.End(record, null);
            return Runtime.Wrap(result);
        }
        catch (Exception ex)
        {
            Runtime.End(record, ex);
            throw;
        }
    }

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}

public sealed class TrackingInProcessJSObjectReference : TrackingJSObjectReference, IJSInProcessObjectReference
{
    private readonly IJSInProcessObjectReference _inner;

    internal TrackingInProcessJSObjectReference(IJSInProcessObjectReference inner, TrackingJSRuntime runtime) : base(inner, runtime)
    {
        _inner = inner;
    }

    public TValue Invoke<TValue>(string identifier, params object?[]? args)
    {
        var record = Runtime.Begin<TValue>("module." + identifier, args, isModule: true, isSync: true);
        try
        {
            var result = _inner.Invoke<TValue>(identifier, args);
            Runtime.End(record, null);
            return Runtime.Wrap(result);
        }
        catch (Exception ex)
        {
            Runtime.End(record, ex);
            throw;
        }
    }

    public void Dispose() => _inner.Dispose();
}

public static class DevToolsJSRuntimeExtensions
{
    /// <summary>
    /// Returns a runtime that records calls in the DevTools timeline. Use in services that make JS calls; components
    /// are wrapped automatically. Returns the original runtime when DevTools is disabled.
    /// </summary>
    public static IJSRuntime WithDevToolsTracking(this IJSRuntime jsRuntime, DevToolsSession session)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        ArgumentNullException.ThrowIfNull(session);
        return session.IsEnabled ? TrackingJSRuntime.Create(jsRuntime, session, null) : jsRuntime;
    }
}
