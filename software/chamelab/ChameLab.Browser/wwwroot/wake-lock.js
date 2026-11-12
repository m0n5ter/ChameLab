let enabled = false;
let sentinel = null;
let pending = null;
let retryWhenReady = false;

async function acquire() {
    if (!enabled || document.visibilityState !== 'visible' || !navigator.wakeLock || sentinel || pending) return;
    pending = navigator.wakeLock.request('screen');
    try {
        const lock = await pending;
        // A stop or a hidden tab may have overtaken the permission request.
        if (!enabled || document.visibilityState !== 'visible') {
            await lock.release();
            return;
        }
        if (lock.released) return;
        sentinel = lock;
        lock.addEventListener('release', () => {
            if (sentinel === lock) sentinel = null;
        });
    } catch (error) {
        console.warn('ChameLab could not keep the screen awake:', error);
    } finally {
        pending = null;
        if (retryWhenReady) {
            retryWhenReady = false;
            void acquire();
        }
    }
}

export async function setEnabled(value) {
    enabled = value;
    if (enabled) await acquire();
    else {
        retryWhenReady = false;
        const lock = sentinel;
        sentinel = null;
        if (lock) await lock.release();
    }
}

// Browsers release screen locks when a tab is hidden. Restore only during an active operation.
document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && pending) retryWhenReady = true;
    else void acquire();
});
