namespace ChameLab.Models;

/// <summary>Where one key (A or B) of a sector stands while a dump runs.</summary>
public enum KeyCellState { Unknown, Searching, Found, NotFound }

/// <summary>
/// A sector's two key cells, as the dump sector grid reads them. Implemented by the
/// dump row view model so the grid control can draw without depending on the view models.
/// </summary>
public interface ISectorKeys
{
    int Sector { get; }
    int FirstBlock { get; }
    int LastBlock { get; }
    /// <summary>Every block of the sector is present, independently of key authentication.</summary>
    bool IsRead { get; }
    KeyCellState KeyAState { get; }
    KeyCellState KeyBState { get; }
    /// <summary>Share of the candidate keys tried so far (0..1), or -1 when the total is unknown.</summary>
    double KeyAProgress { get; }
    double KeyBProgress { get; }
    /// <summary>Human text for the tooltip (hex key, "searching 12/40", "—", or placeholder).</summary>
    string KeyAText { get; }
    string KeyBText { get; }
}
