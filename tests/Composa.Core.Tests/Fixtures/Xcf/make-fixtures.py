# Builds the GIMP sample files in this folder with GIMP 3 itself, so the reader is checked against what GIMP writes
# rather than only against the test project's own writer. Run by hand, from the repository root:
#
#   flatpak run --command=gimp-console-3.2 org.gimp.GIMP --batch-interpreter=python-fu-eval --quit \
#     -b "exec(open('tests/Composa.Core.Tests/Fixtures/Xcf/make-fixtures.py').read())"
#
# The files are committed; XcfTests reads each and checks the layers it should produce. Every file is small.
import os, sys
import gi
gi.require_version('Gimp', '3.0')
gi.require_version('Gegl', '0.4')
from gi.repository import Gimp, Gegl, Gio, GLib

OUT = os.path.join(os.getcwd(), 'tests', 'Composa.Core.Tests', 'Fixtures', 'Xcf')
os.makedirs(OUT, exist_ok=True)

def color(r, g, b, a=1.0):
    c = Gegl.Color.new('black')
    c.set_rgba(r, g, b, a)
    return c

def fill(layer, r, g, b, a=1.0):
    Gimp.context_set_foreground(color(r, g, b, a))
    layer.edit_fill(Gimp.FillType.FOREGROUND)

def rgba_layer(image, name, w, h, x, y, r, g, b, a=1.0, mode=Gimp.LayerMode.NORMAL, opacity=100.0):
    layer = Gimp.Layer.new(image, name, w, h, Gimp.ImageType.RGBA_IMAGE, opacity, mode)
    image.insert_layer(layer, None, 0)
    layer.set_offsets(x, y)
    layer.fill(Gimp.FillType.TRANSPARENT)
    fill(layer, r, g, b, a)
    return layer

def save(image, name):
    path = os.path.join(OUT, name)
    Gimp.file_save(Gimp.RunMode.NONINTERACTIVE, image, Gio.File.new_for_path(path), None)
    image.delete()
    print('wrote', name, os.path.getsize(path), 'bytes')

def basic(precision, name, base=Gimp.ImageBaseType.RGB):
    image = Gimp.Image.new_with_precision(64, 48, base, precision)
    image.set_resolution(144.0, 144.0)
    rgba_layer(image, 'Background', 64, 48, 0, 0, 0.95, 0.90, 0.80)
    rgba_layer(image, 'Red square', 20, 20, 10, 8, 0.85, 0.20, 0.15)
    half = rgba_layer(image, 'Half blue', 24, 16, 30, 20, 0.10, 0.30, 0.90, 1.0, Gimp.LayerMode.MULTIPLY, 60.0)
    half.set_visible(False)
    save(image, name)

try:
    basic(Gimp.Precision.U8_NON_LINEAR, 'basic-8bit.xcf')
    basic(Gimp.Precision.U16_NON_LINEAR, 'deep-16bit.xcf')
    basic(Gimp.Precision.FLOAT_LINEAR, 'deep-float-linear.xcf')
    basic(Gimp.Precision.U8_NON_LINEAR, 'gray.xcf', Gimp.ImageBaseType.GRAY)
except Exception as e:
    print('basic failed:', e)

# Indexed: converted after drawing.
try:
    image = Gimp.Image.new(32, 32, Gimp.ImageBaseType.RGB)
    rgba_layer(image, 'Background', 32, 32, 0, 0, 0.2, 0.6, 0.3)
    rgba_layer(image, 'Dot', 10, 10, 11, 11, 0.9, 0.9, 0.1)
    image.convert_indexed(Gimp.ConvertDitherType.NONE, Gimp.ConvertPaletteType.GENERATE, 8, False, False, '')
    save(image, 'indexed.xcf')
except Exception as e:
    print('indexed failed:', e)

