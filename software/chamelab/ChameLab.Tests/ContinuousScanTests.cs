using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public sealed class ContinuousScanTests
{
    private static ContinuousScanViewModel Scanner(Func<CardSave, CardSave?>? findSaved = null) =>
        new(new DeviceService([]), findSaved ?? (_ => null), _ => { });

    private static CardSave Card(TagSpecificType type, string uid) => new()
    {
        Tag = type,
        Uid = Convert.FromHexString(uid),
    };

    [Fact]
    public void StartsWithAnEmptyList()
    {
        var scan = Scanner();
        Assert.Empty(scan.Cards);
        Assert.False(scan.HasCards);
        Assert.False(scan.IsRunning);
    }

    [Fact]
    public void SameTypeAndUidHighlightsTheExistingCardWithoutAddingADuplicate()
    {
        var scan = Scanner();
        var card = Card(TagSpecificType.Mifare1024, "01020304");
        Assert.True(scan.Observe(card));
        var row = scan.Cards.Single();
        scan.Selected = null;

        Assert.False(scan.Observe(Card(TagSpecificType.Mifare1024, "01020304")));
        Assert.Single(scan.Cards);
        Assert.Same(row, scan.Cards[0]);
        Assert.Same(row, scan.Selected);
    }

    [Fact]
    public void LatestDetectedCardIsTheOnlyHighlightedRow()
    {
        var scan = Scanner();
        scan.Observe(Card(TagSpecificType.Em410x, "0102030405"));
        scan.Observe(Card(TagSpecificType.Em410x, "AABBCCDDEE"));

        Assert.Equal(2, scan.Cards.Count);
        Assert.Same(scan.Cards[1], scan.Selected);

        scan.Observe(Card(TagSpecificType.Em410x, "0102030405"));
        Assert.Same(scan.Cards[0], scan.Selected);
    }

    [Fact]
    public void SameUidOnDifferentProtocolsIsNotTheSameCard()
    {
        var scan = Scanner();
        Assert.True(scan.Observe(Card(TagSpecificType.Em410x, "0102030405")));
        Assert.True(scan.Observe(Card(TagSpecificType.HidProx, "0102030405")));
        Assert.Equal(2, scan.Cards.Count);
    }

    [Fact]
    public void UsesTheSavedNameAndEditActionForALibraryCard()
    {
        var saved = Card(TagSpecificType.Mifare1024, "01020304");
        saved.Name = "Front door";
        var scan = Scanner(_ => saved);

        scan.Observe(Card(TagSpecificType.Mifare1024, "01020304"));

        var row = scan.Cards.Single();
        Assert.Same(saved, row.Card);
        Assert.Equal("Front door", row.Name);
        Assert.True(row.IsSavedMatch);
    }

    [Theory]
    [InlineData(TagSpecificType.Mifare1024, "01020304", "#00FFFF")]
    [InlineData(TagSpecificType.Em410x, "0102030405", "#FFFF00")]
    public void NewCardsUseTheirFrequencyColor(TagSpecificType type, string uid, string color)
    {
        var scan = Scanner();
        scan.Observe(Card(type, uid));
        Assert.Equal(color, scan.Cards.Single().Card.Color);
    }

    [Fact]
    public async Task ContinuesAfterBccErrorAndAddsTheNextValidCard()
    {
        var transport = new BccThenCardTransport();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        using var cts = new CancellationTokenSource();
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = new ContinuousScanViewModel(new DeviceService([]), _ => null, _ => { }, () => found.TrySetResult());

        var running = scan.ScanAsync(new ChameleonClient(connection), cts, CardBand.Hf);
        await found.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await running;

        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Hf14aScan));
        Assert.Equal("01 02 03 04", scan.Cards.Single().UidHex);
    }

    private sealed class BccThenCardTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        private int _scans;
        public List<ChameleonFrame> Requests { get; } = [];
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Continuous scan test device";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                if (request.Command == Command.ChangeDeviceMode)
                {
                    _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, [], (ushort)Status.Success));
                    continue;
                }
                var first = ++_scans == 1;
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command,
                    first ? [] : Convert.FromHexString("040102030404000800"),
                    (ushort)(first ? Status.HfErrBcc : Status.HfTagOk)));
            }
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
