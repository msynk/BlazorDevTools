import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

const source = fs.readFileSync(new URL("../../src/BlazorDevTools/wwwroot/devtools.js", import.meta.url), "utf8");

function loadBridge() {
    const encoded = Buffer.from(source).toString("base64");
    return import(`data:text/javascript;base64,${encoded}#${crypto.randomUUID()}`);
}

class Storage {
    constructor(values = {}) {
        this.values = values;
    }

    get length() {
        return Object.keys(this.values).length;
    }

    key(index) {
        return Object.keys(this.values)[index];
    }

    getItem(key) {
        return this.values[key] ?? null;
    }

    setItem(key, value) {
        this.values[key] = value;
    }
}

function installBrowser({ localStorage = new Storage(), sessionStorage = new Storage() } = {}) {
    const listeners = new Map();
    const eventTarget = {
        addEventListener(type, handler) {
            listeners.set(type, handler);
        },
        removeEventListener(type) {
            listeners.delete(type);
        },
    };

    globalThis.window = {
        ...eventTarget,
        localStorage,
        sessionStorage,
        DotNet: {
            invokeMethodAsync: () => Promise.resolve(null),
            invokeMethod: () => null,
        },
    };
    globalThis.document = { ...eventTarget, visibilityState: "visible" };
    Object.defineProperty(globalThis, "navigator", {
        value: { onLine: true, userAgent: "node" },
        configurable: true,
    });
}

test("storage values are redacted before leaving the browser", async () => {
    installBrowser({
        localStorage: new Storage({
            profile: '{"access_token":"secret"}',
            session: "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature",
            envelope: '{"user":"alice","value":"prefix eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature"}',
            settings: "password=hunter2",
            cachedRequest: "Authorization: Bearer secret",
            nested: '{"credential":"opaque-value"}',
            keyMaterial: "-----BEGIN PRIVATE KEY-----\nopaque",
            theme: "dark",
        }),
    });
    const bridge = await loadBridge();

    const entries = bridge.getStorage("local", []);

    assert.equal(entries.find(entry => entry.key === "profile").redacted, true);
    assert.equal(entries.find(entry => entry.key === "session").redacted, true);
    assert.equal(entries.find(entry => entry.key === "envelope").redacted, true);
    assert.equal(entries.find(entry => entry.key === "settings").redacted, true);
    assert.equal(entries.find(entry => entry.key === "cachedRequest").redacted, true);
    assert.equal(entries.find(entry => entry.key === "nested").redacted, true);
    assert.equal(entries.find(entry => entry.key === "keyMaterial").redacted, true);
    assert.equal(entries.find(entry => entry.key === "theme").redacted, false);
});

test("downloadText hands the export to the browser and cleans up after itself", async () => {
    installBrowser();
    const clicks = [];
    const appended = [];
    let removed = 0;
    const anchor = {
        style: {},
        click() { clicks.push({ href: this.href, download: this.download, rel: this.rel }); },
        remove() { removed++; },
    };
    globalThis.document.createElement = tag => {
        assert.equal(tag, "a");
        return anchor;
    };
    globalThis.document.body = { appendChild: node => appended.push(node) };
    const revoked = [];
    const originalRevoke = URL.revokeObjectURL;
    URL.revokeObjectURL = url => revoked.push(url);
    try {
        const bridge = await loadBridge();

        const ok = bridge.downloadText("session.json", "{\"format\":\"blazor-devtools/1\"}", "application/json");

        assert.equal(ok, true);
        assert.equal(appended.length, 1);
        assert.equal(clicks.length, 1);
        assert.equal(clicks[0].download, "session.json");
        assert.equal(clicks[0].rel, "noopener");
        assert.match(clicks[0].href, /^blob:/);
        await new Promise(resolve => setTimeout(resolve, 10));
        assert.equal(removed, 1);
        assert.deepEqual(revoked, [clicks[0].href]);
    } finally {
        URL.revokeObjectURL = originalRevoke;
    }
});

test("downloadText reports failure instead of throwing when the DOM is unavailable", async () => {
    installBrowser();
    globalThis.document.createElement = () => { throw new Error("no DOM"); };
    const bridge = await loadBridge();

    assert.equal(bridge.downloadText("x.txt", "content"), false);
});

test("detach drops pending calls and bridge callbacks are not traced", async () => {
    installBrowser();
    const bridge = await loadBridge();
    let resolveFirst;
    window.DotNet.invokeMethodAsync = (assembly, method) =>
        assembly === "App" && method === "First"
            ? new Promise(resolve => { resolveFirst = resolve; })
            : Promise.resolve(null);
    const firstReports = [];
    const firstRef = { invokeMethodAsync: (...args) => { firstReports.push(args); return Promise.resolve(); } };

    bridge.attach(firstRef, { trackJsToDotNet: true });
    const firstCall = window.DotNet.invokeMethodAsync("App", "First");
    bridge.detach();

    const secondReports = [];
    const secondRef = { invokeMethodAsync: (...args) => { secondReports.push(args); return Promise.resolve(); } };
    bridge.attach(secondRef, { trackJsToDotNet: true });
    resolveFirst();
    await firstCall;
    await window.DotNet.invokeMethodAsync(secondRef, "OnBrowserEvent", "online", null);
    await window.DotNet.invokeMethodAsync("App", "Second");
    await new Promise(resolve => setTimeout(resolve, 550));

    const batches = secondReports.filter(args => args[0] === "ReportJsToDotNet");
    assert.equal(batches.length, 1);
    assert.deepEqual(batches[0][1].map(entry => entry.identifier), ["App.Second"]);
    bridge.detach();
});