# Groups, masks, guides, modes, a text layer and a path.
try:
    image = Gimp.Image.new(96, 64, Gimp.ImageBaseType.RGB)
    rgba_layer(image, 'Background', 96, 64, 0, 0, 1.0, 1.0, 1.0)
    group = Gimp.GroupLayer.new(image, 'Group')
    image.insert_layer(group, None, 0)
    inner = Gimp.Layer.new(image, 'Inside', 40, 30, Gimp.ImageType.RGBA_IMAGE, 100.0, Gimp.LayerMode.SCREEN)
    image.insert_layer(inner, group, 0)
    inner.set_offsets(20, 10)
    inner.fill(Gimp.FillType.TRANSPARENT)
    fill(inner, 0.2, 0.4, 0.8)
    nested = Gimp.GroupLayer.new(image, 'Nested')
    image.insert_layer(nested, group, 0)
    deep = Gimp.Layer.new(image, 'Deep', 20, 20, Gimp.ImageType.RGBA_IMAGE, 50.0, Gimp.LayerMode.NORMAL)
    image.insert_layer(deep, nested, 0)
    deep.set_offsets(60, 30)
    deep.fill(Gimp.FillType.TRANSPARENT)
    fill(deep, 0.9, 0.5, 0.1)
    masked = rgba_layer(image, 'Masked', 30, 30, 5, 30, 0.7, 0.1, 0.7)
    mask = masked.create_mask(Gimp.AddMaskType.WHITE)
    masked.add_mask(mask)
    Gimp.context_set_foreground(color(0, 0, 0))
    image.select_rectangle(Gimp.ChannelOps.REPLACE, 5, 30, 15, 30)
    mask.edit_fill(Gimp.FillType.FOREGROUND)
    Gimp.Selection.none(image)
    grain = rgba_layer(image, 'Grain', 96, 64, 0, 0, 0.5, 0.5, 0.5, 1.0, Gimp.LayerMode.GRAIN_MERGE, 100.0)
    group.set_expanded(False)
    image.add_hguide(16)
    image.add_vguide(48)
    text = Gimp.TextLayer.new(image, 'Hello GIMP', Gimp.Font.get_by_name('Sans-serif Bold'), 18.0, Gimp.Unit.pixel())
    image.insert_layer(text, None, 0)
    text.set_offsets(4, 40)
    text.set_color(color(0.1, 0.1, 0.5))
    path = Gimp.Path.new(image, 'Outline')
    image.insert_path(path, None, 0)
    path.stroke_new_from_points(Gimp.PathStrokeType.BEZIER, [10.0, 10.0, 10.0, 10.0, 10.0, 10.0, 80.0, 50.0, 80.0, 50.0, 80.0, 50.0], False)
    save(image, 'groups-masks-text.xcf')
except Exception as e:
    print('groups failed:', e)

# A layer with a non-destructive effect, to see whether the stored pixels carry it.
try:
    image = Gimp.Image.new(64, 64, Gimp.ImageBaseType.RGB)
    rgba_layer(image, 'Background', 64, 64, 0, 0, 1.0, 1.0, 1.0)
    sharp = rgba_layer(image, 'Sharp square', 24, 24, 20, 20, 0.0, 0.0, 0.0)
    blur = Gimp.DrawableFilter.new(sharp, 'gegl:gaussian-blur', 'Blur')
    config = blur.get_config()
    config.set_property('std-dev-x', 6.0)
    config.set_property('std-dev-y', 6.0)
    blur.update()
    sharp.append_filter(blur)
    save(image, 'effect.xcf')
except Exception as e:
    print('effect failed:', e)

# GIMP 3.2: a vector layer and a link layer, where the API allows.
try:
    image = Gimp.Image.new(48, 48, Gimp.ImageBaseType.RGB)
    rgba_layer(image, 'Background', 48, 48, 0, 0, 1.0, 1.0, 1.0)
    path = Gimp.Path.new(image, 'Shape')
    image.insert_path(path, None, 0)
    path.stroke_new_from_points(Gimp.PathStrokeType.BEZIER, [8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 40.0, 8.0, 40.0, 8.0, 40.0, 8.0, 40.0, 40.0, 40.0, 40.0, 40.0, 40.0], True)
    vector = Gimp.VectorLayer.new(image, path)
    image.insert_layer(vector, None, 0)
    save(image, 'vector-layer.xcf')
except Exception as e:
    print('vector layer failed:', e)

try:
    image = Gimp.Image.new(48, 48, Gimp.ImageBaseType.RGB)
    rgba_layer(image, 'Background', 48, 48, 0, 0, 1.0, 1.0, 1.0)
    png = os.path.join(OUT, 'linked.png')
    small = Gimp.Image.new(16, 16, Gimp.ImageBaseType.RGB)
    rgba_layer(small, 'L', 16, 16, 0, 0, 0.3, 0.8, 0.3)
    Gimp.file_save(Gimp.RunMode.NONINTERACTIVE, small, Gio.File.new_for_path(png), None)
    small.delete()
    layer = Gimp.LinkLayer.new_from_file(image, Gio.File.new_for_path(png)) if hasattr(Gimp.LinkLayer, 'new_from_file') else Gimp.LinkLayer.new(image, Gio.File.new_for_path(png))
    image.insert_layer(layer, None, 0)
    save(image, 'link-layer.xcf')
    os.remove(png)
except Exception as e:
    print('link layer failed:', e)

print('done')
