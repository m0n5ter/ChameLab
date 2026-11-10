// node key-recovery-worker.cjs PUBLISH/wwwroot crypto_vectors.json [CHROME_EXECUTABLE]
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require('playwright');
const root = path.resolve(process.argv[2]);
const vectors = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const hex = text => BigInt('0x' + text).toString();
const nested = vectors.find(v => v.kind === 'nested' && v.key === '112233445566');
const darkside = vectors.find(v => v.kind === 'darkside' && v.key === 'A0A1A2A3A4A5');
const zero = vectors.find(v => v.kind === 'darkside-zero');
const nestedRequest = JSON.stringify({ Operation: 0, Uid: nested.uid, Distance: nested.distance,
    Nonces: [0, 1].map(i => ({ Nt: nested['nt' + i], NtEnc: nested['enc' + i], Parity: nested['par' + i] })) });
const item = (v, i) => `{"Nt":${v.nt},"Ks1":${hex(i === undefined ? v.ks : v['ks' + i])},"Par":${i === undefined ? hex(v.par) : 0},"Nr":${i === undefined ? v.nr : v['nr' + i]},"Ar":${v.ar}}`;
// Preserve full 64-bit parity as decimal JSON text through the worker transport.
const cases = [
    { name: 'nested', request: nestedRequest, keys: nested.cKeys.map(hex) },
    { name: 'darkside', request: `{"Operation":4,"Uid":${darkside.uid},"Items":[${item(darkside)}]}`, keys: [hex(darkside.key)] },
    { name: 'darkside-zero', request: `{"Operation":4,"Uid":${zero.uid},"Items":[${item(zero, 0)},${item(zero, 1)}]}`, keys: zero.cKeys.map(hex) },
    { name: 'static-encrypted', request: '{"Operation":2,"Uid":2705213698,"Nonces":[{"Nt":3510735283,"NtEnc":782125564,"Parity":15}]}', count: 44075, fingerprint: '79007A0A206AF947' }
];
const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/') { response.end('<!doctype html><title>Key recovery worker test</title>'); return; }
    const file = path.resolve(root, '.' + decodeURIComponent(url.pathname));
    if (!file.startsWith(root + path.sep)) { response.writeHead(403).end(); return; }
    const mime = { '.js': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json' };
    try { response.setHeader('Content-Type', mime[path.extname(file)] || 'application/octet-stream'); response.end(fs.readFileSync(file)); }
    catch { response.writeHead(404).end(); }
});
(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({ headless: true, executablePath: process.argv[4] });
    try {
        const page = await browser.newPage();
        page.on('console', message => console.log('browser:', message.text()));
        page.on('pageerror', error => console.error('pageerror:', error.message));
        await page.goto(`http://127.0.0.1:${server.address().port}/`);
        const result = await page.evaluate(async cases => {
            const api = await import('./key-recovery.js');
            let ticks = 0; const timer = setInterval(() => ticks++, 20); const rows = [];
            try {
                for (const c of [...cases, cases[cases.length - 1]]) {
                    const started = performance.now();
                    const keys = JSON.parse(await api.recover(c.name, c.request)).map(String).sort((a, b) => Number(a) - Number(b));
                    if (c.keys && keys.join() !== c.keys.slice().sort((a, b) => Number(a) - Number(b)).join()) throw new Error(c.name + ': wrong candidates');
                    if (c.count !== undefined) {
                        if (keys.length !== c.count) throw new Error(c.name + ': wrong count ' + keys.length);
                        let hash = 14695981039346656037n;
                        for (const key of keys) for (let b = 0n; b < 6n; b++) hash = BigInt.asUintN(64, (hash ^ ((BigInt(key) >> (8n * b)) & 255n)) * 1099511628211n);
                        if (hash.toString(16).toUpperCase() !== c.fingerprint) throw new Error(c.name + ': wrong fingerprint');
                    }
                    rows.push({ name: c.name, candidates: keys.length, elapsedMs: performance.now() - started });
                }
                const cancelling = api.recover('cancel', cases[0].request);
                setTimeout(() => api.cancel('cancel'), 20);
                if (await cancelling !== '[]') throw new Error('Cancellation did not interrupt recovery');
                const restarted = JSON.parse(await api.recover('restart', cases[1].request)).map(String);
                if (restarted.join() !== cases[1].keys.join()) throw new Error('Worker restart failed');
                if (ticks < 5) throw new Error('UI timer blocked');
                return { rows, cancelled: true, restarted: true, ticks, crossOriginIsolated };
            } finally { clearInterval(timer); }
        }, cases);
        console.log('PASS', JSON.stringify(result));
    } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
