#!/usr/bin/env bash
# Builds every Linux package for one architecture.
#
#   usage: all.sh [rid] [output-dir]      default: linux-x64, dist/
#
# The tarball is published on its own because it is the one single-file build. Everything else is
# packed from one staged tree, so a layout mistake shows up in all three at once rather than in
# whichever format happened to be tested.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/common.sh"

RID="${1:-linux-x64}"
OUT="$(ensure_dir "${2:-$BUILD}")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# Resolve the version once and hand it to every format, so they cannot disagree and so the restore
# MinVer needs happens a single time.
COMPOSA_VERSION="$(app_version)"
export COMPOSA_VERSION
echo "Building Composa $COMPOSA_VERSION for $RID"

echo "==> tarball"
"$HERE/tarball.sh" "$RID" "$OUT"

echo "==> staging for the package formats"
# The .deb and .rpm are downloaded from the releases page and installed by hand, so no repository
# will ever offer their users an update: they keep the github channel, like every other download,
# and upgrade by installing the next release's file over the old one. A build that really does come
# from a repository is packaged with UPDATE_CHANNEL=managed, which tells the user to look there instead.
UPDATE_CHANNEL="${UPDATE_CHANNEL:-github}" "$HERE/stage.sh" "$RID" "$STAGE"

echo "==> deb"
"$HERE/deb.sh" "$RID" "$STAGE" "$OUT"

echo "==> rpm"
"$HERE/rpm.sh" "$RID" "$STAGE" "$OUT"

# The AppImage is the download for anyone whose distribution is neither Debian nor Fedora shaped. It
# is built from the same tree; nothing ever manages it, so it is always on the github channel.
echo "==> AppImage"
if [ "${UPDATE_CHANNEL:-github}" != "github" ]; then
  UPDATE_CHANNEL=github "$HERE/stage.sh" "$RID" "$STAGE" >/dev/null
fi
"$HERE/appimage.sh" "$RID" "$STAGE" "$OUT"

echo
echo "Built for $RID:"
ls -1sh "$OUT"
