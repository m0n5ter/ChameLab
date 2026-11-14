// Camera QR scanner for the web app: a full-screen overlay with the camera view, a status line and
// Cancel. Frames are decoded by the browser's BarcodeDetector where it has one (Chrome on Android,
// macOS, ChromeOS); elsewhere they go to the app as grayscale and ZXing decodes them (WebQrCamera).

const MaxSide = 720;
const FrameInterval = 180;

let overlay = null;
let video = null;
let statusLine = null;
let stream = null;
let detector = null;
let closed = false;
let canvas = null;
let gray = null;
let waiter = null;

function cleanup() {
    if (stream) stream.getTracks().forEach(t => t.stop());
    stream = null;
    if (overlay) overlay.remove();
    overlay = video = statusLine = null;
}

/// Opens the camera and the overlay: "ok", "denied", "nocamera" or "error:<message>".
export async function open(status, cancelLabel) {
    close();
    closed = false;
    if (!navigator.mediaDevices?.getUserMedia) return "nocamera";
    try {
        stream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 }, height: { ideal: 720 } },
            audio: false,
        });
    } catch (e) {
        stream = null;
        if (e?.name === "NotAllowedError" || e?.name === "SecurityError") return "denied";
        if (e?.name === "NotFoundError" || e?.name === "OverconstrainedError") return "nocamera";
        return "error:" + (e?.message ?? e);
    }

    detector = null;
    try {
        if ("BarcodeDetector" in globalThis) {
            const formats = await BarcodeDetector.getSupportedFormats();
            if (formats.includes("qr_code")) detector = new BarcodeDetector({ formats: ["qr_code"] });
        }
    } catch { detector = null; }

    overlay = document.createElement("div");
    overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;background:#000;display:flex;" +
        "flex-direction:column;align-items:center;justify-content:center;font-family:system-ui,sans-serif";
    video = document.createElement("video");
    video.setAttribute("playsinline", "");
    video.muted = true;
    video.style.cssText = "position:absolute;inset:0;width:100%;height:100%;object-fit:cover";
    const frame = document.createElement("div");
    frame.style.cssText = "position:relative;width:min(70vw,70vh);height:min(70vw,70vh);border:3px solid rgba(255,255,255,.85);" +
        "border-radius:16px;box-shadow:0 0 0 100vmax rgba(0,0,0,.45)";
    const bar = document.createElement("div");
    bar.style.cssText = "position:absolute;left:0;right:0;bottom:0;padding:16px 16px calc(16px + env(safe-area-inset-bottom));" +
        "display:flex;flex-direction:column;align-items:center;gap:12px";
    statusLine = document.createElement("div");
    statusLine.style.cssText = "color:#fff;font-size:16px;text-align:center;text-shadow:0 1px 3px #000;max-width:520px";
    statusLine.textContent = status;
    const cancel = document.createElement("button");
    cancel.textContent = cancelLabel;
    cancel.style.cssText = "padding:10px 28px;border-radius:999px;border:0;background:#fff;color:#000;font-size:16px;cursor:pointer";
    cancel.onclick = () => close();
    bar.append(statusLine, cancel);
    overlay.append(video, frame, bar);
    document.body.append(overlay);

    video.srcObject = stream;
    try { await video.play(); } catch { /* autoplay of a muted inline video is allowed */ }
    return "ok";
}

export function setStatus(text) {
    if (statusLine) statusLine.textContent = text;
}

export function close() {
    closed = true;
    cleanup();
    if (waiter) { const w = waiter; waiter = null; w(); }
}

/// Waits for the next frame: "text:<code>" (read by BarcodeDetector), "frame:<w>:<h>" (grayscale ready for
/// copyFrame), or "closed" once the user canceled.
export async function nextFrame() {
    while (!closed) {
        await new Promise(resolve => { waiter = resolve; setTimeout(() => { if (waiter === resolve) { waiter = null; resolve(); } }, FrameInterval); });
        if (closed || !video || video.readyState < 2 || !video.videoWidth) continue;
        if (detector) {
            try {
                const codes = await detector.detect(video);
                if (closed) break;
                if (codes.length > 0) return "text:" + codes[0].rawValue;
                continue;
            } catch { detector = null; /* fall back to the app's decoder */ }
        }
        const scale = Math.min(1, MaxSide / Math.max(video.videoWidth, video.videoHeight));
        const w = Math.max(1, Math.round(video.videoWidth * scale));
        const h = Math.max(1, Math.round(video.videoHeight * scale));
        canvas ??= document.createElement("canvas");
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(video, 0, 0, w, h);
        const rgba = ctx.getImageData(0, 0, w, h).data;
        gray = new Uint8Array(w * h);
        for (let i = 0, p = 0; i < gray.length; i++, p += 4)
            gray[i] = (rgba[p] * 77 + rgba[p + 1] * 150 + rgba[p + 2] * 29) >> 8;
        return `frame:${w}:${h}`;
    }
    return "closed";
}

/// Copies the grayscale frame announced by nextFrame into the app's buffer.
export function copyFrame(target) {
    if (gray) target.set(gray.subarray(0, target.length));
}
