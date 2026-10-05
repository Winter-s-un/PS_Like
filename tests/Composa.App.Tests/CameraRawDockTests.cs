using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>
/// Window > Camera Raw: the grading panel in the right-hand dock. Where Filter > Camera Raw Filter… opens the dialog
/// and applies on OK, the dock previews as the person drags and applies the sitting when they move on, as one step.
/// </summary>
public class CameraRawDockTests
{
    [AvaloniaFact]
    public void The_dock_panel_grades_live_and_applies_one_step_when_it_is_switched_off()
    {
        var (window, session) = NewWindowWithPhoto();
        // The panel is off until Window > Camera Raw asks for it, so the dialog stays the default way to grade.
        Assert.DoesNotContain(window.GetVisualDescendants(), v => v is CameraRawPanel);
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<CameraRawPanel>().Single();
        Assert.True(panel.IsEnabled);
        Assert.True(panel.IsBound);
        Assert.Equal(9, panel.GetVisualDescendants().OfType<Expander>().Count());
        Screenshots.Save(window, "31-camera-raw-dock");

        // A slider stepped is the person's grade, and nothing is in the history until they move on.
        var brightness = session.ActiveLayer!.Pixels!.GetPixel(10, 10).Red;
        var exposure = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        // The panel's list scrolls, so the field is brought into view before it is clicked, as a person would scroll to it.
        exposure.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var where = exposure.TranslatePoint(new Point(exposure.Bounds.Width / 2, exposure.Bounds.Height / 2), window)!.Value;
        window.MouseDown(where, MouseButton.Left);
        window.MouseUp(where, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(exposure.Value > 0, $"value={exposure.Value} where={where} field={exposure.Bounds} panel={panel.Bounds} client={window.ClientSize} focused={exposure.IsFocused}");
        // It is on the canvas at once: the preview has already replaced the layer's pixels, and the history has no
        // step yet because the sitting is not over.
        Assert.True(session.IsPreviewing, "the grade previews as it is made");
        Assert.True(session.ActiveLayer!.Pixels!.GetPixel(10, 10).Red > brightness, "the grade is on the canvas already");
        Assert.NotEqual("Camera Raw Filter", session.History.UndoName);
        // The group's caption stays inside the panel once the group's eye appears beside it.
        var caption = panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Light");
        var origin = caption.TranslatePoint(new Point(0, 0), panel);
        Assert.True(origin is { X: >= 0 }, $"the caption sits at x {origin?.X} in the panel");
        var eye = panel.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible && b.GetVisualAncestors().Any(a => a is Expander));
        Assert.True(eye.Bounds.Width > 0, "the group's eye is laid out");
        Screenshots.Save(window, "32-camera-raw-dock-graded");

        // Switching the panel off applies the sitting: one step, named after the filter, and the layer is brighter.
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Camera Raw Filter", session.History.UndoName);
        Assert.True(session.ActiveLayer!.Pixels!.GetPixel(10, 10).Red > brightness);
        Assert.DoesNotContain(window.GetVisualDescendants(), v => v is CameraRawPanel);
    }

