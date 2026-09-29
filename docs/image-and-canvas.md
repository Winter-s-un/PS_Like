# Image and canvas

## Canvas Size

Image > Canvas Size (Ctrl+Alt+C) changes the size of the canvas without scaling anything on it. Enter a new width and height, or tick Relative and enter how much to add or remove, and choose an anchor in the three by three grid to say which edges move. Layer pixels outside the canvas are kept, not cut, so a canvas can be enlarged again later.

## Image Size

Image > Image Size (Ctrl+Alt+I) resamples the whole document to a new width and height, with Constrain proportions on by default, and sets the resolution in pixels per inch, which is what the status bar shows and what the project remembers.

## Trim

Image > Trim crops away edges that are transparent, or that have the color of the top-left or bottom-right pixel, on whichever sides you tick. The Crop tool's options bar has a Trim transparent edges button for the common case.

## Reveal All

Image > Reveal All grows the canvas until every layer shows, effects and hidden layers included. Since a crop keeps the pixels outside the new canvas, this is the way back from one. Guides move with the canvas.

## Duplicate

Image > Duplicate opens a copy of the document in a new tab, named after it with "copy", with a history of its own. It needs saving only when the original did.

## Crop

The Crop tool (C) crops to a box you draw; see [Tools](tools.md).

## Rotating and flipping

Image > Rotate Canvas 90° Clockwise and 90° Counterclockwise turn the whole document; Flip Canvas Horizontal and Vertical mirror it. Guides, masks and the selection follow. The Layer menu has the same for a single layer, plus a 180 degree turn, around the layer's own center.
