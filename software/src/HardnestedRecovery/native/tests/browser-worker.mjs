// Run the browser lifecycle code with real worker threads and the real WASM core.
// Usage: node browser-worker.mjs path/to/hardnested.js synthetic-nonces.txt
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { Worker as NodeWorker } from "node:worker_threads";
import { pathToFileURL } from "node:url";
import path from "node:path";

const wasmUrl = pathToFileURL(path.resolve(process.argv[2])).href;
const wasmBinary = new Uint8Array(await readFile(path.resolve(process.argv[2]).replace(/\.js$/, ".wasm")));
const capture = await readFile(process.argv[3], "utf8");
const browser = new URL("../../../../chamelab/ChameLab.Browser/wwwroot/", import.meta.url);
let stopped = 0;
globalThis.Worker = class {
    constructor(url) {
        assert.equal(url.href, new URL("hardnested-worker.js", browser).href);
        this.worker = new NodeWorker(new URL(`data:text/javascript,${encodeURIComponent(`
            import { parentPort, workerData } from 'node:worker_threads';
            globalThis.self = { postMessage: data => parentPort.postMessage(data) };
            globalThis.__testWasmBinary = workerData.wasmBinary;
            await import(workerData.script);
            parentPort.on('message', data => self.onmessage({ data }));
        `)}`), { workerData: { script: workerScript, wasmBinary } });
        this.worker.on("message", data => this.onmessage?.({ data }));
        this.worker.on("error", error => this.onerror?.({ message: error.message, preventDefault() {} }));
    }
    postMessage(data) { this.worker.postMessage(data); }
    terminate() { stopped++; void this.worker.terminate(); }
};
const original = await readFile(new URL("hardnested-worker.js", browser), "utf8");
const workerScript = `data:text/javascript,${encodeURIComponent(original
    .replace('"./hardnested/hardnested.js"', JSON.stringify(wasmUrl))
    .replace('const engine = await createModule({', 'const engine = await createModule({ wasmBinary: globalThis.__testWasmBinary,'))}`;
const { recover, cancel, takeProgress } = await import(new URL("hardnested.js", browser));

const pending = recover("cancelled", 0x12345678, capture);
assert.match(takeProgress("cancelled"), /Loading/);
cancel("cancelled");
assert.equal(await pending, "");
assert.equal(stopped, 1, "cancellation must terminate the actual worker");
assert.equal(takeProgress("cancelled"), "");

const key = await recover("success", 0x12345678, capture);
assert.equal(key, "112233445566");
assert.equal(stopped, 2, "success must release the worker and WASM memory");
cancel("success");
assert.equal(takeProgress("success"), "");
console.log("Browser worker: progress, cancellation, known-key recovery and disposal passed.");
