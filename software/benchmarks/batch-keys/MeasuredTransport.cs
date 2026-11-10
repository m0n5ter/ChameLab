using ChameLab.Protocol;
using ChameLab.Transport;

internal sealed class MeasuredTransport(ITransport inner) : ITransport
{
    private readonly FrameParser requests = new(), replies = new();
    public int Commands, AuthCommands, BatchCommands, TxBytes, RxBytes;
    public readonly List<int> BatchSizes = [];
    public readonly Dictionary<Status, int> Statuses = [];
    public void Reset() { Commands = AuthCommands = BatchCommands = TxBytes = RxBytes = 0; BatchSizes.Clear(); Statuses.Clear(); }
    public TransportKind Kind => inner.Kind;
    public string DisplayName => inner.DisplayName;
    public bool IsConnected => inner.IsConnected;
    public bool OneRequestAtATime => inner.OneRequestAtATime;
    public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        TxBytes += data.Length;
        foreach (var frame in requests.Push(data))
        {
            Commands++;
            if (frame.Command == Command.Mf1AuthOneKeyBlock) AuthCommands++;
            if (frame.Command == Command.Mf1CheckKeysOfSectors) { BatchCommands++; BatchSizes.Add((frame.Data.Length - 10) / 6); }
        }
        return inner.WriteAsync(data, ct);
    }
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct); RxBytes += n;
        foreach (var frame in replies.Push(buffer[..n])) Statuses[frame.Status] = Statuses.GetValueOrDefault(frame.Status) + 1;
        return n;
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