    [AvaloniaFact]
    public void Selecting_another_layer_applies_the_sitting_and_the_panel_follows_it()
    {
        var (window, session) = NewWindowWithPhoto();
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<CameraRawPanel>().Single();
        var graded = session.ActiveLayer!;                       // the layer the sitting is made on
        var brightness = graded.Pixels!.GetPixel(10, 10).Red;
        var exposure = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        exposure.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var where = exposure.TranslatePoint(new Point(exposure.Bounds.Width / 2, exposure.Bounds.Height / 2), window)!.Value;
        window.MouseDown(where, MouseButton.Left);
        window.MouseUp(where, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(exposure.Value > 0, $"value={exposure.Value} where={where} field={exposure.Bounds}");

        var second = Rendering.Pixels.NewColor(160, 100);
        second.Erase(new SKColor(60, 60, 60));
        session.AddImageLayer("Second", second);
        Dispatcher.UIThread.RunJobs();

        // The sitting landed on the layer it was made on, the new layer is untouched, and the panel is on it now.
        Assert.True(graded.Pixels!.GetPixel(10, 10).Red > brightness);
        Assert.True(session.ActiveLayer!.Pixels!.GetPixel(10, 10).Red < brightness);
        Assert.True(panel.IsBound);
    }

    /// <summary>The docked panel is drawn in the application's own text size, the same as the dialog and the panels beside it.</summary>
    [AvaloniaFact]
    public void The_dock_panel_uses_the_text_size_of_the_interface()
    {
        var (window, session) = NewWindowWithPhoto();
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        var docked = window.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        var dockedSize = TextElement.GetFontSize(docked);

        var original = session.ActiveLayer!.Pixels!;
        _ = CameraRawDialog.Show(window, new CameraRawSettings(), original, _ => { }, () => original);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Last();
        var inDialog = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        var dialogSize = TextElement.GetFontSize(inDialog);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(12.5, dockedSize);
        Assert.Equal(dialogSize, dockedSize);
        // The rendered text reads like the interface around it: the same metrics as the Layers panel's own labels.
        var layerLabel = window.GetVisualDescendants().OfType<LayersPanel>().Single()
            .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Photo");
        var dockedText = Metrics(docked);
        var dialogText = Metrics(inDialog);
        var layerText = Metrics(layerLabel);
        Assert.True(dockedText == layerText,
            $"Layers {layerText} family={TextElement.GetFontFamily(layerLabel)} size={TextElement.GetFontSize(layerLabel)} | docked {dockedText} family={TextElement.GetFontFamily(docked)} | dialog {dialogText} family={TextElement.GetFontFamily(inDialog)}");
    }

    /// <summary>What a field or label draws its text with, as <see cref="SliderField"/> builds a <see cref="FormattedText"/>.</summary>
    /// <summary>A wheel over a row steps that row, as the field's tooltip promises, instead of scrolling the list.</summary>
    [AvaloniaFact]
    public void The_wheel_over_a_row_steps_it_and_leaves_the_list_alone()
    {
        var (window, _) = NewWindowWithPhoto();
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<CameraRawPanel>().Single();
        var exposure = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        exposure.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var scroller = exposure.GetVisualAncestors().OfType<ScrollViewer>().First();
        var offset = scroller.Offset;
        var before = exposure.Value;
        var at = exposure.TranslatePoint(new Point(exposure.Bounds.Width / 2, exposure.Bounds.Height / 2), window);
        Assert.NotNull(at);

        window.MouseWheel(at!.Value, new Vector(0, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + exposure.Step, exposure.Value);
        Assert.Equal(offset, scroller.Offset);
    }

    /// <summary>A double-click on a compact row's label resets it, as a developing panel does; the value still types.</summary>
    [AvaloniaFact]
    public void Double_clicking_a_rows_label_resets_it()
    {
        var (window, _) = NewWindowWithPhoto();
        Click(window, "Camera Raw");
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<CameraRawPanel>().Single();
        var exposure = panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Exposure");
        exposure.BringIntoView();
        exposure.Value = 0.4;
        Dispatcher.UIThread.RunJobs();
        var at = exposure.TranslatePoint(new Point(18, exposure.Bounds.Height / 2), window);
        Assert.NotNull(at);

        var label = at!.Value;
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        window.MouseDown(label, MouseButton.Left);
        window.MouseUp(label, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, exposure.Value);
        Assert.False(exposure.IsEditing);
    }

    private static (double Width, double Height) Metrics(Control control)
    {
        var text = new FormattedText("Contrast", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(control)), TextElement.GetFontSize(control), Brushes.White);
        return (Math.Round(text.Width, 2), Math.Round(text.Height, 2));
    }

    private static (MainWindow Window, EditorSession Session) NewWindowWithPhoto()
    {
        var window = new MainWindow { Width = 1280, Height = 1000 };
        window.Show();
        var session = EditorSession.NewCanvas(200, 120, SKColors.White);
        window.AddSession(session);
        var photo = Rendering.Pixels.NewColor(160, 100);
        photo.Erase(new SKColor(128, 110, 100));
        session.AddImageLayer("Photo", photo);
        Dispatcher.UIThread.RunJobs();
        return (window, session);
    }

    /// <summary>Clicks a menu bar item by its header, as the other window tests do.</summary>
    private static void Click(MainWindow window, string header) =>
        window.GetLogicalDescendants().OfType<Menu>().First().GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => m.Header as string == header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}
