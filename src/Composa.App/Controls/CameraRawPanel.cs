using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.App.Dialogs;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>
/// The Camera Raw Filter's panel: a histogram of the graded layer, a thumbnail that doubles as the white-balance
/// eyedropper, then the groups Light, Color, Color Grading, Effects, Curve, Color Mixer, Detail, Optics and
/// Calibration, each collapsible and switchable off with an eye without clearing its sliders.
/// It is built once and hosted in two places: the dialog (<see cref="Dialogs.CameraRawDialog"/>), which returns the
/// grade when it closes, and the right-hand dock, where it stays open and every change previews on the canvas as it
/// is made. <see cref="Changed"/> carries the grade as rendered, which is what the canvas should show; a hidden group
/// contributes nothing to it.
/// </summary>
public sealed class CameraRawPanel : UserControl
{
    private readonly double labelWidth, fieldWidth, previewSide, headerWidth, histogramWidth, histogramHeight;
    private readonly bool narrow;
    private readonly HistogramView histogram;
    private readonly Image preview = new();
    private readonly TextBlock readout = Ui.Label("R —   G —   B —", Palette.Secondary);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly List<(SliderField Field, Func<CameraRawSettings, double> Get)> fields = [];
    private readonly Dictionary<CameraRawGroup, Button> eyes = [];
    private readonly SliderField[] wheelHue = new SliderField[4];
    private readonly SliderField[] wheelSaturation = new SliderField[4];
    private readonly StackPanel mixerRows = new() { Spacing = 6 };
    private CurveEditor? curveEditor;
    private ComboBox? balance, glowStyle, vignetteStyle, channel, process, mixerTab;
    private TextBlock? processSummary, saved;
    private Button? saveLook;
    /// <summary>The Save Look row, hidden when there is nowhere to save to.</summary>
    private Control? footer;
    private CameraRawSettings current = new();
    private readonly HashSet<CameraRawGroup> hidden = [];
    private SKBitmap? original;
    private Func<SKBitmap?>? graded;
    private Func<Task<string?>>? saveLookPath;
    private string title = "";
    private int mixerTabIndex;
    // Pushing a grade into the controls must not read back as the user's change.
    private bool loading;
    /// <summary>Whether a change has been previewed since the last <see cref="Stop"/>, which is this sitting's first.</summary>
    private bool previewed;

    /// <summary>Raised with the grade as rendered, debounced while a slider is dragged.</summary>
    public event Action<CameraRawSettings>? Changed;

    /// <param name="narrow">True for the dock, whose column is 296 wide and whose list has a scrollbar; false for the dialog.</param>
    public CameraRawPanel(bool narrow) : this(narrow, narrow ? 80 : 96, narrow ? 236 : 300, narrow ? 208 : 240, narrow ? 224 : 288, narrow ? 240 : 300) { }

