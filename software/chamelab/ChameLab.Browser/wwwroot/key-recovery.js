let worker = null;
let ready = false;
let job = null;
let startupTimer = null;

function stopWorker() {
    clearTimeout(startupTimer);
    startupTimer = null;
    worker?.terminate();
    worker = null;
    ready = false;
}

function fail(message) {
    const pending = job;
    job = null;
    stopWorker();
    pending?.reject(new Error(message));
}

// Reuse the runtime between sectors/attempts. Starting a new runtime per nonce
// would repeatedly load assemblies and initialize the Crypto1 lookup tables.
export function recover(id, request) {
    if (job) return Promise.reject(new Error("Key recovery is already running."));
    return new Promise((resolve, reject) => {
        job = { id, request, resolve, reject };
        if (!worker) {
            try {
                worker = new Worker(new URL("./key-recovery-worker.js", import.meta.url), { type: "module" });
                startupTimer = setTimeout(() => fail("Could not load the key recovery WebAssembly worker."), 60000);
                worker.onmessage = ({ data }) => {
                    if (data.type === "ready") {
                        clearTimeout(startupTimer);
                        startupTimer = null;
                        ready = true;
                        if (job) worker.postMessage({ id: job.id, request: job.request });
                    } else if (data.type === "error") {
                        fail(data.message || "Key recovery worker failed.");
                    } else if (data.type === "result" && job?.id === data.id) {
                        const completed = job;
                        job = null;
                        completed.resolve(data.result);
                    }
                };
                worker.onerror = event => {
                    event.preventDefault();
                    fail(event.message || "Key recovery worker failed.");
                };
                worker.onmessageerror = () => fail("Invalid response from the key recovery worker.");
            } catch (error) { fail(error instanceof Error ? error.message : String(error)); }
        } else if (ready) worker.postMessage({ id, request });
    });
}

export function cancel(id) {
    if (job?.id !== id) return;
    const pending = job;
    job = null;
    // terminate() interrupts even a synchronous solver, without shared memory.
    stopWorker();
    pending.resolve("[]");
}
