# Benchmarks

Measured with `tools/Aeris.Bench` on 2026-10-21. Release build, .NET 8, nothing else heavy running. These measurements predate the .NET 10 migration; rerun the benchmark before comparing the current build.

Machine: Intel Core i7-8750H (6 cores, 12 threads), 16 GB RAM, Crucial MX500 1 TB SATA SSD, Windows 11 (build 26300).

```powershell
dotnet run --project tools/Aeris.Bench -c Release -- C:\
dotnet run --project tools/Aeris.Bench -c Release -- --synthetic 500000
```

## Scanning

"Old walker" is the 1.0 code (`Storage.Walk`, one directory listing plus one attribute call per entry, single thread). `StandardScanner` is the 1.1 scanner (one `GetFileInformationByHandleEx` buffer per 64 KB of entries, up to 8 threads).

| Tree | Files | Old walker | StandardScanner | Memory |
| --- | ---: | ---: | ---: | ---: |
| Whole system drive `C:\` | 1,366,254 | 183.2 s | 23.0 s (warm 23.1 s) | ~123 bytes per node |
| Synthetic tree in `%TEMP%` | 367,283 | 6.0 s | 0.82 s (warm 0.72 s) | ~96 bytes per node |

The target was 500,000 files in 30 s or less on an SSD. The system drive has almost three times as many files and finishes in 23 s.

On `C:\` the new scanner reports 627.1 GB against 640.6 GB from the old walker. The difference is hard links (mostly `WinSxS`), which the old walker counted once per link and the new one counts once per file. On folders without hard links both report the same logical sizes; `ScannerTests.LogicalSizesMatchTheOldWalker` checks that.

The synthetic run was meant to create 500,000 files, but the first attempt was stopped while it was still creating them, so the tree had 367,283 files. The numbers above are for that tree.

## Treemap

Layout of the whole synthetic tree into a 1280 × 800 area, nested up to 12 levels:

| Smallest drawn cell | Cells | Layout | Building 10,000 rectangles | Software rasterization |
| ---: | ---: | ---: | ---: | ---: |
| 3 px | 26,946 | 62.5 ms | 139.5 ms (first run, includes JIT) | 110.6 ms |
| 1 px | 28,050 | 23.4 ms | 8.5 ms | 103.4 ms |

The goal was a redraw on resize within 50 ms for 10,000 visible rectangles. Layout plus building the drawing takes about 32 ms once the code is warm. The app itself draws through WPF's hardware-accelerated render thread, so the software rasterization column (rendering into a bitmap on the CPU) is an upper bound, not what the window pays. The app also stops at 3 px cells and 10 levels, which on a typical folder gives a few thousand cells.

## Not measured

There is no MFT-based scanner. Reading the MFT needs administrator rights, which Aeris doesn't ask for, and the standard scanner already meets the target.
