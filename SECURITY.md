# Security

Aeris deletes cache files permanently or moves them to its quarantine, and moves selected personal files to the Recycle Bin. Run it with normal user rights and save your work before cleaning caches.

## Safeguards

Cache cleanup only works inside the folders named by the built-in rules and by rules you add yourself. `PathGuard` refuses drive roots, Windows (except its `Temp` folder), Program Files, the Users folder, profile and AppData roots and personal folders as cleanup folders, resolves `..` and 8.3 short names, and rejects any path with a link or junction on it. The minimum retention period is 48 hours. Every file is checked during the scan and again right before it is touched: it must still be inside a current rule's folder, unchanged since the scan and old enough. Excluded folders are skipped. Nothing is deleted before the review window has been shown and confirmed, and confirmations default to "No".

Quarantined files are moved, never copied, and only on the drive that holds `%LOCALAPPDATA%`. Restoring never overwrites an existing file. Every operation is logged per file in `%LOCALAPPDATA%\Aeris\history`.

The scheduled cleanup only runs Safe categories, through a per-user task without administrator rights. The Explorer menu entries are written under `HKCU` only. A second instance talks to the first one through a named pipe that only the same user can open.

Personal files are only moved to the Recycle Bin on local fixed drives. Before a duplicate is moved, Aeris hashes both the copy and the file that stays, and it refuses to empty a duplicate group completely.

## Known limitations

The path checks and the deletion are separate steps, so another process that swaps directories in between on purpose could win that race. User rules are trusted as much as the account that can write to `%LOCALAPPDATA%`. Aeris is not a security boundary between untrusted processes. There is no automatic update and the builds are not code-signed. The portable build ships its own .NET runtime, so runtime fixes arrive only with a new Aeris release.

## Reporting a problem

If Aeris deletes anything outside the allowed cache folders, stop using cleanup and report it through [GitHub private vulnerability reporting](https://github.com/mayskiiyy/Aeris/security/advisories/new). Include your Windows version, the Aeris version and a minimal example that uses test files. Don't attach personal files or full paths that contain your user name.
