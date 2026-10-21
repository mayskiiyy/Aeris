# Changelog

## 1.1.0

Cleanup

- Cleanup categories now come from a JSON rule set. New categories: crash dumps and Windows Error Reporting files, DirectX, NVIDIA and AMD shader caches, the Steam web and shader caches, npm, Yarn, pip, NuGet HTTP and Gradle caches, and the browser caches of Discord, Slack, classic Teams and VS Code.
- Categories are grouped (Windows, Browsers, Games and GPU, Developer tools, Apps) and rated Safe, Check first or Advanced. Only Safe ones are selected after a check.
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
