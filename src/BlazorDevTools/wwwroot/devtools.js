// Blazor DevTools browser bridge.
// Only browser-only facts cross this bridge: JS errors, online/offline, visibility, reconnect state,
// storage inventory (on request), JS -> .NET call statistics and the toggle shortcut.
// It is loaded as an ES module by the DevTools panel; nothing runs until attach() is called.

let dotnetRef = null;
let shortcut = null;
let jsToDotNetBatch = [];
let flushTimer = null;
let untrackedDotNetToJs = 0;
let originalInvokeAsync = null;
let originalInvoke = null;
const listeners = [];

function on(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    listeners.push(() => target.removeEventListener(type, handler, options));
}

function report(method, ...args) {
    if (!dotnetRef) return;
    try {
        const p = dotnetRef.invokeMethodAsync(method, ...args);
        if (p && p.catch) p.catch(() => { /* circuit gone */ });
    } catch { /* ignore */ }
}

function parseShortcut(text) {
    const parts = (text || "Ctrl+Shift+D").split("+").map(p => p.trim().toLowerCase());
    return {
        ctrl: parts.includes("ctrl") || parts.includes("control"),
        shift: parts.includes("shift"),
        alt: parts.includes("alt"),
        meta: parts.includes("meta") || parts.includes("cmd"),
        key: parts[parts.length - 1],
    };
}

function matchesShortcut(e) {
    if (!shortcut) return false;
    return e.ctrlKey === shortcut.ctrl && e.shiftKey === shortcut.shift && e.altKey === shortcut.alt && e.metaKey === shortcut.meta
        && (e.key || "").toLowerCase() === shortcut.key;
}

function scheduleFlush() {
    if (flushTimer) return;
    flushTimer = setTimeout(() => {
        flushTimer = null;
        if (jsToDotNetBatch.length === 0 && untrackedDotNetToJs === 0) return;
        const batch = jsToDotNetBatch;
        jsToDotNetBatch = [];
        const untracked = untrackedDotNetToJs;
        untrackedDotNetToJs = 0;
        report("ReportJsToDotNet", batch, untracked);
    }, 500);
}

function isDevToolsCall(assemblyOrRef, method) {
    return assemblyOrRef === "BlazorDevTools" || (typeof method === "string" && method.startsWith("BlazorDevTools."));
}

function wrapDotNet() {
    const DotNet = window.DotNet;
    if (!DotNet || originalInvokeAsync) return;
    originalInvokeAsync = DotNet.invokeMethodAsync;
    originalInvoke = DotNet.invokeMethod;
    DotNet.invokeMethodAsync = function (assemblyName, methodIdentifier, ...args) {
        const isObjectRef = typeof assemblyName !== "string";
        const target = isObjectRef ? "object#" + (assemblyName && assemblyName.__dotNetObject !== undefined ? assemblyName.__dotNetObject : "?") : assemblyName;
        if (isDevToolsCall(assemblyName, methodIdentifier)) {
            return originalInvokeAsync.apply(this, [assemblyName, methodIdentifier, ...args]);
        }
        const start = performance.now();
        const entry = { identifier: target + "." + methodIdentifier, args: args.length, sync: false, at: Date.now() };
        let promise;
        try {
            promise = originalInvokeAsync.apply(this, [assemblyName, methodIdentifier, ...args]);
        } catch (err) {
            entry.durationMs = performance.now() - start;
            entry.error = String(err && err.message ? err.message : err);
            jsToDotNetBatch.push(entry);
            scheduleFlush();
            throw err;
        }
        return promise.then(r => {
            entry.durationMs = performance.now() - start;
            jsToDotNetBatch.push(entry);
            scheduleFlush();
            return r;
        }, err => {
            entry.durationMs = performance.now() - start;
            entry.error = String(err && err.message ? err.message : err);
            jsToDotNetBatch.push(entry);
            scheduleFlush();
            throw err;
        });
    };
    if (originalInvoke) {
        DotNet.invokeMethod = function (assemblyName, methodIdentifier, ...args) {
            const start = performance.now();
            const entry = { identifier: assemblyName + "." + methodIdentifier, args: args.length, sync: true, at: Date.now() };
            try {
                const r = originalInvoke.apply(this, [assemblyName, methodIdentifier, ...args]);
                entry.durationMs = performance.now() - start;
                jsToDotNetBatch.push(entry);
                scheduleFlush();
                return r;
            } catch (err) {
                entry.durationMs = performance.now() - start;
                entry.error = String(err && err.message ? err.message : err);
                jsToDotNetBatch.push(entry);
                scheduleFlush();
                throw err;
            }
        };
    }
}

