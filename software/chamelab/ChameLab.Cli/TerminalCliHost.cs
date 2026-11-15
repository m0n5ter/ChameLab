using ChameLab.Transport;
using ChameLab.Transport.Serial;

namespace ChameLab.Cli;

/// <summary>The terminal: opens serial ports too, and shows waveforms in a window of their own.</summary>
internal sealed class TerminalCliHost : StandaloneCliHost
{
    protected override ITransportScanner SerialScanner { get; } = new SerialScanner();

    public override Task ShowWaveformAsync(byte[] samples, int offset, CancellationToken ct) => WaveformPlot.ShowAsync(samples, offset, ct);
}
