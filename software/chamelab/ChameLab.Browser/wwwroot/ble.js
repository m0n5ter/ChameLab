// Web Bluetooth bridge for the WebAssembly build.
// The Chameleon's command channel is the Nordic UART Service: commands go to RX
// (write without response), replies arrive as notifications on TX. Devices are
// kept here and referenced by their Web Bluetooth id from C#.

const NUS = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
const NUS_RX = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
const NUS_TX = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

// The browser doesn't say which ATT MTU it negotiated, and some stacks silently cut a
// longer write without response, so writes stay within the minimum MTU (23 - 3 bytes).
const CHUNK = 20;

// gatt.connect() to a device that is asleep or out of range may never settle.
const CONNECT_TIMEOUT_MS = 15000;

const devices = new Map(); // id -> BluetoothDevice
const links = new Map();   // id -> open NUS link

export function isSupported() {
    return typeof navigator !== "undefined" && "bluetooth" in navigator;
}

function remember(device) {
    devices.set(device.id, device);
    return `${device.id}\t${device.name || ""}`;
}

// Devices this page may use without a prompt, one "id<TAB>name" per line: the ones picked
// this session, plus earlier grants where the browser can list them (getDevices).
export async function listKnown() {
    if (!isSupported()) return "";
    await refreshKnown();
    return [...devices.values()].filter(d => !isBootloader(d)).map(remember).join("\n");
}

async function refreshKnown() {
    if (navigator.bluetooth.getDevices) {
        try {
            for (const d of await navigator.bluetooth.getDevices()) devices.set(d.id, d);
        } catch (e) { }
    }
}

// Prompt the user to pick a Chameleon. Must be called from a user gesture.
// Returns "id<TAB>name", or "" if dismissed / unsupported.
export async function requestDevice() {
    if (!isSupported()) return "";
    try {
        const device = await navigator.bluetooth.requestDevice({
            filters: [{ namePrefix: "Chameleon" }, { services: [NUS] }],
            optionalServices: [NUS],
        });
        return remember(device);
    } catch (e) {
        return ""; // user dismissed the chooser
    }
}

function withTimeout(promise, ms, message) {
    let timer;
    const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); });
    return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

export async function connect(id) {
    const device = devices.get(id);
    if (!device) throw new Error("Bluetooth device is no longer available. Pick it again.");

    let server;
    try {
        server = await withTimeout(device.gatt.connect(), CONNECT_TIMEOUT_MS,
            "The Chameleon didn't answer over Bluetooth. Press a button on it to wake it up.");
    } catch (e) {
        try { device.gatt.disconnect(); } catch (_) { }
        throw e;
    }
    const service = await server.getPrimaryService(NUS);
    const rx = await service.getCharacteristic(NUS_RX);
    const tx = await service.getCharacteristic(NUS_TX);

    const link = { device, rx, tx, chunks: [], waiter: null, closed: false, writes: Promise.resolve() };
    link.onValue = e => {
        const v = e.target.value;
        link.chunks.push(new Uint8Array(v.buffer, v.byteOffset, v.byteLength).slice());
        wake(link);
    };
    link.onDisconnected = () => {
        link.closed = true;
        wake(link);
    };
    tx.addEventListener("characteristicvaluechanged", link.onValue);
    device.addEventListener("gattserverdisconnected", link.onDisconnected);
    await tx.startNotifications();
    links.set(id, link);
}

function wake(link) {
    const waiter = link.waiter;
    link.waiter = null;
    waiter?.();
}

export function isConnected(id) {
    const link = links.get(id);
    return !!link && !link.closed && link.device.gatt.connected;
}

// Returns everything received so far as base64, waiting for the next notification if
// nothing is queued. Throws once the link drops.
export async function readBase64(id) {
    const link = links.get(id);
    if (!link) throw new Error("BLE link closed.");
    while (link.chunks.length === 0) {
        if (link.closed) throw new Error("BLE link closed.");
        await new Promise(resolve => { link.waiter = resolve; });
    }
    const chunks = link.chunks.splice(0);
    let binary = "";
    for (const bytes of chunks)
        for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return btoa(binary);
}

export function writeBase64(id, b64) {
    const link = links.get(id);
    if (!link || link.closed) return Promise.reject(new Error("BLE link closed."));
    const binary = atob(b64);
    const data = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) data[i] = binary.charCodeAt(i);

    // GATT runs one operation at a time, so writes queue behind each other.
    const write = link.writes.then(async () => {
        for (let offset = 0; offset < data.length; offset += CHUNK) {
            const chunk = data.subarray(offset, offset + CHUNK);
            if (link.rx.writeValueWithoutResponse) await link.rx.writeValueWithoutResponse(chunk);
            else await link.rx.writeValue(chunk);
        }
    });
    link.writes = write.catch(() => { });
    return write;
}

export async function close(id) {
    const link = links.get(id);
    if (!link) return;
    links.delete(id);
    link.closed = true;
    wake(link);
    link.tx.removeEventListener("characteristicvaluechanged", link.onValue);
    link.device.removeEventListener("gattserverdisconnected", link.onDisconnected);
    try { await link.tx.stopNotifications(); } catch (e) { }
    try { link.device.gatt.disconnect(); } catch (e) { }
}

