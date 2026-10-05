using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The prompt that asks about unsaved work when a window closes.</summary>
public class UnsavedChangesTests
{
    /// <summary>
    /// A window that is always on top covers an ordinary prompt while it holds the keyboard and is disabled itself, so
    /// the prompt has to match it. A window made topmost outside the toolkit is not something the toolkit can see.
    /// </summary>
    [AvaloniaFact]
    public async Task The_prompt_matches_an_always_on_top_window()
    {
        var window = new MainWindow { Width = 900, Height = 700, Topmost = true };
        window.Show();
        var session = EditorSession.NewCanvas(60, 40, SKColors.White);
        window.AddSession(session);
        session.AddImageLayer("Photo", Rendering.Pixels.NewColor(20, 20));
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.IsModified);

        window.Close();
        await Pump(() => window.OwnedWindows.Count > 0);

        var prompt = Assert.Single(window.OwnedWindows);
        Assert.Equal("Unsaved Changes", prompt.Title);
        Assert.True(prompt.Topmost);
        prompt.Close();                                             // Cancel: the window stays open.
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.True(window.IsVisible);
    }

    /// <summary>The prompt has Enter and Escape wired, so it can still be answered when it cannot be clicked.</summary>
    [AvaloniaFact]
    public async Task An_ordinary_window_asks_with_a_prompt_that_can_be_cancelled()
    {
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        var session = EditorSession.NewCanvas(60, 40, SKColors.White);
        window.AddSession(session);
        session.AddImageLayer("Photo", Rendering.Pixels.NewColor(20, 20));
        Dispatcher.UIThread.RunJobs();

        window.Close();
        await Pump(() => window.OwnedWindows.Count > 0);

        var prompt = Assert.Single(window.OwnedWindows);
        Assert.Equal("Unsaved Changes", prompt.Title);
        Assert.False(prompt.Topmost);
        prompt.Close();
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.True(window.IsVisible);
        Assert.True(session.IsModified);                            // Cancelled: nothing was written or thrown away.
    }

    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }
}
