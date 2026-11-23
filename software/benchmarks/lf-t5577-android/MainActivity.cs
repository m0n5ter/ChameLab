using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using ChameLab.Mobile;

[Activity(Name = "io.github.m0n5ter.chamelab.lft5577bench.MainActivity",
    Label = "ChameLab LF T5577 Test", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        var label = new TextView(this) { Text = "LF T5577 write/read test over Android BLE. Full logs saved after every cycle." };
        SetContentView(label);
        Console.SetOut(new LogWriter());
        try
        {
            var address = Intent?.GetStringExtra("address") ?? throw new ArgumentException("Supply --es address MAC");
            var id = new Guid("00000000-0000-0000-0000-" + address.Replace(":", ""));
            using var asset = Assets!.Open("schedule.json");
            using var input = new StreamReader(asset);
            await LfT5577Runner.Run(new BleTransport(id, "Android BLE " + address),
                await input.ReadToEndAsync(), FilesDir!.AbsolutePath,
                Intent?.GetStringExtra("phase"), Intent?.GetIntExtra("limit", 0) ?? 0);
            label.Text = "LF_BLE_COMPLETE. Results: files/results.json and files/cycles.jsonl";
            Console.WriteLine("LF_BLE_COMPLETE");
        }
        catch (Exception e)
        {
            label.Text = e.ToString();
            Console.WriteLine("LF_BLE_FATAL " + e);
            await File.WriteAllTextAsync(Path.Combine(FilesDir!.AbsolutePath, "fatal.txt"), e.ToString());
        }
    }

    private sealed class LogWriter : StringWriter
    {
        public override void WriteLine(string? value) => Android.Util.Log.Info("LfT5577Bench", value ?? "");
    }
}
