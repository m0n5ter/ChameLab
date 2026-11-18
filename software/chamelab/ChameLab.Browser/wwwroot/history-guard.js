// Keeps the browser's Back (button, gesture, mouse back button) inside the app.
//
// The page sits on an extra history entry (a guard) for the same address. Back leaves the guard,
// which the app sees as popstate: if the app takes the Back (closes a dialog, leaves an editor,
// shows "Press Back again to exit"), a new guard is pushed; if not, the tab goes back for real.
//
// Avalonia has the same scheme, but it pushes its entry once, while the page loads. Chrome and
// Safari skip, on Back, history entries a page added before the user interacted with it, so the
// first Back left the site. A guard pushed without user activation is therefore pushed again on
// the next tap or key press, which the browser does not skip.

let handler = null;
let stale = true;
let leaving = false;
let rearmTimer = 0;

function push() {
    history.pushState({ chameLabGuard: true }, '', location.href);
    stale = !(navigator.userActivation?.isActive ?? false);
}

function onActivation() {
    if (stale && !leaving) push();
}

function onPopState() {
    if (leaving) {
        leave();
        return;
    }
    let handled = false;
    try {
        handled = handler?.() ?? false;
    } catch (error) {
        console.error('ChameLab Back failed:', error);
    }
    if (handled) {
        push();
        return;
    }
    leaving = true;
    leave();
}

// Goes back past the app's own entries, the guards and Avalonia's.
function leave() {
    history.back();
    // A tab opened on the app has nothing to go back to, and Back does nothing: keep guarding.
    clearTimeout(rearmTimer);
    rearmTimer = setTimeout(rearm, 1000);
}

function rearm() {
    leaving = false;
    push();
}

export function install(onBack) {
    handler = onBack;
    // Avalonia's own popstate handler (set now or later) would answer the same Back a second time.
    window.onpopstate = null;
    Object.defineProperty(window, 'onpopstate', { configurable: true, get: () => null, set: () => {} });
    window.addEventListener('popstate', onPopState);
    // Coming back to the page from the next site (back-forward cache) lands on the bare entry.
    window.addEventListener('pageshow', event => {
        if (event.persisted) rearm();
    });
    window.addEventListener('pointerdown', onActivation, true);
    window.addEventListener('keydown', onActivation, true);
    push();
}
