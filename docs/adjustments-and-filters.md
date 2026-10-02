# Adjustments and filters

An **adjustment** changes colors and tones. Applied from the Image menu it changes the active layer's pixels for good; added from Layer > New Adjustment Layer it becomes an adjustment layer that changes the look of every layer below it, keeps its settings editable (double-click it in the Layers panel, or Layer > Edit Adjustment), and takes the current selection as its mask. A **filter** (the Filter menu) always changes the pixels of the active layer or of its mask, within the selection when there is one.

Every dialog previews on the canvas as you drag, with a Preview checkbox to compare. Sliders are dragged; hold Alt for finer steps, double-click to type a value, and a Reset button then puts the slider back to the value that changes nothing, or to a filter's default.

## Adjustments

- **Curves** (Ctrl+M): a curve over a histogram, for all channels or for red, green or blue. Click to add a point, drag it, and drag it off the graph to remove it; up to sixteen points.
- **Levels** (Ctrl+L): input black and white points, a midtone gamma and output black and white, per channel, over a histogram, with an Auto button. Image > Auto Levels (Ctrl+Shift+L) applies the automatic stretch directly.
- **Hue/Saturation** (Ctrl+U): hue, saturation and lightness for all colors or for one range (reds, yellows, greens, cyans, blues, magentas). Colorize tints the whole layer with one hue.
- **Brightness/Contrast**, each from -100 to 100.
- **Exposure**: exposure in stops, an offset for the shadows and a gamma.
- **Black & White**: how light each color range comes out, from -200 to 300, and an optional tint with a hue and a saturation for a sepia or a cyanotype.
- **Color Balance**: cyan to red, magenta to green and yellow to blue for the shadows, midtones and highlights separately, with Preserve Luminosity keeping each pixel's brightness.
- **Gradient Map**: maps the tones onto a gradient from a shadows color to a highlights color, with buttons for the current foreground and background colors and a Reverse option.
- **Color Lookup**: grades the picture through a 3D lookup table, the `.cube` and `.3dl` files that DaVinci Resolve, Lightroom and purchased look packs exchange. Four film looks come bundled (Fine Mono, Muted Chrome, Standard Slide and Vivid Slide); each is drawn on the picture you are editing as a small tile, so you see what it does before you choose it, and Load File… adds a table of your own as a tile beside them. Amount mixes the look into the original. As an adjustment layer it is named after the look, the table travels inside the project file, and the layer's opacity does what Amount does. A table expects ordinary sRGB pixels; one made for log footage looks wrong here, as it does everywhere else. File > Export Look as .cube writes the document's own adjustments as a table; see [Files](files.md#exporting).
- **Grain**: film grain with an amount, a size and a roughness.
- **Invert** (Ctrl+I): inverts the colors.
- **Gaussian Blur**, **Motion Blur** and **Add Noise** exist as adjustment layers, so a blur or a grain can sit above a stack and be turned off later; as direct edits they are in the Filter menu.

The Hue/Saturation, Black & White and Color Balance sliders show their colors on the track, so you see what a slider does before you drag it.

## Remove Background

Image > Remove Background hides everything around the layer's subject. Detect picks how the subject is found, the same choice as the Object Selection tool's: with Any subject or Person a model run on your machine finds it and the layer gets a layer mask hiding the rest, so a wrong edge can be painted back on the mask; with Plain backdrop the near-uniform backdrop connected to the layer's edges is erased, and Tolerance says how different a pixel may be from it and still go. A layer that already has a mask keeps what it hid. Editing a mask, only the plain backdrop applies.

## Filters

- **Gaussian Blur**: a radius in pixels. A layer that fills the canvas keeps its edge colors; a floating layer's blur spreads past its edges and the layer grows to hold it.
- **Motion Blur**: a distance and an angle, with a dial that turns the full circle.
- **Add Noise**: an amount, an even or a bell-shaped distribution, gray or colored.
- **Sharpen**: an amount and a radius.
- **Vignette**: blends a color into the edges while keeping the center. Amount, Midpoint (where the falloff starts), Roundness (from following the frame to a circle), Feather and Highlights, which spares bright pixels near the edge. On an empty layer it paints across the whole canvas, so a vignette can live on its own layer above a photo.
- **Bloom / Glow**: makes the bright parts glow, with an amount and a radius.
- **Dither**: described below.
- **Tonal Contrast**: local contrast, with an amount, a radius and how much the shadows, midtones and highlights each get.
- **Lens Correction**: removes barrel distortion (positive) or pincushion distortion (negative).
- **Camera Raw Filter**: a full grading panel, described in [Camera Raw Filter](camera-raw.md).
- **Painterly**: described below.

## Dither

Dither turns a layer into dithered pixels, the way old screens and printers drew tones with only two colors. The Style menu offers four kinds: error diffusion (Atkinson, the classic Mac look that passes on only part of each pixel's error and stays crisp, and Floyd-Steinberg), ordered Bayer grids of 2, 4 or 8 pixels, halftone screens (dots, lines or diamonds, with a Cell Size and an Angle dial) and marks (the old Mac fill patterns, and ASCII drawn as readable characters laid out like lines of text, with a Text Size and the characters to use).

Pixel Size makes chunky pixels: the layer is averaged down by that much, dithered and blown back up, and Pixel Shape draws each one as a solid square or a round dot on the dark color, like an LED screen. Diffusion and Bayer styles have Tones (2 is pure 1-bit), the diffusion styles a Diffusion amount, and every style Density (more or less ink before dithering) and Contrast. Colors picks black and white, two colors chosen from swatches that preview on the layer, or the picture's own colors, dithered channel by channel. For halftone, patterns and ASCII, Light on Dark draws the marks for the light tones in the light color on the dark, like a glowing screen; off, the marks are the dark tones on the light color, like ink on paper.

## Painterly

Painterly repaints a layer in brush strokes that follow the picture, so a photo becomes a painting that is still recognizably the same photo. The largest brush paints first. Each smaller brush then repaints only the places where the canvas still differs from the picture, so flat areas stay loose while eyes, mouths and edges are painted finely. Every stroke takes its color from the picture and runs along the edge it started on.

- **Style**: Impressionist paints faithfully; Expressionist uses long strokes that bend with the picture and lets colors drift; Colorist Wash lays thin, overlapping washes; Pointillist paints dots.
- **Brush Size**: the diameter of the largest brush, from 0 to 200 pixels. At 0 the brush fits itself to the picture, a fiftieth of its shorter side.
- **Passes**: how many brushes are used, from 1 to 4, each half the size of the last.
- **Detail**: from 0 to 100, how closely the strokes follow the picture. Higher paints more of it again with the smaller brushes.

Gaps between strokes stay transparent, so a layer filled with a paper color below the painting gives a painting on paper. On a large photo the Expressionist style's smallest brush can get very thin and the result looks hairy; a larger brush size or fewer passes fixes that. The strokes are laid down within a few seconds on a photo of a few megapixels.
