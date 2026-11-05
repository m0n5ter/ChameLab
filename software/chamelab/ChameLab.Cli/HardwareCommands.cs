using ChameLab.Protocol;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private partial void RegisterHardware()
    {
        Add("hw connect", ConnectAsync);
        Add("hw disconnect", (_, _) => DisconnectAsync());
        Add("hw chipid", async (_, ct) => Print("Device chip ID: " + await Client.GetChipIdAsync(ct)));
        Add("hw address", async (_, ct) => Print("Device address: " + await Client.GetAddressAsync(ct)));
        Add("hw version", async (_, ct) => Print($"Chameleon {await Client.GetDeviceModelAsync(ct)}, Version: v{await Client.GetAppVersionAsync(ct)} ({await Client.GetGitVersionAsync(ct)})"));
        Add("hw mode", async (a, ct) =>
        {
            if (a.Flag("reader") || a.Flag("emulator")) await Client.SetDeviceModeAsync(a.Flag("reader") ? DeviceMode.Reader : DeviceMode.Tag, ct);
            Print("Device Mode: " + await Client.GetDeviceModeAsync(ct));
        });
        Add("hw battery", async (_, ct) =>
        {
            var b = await Client.GetBatteryInfoAsync(ct);
            Print($"voltage: {b.Millivolts} mV\npercentage: {b.Percent}%\ncondition: {b.ConditionText}");
        });
        Add("hw dfu", async (_, ct) => { await Wire((int)Command.EnterBootloader, null, ct); await DisconnectAsync(); });
        Add("hw factory_reset", async (a, ct) => { Force(a); await Client.WipeFdsAsync(ct); });
        Add("hw raw", async (a, ct) =>
        {
            var id = a.Has("command") ? CommandCatalog.EnumValue("Command", a.Text("command")) : a.Int("num_command");
            Range(id, 0, ushort.MaxValue, "command");
            var f = await Client.Connection.SendAsync((Command)id, a.Hex("data"), timeoutMs: checked(Range(a.Int("timeout"), 1, 3600, "timeout") * 1000), ct: ct);
            Print($"Command: {(ushort)f.Command}; status: 0x{(ushort)f.Status:X2} ({f.Status.Describe()})"); Print(f.Data);
            if (!f.Status.IsOk()) throw new ChameleonException(f.Status.Describe(), f.Status);
        });
        Add("hw slot list", async (a, ct) =>
        {
            foreach (var s in await Client.GetAllSlotsAsync(ct))
            {
                Print($"Slot {s.Index}: {(s.IsActive ? "active" : "")} HF {s.HfType.DisplayName()} ({s.HfEnabled}); LF {s.LfType.DisplayName()} ({s.LfEnabled})");
                if (!a.Flag("short")) Print($"  HF: {s.HfNick} {s.HfColor}; LF: {s.LfNick} {s.LfColor}");
            }
        });
        Add("hw slot change", async (a, ct) => await Client.SetActiveSlotAsync(a.Int("slot"), ct));
        Add("hw slot type", async (a, ct) => await Client.SetSlotTagTypeAsync(await Slot(a, ct), (TagSpecificType)CommandCatalog.EnumValue("TagSpecificType", a.Text("type")), ct));
        Add("hw slot init", async (a, ct) => await Client.SetSlotDataDefaultAsync(await Slot(a, ct), (TagSpecificType)CommandCatalog.EnumValue("TagSpecificType", a.Text("type")), ct));
        Add("hw slot delete", async (a, ct) => await Client.DeleteSlotSenseTypeAsync(await Slot(a, ct), Sense(a), ct));
        Add("hw slot enable", async (a, ct) => await Client.SetSlotEnableAsync(await Slot(a, ct), Sense(a), true, ct));
        Add("hw slot disable", async (a, ct) => await Client.SetSlotEnableAsync(await Slot(a, ct), Sense(a), false, ct));
        Add("hw slot store", (_, ct) => Client.SaveSlotConfigAsync(ct));
        Add("hw slot prng", async (a, ct) => await InSlot(a, ct, async () =>
        {
            if (a.Has("type")) await Client.SetMf1PrngTypeAsync((MifareClassicPrngType)a.Int("type"), ct);
            Print(await Client.GetMf1PrngTypeAsync(ct));
        }));
        Add("hw slot nick", async (a, ct) =>
        {
            var slot = await Slot(a, ct); var sense = Sense(a);
            if (a.Flag("delete")) await Client.DeleteSlotNickAsync(slot, sense, ct);
            else if (a.Has("name")) await Client.SetSlotNickAsync(slot, sense, a.Text("name"), ct);
            var s = (await Client.GetAllSlotsAsync(ct)).Single(s => s.Index == slot);
            Print(sense == TagSenseType.Hf ? s.HfNick : s.LfNick);
        });
        Add("hw slot color", async (a, ct) =>
        {
            var slot = await Slot(a, ct); var sense = Sense(a);
            if (a.Flag("default") || a.Has("color"))
            {
                var color = a.Flag("default") ? null : "#" + Convert.ToHexString(a.Hex("color", 3));
                await Client.SetSlotColorAsync(slot, sense, color, ct);
            }
            var s = (await Client.GetAllSlotsAsync(ct)).Single(s => s.Index == slot);
            Print((sense == TagSenseType.Hf ? s.HfColor : s.LfColor) ?? "default");
        });
        Add("hw slot openall", async (_, ct) =>
        {
            for (var slot = 1; slot <= 8; slot++)
                foreach (var type in new[] { TagSpecificType.Mifare1024, TagSpecificType.Em410x })
                {
                    await Client.SetSlotTagTypeAsync(slot, type, ct); await Client.SetSlotDataDefaultAsync(slot, type, ct);
                    await Client.SetSlotEnableAsync(slot, type.SenseType(), true, ct);
                }
            await Client.SaveSlotConfigAsync(ct);
        });
        Add("hw settings animation", async (a, ct) =>
        {
            if (a.Has("mode")) await Client.SetAnimationModeAsync((AnimationMode)CommandCatalog.EnumValue("AnimationMode", a.Text("mode")), ct);
            Print(await Client.GetAnimationModeAsync(ct));
        });
        Add("hw settings sleeptimeout", async (a, ct) =>
        {
            if (a.Has("seconds")) await Client.SetSleepTimeoutAsync(Range(a.Int("seconds"), 5, 60, "seconds"), ct);
            Print(await Client.GetSleepTimeoutAsync(ct));
        });
        Add("hw settings longpressthreshold", async (a, ct) =>
        {
            if (a.Has("duration")) await Client.SetLongPressThresholdAsync(Range(a.Int("duration"), 200, 65535, "duration"), ct);
            Print(await Client.GetLongPressThresholdAsync(ct));
        });
        Add("hw settings bleclearbonds", async (a, ct) => { Force(a); await Client.DeleteAllBleBondsAsync(ct); });
        Add("hw settings store", (_, ct) => Client.SaveSettingsAsync(ct));
        Add("hw settings reset", async (a, ct) => { Force(a); await Client.ResetSettingsAsync(ct); });
        Add("hw settings blekey", async (a, ct) =>
        {
            if (a.Has("key")) await Client.SetBlePairingKeyAsync(a.Text("key"), ct);
            Print(await Client.GetBlePairingKeyAsync(ct));
        });
        Add("hw settings blepair", async (a, ct) =>
        {
            if (a.Flag("enable") || a.Flag("disable")) await Client.SetBlePairingEnableAsync(a.Flag("enable"), ct);
            Print(await Client.GetBlePairingEnableAsync(ct));
        });
        Add("hw settings btnpress", async (a, ct) =>
        {
            var buttons = a.Flag("a") ? new[] { ButtonType.A } : a.Flag("b") ? new[] { ButtonType.B } : [ButtonType.A, ButtonType.B];
            foreach (var b in buttons)
            {
                if (a.Has("function")) await Client.SetButtonConfigAsync(b, a.Flag("long"), (ButtonPressFunction)CommandCatalog.EnumValue("ButtonPressFunction", a.Text("function")), ct);
                Print($"{b}: {await Client.GetButtonConfigAsync(b, a.Flag("long"), ct)}");
            }
        });
    }

    private async Task InSlot(CliArguments a, CancellationToken ct, Func<Task> work)
    {
        var before = await Client.GetActiveSlotAsync(ct);
        try
        {
            if (a.Has("slot")) await Client.SetActiveSlotAsync(Range(a.Int("slot"), 1, 8, "slot"), ct);
            await work();
        }
        finally
        {
            if (Connected && a.Has("slot"))
            {
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Client.SetActiveSlotAsync(before, restore.Token);
            }
        }
    }
}