    private CameraRawPanel(bool narrow, double labelWidth, double fieldWidth, double curveSide, double headerWidth, double previewSide)
    {
        this.narrow = narrow;
        this.labelWidth = labelWidth;
        this.fieldWidth = fieldWidth;
        this.headerWidth = headerWidth;
        this.previewSide = previewSide;
        histogramWidth = previewSide;
        histogramHeight = Math.Round(previewSide * 0.3);
        Focusable = true;

        histogram = new HistogramView { Width = histogramWidth, Height = histogramHeight, HorizontalAlignment = HorizontalAlignment.Left };
        readout.FontSize = 11;

        // The thumbnail: click a pixel that should be neutral and Temperature and Tint follow.
        preview.Cursor = new Cursor(StandardCursorType.Cross);
        preview.Stretch = Stretch.Uniform;
        ToolTip.SetTip(preview, "Click a pixel that should be neutral to set the white balance from it");
        preview.PointerMoved += (_, e) => readout.Text = Sampled(e.GetPosition(preview)) is { } c ? $"R {Math.Round(c.Red * 255)}   G {Math.Round(c.Green * 255)}   B {Math.Round(c.Blue * 255)}" : "R —   G —   B —";
        preview.PointerPressed += (_, e) =>
        {
            if (Sampled(e.GetPosition(preview)) is not { } c) return;
            if (CameraRawSettings.NeutralizeSrgb(c.Red, c.Green, c.Blue) is not { } solved) return;
            User(() =>
            {
                Update(current with { Temperature = Math.Clamp(solved.Temperature, -100, 100), Tint = Math.Clamp(solved.Tint, -100, 100), WhiteBalance = CameraRawWhiteBalance.Custom });
                SetField("Temperature", current.Temperature);
                SetField("Tint", current.Tint);
                if (balance != null) balance.SelectedIndex = 0;
            });
        };
        preview.PointerExited += (_, _) => readout.Text = "R —   G —   B —";

        var groups = new StackPanel { Spacing = 6 };
        Expander Group(CameraRawGroup group, string caption, Control body, bool open = false)
        {
            var eye = new Button { Classes = { "flat" }, Padding = new Thickness(4), Content = Icons.Create(Icons.Eye, 13, Palette.Secondary), IsVisible = current.Adjusts(group) };
            ToolTip.SetTip(eye, "Switch this group off or on without clearing its sliders");
            eye.Click += (_, _) =>
            {
                if (!hidden.Remove(group)) hidden.Add(group);
                eye.Content = Icons.Create(hidden.Contains(group) ? Icons.EyeOff : Icons.Eye, 13, Palette.Secondary);
                Restart();
            };
            eyes[group] = eye;
            // Left-aligned and narrower than the fields, so the group's own eye and the expander's chevron never push
            // the caption off the panel's left edge: a centred header of a fixed width does exactly that when it fits tight.
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = headerWidth, HorizontalAlignment = HorizontalAlignment.Left };
            header.Children.Add(Ui.Label(caption, weight: FontWeight.SemiBold));
            Grid.SetColumn(eye, 1);
            header.Children.Add(eye);
            body.Margin = new Thickness(8, 6, 0, 4);
            var expander = new Expander
            {
                Header = header, Content = body, IsExpanded = open, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
            };
            groups.Children.Add(expander);
            return expander;
        }
        // Reset puts a slider back to what a fresh grade has, read from the defaults with the same getter that reads the value.
        var defaults = new CameraRawSettings();
        SliderField Slider(string label, Func<CameraRawSettings, double> get, double min, double max, Func<CameraRawSettings, double, CameraRawSettings> set, double step = 1, string format = "0", string? tip = null, IReadOnlyList<Color>? track = null)
        {
            var field = Ui.SliderField(label, get(current), min, max, v => User(() => Update(set(current, v))), step, format, fieldWidth, track, get(defaults));
            // The field's own tip explains its gestures; what the slider adjusts goes above that.
            if (tip != null) ToolTip.SetTip(field, Ui.Column(6, Ui.Label(tip), (Control)ToolTip.GetTip(field)!));
            fields.Add((field, get));
            return field;
        }
        Control Column(params Control[] rows) => Ui.Column(6, rows);
        Control Heading(string text) { var label = Ui.Label(text, Palette.Secondary); label.Margin = new Thickness(0, 4, 0, 0); return label; }

        // Light.
        Group(CameraRawGroup.Light, "Light", Column(
            Slider("Exposure", s => s.Exposure, -5, 5, (s, v) => s with { Exposure = v }, 0.05, "0.00", "Brightens or darkens the whole picture, in stops of light"),
            Slider("Contrast", s => s.Contrast, -100, 100, (s, v) => s with { Contrast = v }, tip: "Makes light and dark tones more or less different, mostly around the middle"),
            Slider("Highlights", s => s.Highlights, -100, 100, (s, v) => s with { Highlights = v }),
            Slider("Shadows", s => s.Shadows, -100, 100, (s, v) => s with { Shadows = v }),
            Slider("Whites", s => s.Whites, -100, 100, (s, v) => s with { Whites = v }, tip: "Sets the brightest point"),
            Slider("Blacks", s => s.Blacks, -100, 100, (s, v) => s with { Blacks = v }, tip: "Sets the darkest point")), open: true);

