# Security

Aeris deletes cache files permanently or moves them to its quarantine, and moves selected personal files to the Recycle Bin. Run it with normal user rights and save your work before cleaning caches.

## Safeguards

Cache cleanup only works inside the folders named by the built-in rules and by rules you add yourself. `PathGuard` refuses drive roots, Windows (except its `Temp` folder), Program Files, the Users folder, profile and AppData roots and personal folders as cleanup folders, resolves `..` and 8.3 short names, and rejects any path with a link or junction on it. The minimum retention period is 48 hours. Every file is checked during the scan and again right before it is touched: it must still be inside a current rule's folder, unchanged since the scan and old enough. Excluded folders are skipped. Nothing is deleted before the review window has been shown and confirmed, and confirmations default to "No".

Quarantined files are moved, never copied, and only on the filesystem that holds the quarantine. Restoring never overwrites an existing file. Every operation is logged per file in the user's Aeris application-data folder.

The scheduled cleanup only runs Safe categories, through a per-user task without administrator rights. The Explorer menu entries are written under `HKCU` only. A second instance talks to the first one through a named pipe that only the same user can open.

On Windows, personal files are only moved to the Recycle Bin on local fixed drives. Linux CLI trash operations use `gio trash` and fail if the desktop trash service is unavailable; they never fall back to deletion. Before a duplicate is moved, Aeris hashes both the copy and the file that stays, and it refuses to empty a duplicate group completely. The Linux GUI reports duplicates without deleting them.

Linux uses case-sensitive path comparisons and file identities from `statx`. Scans skip symlinks and special files, including FIFOs and devices. System directories are blocked as cleanup roots. A cache file must have both an old modification time and an old birth time (or, where unavailable, metadata-change time).

## Update trust

Desktop apps contact GitHub Releases once at startup and on manual checks. Scanned paths and file contents stay local. Downloads are accepted only from this repository's HTTPS release URLs and only with a matching release SHA-256; GitHub's asset digest, when available, must agree. The byte count is checked and failed partial downloads are removed. Installation requires confirmation. Windows uses its per-user installer; Linux and portable Windows archives are installed manually.

## Image processing

Similar-image search opens image files through Windows Imaging Component. It skips inputs above 256 MiB, dimensions above 32,768 pixels on either side and images above 100 million pixels before expensive scaling. These limits do not sandbox a codec or guarantee a memory/time bound: native decoding still runs in the app process and cancellation waits for it to return. Scanning security-test payloads or other hostile images can trigger antivirus protection; Aeris does not add antivirus exclusions or disable protection.

## Known limitations

The path checks and the deletion are separate steps, so another process that swaps directories in between on purpose could win that race. User rules are trusted as much as the account that can write to the application-data folder. Aeris is not a security boundary between untrusted processes. Builds are not code-signed. A checksum and an installer hosted in the same GitHub account are not an independent publisher signature: account compromise remains a risk. Self-contained builds ship their own .NET runtime, so runtime fixes arrive only with a new Aeris release.

## Reporting a problem

If Aeris deletes anything outside the allowed cache folders, stop using cleanup and report it through [GitHub private vulnerability reporting](https://github.com/mayskiiyy/Aeris/security/advisories/new). Include your OS version, the Aeris version and a minimal example that uses test files. Don't attach personal files or full paths that contain your user name.
