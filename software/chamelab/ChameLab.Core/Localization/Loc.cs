using System;
using System.Globalization;

namespace ChameLab.Localization;

/// <summary>Formatting helpers for localized strings.</summary>
public static class Loc
{
    /// <summary>
    /// Picks the plural form for <paramref name="count"/> from <paramref name="forms"/> — forms
    /// separated by '|', in the UI language's order (English: one|other; Russian: one|few|many) —
    /// and formats it with the count as {0}, followed by <paramref name="args"/> as {1}, {2}…
    /// </summary>
    public static string Plural(string forms, long count, params object?[] args)
    {
        var parts = forms.Split('|');
        var form = parts[Math.Min(FormIndex(count, CultureInfo.CurrentUICulture), parts.Length - 1)];
        var all = new object?[args.Length + 1];
        all[0] = count;
        args.CopyTo(all, 1);
        return string.Format(CultureInfo.CurrentCulture, form, all);
    }

    private static int FormIndex(long count, CultureInfo culture)
    {
        var n = Math.Abs(count);
        switch (culture.TwoLetterISOLanguageName)
        {
            case "ru":
            case "uk":
            case "be":
                if (n % 10 == 1 && n % 100 != 11) return 0;
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return 1;
                return 2;
            case "fr":
                // French treats 0 and 1 as singular.
                return n <= 1 ? 0 : 1;
            default:
                return n == 1 ? 0 : 1;
        }
    }
}
