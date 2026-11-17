using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChameLab.Models;

/// <summary>
/// Folders nest: a folder is stored as the path of names from the top, joined by
/// <see cref="Separator"/> (<c>Work/Doors</c>); the empty path is no folder. Names never hold the
/// separator, and folder names compare ignoring case.
/// </summary>
public static class FolderPath
{
    public const char Separator = '/';

    /// <summary>Longest name of one folder kept.</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// The path cleaned up: names trimmed and cut to <see cref="MaxNameLength"/>, empty ones dropped.
    /// A name with the separator in it (<c>Work / Doors</c>, as older versions filed nested folders)
    /// becomes a path.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return string.Join(Separator, path.Split(Separator).Select(Cut).Where(n => n.Length > 0));
    }

    /// <summary>One folder's name as typed: trimmed, the separator replaced, cut to <see cref="MaxNameLength"/>.</summary>
    public static string NormalizeName(string? name) => Cut(name?.Replace(Separator, '-'));

    private static string Cut(string? name)
    {
        var n = name?.Trim() ?? string.Empty;
        return n.Length > MaxNameLength ? n[..MaxNameLength].TrimEnd() : n;
    }

    /// <summary>The folder holding this one; empty for a top folder.</summary>
    public static string Parent(string path)
    {
        var i = path.LastIndexOf(Separator);
        return i < 0 ? string.Empty : path[..i];
    }

    /// <summary>The folder's own name, the last of the path.</summary>
    public static string Name(string path) => path[(path.LastIndexOf(Separator) + 1)..];

    /// <summary>0 for a top folder, 1 for a folder in it, and so on.</summary>
    public static int Depth(string path) => path.Count(c => c == Separator);

    public static string Combine(string parent, string name) =>
        parent.Length == 0 ? name : name.Length == 0 ? parent : parent + Separator + name;

    /// <summary>The folder is <paramref name="ancestor"/> itself or somewhere inside it.</summary>
    public static bool IsWithin(string path, string ancestor) =>
        ancestor.Length == 0
        || string.Equals(path, ancestor, StringComparison.CurrentCultureIgnoreCase)
        || path.Length > ancestor.Length && path[ancestor.Length] == Separator
            && string.Equals(path[..ancestor.Length], ancestor, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>A path inside <paramref name="from"/> (or it itself) moved to <paramref name="to"/>.</summary>
    public static string Rebase(string path, string from, string to) =>
        IsWithin(path, from) ? Combine(to, path[from.Length..].TrimStart(Separator)) : path;

    /// <summary>The folders from the top down to this one: <c>Work</c>, <c>Work/Doors</c>.</summary>
    public static IEnumerable<string> SelfAndAncestors(string path)
    {
        for (var i = path.IndexOf(Separator); i >= 0; i = path.IndexOf(Separator, i + 1))
            yield return path[..i];
        if (path.Length > 0) yield return path;
    }

    public static bool Same(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Tree order: a folder before the folders in it, those before its next sibling; names A–Z
    /// ignoring case, with runs of digits compared as numbers where the platform can.
    /// </summary>
    public static IComparer<string> TreeOrder { get; } = new TreeComparer();

    private sealed class TreeComparer : IComparer<string>
    {
        private static readonly StringComparer Names = MakeNameOrder();

        private static StringComparer MakeNameOrder()
        {
            try
            {
                var comparer = CultureInfo.CurrentCulture.CompareInfo
                    .GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
                comparer.Compare("a2", "a10");
                return comparer;
            }
            catch (Exception)
            {
                return StringComparer.CurrentCultureIgnoreCase;
            }
        }

        public int Compare(string? x, string? y)
        {
            var a = (x ?? string.Empty).Split(Separator);
            var b = (y ?? string.Empty).Split(Separator);
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = Names.Compare(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
