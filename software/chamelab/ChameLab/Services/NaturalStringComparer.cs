using System;
using System.Collections.Generic;
using System.Globalization;

namespace ChameLab.Services;

/// <summary>
/// "Natural" name order: runs of digits compare by their numeric value, so "A2" sorts before
/// "A11". Text between the numbers compares with the current culture, ignoring case.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var culture = CultureInfo.CurrentCulture.CompareInfo;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            bool xDigit = char.IsAsciiDigit(x[i]), yDigit = char.IsAsciiDigit(y[j]);
            int iEnd = RunEnd(x, i, xDigit), jEnd = RunEnd(y, j, yDigit);
            var a = x.AsSpan(i, iEnd - i);
            var b = y.AsSpan(j, jEnd - j);

            int c = xDigit && yDigit
                ? CompareNumbers(a, b)
                : culture.Compare(a, b, CompareOptions.IgnoreCase);
            if (c != 0) return c;

            i = iEnd;
            j = jEnd;
        }

        if (i < x.Length) return 1;
        if (j < y.Length) return -1;
        // Equal up to case and leading zeros ("a01" / "A1"): fall back so the order stays stable.
        int tie = culture.Compare(x, y, CompareOptions.IgnoreCase);
        return tie != 0 ? tie : string.CompareOrdinal(x, y);
    }

    private static int RunEnd(string s, int start, bool digits)
    {
        int k = start + 1;
        while (k < s.Length && char.IsAsciiDigit(s[k]) == digits) k++;
        return k;
    }

    /// <summary>Compares two digit runs by value, of any length (no overflow).</summary>
    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var ta = a.TrimStart('0');
        var tb = b.TrimStart('0');
        if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
        int c = ta.SequenceCompareTo(tb);
        // Same value: fewer leading zeros first ("1" before "01").
        return c != 0 ? c : a.Length.CompareTo(b.Length);
    }
}
