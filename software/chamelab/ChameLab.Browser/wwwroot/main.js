import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    // The runtime otherwise loads translations only for the browser's own culture, and the UI
    // language picked in Settings is applied later, from managed code.
    .withConfig({ loadAllSatelliteResources: true })
    .create();

const config = dotnetRuntime.getConfig();

// A shared card arrives as #card=… (see CardQr): handed to the app once, then dropped from the
// address so a reload or a bookmark doesn't offer the same card again.
const href = globalThis.location.href;
if (globalThis.location.hash.includes('card=')) {
    history.replaceState(null, '', globalThis.location.pathname + globalThis.location.search);
}

await dotnetRuntime.runMain(config.mainAssemblyName, [href]);
