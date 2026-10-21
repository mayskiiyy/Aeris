using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aeris;

// Usage: dotnet run --project tools/Aeris.Bench -c Release -- [folder] [--synthetic N]
// Scans the folder with the old walker and the new scanner, then times the treemap layout and drawing.
// Nothing is written outside the system temp folder, and only when --synthetic is given.

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Loc.Load(Loc.Fallback);
string target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string? synthetic = null;
int syntheticIndex = Array.IndexOf(args, "--synthetic");
if (syntheticIndex >= 0)
{
    int count = int.Parse(args[syntheticIndex + 1], CultureInfo.InvariantCulture);
    synthetic = target = Path.Combine(Path.GetTempPath(), "aeris-bench-" + count);
    if (!Directory.Exists(synthetic))
    {
        Console.WriteLine($"Creating {count:N0} files in {synthetic}…");
        var random = new Random(1);
        for (int i = 0; i < count; i++)
        {
            string folder = Path.Combine(synthetic, $"a{i % 50}", $"b{i % 977 % 40}", $"c{i % 13}");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"f{i}.dat"), new byte[random.Next(0, 4096)]);
        }
    }
}

Console.WriteLine($"Folder: {target}");
Console.WriteLine($"CPU threads: {Environment.ProcessorCount}, OS: {Environment.OSVersion.VersionString}");

var watch = Stopwatch.StartNew();
long oldFiles = 0, oldBytes = 0;
foreach (var file in Storage.Walk(target, CancellationToken.None, () => { }))
    try { oldBytes += file.Length; oldFiles++; } catch (IOException) { }
var oldTime = watch.Elapsed;
Console.WriteLine($"Old walker (1.0):     {oldFiles,12:N0} files  {Storage.Format(oldBytes),10}  {oldTime.TotalSeconds,7:F2} s");

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
long before = GC.GetTotalMemory(true);
var result = StandardScanner.Scan(target, new([]), null, CancellationToken.None);
long after = GC.GetTotalMemory(true);
long nodes = result.Nodes.Count;
Console.WriteLine($"StandardScanner:      {result.Nodes.Files(0),12:N0} files  {Storage.Format(result.Nodes.Size(0)),10}  {result.Elapsed.TotalSeconds,7:F2} s  ({nodes:N0} nodes, ~{(after - before) / Math.Max(1, nodes)} bytes per node, skipped {result.Skipped:N0})");
watch.Restart();
var again = StandardScanner.Scan(target, new([]), null, CancellationToken.None);
Console.WriteLine($"StandardScanner warm: {again.Nodes.Files(0),12:N0} files  {Storage.Format(again.Nodes.Size(0)),10}  {again.Elapsed.TotalSeconds,7:F2} s");

var bounds = new TreemapRect(0, 0, 1280, 800);
foreach (double minSide in new[] { 3.0, 1.0 })
{
    watch.Restart();
    var cells = Treemap.Layout(result.Nodes, 0, bounds, false, maxDepth: 12, minSide: minSide);
    var layoutTime = watch.Elapsed;
    double buildMs = 0, renderMs = 0;
    var thread = new Thread(() =>
    {
        var brushes = Enumerable.Range(0, 8).Select(i => { var b = new SolidColorBrush(Color.FromRgb((byte)(40 + i * 25), 120, 160)); b.Freeze(); return b; }).ToArray();
        var pen = new Pen(Brushes.Black, 1); pen.Freeze();
        var sw = Stopwatch.StartNew();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            foreach (var cell in cells.Take(10000))
                dc.DrawRectangle(brushes[cell.Depth % brushes.Length], pen, new Rect(cell.Rect.X, cell.Rect.Y, cell.Rect.Width, cell.Rect.Height));
        buildMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        renderMs = sw.Elapsed.TotalMilliseconds;
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    Console.WriteLine($"Treemap, min side {minSide} px: {cells.Count,8:N0} cells · layout {layoutTime.TotalMilliseconds,6:F1} ms · drawing {Math.Min(10000, cells.Count):N0} rects {buildMs,6:F1} ms · software raster {renderMs,6:F1} ms");
}
if (synthetic != null) Console.WriteLine($"Synthetic tree kept in {synthetic}; delete it when done.");
