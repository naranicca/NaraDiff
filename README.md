# NaraDiff

NaraDiff is a desktop diff and merge tool for Windows. It compares two files, merges two variants of
a file against a common ancestor, and compares two folder trees. Changes are drawn as curved Bezier
ribbons between the editors, so it stays visible which block on the left belongs to which block on
the right even when the two sides have different line counts.

Version 1.0.0 targets Windows 10 22H2 or later and Windows 11 on x64. The release is distributed as
one self-contained `NaraDiff.exe`; the .NET runtime does not need to be installed separately.

## Table of contents

- [Highlights](#highlights)
- [Comparing two files](#comparing-two-files)
- [The connector ribbons](#the-connector-ribbons)
- [Three way merge](#three-way-merge)
- [Comparing two folders](#comparing-two-folders)
- [Comparison options](#comparison-options)
- [Encodings, line endings and binary files](#encodings-line-endings-and-binary-files)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Command line](#command-line)
- [Settings and data](#settings-and-data)
- [Build from source](#build-from-source)
- [Project structure](#project-structure)
- [Current limitations](#current-limitations)
- [License](#license)

## Highlights

- Two file comparison with line level and word or character level differences
- Curved ribbon connectors between the panes, recalculated on every scroll, edit and resize
- Copy a hunk, or every change, in either direction
- Three way merge with automatic merging, conflict detection, six resolution modes and a result pane
- Recursive folder comparison with a dry run before any file is copied or deleted
- Myers, patience and histogram algorithms, selectable per comparison
- Whitespace, tab width, case, blank line, line ending, regular expression and prefix ignore rules
- Option presets, saved with the settings and switchable while a comparison is open
- Encoding detection with manual override, and an explicit prompt before a save converts anything
- Binary files fall back to a hex comparison with a byte level summary
- Overview ruler with change, conflict and viewport markers; click or drag to navigate
- Dark and light themes, a colour blind friendly palette, and a keyboard driven workflow
- Background comparison with cancellation, so large files never block the window

## Comparing two files

Press **Compare files** , pick the two files, and the comparison starts immediately. Each pane has its
own header with the path, the detected encoding, the line ending that will be written on save, and
read-only and modified indicators.

- Added lines are green, removed lines red, changed lines blue, and relocated blocks purple.
- Inside a changed line, only the differing words (or characters) are highlighted.
- The centre gutter carries one ribbon per hunk with the two copy buttons of that hunk.
- The status bar counts the changes; the pane footer shows the current change and caret position.
- Editing either side recomparisons after a short delay, so typing stays responsive.
- If a file changes on disk it is reloaded automatically, or you are asked first when you have edits.
- **Swap** exchanges the two sides.

Both editors are fully editable unless the file is read-only.

## The connector ribbons

Every hunk is drawn as a closed shape between the two editors: a cubic Bezier along the top from the
first line of the left block to the first line of the right block, a straight edge down the right
side, a Bezier back along the bottom, and a straight edge up the left side. The control points sit at
half the gutter width, which keeps the curve horizontal where it meets each editor and gives the
ribbon its S shape. Blocks of different heights therefore connect smoothly, and a block that exists
on one side only becomes a thin wedge that is still visible and clickable.

The geometry is recalculated whenever either editor scrolls, the window is resized, the text is
edited or the theme changes. Clicking a ribbon scrolls both editors to that change; clicking one of
its arrows copies the hunk in that direction.

Synchronised scrolling follows the pane you are working in. That pane leads and the others follow;
the scrolls this causes are marked so they cannot travel back and move the pane under your cursor.
This matters because a position inside a change that exists on one side only has no exact
counterpart, so mapping it back would land on the end of that change rather than where it started.

## Three way merge

**3-way merge** opens four panes: left (mine), base, right (theirs), and the merged result below a
splitter. Base is compared with both sides, and changes that touch the same base lines are grouped.

- A group changed by one side only is merged automatically.
- A group changed identically by both sides is merged automatically as well.
- A group changed differently by both sides is a conflict, painted amber and marked with a warning
triangle in the gutter.

Each conflict can be resolved with the left version, the right version, both in either order, or the
base version, or by editing the result pane directly. The toolbar shows how many conflicts are still
open, navigates between them, and can resolve all of them with one side. Saving asks for confirmation
while conflicts remain open, and writes to the output path shown above the result pane.

## Comparing two folders

**Compare folders** compares two trees recursively and shows every entry with its status: identical,
modified, left only, right only, a file against a folder, or unreadable. Name, size and modification
time are shown for both sides, and the footer summarises the counts.

- Equality can be decided by full content (the default), by text content using the current diff
  options, by size and time, or by size alone.
- Hidden files, case sensitive names, subfolders and glob exclusions (for example `bin/;obj/;*.log`)
  are switches in the toolbar.
- **Only differences** hides identical entries.
- Enter or a double click opens the selected pair in a file comparison tab.

**Synchronise** never touches the disk on its own. It opens a preview that lists every planned copy,
folder creation and deletion with its reason, direction and size. Copying missing files, overwriting
different files and deleting orphans are separate switches; deletions are off by default and need a
second, explicit confirmation before the run button becomes available.

## Comparison options

The **Options** flyout applies every change immediately to all open comparisons.

| Option | Effect |
| --- | --- |
| Algorithm | Myers (minimal), patience (unique line anchors), histogram (rare line anchors, default) |
| Inline highlighting | Off, word level, character level |
| Detect moved blocks | Pairs a deletion with an insertion that has the same content |
| Ignore leading whitespace | Indentation differences are not reported |
| Ignore trailing whitespace | Trailing spaces are not reported |
| Ignore the number of spaces | Runs of whitespace compare equal |
| Ignore all whitespace | Whitespace is removed before comparing |
| Treat tabs like spaces | Tabs are expanded with the configured tab width (1 to 16) |
| Ignore letter case | Case differences are not reported |
| Ignore blank line differences | Blank lines are hidden from the comparison but stay in the editor |
| Ignore line ending differences | CRLF, LF and CR compare equal; turn it off to report them |
| Ignored lines: expressions | One regular expression per line; a matching line is excluded |
| Ignored lines: prefixes | Space separated prefixes, for example `// # --`, to skip comments |

Presets are named sets of these options. 'Exact', 'Ignore whitespace', "Source code and
Ignore comments ship with the application; **Save** stores the current options under a new name.

## Encodings, line endings and binary files

The encoding of a file is detected from its byte order mark, then from a UTF-16 heuristic, then by
validating UTF-8, and finally by falling back to the system ANSI code page. The pane header lets you
re-read the same bytes with another encoding: UTF-8, UTF-8 with BOM, UTF-16 LE and BE, UTF-32 LE,
ANSI, Korean 949, Japanese 932, Simplified Chinese 936, Western European 1252 and Latin-1.

The line ending selector decides what is written on save: keep as is, or force LF, CRLF or CR.
Whenever a save would change the encoding or the line endings, NaraDiff lists the conversions and
asks for confirmation first. Saving writes to a temporary file and then replaces the original, so an
interrupted save cannot truncate the file.

Files that contain NUL bytes or too many control characters are treated as binary. Both sides are
then shown as a read-only hex dump with the differing rows highlighted, and the footer reports the
size difference, the first differing offset, the number of differing bytes and a hash of each side.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` / `Ctrl+Shift+N` / `Ctrl+M` | New file comparison, folder comparison, three way merge |
| `Ctrl+0` | Open a file into a new comparison |
| `Ctrl+S` | Save the changed files of the active tab |
| `F5` | Reload from disk and compare again |
| `F7`/ `hift+F7` | Next and previous change |
| `F8` / `Shift+F8` | Next and previous conflict (merge) |
| `Alt+Right` / `Alt+Left` | Copy the current change |
| `Ctrl+Alt+Right` / `Ctrl+Alt+Left` | Copy every change of the file |
| `Alt+1` / `Alt+2` / `Alt+3` / `Alt+B` | Resolve a conflict with left, base, right, or both (merge) |
| `Ctrl+F` | Search both files |
| `Ctrl+P` | Show or hide the options flyout |
| `Ctrl+W` | Close the active tab |

## Command line

```
NaraDiff.exe                                        start with an empty comparison
NaraDiff.exe left.txt right.txt                     compare two files
NaraDiff.exe folderA folderB                        compare two folders
NaraDiff.exe base.txt mine.txt theirs.txt           three way merge, result written over mine.txt
NaraDiff.exe base.txt mine.txt theirs.txt out.txt   three way merge with an explicit output file
```

The three and four argument forms match the order that `git mergetool` passes the base, local, remote
and merged paths, so NaraDiff can be configured as a merge tool.

## Settings and data

Settings live in `%LOCALAPPDATA%\NaraDiff\settings.json` and hold the theme, the fonts, the
comparison options, the presets, the folder and synchronisation options, the recent paths and the
window size. The file is written through a temporary file and one backup copy is kept. Unexpected
errors are appended to `%LOCALAPPDATA%\NaraDiff\logs`. NaraDiff makes no network connections.

## Build from source

Requires the .NET SDK with the `net8.0-windows` targeting pack and Windows.

```bash
build.bat
```

The script restores, builds Release, runs any tests present, and publishes the self-contained
single-file executable to `release\NaraDiff.exe`. To work on the code without publishing:

```bash
dotnet build NaraDiff.sln -c Release
```

## Project structure

| Project | Contents |
| --- | --- /
| `src/NaraDiff.Core` | Diff engine, three way merge, folder comparison, text and encoding handling, ribbon geometry, settings model
| `src/NaraDiff. Infrastructure` | File loading and saving, file watching, folder synchronisation, JSON settings store, logging |
| `src/NaraDiff.App` | WPF user interface: editors, connector ribbon, overview ruler, views, themes |

The core project has no user interface dependency, so the diff engine, the merge and the folder
comparison can be used independently of WPF.

## Current limitations

The comparison panes are split evenly and cannot be resized against each other.
- Syntax highlighting is not applied to the compared text; changes are shown by colour instead.
- Folder synchronisation copies and deletes files but does not preserve NTFS permissions or streams.
- Per monitor DPI awareness uses the WPF default (system DPI); mixed DPI setups may need a restart to
  look crisp.
- The user interface is English only.

## License

GPL-3.0-only. See [LICENSE] (LICENSE).
