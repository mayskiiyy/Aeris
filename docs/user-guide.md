# Using Aeris

For installation and the platform feature table, see the [README](../README.md).
Both desktop apps include an offline **Help** tab, opened with **F1**, in all eight interface languages.
The Windows and Linux interfaces have different capabilities; the instructions below distinguish them.

## Find what takes up space

### Windows

1. Open **Large files** or **Folder map**, choose a folder or drive and start a scan.
2. In Large files, narrow results by minimum size and file type. The list shows up to 200 matching files; export the displayed list with the CSV button.
3. In Folder map, explore folders using the map, the adjacent list and breadcrumbs. Small entries may be grouped. Switch between logical size and size on disk when comparing results.
4. Open a file's location before deciding whether it is needed. Selected personal files can be sent to the Recycle Bin after review.

### Linux desktop

1. Enter a folder in **Disk usage**, or choose it with Browse, then scan.
2. Explore folders on the map or double-click a folder in the adjacent list. Use **Up** to return to its parent.
3. The file list shows the largest 500 files. Double-click a file, or press Enter on it, to open its location. Export the list to CSV when needed.

The Linux disk view does not delete personal files. On either platform, scanning alone never deletes anything.

## Review and clean caches

1. Close browsers and other apps whose caches you plan to clean.
2. On Windows, choose the cleanup mode in **Settings** first. **Permanent deletion is the default**; choose **Quarantine** to retain a recovery option. The Linux GUI always uses quarantine.
3. In **Cache cleanup**, select categories and a minimum age. Start with 7 days if unsure; the minimum is 2 days. Both creation and modification dates must pass the age check.
4. Check caches. Only categories whose target folders exist are shown. Review the risk descriptions: Safe is not a promise that every file is unnecessary; Check first and Advanced need a deliberate choice.
5. Use category filters or search to find a category. **Filtering does not deselect hidden categories.** Check the complete review, not just the currently visible categories.
6. Open the review and exclude individual files you want to keep. Confirm only after inspecting the plan.
7. Read the result and History. Files that are locked, changed, inaccessible or no longer satisfy the rules may be skipped.

