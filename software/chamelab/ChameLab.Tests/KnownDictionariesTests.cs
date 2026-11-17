using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

[Collection(AppSettingsCollection.Name)]
public class KnownDictionariesTests
{
    [Fact]
    public void CatalogPointsAtRawFilesOfBothProjects()
    {
        Assert.All(KnownDictionaries.All, d =>
        {
            Assert.StartsWith("https://raw.githubusercontent.com/", d.Url);
            Assert.EndsWith("/" + d.FileName, d.Url);
        });
        Assert.Equal(KnownDictionaries.All.Count, KnownDictionaries.All.Select(d => d.Url).Distinct().Count());
        Assert.Contains(KnownDictionaries.All, d => d.Project == KnownDictionaries.Proxmark3);
        Assert.Contains(KnownDictionaries.All, d => d.Project == KnownDictionaries.FlipperZero);
        Assert.Contains(KnownDictionaries.All, d => d.Project == KnownDictionaries.Unleashed);
    }

    [Fact]
    public void DownloadedListsKeepKeysWithCommentsAfterThemAndDropTooLongOnes()
    {
        const string text = """
            # MIFARE DESFire default keys
            0000000000000000                                    #NXP Default DES
            00000000000000000000000000000000                    #NXP Default 3DES/AES
            000000000000000000000000000000000000000000000000    #NXP Default 3K3DES
            ffffffffffffffff
            """;
        var source = KnownDictionaries.All.First(d => d.FileName == "mfdes_default_keys.dic");

        var dict = KnownDictionaries.Read(source, "DESFire", text);

        Assert.Equal("DESFire", dict.Name);
        Assert.Equal([8, 16, 8], dict.Keys.Select(k => k.Length));
        Assert.Throws<FormatException>(() => KnownDictionaries.Read(source, "Empty", "# nothing here\n"));
    }

    [Fact]
    public void AddOffersSourcesThenTheDownloads()
    {
        var store = new MemoryKeyValueStore();
        var vm = new DictionariesViewModel(new DictionaryLibrary(store), new CardLibrary(store));

        vm.AddCommand.Execute(null);
        Assert.True(vm.IsPickingSource);
        Assert.True(vm.HasPane);
        Assert.False(vm.IsEditorPane);

        vm.ShowDownloadsCommand.Execute(null);
        Assert.True(vm.IsPickingDownload);
        Assert.Equal(["PROXMARK3", "FLIPPER ZERO", "FLIPPER UNLEASHED"], vm.DownloadSections.Select(s => s.Title));
        Assert.Equal(KnownDictionaries.All.Count, vm.DownloadSections.Sum(s => s.Items.Count));
        Assert.Equal("MIFARE Classic", vm.DownloadSections[0].Items[0].Card);
        Assert.Equal(["PM3 - MIFARE Classic", "FZ - MIFARE Classic", "FZU - MIFARE Classic"],
            vm.DownloadSections.Select(s => s.Items[0].Source.Name));

        vm.BackToAddCommand.Execute(null);
        Assert.True(vm.IsPickingSource);

        vm.NewCommand.Execute(null);
        Assert.True(vm.IsNew);
        Assert.True(vm.IsEditorPane);

        // Dropping the new dictionary returns to choosing a source; Back from there closes the pane.
        vm.CancelNewCommand.Execute(null);
        Assert.True(vm.IsPickingSource);
        vm.CloseCommand.Execute(null);
        Assert.False(vm.HasPane);
    }
}
