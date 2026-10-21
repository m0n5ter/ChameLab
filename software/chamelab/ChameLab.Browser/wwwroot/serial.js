// Web Serial API bridge for the WebAssembly build.
// Browsers do not expose OS COM ports to managed System.IO.Ports; the only way
// to reach a USB-serial device from a page is navigator.serial (Chrome/Edge,
// secure context). Ports are kept here and referenced by index from C#.

let ports = [];

export function isSupported() {
    return typeof navigator !== "undefined" && "serial" in navigator;
}

// List ports the user has already granted this origin (no prompt).
export async function listGranted() {
    if (!isSupported()) return 0;
    ports = await navigator.serial.getPorts();
    return ports.length;
}

// Prompt the user to pick a port. Must be called from a user gesture.
// Returns the port index, or -1 if cancelled / unsupported.
export async function requestPort() {
    if (!isSupported()) return -1;
    try {
        // Only offer Chameleons (USB VID 0x6868) in the chooser.
        const port = await navigator.serial.requestPort({ filters: [{ usbVendorId: 0x6868 }] });
        const existing = ports.indexOf(port);
        if (existing >= 0) return existing;
        ports.push(port);
        return ports.length - 1;
    } catch (e) {
        return -1; // user dismissed the chooser
    }
}

// JSON {usbVendorId, usbProductId} — used to flag Chameleon (VID 0x6868 = 26728).
export function portInfo(index) {
    const p = ports[index];
    if (!p || !p.getInfo) return "{}";
    try { return JSON.stringify(p.getInfo()); } catch (e) { return "{}"; }
}

export async function open(index, baud) {
    const p = ports[index];
    await p.open({ baudRate: baud });
    p._reader = p.readable.getReader();
    p._writer = p.writable.getWriter();
}

// Returns the next chunk as base64, or "" on a clean end-of-stream.
export async function readBase64(index) {
    const p = ports[index];
    if (!p || !p._reader) return "";
    const { value, done } = await p._reader.read();
    if (done || !value || value.length === 0) return "";
    let binary = "";
    const bytes = new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
    for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return btoa(binary);
}

export async function writeBase64(index, b64) {
    const p = ports[index];
    if (!p || !p._writer) return;
    const binary = atob(b64);
    const arr = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) arr[i] = binary.charCodeAt(i);
    await p._writer.write(arr);
}

export async function close(index) {
    const p = ports[index];
    if (!p) return;
    try { await p._reader?.cancel(); } catch (e) { }
    try { p._reader?.releaseLock(); } catch (e) { }
    try { await p._writer?.close(); } catch (e) { }
    try { p._writer?.releaseLock(); } catch (e) { }
    try { await p.close(); } catch (e) { }
    p._reader = null;
    p._writer = null;
}
