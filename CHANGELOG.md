# Changelog

## 1.2.1

- Replaced the default Windows scrollbars with slim, rounded controls that follow the light and dark themes, including the sidebar, lists and review windows.

## 1.2.0

- Added an offline Help tab to both desktop apps, available with F1 in all eight interface languages, plus a first-run guide and detailed user documentation.
- Added Windows home search for functions, settings, available cache categories and help topics, with Ctrl+K, keyboard navigation and smooth result expansion.
- Added middle-button autoscroll across Windows pages, lists and review windows.
- Migrated the core, Windows and Linux apps, CLI, tests, benchmark, CI and release packages to .NET 10.

## 1.1.0

Interface and performance

- Added cache category filtering, multi-word search, preselection before scanning and preserved manual choices. Search follows the current theme and supports Ctrl+F, Escape and clearing the query.
- Simplified the folder map to one level with readable sizes and grouped small entries. File-type totals are calculated when opened. Home shows the last cache cleanup after results are invalidated; empty history details and idle status bars are hidden.
- Reused SHA-256 buffers to reduce allocations during duplicate searches. Similar-image grouping indexes distinct hashes once, starts decoding during discovery and retains completed hashes on cancellation.
- Added image input/dimension limits before expensive scaling. Native decoding remains in-process and cannot yet be forcibly interrupted by a timeout.
- Updated the Linux interface to Avalonia 11.3.22. Builds still target .NET 8; migration is needed before its support ends on November 10, 2026.

Updates and Linux

- Desktop apps check GitHub Releases once at startup and from Settings. New stable versions are offered without blocking startup; downloads verify SHA-256 and file size before use. Installed Windows builds can run the per-user installer after confirmation.
- Added a Linux Avalonia desktop app with disk maps, large files, CSV export, exact duplicate reports, reviewed quarantine cleanup, history and restore. Windows retains its WPF interface; similar-image matching remains Windows-only.
- Shared CLI and core support Linux paths, inode identities, hard links, XDG cache rules and filesystem-aware quarantine. Special files and symlinks are skipped; retention uses birth time or metadata-change time instead of inferring creation from modification time.
- Added Linux x64/ARM64 packages, a per-user desktop installer and opt-in systemd cleanup units. Linux builds run separately in CI and release packaging.
- Fixed scheduled cleanup accepting a risky category when that category's folders did not exist, including on GitHub Actions runners.

Cleanup

- Cleanup categories now come from a JSON rule set. New categories: crash dumps and Windows Error Reporting files, DirectX, NVIDIA and AMD shader caches, the Steam web and shader caches, npm, Yarn, pip, NuGet HTTP and Gradle caches, and the browser caches of Discord, Slack, classic Teams and VS Code.
- Categories are grouped (Windows, Browsers, Games and GPU, Developer tools, Apps) and rated Safe, Check first or Advanced. Safe categories are selected automatically only when there is no manual choice.
- Apps with a built-in browser, such as Discord, are now cleaned when you tick them. Before 1.1 they were never cleaned.
- Your own rules can be added as JSON files in `%LOCALAPPDATA%\Aeris\cleaners`. They are validated, never Safe and never selected by default.
- A single path check (`PathGuard`) resolves `..` and 8.3 short names, refuses links and junctions on the path and keeps every file inside its rule's folder. It runs during the scan and again before each file is touched.
- The review window shows every file before anything is deleted, with search, sorting and per-file or per-category exclusion. Big operations ask for one more confirmation.
- Cache files can go to a quarantine instead of being deleted. History restores them with their original dates and never overwrites a newer file.
- Each operation is logged to a JSON Lines file in `%LOCALAPPDATA%\Aeris\history`. Files that changed since the scan are skipped and logged; files in use are counted separately.
- Running apps whose caches are in the plan are named in the review window.

Disk analysis

- A new scanner reads folders in parallel and counts hard links once. A full scan of a 1.37 million file system drive took 23 s instead of 183 s on the test machine.
- The folder map is a treemap colored by file type, with tooltips, breadcrumbs, a synchronized list, a legend, a size-on-disk option and a context menu.
- The last scan stays in memory, so the large-file filters update without a rescan.

Duplicates and junk

- Duplicate search checks the first and last 64 KB before hashing the whole file, hashes in parallel (or one file at a time for hard drives) and caches hashes between runs.
- Rules choose which copy stays: oldest, newest, shortest path or name, folders with originals, folders whose files are never picked.
- Similar image search with a tolerance slider and side-by-side previews.
- A new tab finds empty folders and 0-byte files and moves them to the Recycle Bin after review.

Automation

- `aeris-cli` with `scan`, `clean`, `duplicates`, `empty-folders`, `history`, `restore` and `categories`, JSON output and exit codes.
- Optional scheduled cleanup of Safe categories through Windows Task Scheduler, without administrator rights or a background process.
- Optional Explorer context menu entries for folders and drives.
- `Aeris.exe` accepts `--scan`, `--duplicates`, `--largest` and `--tab`, and only one window opens at a time.

Other

- The Exclusions tab became Settings.
- Settings get a schema version; 1.0 settings and history are migrated as they are.
- winget manifests in `packaging/winget`.
- Logic moved into `Aeris.Core` with an xUnit test project that CI runs on every push.
- The new strings are translated into all eight languages. The translations were not reviewed by native speakers.

## 1.0.0

First public release.

- Cache cleanup for the user and Windows `Temp` folders and for 33 Chromium and Gecko browsers. Only installed browsers are listed, and only files older than the chosen retention period are deleted.
- Large file search with type filters, a custom size threshold and CSV export.
- Folder map with the size and share of each subfolder.
- Duplicate search by size and SHA-256, with hard links excluded.
- Personal files are moved to the Recycle Bin after confirmation, never deleted outright.
- Operation history and folder exclusions.
- Interface in English, German, Russian, Ukrainian, French, Spanish, Brazilian Portuguese and Polish, with light and dark themes.
- Portable archive and a Windows x64 installer that does not need administrator rights.
