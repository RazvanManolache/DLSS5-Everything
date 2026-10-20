# Dark Theme And Gamer Library Audit

## Scope

Audited app-owned UI with WinForms off-screen rendering and STA integration tests. No Computer Use, games, game installations, real process launches, or GPU inference were used. Rendering receipts are local ignored validation artifacts, not public screenshots.

## Verified Problems And Changes

| Problem | Change |
| --- | --- |
| Disabled native button labels became almost black | Paint disabled labels with the theme's muted text color; keep them disabled |
| Fixed-color icons lost contrast on primary buttons | Tint command icons to their current text color |
| Dropdown arrow boxes and borders stayed light | Theme-aware dropdown chrome, with native selection/keyboard behavior and existing localized/renderer item drawing |
| Numeric editors retained bright raised borders and spin buttons | Flat themed numeric fields and spin glyphs; retain inherited limits and increment/decrement logic |
| Phoenix DLSS5 tabs had light gutters and raised page borders | Theme-aware tab strip/page frame; multiline tabs at compact widths |
| Native progress control kept a white frame | Theme-aware determinate progress control, preserving min/max/value validation |
| Checkboxes used bright native squares and inconsistent disabled text | Theme-aware checked/unchecked/disabled presentation |
| Native list selection did not match the palette | Palette selection backgrounds and readable selected text |
| Cover captions became white boxes when selected | Theme-aware caption area and selected-cover outline |
| Status red/green was hardcoded for light backgrounds | Separate light/dark error and success colors, tested for text contrast |
| Toolbar/menu gutters and separators stayed light | Palette-aware ToolStrip/menu renderer and item colors |
| Folder lists used raised bright borders | Surface backgrounds and flat single borders |
| Drag feedback reset to pale blue | Use the active palette for both resting and drag feedback |
| Translation-credit links/dialog could remain light | Theme-aware links and credits window |
| Some command captions clipped despite sufficient space | Correct text padding in themed button painting |
| Library widths reserved scrollbar space twice | Size columns from the actual client width |
| Inspector action flow created a stray scrollbar | Disable scrolling for the fixed-height metadata actions |
| Theme/navigation could discard unsaved game options | Preserve staged options while the selected game is unchanged |
| Startup mixed installed titles with scanning candidates | Separate My Games startup page and Discover workspace |

## My Games Rules

- Uses its own ListView, search, favorites toggle, sorting and selection. It is not an Installed dropdown shortcut.
- Populates from known executable candidates with validated installer manifests and recorded files present. Incomplete installs and unrelated executables are excluded.
- Shares the existing game inspector and rendering-control workflow without copying their implementation. Play is the primary action for installed titles.
- List/Previews preference is remembered and shared between views; discovery filters and sorting do not affect My Games.
- Refresh explicitly invalidates cached installation evidence and rechecks files. Successful install/remove operations also refresh through the existing history path.
- Installed means file evidence only, never confirmed inference or image quality.

## Verification

Local validation on 12 October 2026: 714/714 .NET tests passed, including 15 theme/installed-library cases; 37/37 distribution-notice checks passed. The self-contained local package contains no external payload or captured validation images.

The dark-theme tests render every navigation page at 1060x740, 1400x900 and 1920x1080, all Phoenix DLSS5 control tabs, preferences, recents, command search and the redacted-report dialog. Installed-library tests render both view modes at minimum and wide sizes, validate real isolated manifest fixtures, preserve selection, and intercept Play at the process boundary.

Pixel tests cover disabled text, dropdown theme round trips and progress fill/border colors. Text/status colors meet a 4.5:1 contrast threshold against their surface in light and dark themes. Numeric behavior, incomplete/missing installs, refresh, independent filtering, and preservation of unapplied options have regression coverage.

## Remaining Boundaries

- Windows file/folder pickers, message boxes, UAC prompts and system menus remain OS-owned. Their appearance follows Windows, not the app palette.
- Dark title-bar and native scrollbar theme requests are applied. DrawToBitmap does not reliably capture DWM nonclient rendering, so these captures do not certify the title bar's on-screen appearance.
- The rendered viewport matrix uses the test machine's DPI. It is not a claim of exhaustive multi-monitor, mixed-DPI, every-language or high-contrast visual certification.
- Public README screenshots are labeled as older previews. No private captures or local paths were added to public assets.
