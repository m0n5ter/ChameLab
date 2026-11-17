using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ChameLab.Models;
using ChameLab.ViewModels;

namespace ChameLab.Behaviors;

/// <summary>
/// Remembers where a library list (Cards, Dictionaries) was scrolled in each folder on the way down, so
/// going back up lands where the list was left: open a folder, then one in it, go back twice, and each
/// level shows what it showed before. A folder opened anew starts at the top.
/// </summary>
public sealed class FolderScrollMemory
{
    private readonly Control _view;
    private readonly Func<object?, FolderBrowser?> _browserOf;
    // Outer scrollers first: an inner one is laid out within them.
    private readonly ScrollViewer[] _scrolls;
    // The offsets of the folders above the one shown, by path ("" for the items in no folder).
    private readonly Dictionary<string, Vector[]> _saved = new(StringComparer.CurrentCultureIgnoreCase);
    private FolderBrowser? _browser;
    private string _shown = string.Empty;

    public FolderScrollMemory(Control view, Func<object?, FolderBrowser?> browserOf, params ScrollViewer[] scrolls)
    {
        _view = view;
        _browserOf = browserOf;
        _scrolls = scrolls;
        view.DataContextChanged += (_, _) => Attach(_browserOf(_view.DataContext));
        Attach(_browserOf(view.DataContext));
    }

    private void Attach(FolderBrowser? browser)
    {
        if (ReferenceEquals(browser, _browser)) return;
        if (_browser is not null) _browser.CurrentChanged -= OnCurrentChanged;
        _browser = browser;
        _saved.Clear();
        _shown = browser?.Location ?? string.Empty;
        if (browser is not null) browser.CurrentChanged += OnCurrentChanged;
    }

    private void OnCurrentChanged()
    {
        if (_browser is null) return;
        var from = _shown;
        var to = _browser.Location;
        _shown = to;
        if (FolderPath.Same(from, to)) return;

        // Not laid out anew yet: the scrollers still show the folder left.
        _saved[from] = _scrolls.Select(s => s.Offset).ToArray();
        var back = IsAbove(to, from) && _saved.TryGetValue(to, out var offsets) ? offsets : null;
        // Only the folders above the one shown can be gone back to.
        foreach (var path in _saved.Keys.Where(p => !IsAbove(p, to)).ToList()) _saved.Remove(path);

        // Once the new folder's items are laid out, so the offsets are not cut to the old extent.
        Dispatcher.UIThread.Post(() =>
        {
            if (!FolderPath.Same(_shown, to)) return;
            for (var i = 0; i < _scrolls.Length; i++)
                _scrolls[i].Offset = back is null ? default : back[i];
        }, DispatcherPriority.Loaded);
    }

    /// <summary><paramref name="path"/> holds <paramref name="inner"/> (no folder holds every folder).</summary>
    private static bool IsAbove(string path, string inner) =>
        FolderPath.IsWithin(inner, path) && !FolderPath.Same(inner, path);
}
