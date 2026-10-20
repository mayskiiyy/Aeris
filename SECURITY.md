# Security

Aeris deletes cache files permanently and moves selected personal files to the Recycle Bin. Run it with normal user rights and save your work before cleaning caches.

## Safeguards

Cache cleanup only works inside a built-in list of allowed folders. The minimum retention period is 48 hours, both file dates are checked during the scan and again right before deletion, excluded folders are skipped, and symlinks and junctions are never followed. The confirmation dialog defaults to "No".

Personal files are only moved to the Recycle Bin on local fixed drives. Before a duplicate is moved, Aeris hashes both the copy and the file that stays, and it refuses to empty a duplicate group completely.

## Known limitations

The path checks and the deletion are separate steps, so another process that swaps directories in between on purpose could win that race. Aeris is not a security boundary between untrusted processes. There is no automatic update and the builds are not code-signed. The portable build ships its own .NET runtime, so runtime fixes arrive only with a new Aeris release.

## Reporting a problem

If Aeris deletes anything outside the allowed cache folders, stop using cleanup and report it through [GitHub private vulnerability reporting](https://github.com/mayskiiyy/Aeris/security/advisories/new). Include your Windows version, the Aeris version and a minimal example that uses test files. Don't attach personal files or full paths that contain your user name.
