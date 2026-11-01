// Boot before installing message handlers: .NET uses the worker environment
// during startup. Never run Main, which would start the UI and device scanners.
try {
    const { dotnet } = await import("./_framework/dotnet.js");
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    self.onmessage = ({ data: { id, request } }) => {
        try {
            const result = exports.ChameLab.Browser.KeyRecoveryWorkerExports.Execute(request);
            self.postMessage({ type: "result", id, result });
        } catch (error) {
            self.postMessage({ type: "error", id, message: error instanceof Error ? error.message : String(error) });
        }
    };
    self.postMessage({ type: "ready" });
} catch (error) {
    self.postMessage({ type: "error", message: error instanceof Error ? error.message : String(error) });
}
