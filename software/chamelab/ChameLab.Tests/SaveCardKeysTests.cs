using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class SaveCardKeysTests
{
    private const string Empty = "00000000000000000000000000000000";

    private static string Sector(int n, string trailer, string first = Empty) =>
        $"+Sector: {n}\n{first}\n{Empty}\n{Empty}\n{trailer}\n";

    // Sector 0: key A unknown, key B 112233445566; sector 1: A0… / B0…; sector 2 repeats key B of sector 0.
    private static CardSave MiniCard()
    {
        var card = CardFormats.FromMct(
            Sector(0, "------------FF078069112233445566", "4D4F4E531F0804000000000000000000")
            + Sector(1, "A0A1A2A3A4A5FF078069B0B1B2B3B4B5")
            + Sector(2, "112233445566FF078069112233445566"));
        card.Name = "Office";
        return card;
    }

    private static string[] Hex(System.Collections.Generic.IEnumerable<byte[]> keys) =>
        keys.Select(Convert.ToHexString).ToArray();

    [Fact]
    public void ClassicKeysSkipUnknownOnesAndRepeats()
    {
        var editor = new CardEditorViewModel(MiniCard());
        Assert.Equal(["112233445566", "A0A1A2A3A4A5", "B0B1B2B3B4B5"], Hex(editor.Keys()));

        // A key typed into the unknown slot becomes one of the card's keys.
        editor.Memory!.Write(3 * 16, Convert.FromHexString("C0C1C2C3C4C5"));
        Assert.Equal(["C0C1C2C3C4C5", "112233445566", "A0A1A2A3A4A5", "B0B1B2B3B4B5"], Hex(editor.Keys()));
    }

    [Fact]
    public void KeysGoToANewDictionaryAndOnlyMissingOnesToAnExistingOne()
    {
        var card = MiniCard();
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        library.Add(card);
        var dictionaries = new DictionaryLibrary(store);
        dictionaries.Add(new KeyDictionary { Name = "Mine", Keys = [Convert.FromHexString("A0A1A2A3A4A5")] });
        var vm = new CardsViewModel(new DeviceService([]), library, dictionaries);
        vm.OpenCard(card.Id);

        Assert.True(vm.CanSaveKeys);
        Assert.Equal("112233445566\nA0A1A2A3A4A5\nB0B1B2B3B4B5\n", vm.CardKeysText());

        var target = Assert.Single(vm.KeysTargets);
        Assert.Equal("Mine", target.Item.Name);
        target.SaveCommand.Execute(null);
        Assert.Equal(["A0A1A2A3A4A5", "112233445566", "B0B1B2B3B4B5"], Hex(dictionaries.Dictionaries[0].Keys));

        vm.SaveKeysToNewDictionaryCommand.Execute(null);
        Assert.Equal(2, dictionaries.Dictionaries.Count);
        var created = dictionaries.Dictionaries[1];
        Assert.Equal("Office", created.Name);
        Assert.Equal(["112233445566", "A0A1A2A3A4A5", "B0B1B2B3B4B5"], Hex(created.Keys));

        // Saving again does not repeat the name.
        vm.SaveKeysToNewDictionaryCommand.Execute(null);
        vm.SaveKeysToNewDictionaryCommand.Execute(null);
        Assert.Equal(["Mine", "Office", "Office (2)", "Office (3)"], dictionaries.Dictionaries.Select(d => d.Name));
    }
}
