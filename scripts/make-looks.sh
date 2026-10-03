#!/usr/bin/env bash
# Regenerates src/Composa.Core/Looks/*.cube, the film looks bundled with the Color Lookup adjustment.
#
# The sources are the HaldCLUT TIFFs of sguyader/FilmSim (https://github.com/sguyader/FilmSim), dedicated
# to the public domain under CC0 1.0. Each is a 144-point lookup table stored as a 1728 by 1728 8-bit RGB
# image, red-fastest, so grid point (r, g, b) sits at byte offset (r + g * 144 + b * 144 * 144) * 3. Each is
# resampled here to a 33-point .cube by trilinear interpolation, the size grading software exchanges, which
# takes 970 KB of text instead of 9 MB of TIFF. The output is committed so no build needs the sources or a
# network; run this only to add a look or to change the resampling. Requires curl, ImageMagick and Python 3.
#
# The looks ship under descriptive names: the source file names are Fujifilm's film stock names, which are
# trademarks, so they appear only in the provenance comment of each file and in the third-party notices.
# The commit is pinned and every source is checked against its SHA-256 before it is used, so the files can
# be regenerated and verified by anyone. An optional argument names a FilmSim checkout to read instead of
# downloading.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=src/Composa.Core/Looks
COMMIT=1453b2b55c48d99a889b1e455f91f6898ba2db41
SRC="${1:-}"
mkdir -p "$OUT"

# Source file, its SHA-256 at the pinned commit, the shipped file name and the title the dialog shows.
LOOKS=(
  "Acros            d4c9f720c1588531b7f748e13505e11f392af663415e625bda0ff3455b41c7a5  fine-mono       Fine Mono"
  "Classic_Chrome   cc303723b76205aedf970dc814dbecd6d9795955411aa2d6b265873864de7a66  muted-chrome    Muted Chrome"
  "Provia_Std       acf26aee152dbce9fff685aa727a5550855d425aa90610dd63b3ff2f15be0fe7  standard-slide  Standard Slide"
  "Velvia_Vivid_v2  1b347b4c5afec78ad7f79103bf04b319e182b7133ffe2a8864c732a7003b7eea  vivid-slide     Vivid Slide"
)

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

for look in "${LOOKS[@]}"; do
  read -r source sha id title <<<"$look"
  tif="$WORK/$source.tif"
  if [[ -n "$SRC" ]]; then cp "$SRC/$source.tif" "$tif"
  else curl -sSfL "https://raw.githubusercontent.com/sguyader/FilmSim/$COMMIT/$source.tif" -o "$tif"
  fi
  echo "$sha  $tif" | sha256sum -c --quiet
  magick "$tif" -depth 8 "rgb:$WORK/$source.rgb"
  python3 - "$WORK/$source.rgb" "$OUT/$id.cube" "$title" "$source" "$COMMIT" <<'PY'
import sys
raw, out, title, source, commit = open(sys.argv[1], "rb").read(), sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
EDGE, N = 144, 33
assert len(raw) == EDGE ** 3 * 3, f"{sys.argv[1]} is not a 144-point Hald image"

def node(r, g, b, c):
    return raw[(r + g * EDGE + b * EDGE * EDGE) * 3 + c] / 255

def sample(r, g, b):
    x, y, z = r * (EDGE - 1), g * (EDGE - 1), b * (EDGE - 1)
    x0, y0, z0 = min(int(x), EDGE - 2), min(int(y), EDGE - 2), min(int(z), EDGE - 2)
    fx, fy, fz = x - x0, y - y0, z - z0
    out = []
    for c in range(3):
        c00 = node(x0, y0, z0, c) * (1 - fx) + node(x0 + 1, y0, z0, c) * fx
        c10 = node(x0, y0 + 1, z0, c) * (1 - fx) + node(x0 + 1, y0 + 1, z0, c) * fx
        c01 = node(x0, y0, z0 + 1, c) * (1 - fx) + node(x0 + 1, y0, z0 + 1, c) * fx
        c11 = node(x0, y0 + 1, z0 + 1, c) * (1 - fx) + node(x0 + 1, y0 + 1, z0 + 1, c) * fx
        out.append((c00 * (1 - fy) + c10 * fy) * (1 - fz) + (c01 * (1 - fy) + c11 * fy) * fz)
    return out

lines = [
    f'TITLE "{title}"',
    "# A film look bundled with Composa's Color Lookup adjustment.",
    f'# Derived from sguyader/FilmSim "{source}" at {commit[:7]} (CC0 1.0, https://github.com/sguyader/FilmSim),',
    "# resampled from the 144-point HaldCLUT to 33 points by scripts/make-looks.sh. Public domain; see THIRD-PARTY-NOTICES.txt.",
    f"LUT_3D_SIZE {N}",
    "DOMAIN_MIN 0.0 0.0 0.0",
    "DOMAIN_MAX 1.0 1.0 1.0",
]
for b in range(N):
    for g in range(N):
        for r in range(N):
            lines.append(" ".join(f"{min(1, max(0, v)):.6f}" for v in sample(r / (N - 1), g / (N - 1), b / (N - 1))))
open(out, "w", newline="\n").write("\n".join(lines) + "\n")
print(f"wrote {out} from {source}.tif")
PY
done
