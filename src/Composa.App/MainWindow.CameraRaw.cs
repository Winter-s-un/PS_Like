using Composa.App.Controls;
using Composa.Editing;
using Composa.Filters;

namespace Composa.App;

/// <summary>
/// Window > Camera Raw: the grading panel (<see cref="Controls.CameraRawPanel"/>) as a section of the right-hand dock.
/// The dialog behind Filter > Camera Raw Filter applies a grade when it closes on OK; the dock opens a preview on the
/// first change and keeps it while the person works, so a slider shows on the canvas as it is dragged and the whole
/// sitting is one undo step when they move on, which <see cref="EditorSession.FinishInteraction"/> does for any edit.
/// </summary>
public sealed partial class MainWindow
{
    private readonly CameraRawPanel cameraRaw = new(narrow: true);
    private readonly DockSection cameraRawSection;
    /// <summary>The session and layer the panel is pointed at, which is not the same as an open preview.</summary>
    private EditorSession? cameraRawTarget;
    private Guid? cameraRawBoundLayer;
    /// <summary>True while the preview the panel's first change opened is still open.</summary>
    private bool cameraRawPreviewOpen;
    private uint cameraRawSeed;
    /// <summary>Set while binding, applying or dropping, because the LayersChanged that follows must not re-enter.</summary>
    private bool cameraRawBusy;
    private bool cameraRawWasVisible;

    /// <summary>The section was shown, hidden, collapsed or resized: showing binds the panel, hiding applies what it holds.</summary>
    private void OnCameraRawSectionChanged()
    {
        if (cameraRawSection.State.Visible == cameraRawWasVisible) return;
        cameraRawWasVisible = cameraRawSection.State.Visible;
        if (cameraRawWasVisible) BindCameraRaw();
        else CommitCameraRaw();
    }

    /// <summary>
    /// Points the panel at the active layer. The grade the panel was holding is applied first, so every layer's sitting
    /// is its own undo step and nothing is lost by selecting another layer or another tab. No preview is opened here:
    /// the first change opens one, which is what keeps an untouched panel from leaving an empty step in the history.
    /// </summary>
    private void BindCameraRaw(bool force = false)
    {
        if (!cameraRawSection.State.Visible || cameraRawBusy) return;
        var target = session;
        var layer = target?.ActiveLayerIdOrNull();
        if (!force && ReferenceEquals(target, cameraRawTarget) && layer == cameraRawBoundLayer) return;
        var wasBusy = cameraRawBusy;
        cameraRawBusy = true;
        try
        {
            CommitCameraRaw();
            cameraRawTarget = target;
            cameraRawBoundLayer = layer;
            if (target is not { } host) { cameraRaw.Clear(); return; }
            // A group or an adjustment layer has no pixels of its own; a mask is graded through the mask button.
            var pixels = host.IsEditingMask ? host.ActiveLayer?.Mask : host.ActiveLayer?.Pixels;
            if (pixels == null) { cameraRaw.Clear(); return; }
            cameraRaw.Bind(pixels,
                () => host.ActiveLayer is { } active ? (host.IsEditingMask ? active.Mask : active.Pixels) : null,
                lastCameraRaw, () => PickLookSavePath(host.Title), host.Title, previewOnOpen: false);
        }
        finally { cameraRawBusy = wasBusy; }
    }

    /// <summary>
    /// Applies the grade the dock is previewing, as one undo step. The panel stays bound and keeps showing it, so the
    /// next slider dragged opens a fresh preview on that layer.
    /// </summary>
    private void CommitCameraRaw()
    {
        if (cameraRawPreviewOpen && cameraRawTarget is { } target)
        {
            // A change still on the throttle is part of the sitting, and the sitting is one step.
            cameraRaw.Flush();
            cameraRawPreviewOpen = false;
            if (!target.IsPreviewing) return;
            var wasBusy = cameraRawBusy;
            cameraRawBusy = true;
            try { target.CommitPreview(); }
            finally { cameraRawBusy = wasBusy; cameraRaw.Stop(); }
            StartFreshSitting();
            return;
        }
        // Nothing reached the canvas yet, or something else committed the preview: the sitting is over.
        cameraRaw.Stop();
        StartFreshSitting();
    }

    /// <summary>
    /// The grade is part of the layer's pixels once a sitting ends, and this filter cannot be re-edited from them, so
    /// the panel shows a fresh grade rather than values that a second application would put on the picture twice.
    /// </summary>
    private void StartFreshSitting()
    {
        lastCameraRaw = new CameraRawSettings();
        cameraRaw.Load(lastCameraRaw);
    }

    /// <summary>Takes back the grade being previewed: what Ctrl+Z means while the person is still grading.</summary>
    private void DropCameraRawPreview()
    {
        cameraRaw.Stop();
        if (!cameraRawPreviewOpen || cameraRawTarget is not { } target) { cameraRawPreviewOpen = false; return; }
        cameraRawPreviewOpen = false;
        if (!target.IsPreviewing) return;
        var wasBusy = cameraRawBusy;
        cameraRawBusy = true;
        try { target.CancelPreview(); }
        finally { cameraRawBusy = wasBusy; }
    }

    /// <summary>A grade the person made in the dock: on the canvas at once, and one step when they move on.</summary>
    private void OnCameraRawGrade(CameraRawSettings grade)
    {
        lastCameraRaw = grade;
        // Not blocked by cameraRawBusy: a change still on the debounce is flushed from inside a commit or a rebind,
        // and opening the preview it needs is exactly what has to happen first.
        if (cameraRawTarget is not { } target) return;
        // Another edit can commit the preview behind this panel's back (a stroke, a crop); a new one is opened then,
        // which is what keeps the panel live rather than silently dead.
        if (!cameraRawPreviewOpen || !target.IsPreviewing)
        {
            if (!target.BeginFilter(FilterKind.CameraRaw)) { ShowProblem("Select a pixel layer or a mask first."); cameraRaw.Clear(); return; }
            cameraRawPreviewOpen = true;
            cameraRawSeed = (uint)Random.Shared.Next();
        }
        Busy(() => target.PreviewFilter(new FilterSettings { Kind = FilterKind.CameraRaw, CameraRaw = grade, Seed = cameraRawSeed }));
    }

    /// <summary>
    /// Something else committed the grade the panel was previewing, so the sitting is over: the pixels hold it now and
    /// this filter cannot be re-edited from them, so the panel starts a fresh grade rather than applying it twice.
    /// </summary>
    private void OnCameraRawHistoryChanged()
    {
        if (!cameraRawPreviewOpen || cameraRawBusy || cameraRawTarget is not { } target || target.IsPreviewing) return;
        cameraRawPreviewOpen = false;
        cameraRaw.Stop();
        StartFreshSitting();
    }

    /// <summary>Another layer was selected: the panel follows it, which applies the sitting it was holding.</summary>
    private void OnCameraRawLayersChanged()
    {
        if (cameraRawBusy) return;
        BindCameraRaw();
    }

    /// <summary>
    /// Undo, redo and a step in the History panel with the dock's panel in mind: a grade still being previewed is taken
    /// back rather than applied, and the panel then shows a fresh grade, because the pixels it was grading changed.
    /// </summary>
    private void CameraRawThen(Action step)
    {
        DropCameraRawPreview();
        step();
        if (!cameraRawSection.State.Visible) return;
        lastCameraRaw = new CameraRawSettings();
        cameraRaw.Load(lastCameraRaw);
        BindCameraRaw(force: true);
    }
}
