// Integration test against a published browser app. Requires playwright + Chromium.
// node browser-worker.cjs PUBLISH/wwwroot CAPTURE.txt [CHROME_EXECUTABLE]
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require('playwright');
const root = path.resolve(process.argv[2]);
const capture = fs.readFileSync(process.argv[3], 'utf8');
const server = http.createServer((request, response) => {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/') { response.end('<!doctype html><title>Managed hardnested test</title>'); return; }
    const file = path.resolve(root, '.' + decodeURIComponent(url.pathname));
    if (!file.startsWith(root + path.sep)) { response.writeHead(403).end(); return; }
    const mime = { '.js': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json' };
    try {
        response.setHeader('Content-Type', mime[path.extname(file)] || 'application/octet-stream');
        response.end(fs.readFileSync(file));
    } catch { response.writeHead(404).end(); }
});
(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({ headless: true, executablePath: process.argv[4] });
    try {
        const page = await browser.newPage();
        page.on('console', message => console.log('browser:', message.text()));
        page.on('pageerror', error => console.error('pageerror:', error.message));
        await page.goto(`http://127.0.0.1:${server.address().port}/`);
        const result = await page.evaluate(async capture => {
            const api = await import('./hardnested.js');
            let ticks = 0;
            const timer = setInterval(() => ticks++, 20);
            let polling;
            try {
                let rejected = false;
                try { await api.recover('invalid', 0x12345678, '1|16\n'); }
                catch { rejected = true; }
                finally { api.cancel('invalid'); }
                if (!rejected) throw new Error('Invalid capture was accepted');
                const cancelStart = performance.now();
                const cancelled = api.recover('cancel', 0x12345678, capture);
                polling = setInterval(() => {
                    if (api.takeProgress('cancel').startsWith('Reducing')) api.cancel('cancel');
                }, 50);
                await cancelled;
                clearInterval(polling);
                const cancelMs = performance.now() - cancelStart;
                const start = performance.now();
                const recovering = api.recover('recover', 0x12345678, capture);
                polling = setInterval(() => {
                    const message = api.takeProgress('recover');
                    if (message) console.log(message);
                }, 250);
                const key = await recovering;
                api.cancel('recover');
                if (key.toUpperCase() !== '112233445566') throw new Error('Wrong recovered key: ' + key);
                if (ticks < 5) throw new Error('UI timer was blocked');
                return { key, elapsedMs: performance.now() - start, cancelMs, ticks, crossOriginIsolated };
            } finally { clearInterval(timer); clearInterval(polling); }
        }, capture);
        console.log('PASS', JSON.stringify(result));
    } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
