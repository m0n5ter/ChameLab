// Run the shared managed solver in an isolated .NET runtime. Stop terminates
// synchronous CPU work and returns its memory without SharedArrayBuffer.
try {
    const { dotnet } = await import("./_framework/dotnet.js");
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    runtime.setModuleImports("hardnested-worker", {
        reportProgress: message => self.postMessage({ type: "progress", message })
    });
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    self.onmessage = ({ data: { uid, capture } }) => {
        try {
            const key = exports.ChameLab.Browser.HardnestedWorkerExports.Execute(uid, capture);
            self.postMessage({ type: "result", key });
        } catch (error) {
            self.postMessage({ type: "error", message: error instanceof Error ? error.message : String(error) });
        }
    };
    self.postMessage({ type: "ready" });
} catch (error) {
    self.postMessage({ type: "error", message: error instanceof Error ? error.message : String(error) });
}
