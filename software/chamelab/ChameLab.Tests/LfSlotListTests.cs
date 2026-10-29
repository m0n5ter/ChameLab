using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class LfSlotListTests
{
    private static CardSave Card(int value, TagSpecificType type = TagSpecificType.Em410x) => new()
    {
        Name = $"Door {value}", Tag = type, Uid = Convert.FromHexString(value.ToString("X10")),
    };

    private static (LfSlotListViewModel List, SlotSideViewModel Side, CardLibrary Library) Create(bool held = false, bool supported = true)
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        var info = new SlotInfo { Index = 1, LfType = held ? TagSpecificType.Em410x : TagSpecificType.Undefined };
        var side = new SlotSideViewModel(TagSenseType.Lf, info, library, true, () => { }, _ => { }, _ => System.Threading.Tasks.Task.CompletedTask, _ => { });
        if (held) side.UidRead(TagSpecificType.Em410x, Card(1).Uid);
        var list = new LfSlotListViewModel(side, library, () => { }, _ => { });
        list.Load(supported, new LfCardCycle(1, []));
        return (list, side, library);
    }

    [Fact]
    public void AddsUpToEightAndUsesOneChooseButton()
    {
        var (list, side, _) = Create();
        Assert.Equal("Choose saved", list.ChooseLabel);
        for (var i = 1; i <= 9; i++) list.Add(Card(i));
        Assert.Equal(8, list.Cards.Count);
        Assert.False(list.CanAdd);
        Assert.Equal("Add saved", list.ChooseLabel);
        Assert.Equal(Card(1).Uid, side.CurrentCard().Uid);
        Assert.Equal(7, list.Cycle(2).ExtraIds.Length);
        Assert.True(list.IsDirty);
    }

    [Fact]
    public void RefusesDuplicateAndIncompatibleCards()
    {
        var (list, _, _) = Create();
        list.Add(Card(1));
        list.Add(Card(1, TagSpecificType.Em410x64));
        list.Add(Card(2, TagSpecificType.HidProx));
        Assert.Single(list.Cards);
        Assert.False(list.CanChoose(Card(1)));
        Assert.False(list.CanChoose(Card(2, TagSpecificType.HidProx)));
    }

    [Fact]
    public void RemovingFirstPromotesNextAndRemovingLastClearsSlot()
    {
        var (list, side, _) = Create(held: true);
        list.Add(Card(2));
        list.Remove(list.Cards[0]);
        Assert.Equal(Card(2).Uid, side.CurrentCard().Uid);
        Assert.Empty(list.Cycle(1).ExtraIds);
        list.Remove(list.Cards[0]);
        Assert.False(side.HasCard);
        Assert.True(side.IsDirty);
        Assert.False(list.HasCards);
        Assert.True(list.CanAdd);
        Assert.Equal("Choose saved", list.ChooseLabel);
    }

    [Fact]
    public void ReorderingChangesPrimaryAndWireOrderAndKeepsLedSetting()
    {
        var (list, side, _) = Create(held: true);
        list.Add(Card(2)); list.Add(Card(3));
        side.ColorMode = SlotColorMode.Custom;
        side.CustomColor = "#00FF00";
        list.Move(list.Cards[2], -2);
        Assert.Equal(Card(3).Uid, side.CurrentCard().Uid);
        Assert.Equal("010200000000010000000002", Convert.ToHexString(list.Cycle(1).Encode()));
        Assert.Equal(SlotColorMode.Custom, side.ColorMode);
        Assert.Equal("#00FF00", side.CustomColor);
        Assert.True(list.CanReorder);
        Assert.Same(list.Cards, list.ReorderItems);
    }

    [Fact]
    public void ReturningToSavedOrderRestoresCleanPrimary()
    {
        var (list, side, _) = Create(held: true);
        list.Load(true, new LfCardCycle(1, [Card(2).Uid]));
        list.Move(list.Cards[1], -1);
        Assert.True(list.IsDirty);
        Assert.True(side.IsDirty);
        list.Move(list.Cards[0], 1);
        Assert.False(list.IsDirty);
        Assert.False(side.IsDirty);
    }

    [Fact]
    public void DiscardRestoresDeletedAndReorderedCards()
    {
        var (list, side, _) = Create(held: true);
        list.Load(true, new LfCardCycle(1, [Card(2).Uid, Card(3).Uid]));
        list.Remove(list.Cards[0]);
        list.Move(list.Cards[1], -1);
        side.Discard(); list.Discard();
        Assert.Equal(new[] { 1, 2, 3 }, list.Cards.Select(c => (int)c.Card.Uid[^1]));
        Assert.Equal(Card(1).Uid, side.CurrentCard().Uid);
        Assert.False(list.IsDirty);
    }

    [Fact]
    public void LoadedIdsResolveToLinkedLibraryCardsAndRefreshAfterDeletion()
    {
        var (list, _, library) = Create(held: true);
        var second = Card(2);
        library.Add(second);
        list.Load(true, new LfCardCycle(1, [second.Uid]));
        Assert.False(list.Cards[0].HasMatch);
        Assert.True(list.Cards[1].HasMatch);
        Assert.Equal("Door 2", list.Cards[1].Item.Name);
        library.Remove(second);
        list.OnLibraryChanged();
        Assert.False(list.Cards[1].HasMatch);
        Assert.Equal(second.Uid, list.Cards[1].Card.Uid);
        Assert.False(list.IsDirty);
    }

    [Fact]
    public void OldFirmwareAndOtherProtocolsRemainSingleCard()
    {
        var (list, _, _) = Create(supported: false);
        list.Add(Card(1)); list.Add(Card(2));
        Assert.Single(list.Cards);
        Assert.False(list.CanAdd);
        var (other, _, _) = Create();
        other.Add(Card(1, TagSpecificType.Viking));
        Assert.False(other.CanAdd);
        Assert.Single(other.Cards);
    }

    [Fact]
    public void MovesOutsideListAreIgnored()
    {
        var (list, _, _) = Create();
        list.Add(Card(1));
        list.Move(list.Cards[0], -1);
        list.Move(list.Cards[0], 1);
        Assert.Single(list.Cards);
        Assert.False(list.CanReorder);
    }

    [Fact]
    public void DragUsesStableIdentityAndKeepsOrderUnsavedUntilSlotSave()
    {
        var (list, side, _) = Create(held: true);
        list.Load(true, new LfCardCycle(1, [Card(2).Uid, Card(3).Uid]));
        var row = list.Cards[2];
        var key = list.ReorderKey(row);
        list.MoveItem(2, 0);
        list.CommitOrder();
        Assert.Same(row, list.Cards[0]);
        Assert.Equal(key, list.ReorderKey(row));
        Assert.Equal(Card(3).Uid, side.CurrentCard().Uid);
        Assert.True(list.IsDirty);
    }

    [Fact]
    public void RequestsScrollOnlyForNewlyAddedCards()
    {
        var (list, _, _) = Create(held: true);
        Assert.Null(list.RevealCard);
        list.Load(true, new LfCardCycle(1, [Card(2).Uid]));
        Assert.Null(list.RevealCard);
        list.Add(Card(3));
        var added = list.Cards[^1];
        Assert.Same(added, list.RevealCard);
        list.MoveItem(2, 0);
        Assert.Same(added, list.RevealCard);
        list.Remove(added);
        Assert.Null(list.RevealCard);
        list.Add(Card(4));
        Assert.Same(list.Cards[^1], list.RevealCard);
        list.Discard();
        Assert.Null(list.RevealCard);
    }

    [Fact]
    public void SilenceEditsUseTenthsAndDiscardWithTheSlot()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        var editor = new SlotEditorViewModel(new DeviceService([]), library,
            new SlotInfo { Index = 1, LfType = TagSpecificType.Em410x }, () => { }, _ => { });
        editor.SetLfCapabilities([(ushort)Command.Em410xSetCycle, (ushort)Command.Em410xGetCycle]);
        editor.Lf.UidRead(TagSpecificType.Em410x, Card(1).Uid);
        editor.LfCards.Load(true, new LfCardCycle(1, [Card(2).Uid]));
        Assert.False(editor.IsDirty);
        editor.CycleGapSeconds = 1.24m;
        Assert.Equal(1.2m, editor.CycleGapSeconds);
        Assert.True(editor.IsDirty);
        Assert.Equal(12, editor.LfCards.Cycle(editor.CycleBursts, (int)(editor.CycleGapSeconds * 10)).GapSteps);
        editor.Discard();
        Assert.Equal(0, editor.CycleGapSeconds);
        Assert.False(editor.IsDirty);
        editor.CycleGapSeconds = 4;
        Assert.Equal(3, editor.CycleGapSeconds);
        editor.CycleGapSeconds = -1;
        Assert.Equal(0, editor.CycleGapSeconds);
    }

    [Fact]
    public void LegacyFirmwareUsesOriginalPickerAndReplacesExistingLfCard()
    {
        var library = new CardLibrary(new MemoryKeyValueStore());
        var first = Card(1);
        var second = Card(2, TagSpecificType.Viking);
        library.Add(first); library.Add(second);
        var editor = new SlotEditorViewModel(new DeviceService([]), library,
            new SlotInfo { Index = 1, LfType = TagSpecificType.Em410x }, () => { }, _ => { });
        editor.SetLfCapabilities([]);
        editor.Lf.UidRead(TagSpecificType.Em410x, first.Uid);
        Assert.False(editor.SupportsMultiLf);
        Assert.False(editor.ShowLfCycle);
        Assert.False(editor.ShowCycleGap);
        Assert.False(editor.IsDirty);
        editor.Lf.PickCommand.Execute(null);
        Assert.True(editor.IsPicking);
        Assert.Equal("Choose a saved LF card", editor.PickerTitle);
        Assert.Equal(2, editor.PickerCards.Count);
        Assert.Contains(editor.PickerCards, c => c.Card.Id == first.Id);
        editor.PickSavedCard(editor.Lf, second);
        Assert.Equal(TagSpecificType.Viking, editor.Lf.Type);
        Assert.Equal(second.Uid, editor.Lf.CurrentCard().Uid);
        Assert.True(editor.IsDirty);
        editor.Discard();
        Assert.Equal(first.Uid, editor.Lf.CurrentCard().Uid);
        Assert.False(editor.IsDirty);
        editor.Lf.ClearCommand.Execute(null);
        Assert.False(editor.Lf.HasCard);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void MultiLfRequiresBothCycleCommandsAndSilenceIsIndependent()
    {
        var editor = new SlotEditorViewModel(new DeviceService([]), new CardLibrary(new MemoryKeyValueStore()),
            new SlotInfo { Index = 1 }, () => { }, _ => { });
        Assert.False(editor.SupportsMultiLf);
        editor.SetLfCapabilities([(ushort)Command.Em410xSetCycle]);
        Assert.False(editor.SupportsMultiLf);
        editor.SetLfCapabilities([(ushort)Command.Em410xGetCycle]);
        Assert.False(editor.SupportsMultiLf);
        editor.SetLfCapabilities([(ushort)Command.Em410xSetCycleGap, (ushort)Command.Em410xGetCycleGap]);
        Assert.False(editor.ShowCycleGap);
        editor.SetLfCapabilities([(ushort)Command.Em410xSetCycle, (ushort)Command.Em410xGetCycle]);
        Assert.True(editor.SupportsMultiLf);
        Assert.False(editor.ShowCycleGap);
        editor.SetLfCapabilities([(ushort)Command.Em410xSetCycle, (ushort)Command.Em410xGetCycle,
            (ushort)Command.Em410xSetCycleGap, (ushort)Command.Em410xGetCycleGap]);
        Assert.True(editor.SupportsMultiLf);
        Assert.True(editor.ShowCycleGap);
        editor.SetLfCapabilities([]);
        Assert.False(editor.SupportsMultiLf);
        Assert.False(editor.ShowCycleGap);
    }
}
