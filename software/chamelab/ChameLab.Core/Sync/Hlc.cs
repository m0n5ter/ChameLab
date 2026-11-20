using System;
using System.Globalization;

namespace ChameLab.Sync;

/// <summary>
/// A hybrid logical clock timestamp: wall-clock milliseconds, a counter for events within the same
/// millisecond (or while the wall clock lags behind one already seen) and the device that made it.
/// Its text form <c>000001763000000-000000-device</c> sorts like the timestamps themselves.
/// </summary>
public readonly record struct Hlc(long Ms, int Counter, string Node) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, "");

    public int CompareTo(Hlc other)
    {
        var c = Ms.CompareTo(other.Ms);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Node, other.Node);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;

    public DateTimeOffset WallTime => DateTimeOffset.FromUnixTimeMilliseconds(Ms);

    public override string ToString() =>
        $"{Ms.ToString("D15", CultureInfo.InvariantCulture)}-{Counter.ToString("D6", CultureInfo.InvariantCulture)}-{Node}";

    public static Hlc Parse(string text)
    {
        var first = text.IndexOf('-');
        var second = first < 0 ? -1 : text.IndexOf('-', first + 1);
        if (second < 0) throw new FormatException($"Not a timestamp: {text}");
        return new Hlc(
            long.Parse(text.AsSpan(0, first), NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(text.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture),
            text[(second + 1)..]);
    }
}

/// <summary>Issues timestamps that follow the wall clock but always move forward past every one seen.</summary>
public sealed class HlcClock(string node, Func<long>? now = null)
{
    private readonly Func<long> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private long _ms;
    private int _counter;

    public string Node => node;

    public Hlc Next()
    {
        var wall = _now();
        if (wall > _ms)
        {
            _ms = wall;
            _counter = 0;
        }
        else
        {
            _counter++;
        }
        return new Hlc(_ms, _counter, node);
    }

    /// <summary>Takes in a timestamp from elsewhere, so later local ones sort after it.</summary>
    public void Observe(Hlc time)
    {
        if (time.Ms > _ms || time.Ms == _ms && time.Counter > _counter)
        {
            _ms = time.Ms;
            _counter = time.Counter;
        }
    }
}

/// <summary>
/// Sort keys that always have room between two others (fractional indexing): reordering one item
/// gives it a key between its new neighbours and leaves every other key alone.
/// </summary>
public static class FractionalIndex
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Base = 62;

    /// <summary>A key between <paramref name="before"/> and <paramref name="after"/> (null for the ends).</summary>
    public static string Between(string? before, string? after)
    {
        before ??= string.Empty;
        if (after is not null && string.CompareOrdinal(before, after) >= 0)
            throw new ArgumentException($"'{before}' does not sort before '{after}'.");
        return Midpoint(before, after);
    }

    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key[^1] != '0' && key.AsSpan().IndexOfAnyExcept(Digits) < 0;

    private static int Digit(char c) => Digits.IndexOf(c);

    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : '0') == b[n]) n++;
            if (n > 0) return b[..n] + Midpoint(a.Length > n ? a[n..] : string.Empty, b[n..]);
        }
        var da = a.Length > 0 ? Digit(a[0]) : 0;
        var db = b is not null ? Digit(b[0]) : Base;
        if (db - da > 1) return Digits[(da + db) / 2].ToString();
        if (b is not null && b.Length > 1) return b[..1];
        return Digits[da] + Midpoint(a.Length > 0 ? a[1..] : string.Empty, null);
    }
}
