using System.Threading.Channels;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Cli.Tests;

internal sealed class ScriptedTransport(Func<ChameleonFrame, ChameleonFrame> respond) : ITransport
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly FrameParser _parser = new();
    private byte[] _remaining = [];
    public List<ChameleonFrame> Requests { get; } = [];
    public TransportKind Kind => TransportKind.Serial;
    public string DisplayName => "CLI protocol fixture";
    public bool IsConnected { get; private set; }
    public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var request in _parser.Push(data))
        {
            Requests.Add(request);
            var response = respond(request);
            _incoming.Writer.TryWrite(FrameCodec.Encode(response.Command, response.Data, response.RawStatus));
        }
        return Task.CompletedTask;
    }
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining.Length == 0) _remaining = await _incoming.Reader.ReadAsync(ct);
        // Force fragmented responses, as on a real USB/BLE connection.
        var count = Math.Min(3, Math.Min(_remaining.Length, buffer.Length));
        _remaining.AsMemory(0, count).CopyTo(buffer);
        _remaining = _remaining[count..];
        return count;
    }
    public ValueTask DisposeAsync() { IsConnected = false; _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    public static ChameleonFrame Reply(ChameleonFrame request, byte[]? data = null, Status status = Status.Success)
        => new(request.Command, status, data ?? []);
}
