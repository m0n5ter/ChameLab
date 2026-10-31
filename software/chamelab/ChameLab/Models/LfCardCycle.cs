using System;
using System.Linq;

namespace ChameLab.Models;

/// <summary>Additional EM410x UIDs; the slot's primary card is always first.</summary>
public sealed record LfCardCycle(int Bursts, byte[][] ExtraIds, int GapSteps = 0)
{
    public static LfCardCycle Parse(string text, int bursts)
    {
        if (bursts is < 1 or > 10) throw new ArgumentException("Time per card must be between 1 and 10 bursts.");
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 7) throw new ArgumentException("A slot can hold seven additional cards (eight total).");
        var ids = lines.Select(line =>
        {
            var hex = line.Replace(" ", "").Replace(":", "").Replace("-", "");
            if (hex.Length != 10 || !hex.All(Uri.IsHexDigit))
                throw new ArgumentException("Each additional EM410x UID must contain exactly 10 hexadecimal digits.");
            return Convert.FromHexString(hex);
        }).ToArray();
        if (ids.Select(Convert.ToHexString).Distinct().Count() != ids.Length)
            throw new ArgumentException("The list contains duplicate UIDs.");
        return new LfCardCycle(bursts, ids);
    }

    public byte[] Encode()
    {
        if (Bursts is < 1 or > 10 || GapSteps is < 0 or > 30 || ExtraIds.Length > 7 || ExtraIds.Any(id => id.Length != 5))
            throw new ArgumentException("Invalid EM410x cycle.");
        var data = new byte[2 + 5 * ExtraIds.Length];
        data[0] = (byte)Bursts;
        data[1] = (byte)ExtraIds.Length;
        for (var i = 0; i < ExtraIds.Length; i++) ExtraIds[i].CopyTo(data, 2 + 5 * i);
        return data;
    }

    public static LfCardCycle Decode(byte[] data)
    {
        if (data.Length < 2 || data[0] is < 1 or > 10 || data[1] > 7 || data.Length != 2 + 5 * data[1])
            throw new ArgumentException("Invalid EM410x cycle response.");
        return new LfCardCycle(data[0], Enumerable.Range(0, data[1]).Select(i => data[(2 + 5 * i)..(7 + 5 * i)]).ToArray());
    }

    /// <summary>The same timing and cards (records compare the UID arrays by reference).</summary>
    public bool SameAs(LfCardCycle other) => Bursts == other.Bursts && GapSteps == other.GapSteps &&
        ExtraIds.Length == other.ExtraIds.Length && ExtraIds.Zip(other.ExtraIds).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    public string Text => string.Join(Environment.NewLine, ExtraIds.Select(Convert.ToHexString));
}
