// Each recovery gets a fresh WASM instance. terminate() immediately cancels CPU
// work and returns its memory, including on browsers without SharedArrayBuffer.
self.onmessage = async ({ data: { uid, capture } }) => {
    try {
        const { default: createModule } = await import("./hardnested/hardnested.js");
        const engine = await createModule({
            print: line => {
                const prefix = "CHAMELAB_PROGRESS=";
                if (line.startsWith(prefix)) self.postMessage({ type: "progress", message: line.slice(prefix.length).replace(/\x1b\[[0-9;]*m/g, "") });
            },
            printErr: () => {} // Native status codes are translated below.
        });
        self.postMessage({ type: "ready" });
        engine.FS.writeFile("/nonces.txt", capture);
        const keyPtr = engine._malloc(8);
        if (!keyPtr) throw new Error("Not enough memory for hardnested.");
        try {
            const status = engine.ccall("chamelab_hardnested_embedded", "number",
                ["number", "string", "number", "number", "number", "number"],
                [uid, "/nonces.txt", keyPtr, 1792 * 1024 * 1024, 0, 0]);
            if (status < 0) {
                if (status === -3) throw new Error("Hardnested needs more memory than this browser can provide. Try a desktop device.");
                throw new Error(`Hardnested failed (native status ${status}). The capture may be invalid or incomplete.`);
            }
            let key = "";
            if (status === 1) {
                const view = new DataView(engine.HEAPU8.buffer);
                const value = BigInt(view.getUint32(keyPtr, true)) | (BigInt(view.getUint32(keyPtr + 4, true)) << 32n);
                key = value.toString(16).padStart(12, "0");
            }
            self.postMessage({ type: "result", key });
        } finally { engine._free(keyPtr); }
    } catch (error) {
        self.postMessage({ type: "error", message: error instanceof Error ? error.message : String(error) });
    }
};
