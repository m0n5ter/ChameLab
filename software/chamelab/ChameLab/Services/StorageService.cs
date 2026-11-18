using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>A file chosen for reading: its display name and bytes.</summary>
public readonly record struct PickedFile(string Name, byte[] Data);

/// <summary>
/// Cross-platform file open/save built on Avalonia's <see cref="IStorageProvider"/>.
/// The active <see cref="TopLevel"/> is supplied by the shell view once it is
/// attached, so view models can pick files without referencing any view.
/// </summary>
public static class StorageService
{
    /// <summary>Set by the shell view; resolves the current top-level window.</summary>
    public static Func<TopLevel?>? TopLevelAccessor { get; set; }

    public static bool IsAvailable => TopLevelAccessor?.Invoke()?.StorageProvider is { CanOpen: true };

    private static IStorageProvider Provider =>
        TopLevelAccessor?.Invoke()?.StorageProvider
        ?? throw new InvalidOperationException(Strings.Storage_NoWindow);

    /// <summary>Shows an open dialog and returns the chosen file's bytes, or null if canceled.</summary>
    public static async Task<PickedFile?> OpenAsync(string title, params string[] extensions)
    {
        var files = await Provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = BuildFilters(extensions),
        });

        var file = files.FirstOrDefault();
        if (file is null) return null;

        await using var stream = await file.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return new PickedFile(file.Name, ms.ToArray());
    }

    /// <summary>Shows a save dialog and writes <paramref name="data"/>; returns the saved name or null.</summary>
    public static async Task<string?> SaveAsync(string title, string suggestedName, byte[] data, string extension)
    {
        var file = await Provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = SuggestedName(suggestedName, extension),
            DefaultExtension = extension,
            FileTypeChoices = BuildFilters(new[] { extension }),
        });

        if (file is null) return null;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(data);
        return file.Name;
    }

    /// <summary>
    /// Avalonia's Android picker appends <c>DefaultExtension</c> to the suggested name
    /// unconditionally ("key.dic" became "key.dic.dic"), so there the extension is
    /// dropped from the name; the other platforms add it only when it's missing.
    /// </summary>
    private static string SuggestedName(string name, string extension)
    {
        var suffix = "." + extension.TrimStart('.');
        return OperatingSystem.IsAndroid() && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    /// <summary>Shows a folder dialog; returns the chosen folder's local path, or null if canceled or not local.</summary>
    public static async Task<string?> PickFolderAsync(string title, string? start = null)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is not null && Directory.Exists(start))
            options.SuggestedStartLocation = await Provider.TryGetFolderFromPathAsync(start);
        var folders = await Provider.OpenFolderPickerAsync(options);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static FilePickerFileType[] BuildFilters(string[] extensions)
    {
        if (extensions.Length == 0)
            return new[] { FilePickerFileTypes.All };

        var patterns = extensions.Select(e => "*." + e.TrimStart('.')).ToArray();
        return new[]
        {
            new FilePickerFileType(string.Join(" / ", patterns)) { Patterns = patterns },
            FilePickerFileTypes.All,
        };
    }
}
