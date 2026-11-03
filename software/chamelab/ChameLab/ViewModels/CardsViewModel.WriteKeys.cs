using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    private MifareWriteSession? _writeSession;
    private int _writeVersion;
    // The current write session's target started with the factory key in every sector.
    private bool _writeTargetHadDefaultKeys;
    // Write another after a factory-keyed target: the next blank target is assumed to use the factory key too.
    private bool _assumeDefaultWriteKeys;

    public bool IsWriteSuccessful
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
        }
    }

    public bool CanChangeWriteOptions => !IsBusy && _writeSession is null && !IsWriteSuccessful;
    public MifareKeyRecoveryViewModel WriteRecovery { get; }
    public bool ShowWriteSectors => IsMifareWrite;
    public bool ShowRecoveryTools => CanDump && (ShowDumpActions || IsDumpRunning);
    public bool ShowWriteProgress => false;
    public bool ShowMfkey32 => IsCardOpen || _cardPrng is not null;
    public bool HasRequiredWriteKeys => !IsGen2Write || WriteRecovery.AllTargetKeysRecovered || _assumeDefaultWriteKeys;
    public string? WriteKeyHint => !HasRequiredWriteKeys && !WriteRecovery.AllPlannedSectorsWritten && !IsWriting
        ? Strings.Cards_RecoverAllKeysHint
        : null;
    public string TryKeysLabel => IsUltralightDump ? Strings.Common_Read : Strings.Common_TryKeys;
    public string TryKeysTip => IsUltralightDump
        ? Strings.Cards_ReadEveryPage
        : Strings.Cards_ReadMissingBlocks;
    public string RecoverySectorHint => Strings.Cards_RecoverySectorHint;
    public string? RecoveryIdentity => null;
    public int RecoverySelectedSector
    {
        get => Form?.SelectedSector ?? 0;
        set { if (Form is { } form) form.SelectedSector = value; }
    }

    private void RaiseWriteKeyAvailability()
    {
        this.RaisePropertyChanged(nameof(HasRequiredWriteKeys));
        this.RaisePropertyChanged(nameof(WriteKeyHint));
    }

    private void ResetWriteRecovery()
    {
        ClearWriteSession();
        WriteRecovery.SetGeneration(SelectedGeneration);
        RefreshWritePreview();
    }

    private void ClearWriteSession()
    {
        _writeVersion++;
        _writeSession = null;
        _assumeDefaultWriteKeys = false;
        IsWriteSuccessful = false;
        this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
        RaiseWriteKeyAvailability();
    }

    private static bool HasOnlyDefaultKeys(MifareDump? target) => target is not null
        && target.KeyA.Concat(target.KeyB).All(key => key is { } k && k.AsSpan().SequenceEqual(MifareClassic.DefaultKeys[0]));

    // Starts a fresh write with the same options. A Gen2 target needs its own keys recovered first,
    // unless the previous target used the factory key everywhere: then the next one is assumed to as well.
    private async Task WriteAnotherAsync()
    {
        var assumeDefault = _writeTargetHadDefaultKeys;
        ResetWriteRecovery();
        _assumeDefaultWriteKeys = assumeDefault;
        RaiseWriteKeyAvailability();
        WriteBlockStates = Array.Empty<MifareBlockState>();
        WriteProgressText = null;
        ClearMessages();
        if (HasRequiredWriteKeys) await WriteAsync();
    }

    private bool IncludeWriteBlock0 => SelectedGeneration != MagicGeneration.Gen2 || WriteGen2Block0;

    private void RefreshWritePreview()
    {
        if (!IsWriteOpen || !IsMifareWrite) return;
        try
        {
            if (ActionCard() is { } card) WriteRecovery.PreviewWrite(card.Tag, card.ToMifareDump(), IncludeWriteBlock0);
        }
        catch (FormatException)
        {
            if (ActionTag is { } tag) WriteRecovery.PreviewWrite(tag, writeBlock0: IncludeWriteBlock0);
        }
    }

    private CardSave ResolveRecoveryTargetCard(MifareDump target) =>
        _library.Cards.FirstOrDefault(card => card.Tag == MifareClassic.ToSlotTagType(target.Type)
            && card.Uid.AsSpan().SequenceEqual(target.Uid)
            && Mfkey32Wizard.FindSlot(_slotInfos, card) is not null)
        ?? CardFormats.FromMifareDump(target);

    private IReadOnlyList<byte[]> SourceWriteKeys()
    {
        try
        {
            if (ActionCard() is not { } card || !card.Tag.IsMifareClassic()) return Array.Empty<byte[]>();
            var dump = card.ToMifareDump();
            return dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
                .Concat(Enumerable.Range(0, dump.SectorCount).SelectMany(dump.TrailerKeys))
                .DistinctBy(Convert.ToHexString).ToArray();
        }
        catch (FormatException) { return Array.Empty<byte[]>(); }
    }
}