        // Color, with Auto white balance from the layer's average.
        var temperature = Slider("Temperature", s => s.Temperature, -100, 100, (s, v) => s with { Temperature = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from blue to yellow", track: SliderTracks.Temperature);
        var tint = Slider("Tint", s => s.Tint, -100, 100, (s, v) => s with { Tint = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from green to magenta", track: SliderTracks.Tint);
        var balanceLabel = Ui.Label("White Balance");
        balanceLabel.Width = labelWidth;
        balance = Ui.Combo(new[] { "Custom", "Auto" }, current.WhiteBalance == CameraRawWhiteBalance.Auto ? "Auto" : "Custom", c => c, choice => User(() =>
        {
            if (choice != "Auto") { Update(current with { WhiteBalance = CameraRawWhiteBalance.Custom }); return; }
            // Auto balances the average color of the original layer; the sliders show what it chose.
            var solved = original == null ? null : CameraRawPixels.AutoBalance(original);
            Update(current with { WhiteBalance = CameraRawWhiteBalance.Auto, Temperature = Math.Clamp(solved?.Temperature ?? 0, -100, 100), Tint = Math.Clamp(solved?.Tint ?? 0, -100, 100) });
            temperature.Value = current.Temperature;
            tint.Value = current.Tint;
        }), 120);
        ToolTip.SetTip(balance, "Auto balances the average color; Custom follows Temperature and Tint. Click the thumbnail to set them from one pixel.");
        Group(CameraRawGroup.Color, "Color", Column(
            Ui.Row(8, balanceLabel, balance), temperature, tint,
            Slider("Vibrance", s => s.Vibrance, -100, 100, (s, v) => s with { Vibrance = v }, tip: "Strengthens quiet colors more than strong ones, and protects skin tones", track: SliderTracks.Chroma),
            Slider("Saturation", s => s.Saturation, -100, 100, (s, v) => s with { Saturation = v }, track: SliderTracks.Chroma)), open: true);

        // Color Grading, directly under Color and open like it, as upstream shows it: four wheels as sliders, then blending and balance.
        var wheelRows = new List<Control>();
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            wheelRows.Add(Heading(CameraRawGrading.Names[i]));
            // Saturation runs from gray to the wheel's hue and follows the Hue slider as it turns.
            var saturation = Slider("Saturation", s => s.Grading.Wheels[index].Saturation, 0, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Saturation = v }) }, tip: "How strongly the tint takes; 0 leaves this wheel off", track: SliderTracks.Saturation(current.Grading.Wheels[i].Hue));
            var hue = Slider("Hue", s => s.Grading.Wheels[index].Hue, 0, 360, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Hue = v }) }, tip: "Around the color wheel", track: SliderTracks.Spectrum(180));
            hue.Changed += v => saturation.Track = SliderTracks.Saturation(v);
            wheelHue[i] = hue;
            wheelSaturation[i] = saturation;
            wheelRows.Add(hue);
            wheelRows.Add(saturation);
            wheelRows.Add(Slider("Luminance", s => s.Grading.Wheels[index].Luminance, -100, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Luminance = v }) }, track: SliderTracks.Lightness));
        }
        wheelRows.Add(Heading("Overlap"));
        wheelRows.Add(Slider("Blending", s => s.Grading.Blending, 0, 100, (s, v) => s with { Grading = s.Grading with { Blending = v } }, tip: "How much the three tonal wheels overlap"));
        wheelRows.Add(Slider("Balance", s => s.Grading.Balance, -100, 100, (s, v) => s with { Grading = s.Grading with { Balance = v } }, tip: "Negative favors the shadows, positive the highlights"));
        Group(CameraRawGroup.Grading, "Color Grading", Column(wheelRows.ToArray()), open: true);

        // Effects.
        var glowStyles = Enum.GetValues<CameraRawGlowStyle>();
        var vignetteStyles = Enum.GetValues<CameraRawVignetteStyle>();
        static string StyleName(Enum style) => style switch
        {
            CameraRawVignetteStyle.HighlightPriority => "Highlight Priority", CameraRawVignetteStyle.ColorPriority => "Color Priority",
            CameraRawVignetteStyle.PaintOverlay => "Paint Overlay", _ => style.ToString()
        };
        Control StyleRow(string label, Control combo) { var text = Ui.Label(label); text.Width = labelWidth; return Ui.Row(8, text, combo); }
        glowStyle = Ui.Combo(glowStyles, current.GlowStyle, v => StyleName(v), v => User(() => Update(current with { GlowStyle = v })), 140);
        vignetteStyle = Ui.Combo(vignetteStyles, current.VignetteStyle, v => StyleName(v), v => User(() => Update(current with { VignetteStyle = v })), 140);
        Group(CameraRawGroup.Effects, "Effects", Column(
            Slider("Texture", s => s.Texture, -100, 100, (s, v) => s with { Texture = v }, tip: "Adds or softens small detail"),
            Slider("Clarity", s => s.Clarity, -100, 100, (s, v) => s with { Clarity = v }, tip: "Adds or softens contrast along broader shapes"),
            Slider("Dehaze", s => s.Dehaze, -100, 100, (s, v) => s with { Dehaze = v }, tip: "Clears haze when raised, adds it when lowered"),
            Heading("Glow"),
            Slider("Glow", s => s.Glow, 0, 100, (s, v) => s with { Glow = v }, tip: "Spreads a glow from the bright areas"),
            StyleRow("Style", glowStyle),
            Slider("Range", s => s.GlowRange, -100, 100, (s, v) => s with { GlowRange = v }, tip: "How bright an area must be to glow; idle until Glow is raised"),
            Slider("Spread", s => s.GlowSpread, -100, 100, (s, v) => s with { GlowSpread = v }, tip: "How far the glow reaches; idle until Glow is raised"),
            Slider("Warmth", s => s.GlowWarmth, -100, 100, (s, v) => s with { GlowWarmth = v }, tip: "Cool to warm; Halation stays red", track: SliderTracks.Temperature),
            Heading("Vignette"),
            Slider("Amount", s => s.VignetteAmount, -100, 100, (s, v) => s with { VignetteAmount = v }, tip: "Darkens or lightens the edges; the center does not change"),
            StyleRow("Style", vignetteStyle),
            Slider("Midpoint", s => s.VignetteMidpoint, 0, 100, (s, v) => s with { VignetteMidpoint = v }),
            Slider("Roundness", s => s.VignetteRoundness, -100, 100, (s, v) => s with { VignetteRoundness = v }),
            Slider("Feather", s => s.VignetteFeather, 0, 100, (s, v) => s with { VignetteFeather = v }),
            Slider("Highlights", s => s.VignetteHighlights, 0, 100, (s, v) => s with { VignetteHighlights = v }, tip: "Protects bright edges while the vignette darkens (Highlight Priority)"),
            Heading("Grain"),
            Slider("Amount", s => s.GrainAmount, 0, 100, (s, v) => s with { GrainAmount = v }),
            Slider("Size", s => s.GrainSize, 0, 100, (s, v) => s with { GrainSize = v }),
            Slider("Roughness", s => s.GrainRoughness, 0, 100, (s, v) => s with { GrainRoughness = v })));

        // Curve: the parametric sliders and a point curve per channel.
        var editor = new CurveEditor { Width = curveSide, Height = curveSide };
        curveEditor = editor;
        var channels = new[] { "RGB", "Red", "Green", "Blue" };
        editor.Curves = ToEditor(current.Curve);
        editor.Changed += curves => User(() =>
        {
            CurvePoint[] Points(int ch) => curves.Channels[ch].Select(p => new CurvePoint(p.X / 255, p.Y / 255)).ToArray();
            Update(current with { Curve = current.Curve with { Rgb = Points(0), Red = Points(1), Green = Points(2), Blue = Points(3) } });
        });
        channel = Ui.Combo(channels, "RGB", c => c, c => editor.Channel = Array.IndexOf(channels, c), 100);
        var resetCurve = Ui.TextButton("Reset", () => User(() => { Update(current with { Curve = new CameraRawCurve() }); editor.Curves = ToEditor(current.Curve); }));
        resetCurve.MinWidth = 0;
        Group(CameraRawGroup.Curve, "Curve", Column(
            Heading("Parametric"),
            Slider("Highlights", s => s.Curve.Highlights, -100, 100, (s, v) => s with { Curve = s.Curve with { Highlights = v } }),
            Slider("Lights", s => s.Curve.Lights, -100, 100, (s, v) => s with { Curve = s.Curve with { Lights = v } }),
            Slider("Darks", s => s.Curve.Darks, -100, 100, (s, v) => s with { Curve = s.Curve with { Darks = v } }),
            Slider("Shadows", s => s.Curve.Shadows, -100, 100, (s, v) => s with { Curve = s.Curve with { Shadows = v } }),
            Slider("Refine Saturation", s => s.Curve.RefineSaturation, -100, 100, (s, v) => s with { Curve = s.Curve with { RefineSaturation = v } }, tip: "How much the curve also changes saturation"),
            Heading("Point"),
            Ui.Row(8, Ui.Label("Channel", Palette.Secondary), channel, resetCurve),
            editor));

        // Color Mixer: one tab of eight families at a time.
        var mixerTabs = new[] { "Hue", "Saturation", "Luminance" };
        mixerTab = Ui.Combo(mixerTabs, "Hue", t => t, t => User(() => { mixerTabIndex = Array.IndexOf(mixerTabs, t); BuildMixerFromCurrent(); }), 130);
        BuildMixerFromCurrent();
        Group(CameraRawGroup.Mixer, "Color Mixer", Column(
            Ui.Row(8, Ui.Label("Adjust", Palette.Secondary), mixerTab),
            mixerRows));

        // Detail.
        Group(CameraRawGroup.Detail, "Detail", Column(
            Heading("Sharpening"),
            Slider("Amount", s => s.Detail.SharpenAmount, 0, 150, (s, v) => s with { Detail = s.Detail with { SharpenAmount = v } }),
            Slider("Radius", s => s.Detail.SharpenRadius, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenRadius = v } }),
            Slider("Detail", s => s.Detail.SharpenDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenDetail = v } }),
            Slider("Masking", s => s.Detail.SharpenMasking, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenMasking = v } }, tip: "Keeps sharpening to the edges"),
            Heading("Noise Reduction"),
            Slider("Luminance", s => s.Detail.NoiseLuminance, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminance = v } }),
            Slider("Detail", s => s.Detail.NoiseLuminanceDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceDetail = v } }),
            Slider("Contrast", s => s.Detail.NoiseLuminanceContrast, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceContrast = v } }),
            Slider("Color", s => s.Detail.NoiseColor, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColor = v } }),
            Slider("Detail", s => s.Detail.NoiseColorDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorDetail = v } }),
            Slider("Smoothness", s => s.Detail.NoiseColorSmoothness, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorSmoothness = v } })));

        // Optics.
        var o = current.Optics;
        Group(CameraRawGroup.Optics, "Optics", Column(
            Ui.Check("Remove Chromatic Aberration", o.RemoveChromaticAberration, v => User(() => Update(current with { Optics = current.Optics with { RemoveChromaticAberration = v } }))),
            Ui.Check("Enable Lens Profile Corrections", o.EnableLensProfile, v => User(() => Update(current with { Optics = current.Optics with { EnableLensProfile = v } }))),
            Slider("Distortion", s => s.Optics.ProfileDistortion, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileDistortion = v } }, tip: "Profile strength; a rendered layer carries no lens data, so this scales a generic correction"),
            Slider("Vignetting", s => s.Optics.ProfileVignetting, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileVignetting = v } }),
            Heading("Manual"),
            Slider("Distortion", s => s.Optics.Distortion, -100, 100, (s, v) => s with { Optics = s.Optics with { Distortion = v } }, tip: "Positive straightens lines that bow outward, negative lines that bow inward"),
            Heading("Defringe"),
            Slider("Purple Amount", s => s.Optics.PurpleAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { PurpleAmount = v } }),
            Slider("Purple Hue Low", s => s.Optics.PurpleHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueLow = v } }),
            Slider("Purple Hue High", s => s.Optics.PurpleHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueHigh = v } }),
            Slider("Green Amount", s => s.Optics.GreenAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { GreenAmount = v } }),
            Slider("Green Hue Low", s => s.Optics.GreenHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueLow = v } }),
            Slider("Green Hue High", s => s.Optics.GreenHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueHigh = v } }),
            Heading("Vignette"),
            Slider("Amount", s => s.Optics.VignetteAmount, -100, 100, (s, v) => s with { Optics = s.Optics with { VignetteAmount = v } }, tip: "Brightens the corners to counter lens falloff"),
            Slider("Midpoint", s => s.Optics.VignetteMidpoint, 0, 100, (s, v) => s with { Optics = s.Optics with { VignetteMidpoint = v } })));

        // Calibration.
        var c = current.Calibration;
        var processes = Enumerable.Range(1, 6).ToArray();
        processSummary = new TextBlock { Text = CameraRawCalibration.ProcessSummary(c.Process), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = fieldWidth, FontSize = 11 };
        process = Ui.Combo(processes, c.Process, p => $"Version {p}", p => User(() => { Update(current with { Calibration = current.Calibration with { Process = p } }); processSummary!.Text = CameraRawCalibration.ProcessSummary(p); }), 130);
        Group(CameraRawGroup.Calibration, "Calibration", Column(
            StyleRow("Process", process),
            processSummary,
            Slider("Shadow Tint", s => s.Calibration.ShadowTint, -100, 100, (s, v) => s with { Calibration = s.Calibration with { ShadowTint = v } }, track: SliderTracks.Tint, tip: "Green to magenta in the shadows"),
            Heading("Red Primary"),
            Slider("Hue", s => s.Calibration.RedHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedHue = v } }, track: SliderTracks.Hue(0)),
            Slider("Saturation", s => s.Calibration.RedSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedSaturation = v } }, track: SliderTracks.Saturation(0)),
            Heading("Green Primary"),
            Slider("Hue", s => s.Calibration.GreenHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenHue = v } }, track: SliderTracks.Hue(120)),
            Slider("Saturation", s => s.Calibration.GreenSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenSaturation = v } }, track: SliderTracks.Saturation(120)),
            Heading("Blue Primary"),
            Slider("Hue", s => s.Calibration.BlueHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueHue = v } }, track: SliderTracks.Hue(240)),
            Slider("Saturation", s => s.Calibration.BlueSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueSaturation = v } }, track: SliderTracks.Saturation(240))));

        // The dialog sizes to its content, so its list is bounded and scrolls inside that; the dock's row is already a height.
        var scroll = new ScrollViewer { Content = groups, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0), MaxHeight = narrow ? double.PositiveInfinity : 520 };
        var head = Ui.Column(6, histogram, preview, readout);
        // Save Look: the grade's color stages as a .cube any editor can load. What reads neighbours or the position
        // (Effects, Detail, Optics) cannot go into a table and is named in the file and in the note beside the button.
        saved = Ui.Label("", Palette.Secondary, 11);
        saved.VerticalAlignment = VerticalAlignment.Center;
        saved.TextTrimming = TextTrimming.CharacterEllipsis;
        saveLook = Ui.TextButton("Save Look…", () => _ = SaveLook());
        saveLook.IsEnabled = false;
        ToolTip.SetTip(saveLook, "Saves the grade as a .cube lookup table for other editors. Effects, Detail and Optics change pixels by their neighbours or their place and are left out.");
        footer = Ui.Row(8, saveLook, saved);

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(footer);
        body.Children.Add(scroll);
        Content = new Border { Padding = new Thickness(10, 8, 4, 6), Child = body };

        // Every control exists by now: the debounce runs the preview the person's change asked for, then the histogram.
        timer.Tick += (_, _) => Run();
    }

    /// <summary>The grade the panel is showing, as rendered: a group switched off contributes nothing.</summary>
    public CameraRawSettings Rendered() => hidden.Aggregate(current, (settings, group) => settings.Without(group));

    /// <summary>True while the panel is bound to a layer, so the dock knows it can preview.</summary>
    public bool IsBound => original != null;

    /// <summary>
    /// Points the panel at a layer: its pixels the eyedropper and Auto read, a way to read the graded pixels for the
    /// histogram, the grade to open on, and where Save Look writes. <paramref name="previewOnOpen"/> previews the
    /// opening grade at once, which is what the dialog does; the dock opens on the last grade without applying it.
    /// </summary>
    public void Bind(SKBitmap? pixels, Func<SKBitmap?>? gradedPixels, CameraRawSettings grade, Func<Task<string?>>? savePath, string documentTitle, bool previewOnOpen = true)
    {
        timer.Stop();
        original = pixels;
        graded = gradedPixels;
        saveLookPath = savePath;
        title = documentTitle;
        IsEnabled = pixels != null;
        // With nowhere to save to, the row is not there at all.
        if (footer != null) footer.IsVisible = savePath != null;
        if (pixels != null)
        {
            var thumb = Ui.ToAvaloniaBitmap(pixels, (int)previewSide);
            preview.Source = thumb;
            // The thumbnail is its own size, as the dialog shows it: the eyedropper maps a click through these bounds.
            preview.Width = thumb.PixelSize.Width;
            preview.Height = thumb.PixelSize.Height;
            Load(grade);
            if (previewOnOpen) Restart();
        }
        else
        {
            Load(grade);
        }
    }

    /// <summary>Stops the throttle and ends the sitting, so the next change previews at once: a commit that follows cannot lose it.</summary>
    public void Stop()
    {
        timer.Stop();
        previewed = false;
    }

    /// <summary>Previews the grade the panel is showing; the dialog does this as it opens, the dock on the first change.</summary>
    public void PreviewOpeningGrade() => Restart();

    /// <summary>
    /// Runs a change that is still waiting on the debounce, so a commit that follows cannot lose it: a slider dragged
    /// and then another layer selected previews and applies in the same step.
    /// </summary>
    public void Flush()
    {
        if (timer.IsEnabled) Run();
    }

    private void Run()
    {
        timer.Stop();
        Changed?.Invoke(Rendered());
        if (graded?.Invoke() is { } pixels) { histogram.Histogram = Histogram.Of(pixels); histogram.InvalidateVisual(); }
    }

    /// <summary>Leaves the panel with nothing to grade: its sliders stay where they are, greyed, until a layer is selected.</summary>
    public void Clear()
    {
        Stop();
        original = null;
        graded = null;
        IsEnabled = false;
    }

    /// <summary>Shows a grade in every control without previewing it or reading it back as a change.</summary>
    public void Load(CameraRawSettings grade)
    {
        loading = true;
        try
        {
            current = grade.Normalized();
            hidden.Clear();
            foreach (var (field, get) in fields) field.Value = get(current);
            foreach (var (group, eye) in eyes)
            {
                eye.IsVisible = current.Adjusts(group);
                eye.Content = Icons.Create(Icons.Eye, 13, Palette.Secondary);
            }
            for (var i = 0; i < 4; i++) wheelSaturation[i].Track = SliderTracks.Saturation(current.Grading.Wheels[i].Hue);
            if (balance != null) balance.SelectedIndex = current.WhiteBalance == CameraRawWhiteBalance.Auto ? 1 : 0;
            if (glowStyle != null) glowStyle.SelectedIndex = (int)current.GlowStyle;
            if (vignetteStyle != null) vignetteStyle.SelectedIndex = (int)current.VignetteStyle;
            if (process != null) process.SelectedIndex = current.Calibration.Process - 1;
            if (processSummary != null) processSummary.Text = CameraRawCalibration.ProcessSummary(current.Calibration.Process);
            if (curveEditor != null) curveEditor.Curves = ToEditor(current.Curve);
            if (mixerTab != null) { mixerTab.SelectedIndex = 0; mixerTabIndex = 0; BuildMixerFromCurrent(); }
            if (saveLook != null) saveLook.IsEnabled = !LookBake.ColorOnly(Rendered()).IsIdentity;
            if (saved != null) saved.Text = "";
        }
        finally { loading = false; }
    }

    private void BuildMixerFromCurrent()
    {
        mixerRows.Children.Clear();
        for (var family = 0; family < 8; family++)
        {
            var (t, f) = (mixerTabIndex, family);
            var centre = CameraRawMixer.Centers[family];
            var track = t switch { 0 => SliderTracks.Hue(centre), 1 => SliderTracks.Saturation(centre), _ => SliderTracks.Luminance(centre) };
            var field = Ui.SliderField(CameraRawMixer.Names[family], current.Mixer.Get(t, f), -100, 100, v => User(() => Update(current with { Mixer = current.Mixer.With(t, f, v) })), 1, "0", fieldWidth, track, 0);
            mixerRows.Children.Add(field);
        }
    }

    private void SetField(string label, double value)
    {
        foreach (var (field, _) in fields) if (field.Label == label && !ReferenceEquals(field, null)) field.Value = value;
    }

    private void User(Action change)
    {
        if (loading) return;
        change();
    }

    private void Update(CameraRawSettings value)
    {
        if (loading) return;
        current = value;
        foreach (var (group, eye) in eyes) eye.IsVisible = current.Adjusts(group);
        if (saveLook != null) saveLook.IsEnabled = !LookBake.ColorOnly(Rendered()).IsIdentity;
        // The sitting's first change previews at once, so a layer or a tab switched right after it cannot leave the
        // change behind; the changes that follow are throttled, so a drag does not run the filter on every step.
        if (!previewed) { previewed = true; Run(); }
        else Restart();
    }

    private void Restart()
    {
        timer.Stop();
        timer.Start();
    }

    private (double Red, double Green, double Blue)? Sampled(Point at)
    {
        if (original == null || preview.Bounds.Width <= 0 || preview.Bounds.Height <= 0) return null;
        var x = (int)(at.X / preview.Bounds.Width * original.Width);
        var y = (int)(at.Y / preview.Bounds.Height * original.Height);
        return CameraRawPixels.StraightColor(original, x, y);
    }

    private async Task SaveLook()
    {
        var grade = Rendered();
        if (saveLookPath == null || LookBake.ColorOnly(grade).IsIdentity || await saveLookPath() is not { } path) return;
        var leftOut = LookBake.LeftOutOf(grade).Select(GroupName).ToList();
        try
        {
            var comment = leftOut.Count > 0 ? $"Saved from Composa's Camera Raw Filter without {string.Join(", ", leftOut)}, which a table cannot hold" : "Saved from Composa's Camera Raw Filter";
            await Task.Run(() => File.WriteAllText(path, LookBake.Bake(grade, 33, title).ToCube(title, comment)));
            if (saved != null) saved.Text = leftOut.Count > 0 ? $"Saved {Path.GetFileName(path)} without {string.Join(", ", leftOut)}" : $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (TopLevel.GetTopLevel(this) is Window owner) await Prompts.Alert(owner, "Couldn't save the look", error.Message);
        }
    }

    private static string GroupName(CameraRawGroup group) => group switch
    {
        CameraRawGroup.Grading => "Color Grading",
        CameraRawGroup.Mixer => "Color Mixer",
        _ => group.ToString()
    };

    private static CurvesAdjustment ToEditor(CameraRawCurve c)
    {
        var curves = new CurvesAdjustment();
        CurvePoint[][] points = [c.Rgb, c.Red, c.Green, c.Blue];
        for (var i = 0; i < 4; i++) curves = curves.WithChannel(i, points[i].Select(p => new CurvePoint(p.X * 255, p.Y * 255)));
        return curves;
    }
}