function unwrapDotNet() {
    const DotNet = window.DotNet;
    if (!DotNet || !originalInvokeAsync) return;
    DotNet.invokeMethodAsync = originalInvokeAsync;
    if (originalInvoke) DotNet.invokeMethod = originalInvoke;
    originalInvokeAsync = null;
    originalInvoke = null;
}

function snapshot() {
    const nav = performance.getEntriesByType ? performance.getEntriesByType("navigation")[0] : null;
    const mem = performance.memory;
    return {
        online: navigator.onLine,
        visibility: document.visibilityState,
        userAgent: navigator.userAgent,
        jsHeapUsedMb: mem ? mem.usedJSHeapSize / 1048576 : null,
        jsHeapLimitMb: mem ? mem.jsHeapSizeLimit / 1048576 : null,
        navigationLoadMs: nav ? nav.loadEventEnd : null,
        domContentLoadedMs: nav ? nav.domContentLoadedEventEnd : null,
        resourceCount: performance.getEntriesByType ? performance.getEntriesByType("resource").length : null,
    };
}

export function attach(ref, options) {
    detach();
    dotnetRef = ref;
    shortcut = parseShortcut(options && options.shortcut);

    on(window, "error", e => {
        const err = e.error;
        report("OnJsError", {
            message: e.message || (err && err.message) || "Unknown error",
            source: e.filename ? e.filename + ":" + e.lineno + ":" + e.colno : null,
            stack: err && err.stack ? String(err.stack).slice(0, 4000) : null,
            type: err && err.name ? err.name : "Error",
        });
    });
    on(window, "unhandledrejection", e => {
        const reason = e.reason;
        report("OnJsError", {
            message: reason && reason.message ? reason.message : String(reason),
            source: null,
            stack: reason && reason.stack ? String(reason.stack).slice(0, 4000) : null,
            type: "UnhandledRejection",
        });
    });
    on(window, "online", () => report("OnBrowserEvent", "online", null));
    on(window, "offline", () => report("OnBrowserEvent", "offline", null));
    on(document, "visibilitychange", () => report("OnBrowserEvent", "visibility", document.visibilityState));
    on(document, "components:reconnect-state-changed", e => {
        const d = e.detail || {};
        report("OnReconnectState", d.state || "unknown", d.currentAttempt || 0, d.secondsToNextAttempt || 0);
    });
    on(document, "keydown", e => {
        if (matchesShortcut(e)) {
            e.preventDefault();
            report("OnToggleShortcut");
        }
    }, true);

    if (options && options.trackJsToDotNet) {
        wrapDotNet();
    }

    return snapshot();
}

export function detach() {
    while (listeners.length) listeners.pop()();
    unwrapDotNet();
    if (flushTimer) { clearTimeout(flushTimer); flushTimer = null; }
    dotnetRef = null;
}

export function refresh() {
    return snapshot();
}

export function getStorage(kind) {
    const storage = kind === "session" ? window.sessionStorage : window.localStorage;
    const result = [];
    try {
        for (let i = 0; i < storage.length; i++) {
            const key = storage.key(i);
            const value = storage.getItem(key) || "";
            result.push({ key, size: value.length, preview: value.length > 120 ? value.slice(0, 120) + "…" : value });
        }
    } catch (err) {
        result.push({ key: "(error)", size: 0, preview: String(err) });
    }
    return result;
}

export function loadPrefs() {
    try { return window.localStorage.getItem("blazor-devtools:prefs"); } catch { return null; }
}

export function savePrefs(json) {
    try { window.localStorage.setItem("blazor-devtools:prefs", json); } catch { /* ignore */ }
}

export function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        return navigator.clipboard.writeText(text).then(() => true, () => false);
    }
    return false;
}

export function focusElement(element) {
    if (element && element.focus) element.focus();
}

export function scrollIntoView(element) {
    if (element && element.scrollIntoView) element.scrollIntoView({ block: "nearest" });
}
