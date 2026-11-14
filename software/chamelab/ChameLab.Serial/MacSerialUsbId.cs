using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ChameLab.Transport.Serial;

/// <summary>Maps macOS call-out nodes (/dev/cu.usbmodem…) to the USB VID/PID of the device behind them.</summary>
internal static partial class MacSerialUsbId
{
    [GeneratedRegex(@"\+-o .*<class (\w+)")]
    private static partial Regex ObjectLine();

    [GeneratedRegex(@"""(idVendor|idProduct)""\s*=\s*(\d+)")]
    private static partial Regex IdProperty();

    [GeneratedRegex(@"""IOCalloutDevice""\s*=\s*""([^""]+)""")]
    private static partial Regex CalloutProperty();

    /// <summary>Runs <c>ioreg</c> and returns the ids behind every USB serial port; empty if that fails.</summary>
    public static IReadOnlyDictionary<string, (int Vid, int Pid)> ReadAll()
    {
        try
        {
            var start = new ProcessStartInfo("/usr/sbin/ioreg", "-r -c IOUSBHostDevice -l -w0")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null) return new Dictionary<string, (int, int)>();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return new Dictionary<string, (int, int)>();
            }
            return Parse(output.Result);
        }
        catch
        {
            return new Dictionary<string, (int, int)>();
        }
    }

    /// <summary>
    /// Parses <c>ioreg -l</c> output. Objects are <c>+-o Name &lt;class X, …&gt;</c> lines whose column
    /// gives their depth, followed by their properties. A call-out node belongs to the nearest USB
    /// device above it; interfaces carry ids too, so the class decides, not just the presence of ids.
    /// </summary>
    internal static Dictionary<string, (int Vid, int Pid)> Parse(string ioreg)
    {
        var result = new Dictionary<string, (int Vid, int Pid)>(StringComparer.Ordinal);
        var stack = new List<Node>();

        foreach (var raw in ioreg.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ObjectLine().Match(line) is { Success: true } obj)
            {
                var column = line.IndexOf("+-o", StringComparison.Ordinal);
                while (stack.Count > 0 && stack[^1].Column >= column) stack.RemoveAt(stack.Count - 1);
                stack.Add(new Node(column, obj.Groups[1].Value));
                continue;
            }
            if (stack.Count == 0) continue;

            foreach (Match m in IdProperty().Matches(line))
            {
                var value = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (m.Groups[1].Value == "idVendor") stack[^1].Vid ??= value;
                else stack[^1].Pid ??= value;
            }

            if (CalloutProperty().Match(line) is { Success: true } callout)
            {
                for (var i = stack.Count - 1; i >= 0; i--)
                {
                    var node = stack[i];
                    if (node.IsUsbDevice && node.Vid is { } vid && node.Pid is { } pid)
                    {
                        result[callout.Groups[1].Value] = (vid, pid);
                        break;
                    }
                }
            }
        }
        return result;
    }

    private sealed class Node(int column, string cls)
    {
        public int Column { get; } = column;
        public bool IsUsbDevice { get; } = cls is "IOUSBHostDevice" or "IOUSBDevice";
        public int? Vid { get; set; }
        public int? Pid { get; set; }
    }
}
