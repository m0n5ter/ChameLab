using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    private MifareWriteSession? _writeSession;
    private int _writeVersion;
    private bool _isWriteSuccessful;
    public bool IsWriteSuccessful
    {
        get => _isWriteSuccessful;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isWriteSuccessful, value);
            this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
        }
    }
    public bool CanChangeWriteOptions => !IsBusy && _writeSession is null && !IsWriteSuccessful;
    public MifareKeyRecoveryViewModel WriteRecovery { get; }
    public bool ShowWriteSectors => IsMifareWrite;
    public bool ShowRecoveryTools => CanDump && (ShowDumpActions || IsDumpRunning);
    public bool ShowWriteProgress => false;
    public bool ShowMfkey32 => true;
    public bool HasRequiredWriteKeys => !IsGen2Write || WriteRecovery.AllTargetKeysRecovered;
    public string? WriteKeyHint => IsGen2Write && WriteRecovery is { AllTargetKeysRecovered: false, AllPlannedSectorsWritten: false } && !IsWriting
        ? "Recover keys A and B for every sector of the target card. Writing is available when all sectors are green."
        : null;
    public string TryKeysTip => "Read missing blocks with the selected keys";
    public string RecoverySectorHint => "Select a sector to edit its blocks; hover for keys";
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
        IsWriteSuccessful = false;
        this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
    }

    private void WriteAnother()
    {
        ResetWriteRecovery();
        WriteBlockStates = Array.Empty<MifareBlockState>();
        WriteProgressText = null;
        ClearMessages();
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
                .Concat(Enumerable.Range(0, dump.SectorCount)
                    .Select(s => dump.GetBlock(MifareClassic.SectorTrailerBlock(s)))
                    .Where(b => b is { Length: 16 })
                    .SelectMany(b => new[] { b![..6], b![10..] }))
                .DistinctBy(Convert.ToHexString).ToArray();
        }
        catch (FormatException) { return Array.Empty<byte[]>(); }
    }
}
