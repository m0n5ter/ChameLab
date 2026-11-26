using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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

public sealed class SlotCardMatchingTests
{
    private static CardSave ClassicCard(string name = "Office", byte fill = 0x11, string color = "#FF4000")
    {
        var data = new List<byte[]>(64);
        for (var i = 0; i < 64; i++)
        {
            var block = new byte[16];
            Array.Fill(block, fill);
            block[0] = (byte)i;
            data.Add(block);
        }
        return new CardSave
        {
            Name = name,
            Tag = TagSpecificType.Mifare1024,
            Uid = [0x12, 0x34, 0x56, 0x78],
            Sak = 0x08,
            Atqa = [0x04, 0x00],
            Ats = [],
            Color = color,
            Data = data,
        };
    }

    private static CardSave UltralightCard(TagSpecificType tag = TagSpecificType.Ntag213, byte fill = 0x22)
    {
        var pages = tag.UltralightPages();
        var data = Enumerable.Range(0, pages)
            .Select(p => new byte[] { (byte)p, fill, fill, fill })
            .ToList();
        var card = new CardSave
        {
            Name = "NTAG",
            Tag = tag,
            Uid = [0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66],
            Sak = 0x00,
            Atqa = [0x44, 0x00],
            Data = data,
        };
        card.Extra.UltralightVersion = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x0F, 0x03];
        card.Extra.UltralightSignature = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        if (tag is TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21)
            card.Extra.UltralightCounters = [1, 2, 3];
        else if (tag is TagSpecificType.Ntag213 or TagSpecificType.Ntag215 or TagSpecificType.Ntag216)
            card.Extra.UltralightCounters = [7];
        return card;
    }

    [Fact]
    public void ContentHashAndEqualsIgnoreMetadataAndUnknownKeysButDetectAnyCardChange()
    {
        var a = ClassicCard(name: "Card A", color: "#112233");
        a.Folder = "Work";
        a.Extra.UnknownKeyA.Add(2);
        a.Extra.UnknownKeyB.Add(3);

        var b = ClassicCard(name: "Card B", color: "#445566");
        b.Folder = "Personal";

        Assert.Equal(a.ContentHash(), b.ContentHash());
        Assert.True(CardSave.ContentEquals(a, b));

        var variants = new List<CardSave>();
        CardSave CloneAndMutate(Action<CardSave> mutate)
        {
            var copy = a.Clone();
            mutate(copy);
            variants.Add(copy);
            return copy;
        }

        CloneAndMutate(c => c.Tag = TagSpecificType.Mifare2048);
        CloneAndMutate(c => c.Uid[3] ^= 1);
        CloneAndMutate(c => c.Sak ^= 1);
        CloneAndMutate(c => c.Atqa[0] ^= 1);
        CloneAndMutate(c => c.Ats = [0x01]);
        CloneAndMutate(c => c.Data[12][5] ^= 1);
        CloneAndMutate(c => c.Data.RemoveAt(63));
        CloneAndMutate(c => c.Data = []);

        foreach (var variant in variants)
        {
            Assert.NotEqual(a.ContentHash(), variant.ContentHash());
            Assert.False(CardSave.ContentEquals(a, variant));
        }

        var ul = UltralightCard();
        var ulDiffVer = ul.Clone();
        ulDiffVer.Extra.UltralightVersion[0] ^= 1;
        var ulDiffSig = ul.Clone();
        ulDiffSig.Extra.UltralightSignature[0] ^= 1;
        var ulDiffCtr = ul.Clone();
        ulDiffCtr.Extra.UltralightCounters[0] = 99;

        Assert.NotEqual(ul.ContentHash(), ulDiffVer.ContentHash());
        Assert.NotEqual(ul.ContentHash(), ulDiffSig.ContentHash());
        Assert.NotEqual(ul.ContentHash(), ulDiffCtr.ContentHash());
        Assert.False(CardSave.ContentEquals(ul, ulDiffVer));
        Assert.False(CardSave.ContentEquals(ul, ulDiffSig));
        Assert.False(CardSave.ContentEquals(ul, ulDiffCtr));
    }

    [Fact]
    public void ContentHashNormalizesEm410xSlotVariantsOnly()
    {
        var em = new CardSave { Tag = TagSpecificType.Em410x, Uid = [1, 2, 3, 4, 5] };
        var em64 = new CardSave { Tag = TagSpecificType.Em410x64, Uid = [1, 2, 3, 4, 5] };
        var electra = new CardSave { Tag = TagSpecificType.Em410xElectra, Uid = [1, 2, 3, 4, 5] };

        Assert.Equal(em.ContentHash(), em64.ContentHash());
        Assert.True(CardSave.ContentEquals(em, em64));
        Assert.NotEqual(em.ContentHash(), electra.ContentHash());
        Assert.False(CardSave.ContentEquals(em, electra));
    }

    [Fact]
    public void CardLibraryHashDictionaryTracksMutationsAndBreaksTies()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);

        var first = ClassicCard(name: "First", fill: 0x10);
        var second = ClassicCard(name: "Second", fill: 0x10);
        var other = ClassicCard(name: "Other", fill: 0x20);
        library.AddRange([first, second, other]);

        Assert.Equal(2, library.FindMatches(ClassicCard(fill: 0x10)).Count);
        Assert.Same(first, library.FindMatch(ClassicCard(fill: 0x10)));
        Assert.Same(second, library.FindMatch(ClassicCard(fill: 0x10), preferredNick: "Second"));
        Assert.Same(second, library.FindMatch(ClassicCard(fill: 0x10), preferredName: "Second"));
        Assert.Same(second, library.FindMatch(ClassicCard(fill: 0x10), preferredId: second.Id));

        // Updating a card's content moves it to the new hash bucket.
        var updated = other.Clone();
        updated.Id = other.Id;
        updated.Data[0][1] ^= 0xFF;
        library.Update(updated);
        Assert.Null(library.FindMatch(ClassicCard(fill: 0x20)));
        Assert.Same(updated, library.FindMatch(updated));

        // Reordering updates bucket order so first match reflects new order.
        library.Reorder([second.Id, first.Id, updated.Id]);
        Assert.Same(second, library.FindMatch(ClassicCard(fill: 0x10)));

        // Reloading from store rebuilds the index.
        var reloaded = new CardLibrary(store);
        Assert.Equal(2, reloaded.FindMatches(ClassicCard(fill: 0x10)).Count);
        Assert.Equal(updated.Id, reloaded.FindMatch(updated)?.Id);

        // Removing removes from the index.
        library.Remove(first);
        Assert.Single(library.FindMatches(ClassicCard(fill: 0x10)));
        library.Remove(second);
        Assert.Empty(library.FindMatches(ClassicCard(fill: 0x10)));
    }

    [Fact]
    public async Task OpeningSlotPerformsFullComparisonAndRejectsSameUidWithDifferentMemory()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        var wrongDumpSameNick = ClassicCard(name: "Office", fill: 0x11, color: "#00FF00");
        var rightDumpOtherName = ClassicCard(name: "Actual Office", fill: 0x33, color: "#0000FF");
        library.AddRange([wrongDumpSameNick, rightDumpOtherName]);

        var slotCard = ClassicCard(name: "Office", fill: 0x33);
        var transport = new SlotCardTransport(slotCard);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        typeof(DeviceService).GetProperty(nameof(DeviceService.DeviceInfo))!.SetValue(device, new DeviceInfo
        {
            FirmwareVersion = "2.4",
            GitVersion = "v2.4.0",
            Model = DeviceModel.Ultra,
            ChipId = "1",
            Address = "00",
            SupportsSlotColors = true,
        });

        var info = new SlotInfo
        {
            Index = 1,
            HfType = TagSpecificType.Mifare1024,
            HfNick = "Office",
            HfColor = "#0000FF",
        };
        var editor = new SlotEditorViewModel(device, library, info, () => { }, _ => { })
        {
            PostToUi = a => a(),
        };
        // Before reading the slot, nick provisionally matched wrongDumpSameNick.
        Assert.Equal(wrongDumpSameNick.Id, editor.Hf.Match?.Card.Id);

        await editor.OnActivatedAsync();

        // After full read, only rightDumpOtherName matches the slot's memory.
        Assert.Equal(rightDumpOtherName.Id, editor.Hf.Match?.Card.Id);
        Assert.False(editor.Hf.IsUnmatched);
        Assert.Equal(SlotColorMode.Card, editor.Hf.ColorMode);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task OpeningSlotMarksCardUnmatchedWhenMemoryOrAnticollDiffersFromSavedCard()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        var uidOnly = new CardSave
        {
            Name = "Office",
            Tag = TagSpecificType.Mifare1024,
            Uid = [0x12, 0x34, 0x56, 0x78],
            Sak = 0x08,
            Atqa = [0x04, 0x00],
        };
        library.Add(uidOnly);

        var slotCard = ClassicCard(name: "Office", fill: 0x55);
        var transport = new SlotCardTransport(slotCard);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        typeof(DeviceService).GetProperty(nameof(DeviceService.DeviceInfo))!.SetValue(device, new DeviceInfo
        {
            FirmwareVersion = "2.4",
            GitVersion = "v2.4.0",
            Model = DeviceModel.Ultra,
            ChipId = "1",
            Address = "00",
            SupportsSlotColors = true,
        });

        var info = new SlotInfo { Index = 1, HfType = TagSpecificType.Mifare1024, HfNick = "Office" };
        var editor = new SlotEditorViewModel(device, library, info, () => { }, _ => { })
        {
            PostToUi = a => a(),
        };
        await editor.OnActivatedAsync();

        Assert.Null(editor.Hf.Match);
        Assert.True(editor.Hf.IsUnmatched);
        Assert.Contains("12 34 56 78", editor.Hf.UnmatchedSummary);

        // Even with a full dump, differing ATS/SAK/ATQA must not match.
        var fullDifferentAts = ClassicCard(name: "Office", fill: 0x55);
        fullDifferentAts.Ats = [0x05, 0x78];
        library.Update(new CardSave
        {
            Id = uidOnly.Id,
            Name = fullDifferentAts.Name,
            Tag = fullDifferentAts.Tag,
            Uid = fullDifferentAts.Uid,
            Sak = fullDifferentAts.Sak,
            Atqa = fullDifferentAts.Atqa,
            Ats = fullDifferentAts.Ats,
            Data = fullDifferentAts.Data,
        });
        editor.OnLibraryChanged();
        Assert.Null(editor.Hf.Match);
        Assert.True(editor.Hf.IsUnmatched);
    }

    [Theory]
    [InlineData(TagSpecificType.Ntag210)]
    [InlineData(TagSpecificType.Ntag213)]
    public async Task OpeningUltralightSlotMatchesFullDumpIncludingExtras(TagSpecificType tag)
    {
        var saved = UltralightCard(tag);
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.Add(saved);

        var transport = new SlotCardTransport(saved);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));
        typeof(DeviceService).GetProperty(nameof(DeviceService.DeviceInfo))!.SetValue(device, new DeviceInfo
        {
            FirmwareVersion = "2.4",
            GitVersion = "v2.4.0",
            Model = DeviceModel.Ultra,
            ChipId = "1",
            Address = "00",
            SupportsSlotColors = true,
        });

        var info = new SlotInfo { Index = 1, HfType = tag, HfNick = "NTAG" };
        var editor = new SlotEditorViewModel(device, library, info, () => { }, _ => { })
        {
            PostToUi = a => a(),
        };
        await editor.OnActivatedAsync();

        Assert.Equal(saved.Id, editor.Hf.Match?.Card.Id);
        Assert.False(editor.Hf.IsUnmatched);
        if (tag == TagSpecificType.Ntag210)
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf0NtagGetCounterData);
    }

    [Fact]
    public void CardLibraryFreeNameAppendsIndexCaseInsensitively()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        Assert.Equal("Office", library.FreeName(" Office "));

        library.Add(new CardSave { Name = "Office", Folder = "Work" });
        Assert.Equal("office (2)", library.FreeName("office"));

        library.Add(new CardSave { Name = "Office (2)" });
        Assert.Equal("Office (3)", library.FreeName("Office"));
        Assert.Equal("Office (3)", library.FreeName("Office (2)"));
        Assert.Equal("Office (4)", library.FreeName("Office (4)"));

        library.Add(new CardSave { Name = "Office (3)" });
        Assert.Equal("Office (4)", library.FreeName("Office (2)"));

        library.Add(new CardSave { Name = "Card (work)" });
        Assert.Equal("Card (work) (2)", library.FreeName("Card (work)"));
        library.Add(new CardSave { Name = "Card (work) (2)" });
        Assert.Equal("Card (work) (3)", library.FreeName("Card (work) (2)"));
    }

    [Fact]
    public async Task SavingUnmatchedSlotCardUsesLastSelectedFolderAndIndexedName()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.AddFolder("Work/Doors");
        // Existing card with the same name in root, and a different dump.
        library.Add(ClassicCard(name: "Office", fill: 0x11));

        var slotCard = ClassicCard(name: "Office", fill: 0x44);
        var transport = new SlotCardTransport(slotCard);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(connection));

        var selectedFolder = "Work/Doors";
        var info = new SlotInfo { Index = 1, HfType = TagSpecificType.Mifare1024, HfNick = "Office" };
        var editor = new SlotEditorViewModel(device, library, info, () => { }, _ => { },
            saveFolder: () => selectedFolder)
        {
            PostToUi = a => a(),
        };
        await editor.OnActivatedAsync();
        Assert.True(editor.Hf.IsUnmatched);

        await ((ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid>)editor.Hf.SaveToLibraryCommand).Execute();

        var saved = Assert.Single(library.Cards, c => c.Name == "Office (2)");
        Assert.Equal("Work/Doors", saved.Folder);
        Assert.Equal(saved.Id, editor.Hf.Match?.Card.Id);
        Assert.False(editor.Hf.IsUnmatched);
        Assert.Contains(transport.Requests, r => r.Command == Command.SetSlotTagNick);

        // Missing folder falls back safely to root instead of recreating a deleted folder.
        selectedFolder = "DeletedFolder";
        var secondSlotCard = ClassicCard(name: "Office", fill: 0x77);
        var secondTransport = new SlotCardTransport(secondSlotCard);
        await using var secondConnection = new ChameleonConnection(secondTransport);
        await secondConnection.OpenAsync();
        typeof(DeviceService).GetProperty(nameof(DeviceService.Client))!.SetValue(device, new ChameleonClient(secondConnection));

        var secondEditor = new SlotEditorViewModel(device, library, info, () => { }, _ => { },
            saveFolder: () => selectedFolder)
        {
            PostToUi = a => a(),
        };
        await secondEditor.OnActivatedAsync();
        await ((ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid>)secondEditor.Hf.SaveToLibraryCommand).Execute();

        var thirdSaved = Assert.Single(library.Cards, c => c.Name == "Office (3)");
        Assert.Equal(string.Empty, thirdSaved.Folder);
        Assert.Null(library.FindFolder("DeletedFolder"));
    }

    [Fact]
    public void SavingUnmatchedLfCycleCardUsesLastSelectedFolderAndIndexedName()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.AddFolder("LF");
        var uid = Convert.FromHexString("0000000002");
        var existingName = LfSlotListViewModel.DefaultName(new LfCycleCard(TagSpecificType.Em410x, uid));
        library.Add(new CardSave { Name = existingName, Tag = TagSpecificType.Em410x, Uid = [9, 9, 9, 9, 9] });

        var info = new SlotInfo { Index = 1, LfType = TagSpecificType.Em410x };
        var side = new SlotSideViewModel(TagSenseType.Lf, info, library, true, () => { }, _ => { },
            _ => Task.CompletedTask, _ => { });
        side.UidRead(TagSpecificType.Em410x, Convert.FromHexString("0000000001"));
        var list = new LfSlotListViewModel(side, library, () => { }, _ => { }, () => "LF");
        list.Load(true, new LfCardCycle([new LfCycleCard(TagSpecificType.Em410x, uid)]));

        var row = list.Cards[1];
        Assert.False(row.HasMatch);
        row.SaveCommand.Execute(null);

        Assert.True(row.HasMatch);
        var saved = Assert.Single(library.Cards, c => c.Name == $"{existingName} (2)");
        Assert.Equal("LF", saved.Folder);
    }

    [Fact]
    public void DuplicatingCardsAssignsAndIncrementsIndexedNames()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        var office = ClassicCard(name: "Office");
        library.Add(office);
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        vm.OpenCard(office.Id);

        vm.DuplicateCommand.Execute(null);
        Assert.Equal(["Office", "Office (2)"], library.Cards.Select(c => c.Name));

        vm.OpenCard(library.Cards[^1].Id);
        vm.DuplicateCommand.Execute(null);
        Assert.Equal(["Office", "Office (2)", "Office (3)"], library.Cards.Select(c => c.Name));

        vm.OpenCard(office.Id);
        vm.DuplicateCommand.Execute(null);
        Assert.Equal(["Office", "Office (2)", "Office (3)", "Office (4)"], library.Cards.Select(c => c.Name));
    }

    [Fact]
    public void EnsureUniqueNamesAndMergeIndexBatchAndImportedCollisions()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        library.Add(ClassicCard(name: "Door"));

        var batch = new[]
        {
            ClassicCard(name: "Door"),
            ClassicCard(name: "Door"),
            ClassicCard(name: "Door (2)"),
        };
        library.EnsureUniqueNames(batch);
        Assert.Equal(["Door (2)", "Door (3)", "Door (4)"], batch.Select(c => c.Name));

        var mergeCandidate = ClassicCard(name: "Door");
        var (added, _) = library.Merge([mergeCandidate], []);
        Assert.Equal(1, added);
        Assert.Equal("Door (2)", mergeCandidate.Name);
    }

    [Fact]
    public void DuplicatingDictionariesAssignsAndIncrementsIndexedNames()
    {
        var store = new MemoryKeyValueStore();
        var dictionaries = new DictionaryLibrary(store);
        var keys = new KeyDictionary { Name = "Keys", Keys = [[1, 2, 3, 4, 5, 6]] };
        dictionaries.Add(keys);
        var vm = new DictionariesViewModel(dictionaries, new CardLibrary(store));
        vm.Selected = vm.Dictionaries[0];

        vm.DuplicateCommand.Execute(null);
        Assert.Equal(["Keys", "Keys (2)"], dictionaries.Dictionaries.Select(d => d.Name));

        vm.Selected = new DictionaryItemViewModel(dictionaries.Dictionaries[^1]);
        vm.DuplicateCommand.Execute(null);
        Assert.Equal(["Keys", "Keys (2)", "Keys (3)"], dictionaries.Dictionaries.Select(d => d.Name));
    }

    private sealed class SlotCardTransport(CardSave slotCard) : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Slot card test";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                var status = Status.Success;
                byte[] payload = [];
                switch (request.Command)
                {
                    case Command.Hf14aGetAntiCollData:
                        payload = [
                            (byte)slotCard.Uid.Length,
                            .. slotCard.Uid,
                            .. (slotCard.Atqa.Length == 2 ? slotCard.Atqa : new byte[] { 0x04, 0x00 }),
                            slotCard.Sak,
                            (byte)slotCard.Ats.Length,
                            .. slotCard.Ats,
                        ];
                        break;
                    case Command.Mf1ReadEmuBlockData:
                    {
                        var start = request.Data[0];
                        var count = request.Data[1];
                        payload = slotCard.Data.Skip(start).Take(count).SelectMany(b => b).ToArray();
                        break;
                    }
                    case Command.Mf0NtagGetPageCount:
                        payload = [(byte)slotCard.Data.Count];
                        break;
                    case Command.Mf0NtagReadEmuPageData:
                    {
                        var start = request.Data[0];
                        var count = request.Data[1];
                        payload = slotCard.Data.Skip(start).Take(count).SelectMany(b => b).ToArray();
                        break;
                    }
                    case Command.Mf0NtagGetVersionData:
                        if (slotCard.Extra.UltralightVersion.Length == 8)
                            payload = slotCard.Extra.UltralightVersion;
                        else
                            status = Status.InvalidSlotType;
                        break;
                    case Command.Mf0NtagGetSignatureData:
                        if (slotCard.Extra.UltralightSignature.Length == 32)
                            payload = slotCard.Extra.UltralightSignature;
                        else
                            status = Status.InvalidSlotType;
                        break;
                    case Command.Mf0NtagGetCounterData:
                    {
                        var idx = request.Data[0];
                        if (idx < slotCard.Extra.UltralightCounters.Count)
                        {
                            var v = slotCard.Extra.UltralightCounters[idx];
                            payload = [(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), 0xBD];
                        }
                        else
                        {
                            status = Status.InvalidSlotType;
                        }
                        break;
                    }
                    case Command.Mf1GetEmulatorConfig:
                        payload = [0, 0, 0, 0, 0];
                        break;
                }
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
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
