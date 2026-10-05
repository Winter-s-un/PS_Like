using Avalonia.Controls;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>
/// Filter > Camera Raw Filter's dialog: the shared panel (<see cref="Controls.CameraRawPanel"/>) in a window, previewing
/// live through <paramref name="changed"/> and returning the grade as rendered when OK is pressed. The right-hand dock
/// hosts the same panel when Window > Camera Raw is on, where it stays open and every change is applied instead.
/// </summary>
public static class CameraRawDialog
{
    /// <param name="original">The layer's pixels before the filter, for the eyedropper and Auto.</param>
    /// <param name="graded">Reads the layer as currently previewed, for the histogram.</param>
    /// <param name="saveLookPath">Asks where to save the grade as a .cube; null hides Save Look.</param>
    /// <param name="title">The document's title, for the saved look's TITLE.</param>
    public static async Task<CameraRawSettings?> Show(Window owner, CameraRawSettings initial, SKBitmap original, Action<CameraRawSettings> changed, Func<SKBitmap?> graded,
        Func<Task<string?>>? saveLookPath = null, string title = "")
    {
        var panel = new Controls.CameraRawPanel(narrow: false) { Width = 360 };
        panel.Bind(original, graded, initial, saveLookPath, title, previewOnOpen: false);
        panel.Changed += changed;
        var dialog = new DialogWindow("Camera Raw Filter", panel);
        // The panel previews the grade it opened on as the window appears, as this dialog always has.
        dialog.Opened += (_, _) => panel.PreviewOpeningGrade();
        try { return await dialog.Ask(owner) ? panel.Rendered() : null; }
        finally
        {
            panel.Stop();
            panel.Changed -= changed;
        }
    }
}
