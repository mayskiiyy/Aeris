# Aeris

Aeris is a small Windows app that shows where your disk space went and cleans out old cache and temp files. It runs locally, makes no network requests and collects no telemetry.

![Aeris home screen](.github/screenshot.png)

[![Build](https://github.com/mayskiiyy/Aeris/actions/workflows/build.yml/badge.svg)](https://github.com/mayskiiyy/Aeris/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/mayskiiyy/Aeris)](https://github.com/mayskiiyy/Aeris/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

## Features

- Cache cleanup for Windows, every installed browser Aeris knows, GPU shader caches, Steam, developer tools (npm, Yarn, pip, NuGet, Gradle) and a few Electron apps. Only files whose creation and modification dates are both older than the retention period you pick (2 days minimum) are touched. The full list is in [How cleanup works](#how-cleanup-works).
- Before anything is deleted you see every file in a review window, grouped by category, with search and sorting. You can untick single files or whole categories.
- Cache files can be deleted permanently or moved to a quarantine for 1 to 30 days. History restores a quarantined cleanup with the original dates and never overwrites a file that took its place.
- A list of the 200 largest files in any folder or drive, filtered by type and minimum size, with CSV export.
- A folder map drawn as a treemap, colored by file type, with a list next to it, breadcrumbs, logical size or size on disk, and a context menu. Scanning uses all CPU cores and counts hard links once.
- Duplicate search by size, a quick check of the first and last 64 KB, and full SHA-256. Hashes of unchanged files are cached, so a second search is much faster. Rules pick which copy stays (oldest, newest, shortest path or name, folders with originals, folders that are never touched).
- Similar image search (resized, re-saved or slightly cropped copies) with side-by-side previews and the best copy marked.
- Empty folders and 0-byte files. Folders that belong to Windows or to apps are never offered.
- Personal files (duplicates, similar images, large files, empty folders) only ever go to the Recycle Bin, and each duplicate group always keeps at least one copy.
- `aeris-cli` for scripts, and an optional scheduled cleanup of Safe categories through Windows Task Scheduler.
- Optional Explorer context menu entries for folders and drives.
- A history of every operation with a per-file log, folder exclusions that apply everywhere, light and dark themes.
- Eight interface languages: English, Deutsch, Русский, Українська, Français, Español, Português (Brasil) and Polski. On first launch Aeris picks the Windows display language and falls back to English.

## Install

Download the latest build from [Releases](https://github.com/mayskiiyy/Aeris/releases/latest):

- `Aeris-<version>-Setup-x64.exe` installs Aeris and `aeris-cli.exe` for the current user. It does not need administrator rights.
- `Aeris-<version>-win-x64.zip` is the portable build: `Aeris.exe`, `aeris-cli.exe` and the license files. Unpack it anywhere and run it.
- `aeris-cli-<version>-win-x64.exe` is the command-line tool on its own.

Or install it with winget:

```powershell
winget install MAYSKIY.Aeris
```

All builds include the .NET runtime, so nothing else needs to be installed. Aeris requires Windows 10 or 11 (x64).

The builds are not code-signed yet, so SmartScreen may say that Windows protected your PC. Click **More info → Run anyway**. Before that you can check that the file is the one from the release: run `Get-FileHash .\Aeris-<version>-Setup-x64.exe` in PowerShell and compare the hash with `SHA256SUMS.txt` from the same release. Installing through winget doesn't show this warning.

Settings, history, quarantine and caches of hashes are stored in `%LOCALAPPDATA%\Aeris`. Uninstalling the app leaves that folder in place, but removes the scheduled task and the Explorer menu entries.

## How cleanup works

Aeris decides what counts as a cache by location, not by file extension. It only touches the folders listed below, and only the categories whose folders exist on your PC show up.

| Group | Category | Folders | Selected by default |
| --- | --- | --- | --- |
| Windows | User temporary files | `%LOCALAPPDATA%\Temp` | yes |
| Windows | Windows temporary files | `%WINDIR%\Temp` | yes |
| Windows | Crash dumps and error reports | `%LOCALAPPDATA%\CrashDumps`, `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive` and `ReportQueue` | yes |
| Browsers | 25 Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi, Yandex and others) | `Cache`, `Code Cache` and `GPUCache` in every profile | yes |
| Browsers | 8 Gecko browsers (Firefox, LibreWolf, Waterfox, Floorp, Zen and others) | `cache2` in every profile | yes |
| Games and GPU | DirectX shader cache | `%LOCALAPPDATA%\D3DSCache` | yes |
| Games and GPU | NVIDIA shader cache | `NVIDIA\DXCache` and `GLCache` in `%LOCALAPPDATA%` and `AppData\LocalLow\NVIDIA\PerDriverVersion` | yes |
| Games and GPU | AMD shader cache | `%LOCALAPPDATA%\AMD\DxCache`, `DxcCache`, `GLCache`, `VkCache` | yes |
| Games and GPU | Steam web cache | `Cache`, `Code Cache`, `GPUCache` in `%LOCALAPPDATA%\Steam\htmlcache` | no |
| Games and GPU | Steam shader cache | `steamapps\shadercache` in the Steam folder from `HKCU\Software\Valve\Steam` | no |
| Developer tools | npm cache | `%LOCALAPPDATA%\npm-cache\_cacache` and `_logs` | no |
| Developer tools | Yarn cache | `%LOCALAPPDATA%\Yarn\Cache` | no |
| Developer tools | pip cache | `%LOCALAPPDATA%\pip\Cache` | no |
| Developer tools | NuGet HTTP cache | `%LOCALAPPDATA%\NuGet\v3-cache` and `plugins-cache` | no |
| Developer tools | Gradle caches | `%USERPROFILE%\.gradle\caches` | no, marked Advanced |
| Apps | Discord, Discord PTB, Discord Canary, Slack, classic Teams, VS Code | `Cache`, `Code Cache`, `GPUCache` (and `CachedData` for VS Code) in the app's `%APPDATA%` folder | no |

The rules live in [builtin.json](src/Aeris.Core/Cleaners/builtin.json). Categories are rated Safe (selected after a check), Check first (never selected for you) or Advanced (asks once more when you tick it, never selected by "Select all").

Apps with a built-in browser, such as Discord, are only cleaned if you tick them yourself. Spotify is not on the list, because its cache folder also holds downloaded music. These are never cleanup targets: cookies, sessions, Local Storage, IndexedDB, passwords, history, bookmarks, downloads, game saves, `node_modules`, `.nuget\packages`, the pnpm store and Windows Update files.

Every file is checked twice: during the scan and again right before it is removed. The second check makes sure it is still inside a folder of a current rule, that no folder on its path is a link or junction, that its size and dates haven't changed since the scan and that it is still old enough. A file that fails any check is skipped and logged. Folders themselves are never deleted, and Aeris never asks for administrator rights. A cleanup of more than 5,000 files or 5 GB asks for one more confirmation.

If an app named in a category is running (a browser, Discord, Steam and so on), the review window says so; files it keeps open are skipped and counted as "in use".

Sizes are logical by default. Compression, sparse files and hard links can make the freed space differ from the estimate.

### Your own cleanup rules

Put JSON files in `%LOCALAPPDATA%\Aeris\cleaners` (Settings → Your cleanup rules → Open rules folder). A file holds one rule or an array of rules:

```json
{
  "id": "my-editor-cache",
  "name": "My editor cache",
  "description": "Thumbnails the editor rebuilds on its own",
  "risk": "Normal",
  "closeAppsHint": ["myeditor"],
  "targets": [
    { "root": "%LOCALAPPDATA%\\MyEditor\\Thumbnails", "pattern": "*.cache", "recurse": true },
    { "root": "%APPDATA%\\MyEditor", "folders": ["Cache", "GPUCache"] }
  ]
}
```

`root` may use environment variables and `{reg:HKCU\Key:Value}`. `each` can be `subfolders` or `chromium-profiles` to repeat `folders` under every profile. Your rules are never Safe and never selected by default. Aeris refuses a rule whose folder is a drive root, Windows (except `Temp`), Program Files, the Users folder, your profile or AppData roots, or anything inside Documents, Desktop, Pictures, Videos, Music, Downloads or OneDrive. Unknown fields are an error, so a typo doesn't silently widen a rule. Skipped rules are listed on the Cache cleanup tab.

## Command line

`aeris-cli` uses the same code and the same safety checks as the app. Output follows the Windows display language; `--json` gives machine-readable output with a `schema` version.

```powershell
aeris-cli categories
aeris-cli clean --dry-run
aeris-cli clean --categories user-temp,chrome --older-than 7d --mode quarantine
aeris-cli scan D:\ --top 50 --min-size 1GB --type video
aeris-cli duplicates D:\Photos E:\Backup --min-size 1MB --json
aeris-cli duplicates D:\Photos --similar-images --threshold 5
aeris-cli empty-folders D:\Projects
aeris-cli history --last 20
aeris-cli restore 20261021-031500-4f2a
```

`clean` always shows the plan first and asks before deleting. Without a console it refuses unless you pass `--yes`. Duplicates and empty folders are only reported unless you add `--delete-to-recycle --yes`, and even then they go to the Recycle Bin. Exit codes: `0` success, `1` done but some files were skipped or failed, `2` invalid arguments or refused, `3` cancelled.

## Automatic cleanup and Explorer

Settings → Automation can create a Task Scheduler task for your account that runs daily, weekly or monthly. Windows starts Aeris without a window, it cleans the Safe categories older than the age you chose (in quarantine or permanently) and exits; nothing stays in the background. The run appears in History marked as scheduled. Advanced and Check first categories never run on a schedule.

The same page adds "Scan in Aeris", "Find duplicates here" and "Show largest files" to the context menu of folders and drives, for your account only. On Windows 11 they are under "Show more options". `Aeris.exe` also accepts `--scan <folder>`, `--duplicates <folder>`, `--largest <folder>` and `--tab <name>`; a second launch passes them to the open window.

## Build from source

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio is optional.

```powershell
dotnet run --project src/Aeris/Aeris.csproj -c Release
dotnet test Aeris.sln
```

To produce the portable archive and the installer locally, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run:

```powershell
./build.ps1
```

The results land in `artifacts/`: `Aeris.exe` and `aeris-cli.exe` in `Aeris-win-x64`, the zip archive, the standalone CLI, the setup and `SHA256SUMS.txt`. Pass `-IsccPath` if `ISCC.exe` is installed somewhere unusual.

`dotnet run --project tools/Aeris.Bench -c Release -- <folder>` times the scanner and the treemap. Results are in [docs/benchmarks.md](docs/benchmarks.md).

## Project layout

| Path | Contents |
| --- | --- |
| `src/Aeris.Core/` | Everything that isn't UI: cleanup rules, `PathGuard`, plans, quarantine, journal, scanner, treemap layout, duplicates, similar images, settings, localization and the CLI commands |
| `src/Aeris.Core/Cleaners/builtin.json` | Built-in cleanup categories |
| `src/Aeris.Core/Languages/` | One string dictionary per language |
| `src/Aeris/` | The WPF app: main window, review window, treemap control, themes |
| `src/Aeris.Cli/` | Entry point of `aeris-cli.exe` |
| `tests/Aeris.Tests/` | xUnit tests; file tests run in temporary folders |
| `tools/Aeris.Bench/` | Scanner and treemap benchmark |
| `packaging/Aeris.iss` | Inno Setup script |
| `packaging/winget/` | winget manifests, one folder per version |
| `build.ps1` | Release build: publish, zip, installer and checksums |

## Translations

Every language lives in `src/Aeris.Core/Languages/<code>.xaml` and uses the same keys as `en.xaml`. A test fails if a key or a `{0}` placeholder is missing in any language, or if the code uses a key that doesn't exist. To add a language, copy `en.xaml`, translate the values and register the language in `Loc.Languages` in `Localization.cs`. Plural rules are in `Loc.PluralForm`.

## Releases

Releases are published by hand from the Actions tab:

1. Bump `<Version>` in `Directory.Build.props` and add a matching `## <version>` section to `CHANGELOG.md`.
2. Push to `main`.
3. Open **Actions → Release → Run workflow**. You can mark the release as a draft or a pre-release.

The workflow builds everything on Windows, creates the `v<version>` tag, uses the changelog section as release notes and attaches the files with their checksums. It refuses to overwrite a release that already exists. After a release, update `packaging/winget` as described in its README.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to work on the project and [SECURITY.md](SECURITY.md) for how to report a problem privately.

## License

[MIT](LICENSE)
