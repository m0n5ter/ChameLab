// node --experimental-vm-modules --test tests/wake-lock.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../wwwroot/wake-lock.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));

function lock() {
    const listeners = [];
    return {
        released: false,
        releases: 0,
        addEventListener(type, handler) { if (type === 'release') listeners.push(handler); },
        async release() {
            this.released = true;
            this.releases++;
            listeners.forEach(handler => handler());
        }
    };
}

async function setup(request) {
    const events = {};
    const document = {
        visibilityState: 'visible',
        addEventListener(type, handler) { events[type] = handler; }
    };
    const navigator = request ? { wakeLock: { request } } : {};
    const warnings = [];
    const module = new vm.SourceTextModule(source, {
        context: vm.createContext({ document, navigator, console: { warn: (...args) => warnings.push(args) } })
    });
    await module.link(() => { throw new Error('Unexpected import'); });
    await module.evaluate();
    return {
        api: module.namespace, warnings,
        visibility(value) { document.visibilityState = value; events.visibilitychange(); }
    };
}

test('keeps one lock while active and releases it on completion', async () => {
    const held = lock();
    let requests = 0;
    const { api } = await setup(async type => { assert.equal(type, 'screen'); requests++; return held; });
    await api.setEnabled(true);
    await api.setEnabled(true);
    assert.equal(requests, 1);
    assert.equal(held.releases, 0);
    await api.setEnabled(false);
    await api.setEnabled(false);
    assert.equal(held.releases, 1);
});

test('restores after returning to the tab only while an operation is active', async () => {
    const locks = [];
    const { api, visibility } = await setup(async () => { const held = lock(); locks.push(held); return held; });
    await api.setEnabled(true);
    visibility('hidden');
    await locks[0].release(); // Browser releases automatically when hidden.
    visibility('visible');
    await tick();
    assert.equal(locks.length, 2);
    await api.setEnabled(false);
    visibility('hidden');
    visibility('visible');
    await tick();
    assert.equal(locks.length, 2);
    assert.equal(locks[1].releases, 1);
});

test('a request resolving after stop cannot leave the screen awake', async () => {
    let resolve;
    const { api } = await setup(() => new Promise(done => { resolve = done; }));
    const starting = api.setEnabled(true);
    await api.setEnabled(false);
    const held = lock();
    resolve(held);
    await starting;
    assert.equal(held.releases, 1);
});

test('a visibility change during a pending request retries after rejection', async () => {
    let reject;
    let requests = 0;
    const held = lock();
    const { api, visibility } = await setup(() => ++requests === 1
        ? new Promise((_, fail) => { reject = fail; }) : Promise.resolve(held));
    const starting = api.setEnabled(true);
    visibility('hidden');
    visibility('visible');
    reject(new Error('The tab was hidden'));
    await starting;
    await tick();
    assert.equal(requests, 2);
    await api.setEnabled(false);
    assert.equal(held.releases, 1);
});

test('unsupported or denied wake locks do not interrupt the operation', async () => {
    const unsupported = await setup();
    await unsupported.api.setEnabled(true);
    await unsupported.api.setEnabled(false);
    let requests = 0;
    const denied = await setup(async () => { requests++; throw new Error('Denied'); });
    await denied.api.setEnabled(true);
    await tick();
    assert.equal(requests, 1); // Do not retry in a loop on low battery or policy denial.
    assert.equal(denied.warnings.length, 1);
    await denied.api.setEnabled(false);
});
