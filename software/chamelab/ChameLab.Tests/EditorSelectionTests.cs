using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class EditorSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardSelectionResetKeepsEditorButSwitchingOrClosingAsksWhenDirty(bool dirty)
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        library.Add(new CardSave { Name = "First" });
        library.Add(new CardSave { Name = "Second" });
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        var first = vm.Cards[0];
        var second = vm.Cards[1];
        vm.Selected = first;
        var form = vm.Form!;
        if (dirty) form.Name = "Edited";

        // The list can write null while the page is being hidden or recreated.
        vm.Selected = null;
        Assert.Same(first, vm.Selected);
        Assert.Same(form, vm.Form);
        Assert.Equal(dirty, form.IsDirty);
        Assert.False(vm.IsConfirmingLeave);

        vm.Selected = second;
        Assert.Equal(dirty, vm.IsConfirmingLeave);
        if (dirty)
        {
            Assert.Same(first, vm.Selected);
            vm.StayCommand.Execute(null);
            Assert.Same(form, vm.Form);
        }
        else Assert.Same(second, vm.Selected);

        vm.CloseCommand.Execute(null);
        Assert.Equal(dirty, vm.IsConfirmingLeave);
        if (dirty)
        {
            vm.DiscardAndLeaveCommand.Execute(null);
            Assert.False(vm.IsConfirmingLeave);
        }
        Assert.Null(vm.Selected);
        Assert.Null(vm.Form);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DictionarySelectionResetKeepsEditorButSwitchingOrClosingAsksWhenDirty(bool dirty)
    {
        var library = new DictionaryLibrary(new MemoryKeyValueStore());
        library.Add(new KeyDictionary { Name = "First" });
        library.Add(new KeyDictionary { Name = "Second" });
        var vm = new DictionariesViewModel(library);
        var first = vm.Dictionaries[0];
        var second = vm.Dictionaries[1];
        vm.Selected = first;
        var form = vm.Form!;
        if (dirty) form.Name = "Edited";

        vm.Selected = null;
        Assert.Same(first, vm.Selected);
        Assert.Same(form, vm.Form);
        Assert.Equal(dirty, form.IsDirty);
        Assert.False(vm.IsConfirmingLeave);

        vm.Selected = second;
        Assert.Equal(dirty, vm.IsConfirmingLeave);
        if (dirty)
        {
            Assert.Same(first, vm.Selected);
            vm.StayCommand.Execute(null);
            Assert.Same(form, vm.Form);
        }
        else Assert.Same(second, vm.Selected);

        vm.CloseCommand.Execute(null);
        Assert.Equal(dirty, vm.IsConfirmingLeave);
        if (dirty)
        {
            vm.DiscardAndLeaveCommand.Execute(null);
            Assert.False(vm.IsConfirmingLeave);
        }
        Assert.Null(vm.Selected);
        Assert.Null(vm.Form);
    }
}
