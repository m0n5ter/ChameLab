using System;
using System.Globalization;
using System.Text.Json.Nodes;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>Shared by card reading and target recovery; saved through the normal settings store.</summary>
public sealed class MifareKeySearchSettingsViewModel : ReactiveObject
{
    public const string SettingsKey = "mifareKeySearch";
    private const string DefaultsMigratedKey = "oneSectorDefaultsMigrated";
    public static MifareKeySearchSettingsViewModel Shared { get; } = new();
    public string Title => Strings.KeySearch_Options.ToUpper(CultureInfo.CurrentUICulture);
    private MifareKeySearchMethod _method;
    private MifareKeySearchStart _start;
    private MifareKeySearchOrder _order;

    public MifareKeySearchSettingsViewModel()
    {
        var saved = AppSettings.Get(SettingsKey) as JsonObject;
        static T Read<T>(JsonObject? settings, string name, T fallback) where T : struct, Enum =>
            settings?[name] is JsonValue value && value.TryGetValue<string>(out var text)
            && Enum.TryParse<T>(text, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
        static bool IsMigrated(JsonObject? settings) =>
            settings?[DefaultsMigratedKey] is JsonValue value && value.TryGetValue<bool>(out var migrated) && migrated;

        if (!IsMigrated(saved))
        {
            AppSettings.Update(SettingsKey, node =>
            {
                var settings = node as JsonObject ?? new JsonObject();
                if (IsMigrated(settings)) return settings;
                if (Read(settings, "method", MifareKeySearchMethod.AllSectors) == MifareKeySearchMethod.AllSectors
                    && Read(settings, "start", MifareKeySearchStart.Beginning) == MifareKeySearchStart.Beginning
                    && Read(settings, "sectorOrder", MifareKeySearchOrder.Sequential) == MifareKeySearchOrder.Sequential)
                {
                    settings["method"] = MifareKeySearchMethod.OneSector.ToString();
                    settings["start"] = MifareKeySearchStart.Selected.ToString();
                    settings["sectorOrder"] = MifareKeySearchOrder.Random.ToString();
                }
                settings[DefaultsMigratedKey] = true;
                return settings;
            });
            saved = AppSettings.Get(SettingsKey) as JsonObject;
        }

        _method = Read(saved, "method", MifareKeySearchMethod.OneSector);
        _start = Read(saved, "start", MifareKeySearchStart.Selected);
        _order = Read(saved, "sectorOrder", MifareKeySearchOrder.Random);
    }

    private void Save(string name, string value)
    {
        AppSettings.Update(SettingsKey, node =>
        {
            var saved = node as JsonObject ?? new JsonObject();
            saved[name] = value;
            saved["method"] ??= JsonValue.Create(_method.ToString());
            saved["start"] ??= JsonValue.Create(_start.ToString());
            saved["sectorOrder"] ??= JsonValue.Create(_order.ToString());
            return saved;
        });
    }

    private void SelectMethod(MifareKeySearchMethod value)
    {
        if (_method == value) return;
        _method = value;
        Save("method", value.ToString());
        this.RaisePropertyChanged(nameof(SingleKey));
        this.RaisePropertyChanged(nameof(AllSectors));
        this.RaisePropertyChanged(nameof(OneSector));
    }
    private void SelectStart(MifareKeySearchStart value)
    {
        if (_start == value) return;
        _start = value;
        Save("start", value.ToString());
        this.RaisePropertyChanged(nameof(FromBeginning));
        this.RaisePropertyChanged(nameof(FromSelected));
    }
    private void SelectOrder(MifareKeySearchOrder value)
    {
        if (_order == value) return;
        _order = value;
        Save("sectorOrder", value.ToString());
        this.RaisePropertyChanged(nameof(Sequential));
        this.RaisePropertyChanged(nameof(Random));
    }

    public bool SingleKey { get => _method == MifareKeySearchMethod.SingleKey; set { if (value) SelectMethod(MifareKeySearchMethod.SingleKey); } }
    public bool AllSectors { get => _method == MifareKeySearchMethod.AllSectors; set { if (value) SelectMethod(MifareKeySearchMethod.AllSectors); } }
    public bool OneSector { get => _method == MifareKeySearchMethod.OneSector; set { if (value) SelectMethod(MifareKeySearchMethod.OneSector); } }
    public bool FromBeginning { get => _start == MifareKeySearchStart.Beginning; set { if (value) SelectStart(MifareKeySearchStart.Beginning); } }
    public bool FromSelected { get => _start == MifareKeySearchStart.Selected; set { if (value) SelectStart(MifareKeySearchStart.Selected); } }
    public bool Sequential { get => _order == MifareKeySearchOrder.Sequential; set { if (value) SelectOrder(MifareKeySearchOrder.Sequential); } }
    public bool Random { get => _order == MifareKeySearchOrder.Random; set { if (value) SelectOrder(MifareKeySearchOrder.Random); } }

    public MifareKeySearchOptions CreateOptions(int selectedSector) => new(_method, _start, _order, selectedSector);
}
