const jobs = new Map();

export function recover(id, uid, capture) {
    if (jobs.has(id)) return Promise.reject(new Error("Hardnested job already exists."));
    return new Promise((resolve, reject) => {
        const worker = new Worker(new URL("./hardnested-worker.js", import.meta.url), { type: "module" });
        const job = { worker, resolve, reject, progress: "Loading hardnested…", timer: null };
        jobs.set(id, job);
        const finish = (error, key) => {
            clearTimeout(job.timer);
            worker.terminate();
            // Keep the final progress until the managed caller consumes/disposes the job.
            job.worker = null;
            if (error) reject(new Error(error)); else resolve(key ?? "");
        };
        // A missing worker script or failed module import must not leave the UI waiting forever.
        job.timer = setTimeout(() => finish("Could not load the hardnested WebAssembly worker."), 60000);
        worker.onmessage = ({ data }) => {
            if (data.type === "ready") clearTimeout(job.timer);
            else if (data.type === "progress") job.progress = data.message;
            else if (data.type === "result") finish(null, data.key);
            else if (data.type === "error") finish(data.message);
        };
        worker.onerror = event => { event.preventDefault(); finish(event.message || "Hardnested worker failed."); };
        worker.onmessageerror = () => finish("Invalid response from the hardnested worker.");
        worker.postMessage({ uid, capture });
    });
}

export function takeProgress(id) {
    const job = jobs.get(id);
    if (!job) return "";
    const value = job.progress;
    job.progress = "";
    return value;
}

export function cancel(id) {
    const job = jobs.get(id);
    if (!job) return;
    clearTimeout(job.timer);
    job.worker?.terminate();
    job.resolve("");
    jobs.delete(id);
}
