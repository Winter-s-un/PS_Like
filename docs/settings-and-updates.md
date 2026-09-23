# Settings and updates

## What Composa remembers

Between launches, Composa keeps: the window size and whether it was maximized, the recent files, the JPEG export quality, the view options (rulers, grid, guides, snapping and what to snap to, locked guides, the pixel grid and the transform controls), Auto Select, whether the History panel is shown or collapsed and how tall it is, your changed keyboard shortcuts, whether AI control is allowed, and the update check settings. Tool settings and colors carry from tab to tab within a session but start fresh at the next launch.

## Where files live

| | Linux | Windows |
|---|---|---|
| Settings | `~/.config/composa` | `%APPDATA%\Composa` |
| Recovery copies | `~/.cache/composa/recovery` | `%LOCALAPPDATA%\Composa\recovery` |

On Linux the XDG environment variables are honoured if you set them. The pipe an AI agent connects through lives in the same cache folder; see [AI control](ai-control.md).

## Updates

Help > Check for Updates asks the releases page whether a newer version exists and tells you the result. It never downloads or installs anything. With Help > Check for Updates Automatically on, Composa asks once a day at launch and shows a strip under the menu when a newer version is available, with a link to its release notes, Skip this version, and a dismiss button. A check that fails says nothing. A stable version is never offered a pre-release.

The `.deb` and `.rpm` check too: they are downloaded from the releases page and installed by hand, so no repository will offer you the next version. To upgrade, install the new release's file over the old one, with `sudo apt install ./composa_*.deb` or `sudo dnf install ./composa-*.rpm`. To stop the automatic check on any build, set the environment variable `COMPOSA_DISABLE_UPDATE_CHECK` to 1.

## If something goes wrong

An unexpected error shows a "Something went wrong" dialog with the error and advice to save a copy with File > Save As. The document stays open. A crash leaves recovery copies, which the next launch offers to restore.
