# Selections

A selection limits what the next edit touches: a fill, a filter, an adjustment, a brush stroke, a gradient, Clear, Cut and Copy all stay inside it. Marching ants show its outline.

## Making a selection

- **Marquee**: a rectangle or an ellipse.
- **Lasso**: a freehand or a polygonal outline.
- **Magic Wand**: the connected area of similar color, with a tolerance.
- **Object**: the object under the click, traced against the plain backdrop.
- **Select > All** (Ctrl+A): the whole canvas.
- **Select > Subject** (Ctrl+Alt+A): everything that is not the plain backdrop connected to the picture's edges.
- **Select > Layer's Pixels** and **Layer's Mask**: the shape of the active layer's pixels, or of its mask. Ctrl-click a mask thumbnail in the Layers panel for the same, and the layer's context menu has Select Pixels and Select Mask.

Each tool is described in [Tools](tools.md).

## Combining

With any selection tool, Shift adds to the selection, Alt subtracts from it, and Shift and Alt together keep only the overlap. Without a modifier a new selection replaces the old one.

## Changing a selection

- **Select > Deselect** (Ctrl+D) drops it. **Select > Inverse** (Ctrl+Shift+I) selects what was not selected; with nothing selected it selects everything.
- **Select > Expand** and **Contract** grow or shrink the selection by 1 to 500 pixels. **Select > Feather** (Shift+F6) softens its edge by 1 to 250 pixels. The selection tools' options bar has the same three as buttons with a number beside them.
- The **Feather** slider in the Marquee and Lasso options bar softens new selections as you make them, from 0 to 100 pixels.
- Drag inside the selection with a selection tool to move its outline (Shift pressed during the drag keeps it on one axis, and its edges snap to the View > Snap To targets unless Ctrl is held), or use the arrow keys (ten pixels with Shift).
- To move the selected pixels rather than the outline, drag inside the selection with the Move tool, or Ctrl-drag with the Marquee. Alt (or Ctrl and Alt with the Marquee) moves a copy.

## Using a selection

- **Edit > Clear** (Delete) erases the selected pixels; on a mask it paints them black.
- **Edit > Cut**, **Copy** and **Copy Merged** take the selected pixels from the layer, or from the whole picture with Copy Merged. Paste puts them on a new layer where they came from if that fits, otherwise centered.
- **Layer > Layer via Copy** (Ctrl+J) copies the selected pixels to a new layer.
- **Edit > Content-Aware Fill** (Shift+Backspace) fills the selection from its surroundings, on a pixel layer, for selections up to about 16 megapixels.
- A new adjustment layer takes the selection as its mask, and Layer > Add Layer Mask uses it too, so an adjustment applies only inside the selection.
