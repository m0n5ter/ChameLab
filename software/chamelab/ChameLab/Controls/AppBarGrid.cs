using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>Keeps the app bar whole until its contents need room, then hides details in order.</summary>
public class AppBarGrid : Grid
{
    private static readonly string[] Steps =
        ["hide-summary", "hide-disconnect-label", "hide-logo", "hide-connection-label"];

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var step in Steps) SetStep(step, false);
        var desired = base.MeasureOverride(Size.Infinity);
        foreach (var step in Steps)
        {
            if (desired.Width <= availableSize.Width) break;
            SetStep(step, true);
            desired = base.MeasureOverride(Size.Infinity);
        }
        return base.MeasureOverride(availableSize);
    }

    private void SetStep(string step, bool enabled)
    {
        if (Classes.Contains(step) == enabled) return;
        Classes.Set(step, enabled);
        // Nested visibility changes must reach the Grid's cached child measurements immediately.
        foreach (var child in this.GetVisualDescendants().OfType<Layoutable>())
            child.InvalidateMeasure();
    }
}
