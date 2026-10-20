# Aeris

Aeris is a small Windows app that shows where your disk space went and cleans out old cache and temp files. It runs locally, makes no network requests and collects no telemetry.

![Aeris home screen](.github/screenshot.png)

[![Build](https://github.com/mayskiiyy/Aeris/actions/workflows/build.yml/badge.svg)](https://github.com/mayskiiyy/Aeris/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/mayskiiyy/Aeris)](https://github.com/mayskiiyy/Aeris/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

## Features

- Cache cleanup for the user and Windows `Temp` folders and for the caches of every installed browser Aeris knows: Chrome, Edge, Brave, Opera, Opera GX, Vivaldi, Yandex Browser, Chromium and other Chromium-based browsers, plus Firefox, LibreWolf, Waterfox, Floorp, Zen and other Gecko-based ones. Only files whose creation and modification dates are both older than the retention period you pick (2 days minimum) are deleted.
- A list of the 200 largest files in any folder or drive, filtered by type (video, archives, installers, documents, images) and minimum size. The list can be exported to CSV.
- A folder map that shows how much space each subfolder takes, nested content included.
- Duplicate search that groups files by size and then compares SHA-256 hashes. Hard links to the same file are not counted as copies.
- Personal files go to the Recycle Bin, never straight to deletion. In each duplicate group at least one copy has to stay, and its content is hashed again before anything moves.
- A history of every cleanup and Recycle Bin operation, plus folder exclusions that apply to every scan and cleanup.
- Light and dark themes.
- Eight interface languages: English, Deutsch, Русский, Українська, Français, Español, Português (Brasil) and Polski. On first launch Aeris picks the Windows display language and falls back to English. You can switch at any time with the language menu next to the theme button.

## Install

Download the latest build from [Releases](https://github.com/mayskiiyy/Aeris/releases/latest):

- `Aeris-<version>-Setup-x64.exe` installs Aeris for the current user. It does not need administrator rights.
- `Aeris-<version>-win-x64.zip` is the portable build: a single `Aeris.exe` with its license files. Unpack it anywhere and run it.

Both builds include the .NET runtime, so nothing else needs to be installed. Aeris requires Windows 10 or 11 (x64). The builds are not code-signed yet, so SmartScreen may warn about an unknown publisher.

Settings, history and the selected language are stored in `%LOCALAPPDATA%\Aeris`. Uninstalling the app leaves that folder in place.

## How cleanup works

Aeris decides what counts as a cache by location, not by file extension. It only touches a fixed list of folders:

| Category | Folders |
| --- | --- |
| User temporary files | `%LOCALAPPDATA%\Temp` |
| Windows temporary files | `%WINDIR%\Temp` |
| Chromium-based browsers | `Cache`, `Code Cache` and `GPUCache` in every profile |
| Gecko-based browsers | `cache2` in every profile |

Only browsers that are actually installed show up. The full list of supported browsers and their folders is in `ChromiumBrowsers` and `GeckoBrowsers` in [Storage.cs](src/Aeris/Storage.cs). Apps that merely embed Chromium, such as Spotify or Discord, are left alone.

Cache cleanup deletes files permanently and cannot be undone. The cutoff date is fixed during the check and both dates are verified again right before each file is removed. Files that are locked, inaccessible, excluded or reached through a symlink or junction are skipped. Folders themselves are never deleted, and Aeris never asks for administrator rights.

Cookies, passwords, browsing history, bookmarks, downloads and Windows updates are never cleanup targets. Close your browsers before cleaning their caches.

Sizes are logical. Compression, sparse files and hard links can make the freed space differ from the estimate.

## Build from source

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio is optional.

```powershell
dotnet run --project src/Aeris/Aeris.csproj -c Release
```

To produce the portable archive and the installer locally, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run:

```powershell
./build.ps1
```

The results land in `artifacts/`: the single-file `Aeris.exe` in `Aeris-win-x64`, the zip archive, the setup and `SHA256SUMS.txt`. Pass `-IsccPath` if `ISCC.exe` is installed somewhere unusual.

## Project layout

| Path | Contents |
| --- | --- |
| `src/Aeris/MainWindow.xaml`, `MainWindow*.cs` | Window layout, background scans, cleanup and Recycle Bin operations |
| `src/Aeris/Storage.cs` | File walking, cache locations, age checks and deletion |
| `src/Aeris/Analysis.cs` | Folder map, type filters, SHA-256 and hard link detection |
| `src/Aeris/Recycle.cs` | Moving files to the Recycle Bin through the Windows Shell |
| `src/Aeris/UserData.cs` | Settings and history, saved with a backup copy |
| `src/Aeris/Localization.cs`, `Languages/` | Language switching and one string dictionary per language |
| `src/Aeris/Themes/` | Light and dark color palettes |
| `packaging/Aeris.iss` | Inno Setup script |
| `build.ps1` | Release build: publish, zip, installer and checksums |

## Translations

Every language lives in `src/Aeris/Languages/<code>.xaml` and uses the same keys as `en.xaml`. A missing key falls back to English. To add a language, copy `en.xaml`, translate the values and register the language in `Loc.Languages` in `Localization.cs`. Plural rules for day counts are in `Loc.PluralForm`.

## Releases

Releases are published by hand from the Actions tab:

1. Bump `<Version>` in `src/Aeris/Aeris.csproj` and add a matching `## <version>` section to `CHANGELOG.md`.
2. Push to `main`.
3. Open **Actions → Release → Run workflow**. You can mark the release as a draft or a pre-release.

The workflow builds the portable zip and the installer on Windows, creates the `v<version>` tag, uses the changelog section as release notes and attaches the files with their checksums. It refuses to overwrite a release that already exists.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to work on the project and [SECURITY.md](SECURITY.md) for how to report a problem privately.

## License

[MIT](LICENSE)
