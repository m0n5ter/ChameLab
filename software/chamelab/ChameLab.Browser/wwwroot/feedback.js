// Scan sounds and vibration for the WebAssembly build.

// Plays a WAV handed over base64-encoded. Scans start from a click, so the page may play audio.
export function play(base64) {
    new Audio("data:audio/wav;base64," + base64).play().catch(() => { /* autoplay blocked: stay silent */ });
}

// Vibration Pattern API (Android browsers): on/off timings in ms, starting with on.
export function vibrate(found) {
    navigator.vibrate?.(found ? [40] : [70, 80, 70]);
}