Aeris targets known cache locations, not every file with a temporary-looking extension. Browser passwords, cookies, sessions and bookmarks are not built-in cleanup targets.
An app may rebuild its cache after cleanup, so its next launch or first task can be slower.
For category paths and custom JSON rules, see [How cleanup works](../README.md#how-cleanup-works).

## Find duplicate files and similar images

### Windows

Choose the folder in **Duplicates**, select exact duplicates or similar images, and start the search.
Exact duplicates are verified with SHA-256. Similar-image results can include resized or re-saved copies; inspect the side-by-side previews before deciding which version to keep.

Review the keep rule and marked copies. Rules can prefer older or newer copies, shorter paths or names, and original folders; protected folders prevent removal.
Send unwanted copies to the Recycle Bin after reviewing the plan. Aeris retains at least one copy of every exact duplicate group.

### Linux desktop

Enter the folder in **Disk usage**, then open **Duplicates** and start the search.
The results identify exact copies verified with SHA-256. Double-click a result to open its location.
The Linux GUI reports duplicates without deleting them and does not offer similar-image search.

## Find empty folders and zero-byte files

On Windows, open **Empty folders**, choose a folder and scan it. Review the offered folders and zero-byte files before sending selected items to the Recycle Bin.
Folders belonging to Windows or applications are excluded. A zero-byte file can still serve a purpose, so size alone is not a reason to remove it.

The Linux GUI has no empty-folder view. The CLI supports reporting and optional trash operations; see `aeris-cli --help`.

## Recover files and understand reclaimed space

| Action | Where to recover | Limits |
| --- | --- | --- |
| Cache moved to quarantine | Select the operation in History and restore | Requires the quarantined files to still exist; existing replacement files are never overwritten |
| Personal file sent to Recycle Bin or desktop trash | System Recycle Bin or trash | Available only while the system still retains it |
| Permanent cache deletion | No undo in Aeris | A history entry records what happened; it is not a backup |

Quarantine moves files into Aeris app data and **still occupies space**. The reported processed size is not necessarily newly available disk space.
On Windows, automatic quarantine purging enforces the retention age and size limit; exceeding the size limit can remove the oldest operations before their selected retention age.
Restore important files promptly. Removing Aeris app data also removes its quarantine and history.

The Linux GUI moves caches only to quarantine and does not expose the Windows quarantine limits or purge controls.
Linux quarantine requires the cache and app data to be on the same filesystem.

Logical size, allocated size, compression, sparse files and hard links can also make the scan estimate differ from the change in free space.

## Settings and automation

### Find a function or setting (Windows)

Use the search field on Home, or press **Ctrl+K** from any tab.
It finds functions, individual settings, available cache categories and help topics in the interface language.
Search accepts multiple words and ignores case and accents. Cache results also recognize the same aliases and paths as the cache search.
Click a result to open its section; settings and help topics scroll into view, and cache categories open in Cache cleanup.
Enter opens the first result, Down moves focus to results, and Esc clears the query.

### Preferences and schedules

On Windows, Settings contains cleanup mode, quarantine limits, folder exclusions, duplicate keep rules, language and theme.
Exclusions apply across scans and cleanup. After changing exclusions, scan again.
Automation can register a daily, weekly or monthly Task Scheduler run for Safe cache categories only. Review a manual cleanup before enabling a schedule.
Explorer integration is optional; Windows 11 places its entries under **Show more options**.

Linux Settings includes language, update checks and server instructions; the theme button is in the header.
CLI packages include optional systemd user units. These are not enabled automatically.
See [Linux setup](../README.md#linux-desktop-and-servers) for enabling and disabling the timer and viewing its logs.

The desktop checks for updates at launch. File analysis is local; update requests contact GitHub.
An offline update error does not prevent scanning or cleanup. Linux and Windows portable updates require manual installation of the downloaded archive.

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| F1 | Open the built-in Help tab |
| Ctrl+K (Windows) | Open the general search on Home |
| Ctrl+F | Search Aeris on Windows Home; elsewhere, open Cache cleanup and focus its search |
| Enter / Down in home search (Windows) | Open the first result / move focus to results |
| Esc in home search (Windows) | Clear the general search |
| Esc in cache search | Clear the search |
| Tab / Shift+Tab | Move keyboard focus |
| Arrow keys on the tab bar | Choose a tab |
| Page Up / Page Down with the guide focused | Scroll the guide |
| Space on selected rows in the Windows file review | Toggle inclusion in the cleanup plan |
| Enter on a Linux disk-view file or folder | Open the file location or enter the folder |

On Windows, click the **middle mouse button** inside a scrollable area, then move the pointer above, below or to either side of the starting point to scroll.
Moving farther increases speed. Click again or press Esc to stop.
You can also hold the middle button while moving and release it to stop.
This works in pages, lists and file-review windows; ordinary wheel scrolling continues to work.

## Common questions

**Why is a file or category missing?**
Check the selected folder, file type, minimum size, category filter, search text, exclusions and age limit.
A category appears only when its target folder exists. Inaccessible paths, links and special files may be skipped.
The large-file list is capped at 200 on Windows and 500 on Linux.

**Why were files skipped?**
Close the affected app and scan again. Aeris rechecks files immediately before cleanup; changed dates or size, links, permissions and open handles can prevent removal.
Inspect the operation result and History before retrying.

**Why did free space barely change?**
Check whether files went to quarantine or the Recycle Bin, whether an app rebuilt its cache, and whether the displayed figure is logical size.
Do not remove the quarantine if you still need its recovery option.

**Why is Restore unavailable or incomplete?**
The operation may have used permanent deletion or the Recycle Bin, the quarantine may have been purged, or files may already have been restored.
If a new file occupies the original path, Aeris leaves the quarantined copy untouched rather than overwrite the replacement.

**Can I use Aeris without a desktop?**
Yes. Start with `aeris-cli --help` and a dry run:

```text
aeris-cli categories
aeris-cli clean --dry-run
aeris-cli clean --scheduled --older-than 7d --mode quarantine --dry-run --json
aeris-cli history --last 20
```

The dry run previews a plan without cleaning. See the [CLI reference and examples](../README.md#command-line) before enabling unattended cleanup.