// ---- Secure DFU (the nRF5 SDK bootloader) ----
// After ENTER_BOOTLOADER the Chameleon restarts as a different Bluetooth device ("CU-xxxx",
// "CL-xxxx" for a Lite) with the Secure DFU service: requests go to the control point
// (write with response) and their responses come back as its notifications, image data
// goes to the packet characteristic (write without response).

const DFU = "0000fe59-0000-1000-8000-00805f9b34fb";
const DFU_CONTROL = "8ec90001-f315-4f60-9fb8-838830daea50";
const DFU_PACKET = "8ec90002-f315-4f60-9fb8-838830daea50";

const dfuLinks = new Map(); // id -> open DFU link

function isBootloader(device) {
    return /^C[UL]-/.test(device.name || "");
}

// Bootloaders this page may use without a prompt, one "id<TAB>name" per line: picked
// this session, or (where the browser can list them) in an earlier update.
export async function listKnownBootloaders() {
    if (!isSupported()) return "";
    await refreshKnown();
    return [...devices.values()].filter(isBootloader).map(remember).join("\n");
}

// Prompt the user to pick the bootloader. Must be called from a user gesture.
// Returns "id<TAB>name", or "" if dismissed / unsupported.
export async function requestBootloader() {
    if (!isSupported()) return "";
    try {
        const device = await navigator.bluetooth.requestDevice({
            filters: [{ namePrefix: "CU-" }, { namePrefix: "CL-" }, { services: [DFU] }],
            optionalServices: [DFU],
        });
        return remember(device);
    } catch (e) {
        return ""; // user dismissed the chooser
    }
}

export async function dfuOpen(id, timeoutMs) {
    const device = devices.get(id);
    if (!device) throw new Error("Bluetooth device is no longer available. Pick it again.");

    let server;
    try {
        server = await withTimeout(device.gatt.connect(), timeoutMs, "The bootloader didn't answer over Bluetooth.");
    } catch (e) {
        try { device.gatt.disconnect(); } catch (_) { }
        throw e;
    }
    try {
        const service = await server.getPrimaryService(DFU);
        const control = await service.getCharacteristic(DFU_CONTROL);
        const packet = await service.getCharacteristic(DFU_PACKET);

        // Each notification is one whole response, so they are queued apart, not merged.
        const link = { device, control, packet, responses: [], waiter: null, closed: false };
        link.onValue = e => {
            const v = e.target.value;
            link.responses.push(new Uint8Array(v.buffer, v.byteOffset, v.byteLength).slice());
            wake(link);
        };
        link.onDisconnected = () => {
            link.closed = true;
            wake(link);
        };
        control.addEventListener("characteristicvaluechanged", link.onValue);
        device.addEventListener("gattserverdisconnected", link.onDisconnected);
        await control.startNotifications();
        dfuLinks.set(id, link);
    } catch (e) {
        try { device.gatt.disconnect(); } catch (_) { }
        throw e;
    }
}

export function dfuIsOpen(id) {
    const link = dfuLinks.get(id);
    return !!link && !link.closed && link.device.gatt.connected;
}

function bytesOf(b64) {
    const binary = atob(b64);
    const data = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) data[i] = binary.charCodeAt(i);
    return data;
}

function openLink(id) {
    const link = dfuLinks.get(id);
    if (!link || link.closed) throw new Error("DFU link closed.");
    return link;
}

export async function dfuWriteControl(id, b64) {
    const link = openLink(id);
    const data = bytesOf(b64);
    if (link.control.writeValueWithResponse) await link.control.writeValueWithResponse(data);
    else await link.control.writeValue(data);
}

export async function dfuWritePacket(id, b64) {
    const link = openLink(id);
    const data = bytesOf(b64);
    if (link.packet.writeValueWithoutResponse) await link.packet.writeValueWithoutResponse(data);
    else await link.packet.writeValue(data);
}

// The next response as base64, or "" if none came within ms. Throws once the link drops.
export async function dfuReceive(id, ms) {
    const link = dfuLinks.get(id);
    if (!link) throw new Error("DFU link closed.");
    const deadline = Date.now() + ms;
    while (link.responses.length === 0) {
        if (link.closed) throw new Error("DFU link closed.");
        const left = deadline - Date.now();
        if (left <= 0) return "";
        let timer;
        await new Promise(resolve => {
            link.waiter = resolve;
            timer = setTimeout(resolve, left);
        });
        clearTimeout(timer);
        link.waiter = null;
    }
    const bytes = link.responses.shift();
    let binary = "";
    for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return btoa(binary);
}

export async function dfuClose(id) {
    const link = dfuLinks.get(id);
    if (!link) return;
    dfuLinks.delete(id);
    link.closed = true;
    wake(link);
    link.control.removeEventListener("characteristicvaluechanged", link.onValue);
    link.device.removeEventListener("gattserverdisconnected", link.onDisconnected);
    try { await link.control.stopNotifications(); } catch (e) { }
    try { link.device.gatt.disconnect(); } catch (e) { }
}
