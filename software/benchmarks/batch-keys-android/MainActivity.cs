using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using ChameLab.Mobile;

[Activity(Label = "ChameLab Batch Benchmark", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        var label = new TextView(this) { Text = "Running read-only BLE key benchmark. Results are written after each pass." };
        SetContentView(label);
        Console.SetOut(new LogWriter());
        try
        {
            var address = Intent?.GetStringExtra("address") ?? throw new ArgumentException("Supply --es address MAC");
            var id = new Guid("00000000-0000-0000-0000-" + address.Replace(":", ""));
            if (Intent?.GetStringExtra("probe") == "dfu")
                await LfProbe.Flash(id, Path.Combine(FilesDir!.AbsolutePath, "firmware.zip"),
                    Intent.GetBooleanExtra("bootloader", false));
            else if (Intent?.GetStringExtra("probe") is "lf" or "lfraw")
                await LfProbe.Run(new BleTransport(id, "Android BLE " + address),
                    Path.Combine(FilesDir!.AbsolutePath, "lf.json"),
                    Intent.GetStringExtra("probe") == "lfraw" ? 0 : Intent.GetIntExtra("repetitions", 100));
            else
                await Runner.Run(new BleTransport(id, "Android BLE " + address), Path.Combine(FilesDir!.AbsolutePath, "ble.json"),
                    Intent?.GetIntExtra("repetitions", 3) ?? 3, caseFilter: Intent?.GetStringExtra("cases"));
            label.Text = "Benchmark complete. Results: ble.json";
            Console.WriteLine("BENCHMARK_COMPLETE");
        }
        catch (Exception e) { label.Text = e.ToString(); Console.WriteLine("BENCHMARK_FAILED " + e); }
    }
    private sealed class LogWriter : StringWriter
    {
        public override void WriteLine(string? value) => Android.Util.Log.Info("BatchKeysBench", value ?? "");
    }
}
