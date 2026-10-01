using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Dialogs;

/// <summary>
/// A small window for work that runs off the UI thread and may take a moment: a line saying what is happening, a
/// moving bar and Cancel. It opens only when the work is still running after a short while, so a quick result never
/// flashes a window, and it closes itself when the work ends. The subject models and, later, the upscaler use it.
/// </summary>
public sealed class ProgressWindow : Window
{
    /// <summary>How long the work may take before the window shows.</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(300);

    private bool finished;

    private ProgressWindow(string message, Action cancel)
    {
        Title = "Composa";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var button = Ui.TextButton("Cancel", cancel);
        button.IsCancel = true;
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Width = 300,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new ProgressBar { IsIndeterminate = true, Height = 6 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { button } }
            }
        };
        // Closing the window any other way (Escape, the title bar) cancels too; a window closed by the finished work does not.
        Closing += (_, _) => { if (!finished) cancel(); };
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a token that Cancel trips, showing the window if the work is still running
    /// after <see cref="Delay"/>. Returns the result, or default when it was cancelled. Any other failure is thrown
    /// to the caller once the window has closed.
    /// </summary>
    public static async Task<T?> Run<T>(Window owner, string message, Func<CancellationToken, Task<T>> work)
    {
        using var cancellation = new CancellationTokenSource();
        var task = work(cancellation.Token);
        ProgressWindow? window = null;
        if (await Task.WhenAny(task, Task.Delay(Delay)) != task)
        {
            window = new ProgressWindow(message, cancellation.Cancel);
            _ = window.ShowDialog(owner);
        }
        try { return await task; }
        catch (OperationCanceledException) { return default; }
        finally
        {
            if (window != null) { window.finished = true; window.Close(); }
        }
    }
}
