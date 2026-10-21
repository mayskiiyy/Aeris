using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aeris.Tests;

public class SimilarImagesTests
{
    /// <summary>A photo-like test scene: a gradient sky, a few shapes and some texture.</summary>
    private static BitmapSource Scene(int seed, int width = 800, int height = 600)
    {
        var random = new Random(seed);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)random.Next(256), 80, 160), Color.FromRgb(240, (byte)random.Next(256), 90), 90), null, new Rect(0, 0, width, height));
            for (int i = 0; i < 14; i++)
            {
                var brush = new SolidColorBrush(Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
                dc.DrawEllipse(brush, null, new Point(random.Next(width), random.Next(height)), random.Next(30, width / 3), random.Next(30, height / 3));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource image, string path, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception(failure.Message, failure);
    }

    [Fact]
    public void ResizedResavedAndSlightlyCroppedCopiesGroupTogether()
    {
        using var temp = new TempDir();
        Sta(() =>
        {
            var original = Scene(1);
            Save(original, Path.Combine(temp.Path, "original.png"), new PngBitmapEncoder());
            Save(original, Path.Combine(temp.Path, "resaved.jpg"), new JpegBitmapEncoder { QualityLevel = 60 });
            Save(new TransformedBitmap(original, new ScaleTransform(0.4, 0.4)), Path.Combine(temp.Path, "small.png"), new PngBitmapEncoder());
            Save(new CroppedBitmap(original, new Int32Rect(16, 12, 768, 576)), Path.Combine(temp.Path, "cropped.jpg"), new JpegBitmapEncoder { QualityLevel = 85 });
            Save(Scene(2), Path.Combine(temp.Path, "other.png"), new PngBitmapEncoder());
            Save(Scene(3), Path.Combine(temp.Path, "third.jpg"), new JpegBitmapEncoder());
        });
        File.WriteAllText(Path.Combine(temp.Path, "broken.jpg"), "not an image");
        int skipped = 0;
        var groups = SimilarImageSearch.Find([temp.Path], [], 5, 0, CancellationToken.None, _ => { }, () => skipped++);
        var group = Assert.Single(groups);
        Assert.Equal(["cropped.jpg", "original.png", "resaved.jpg", "small.png"], group.Select(i => i.Name).Order());
        Assert.Equal(1, skipped);
        Assert.True(group[0].Best);
        Assert.Equal(800, group[0].Width);
        Assert.Equal("original.png", group[0].Name);
    }

    [Fact]
    public void CachedHashesAreReusedAndInvalidatedOnChange()
    {
        using var temp = new TempDir();
        string file = Path.Combine(temp.Path, "a.png");
        Sta(() => Save(Scene(5), file, new PngBitmapEncoder()));
        HashCache.Folder = Path.Combine(temp.Path, "cache");
        try
        {
            var first = HashCache.Load("dhash");
            SimilarImageSearch.Find([temp.Path], [], 5, 0, CancellationToken.None, _ => { }, () => { }, first);
            var second = HashCache.Load("dhash");
            SimilarImageSearch.Find([temp.Path], [], 5, 0, CancellationToken.None, _ => { }, () => { }, second);
            Assert.Equal(1, second.Hits);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
            var third = HashCache.Load("dhash");
            SimilarImageSearch.Find([temp.Path], [], 5, 0, CancellationToken.None, _ => { }, () => { }, third);
            Assert.Equal(0, third.Hits);
        }
        finally { HashCache.Folder = HashCache.DefaultFolder; }
    }

    [Fact]
    public void BkTreeMatchesBruteForce()
    {
        var random = new Random(42);
        var hashes = new List<ulong>();
        for (int i = 0; i < 300; i++)
        {
            ulong value = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32);
            hashes.Add(value);
            if (i % 3 == 0) hashes.Add(value ^ (1UL << random.Next(64)) ^ (1UL << random.Next(64)));
        }
        var tree = new BkTree<int>(i => hashes[i]);
        for (int i = 0; i < hashes.Count; i++) tree.Add(i);
        foreach (int threshold in new[] { 0, 3, 5, 12 })
        {
            var expected = SimilarImageSearch.BruteForce(hashes, threshold).ToHashSet();
            var actual = new HashSet<(int, int)>();
            for (int i = 0; i < hashes.Count; i++)
                foreach (int j in tree.Search(hashes[i], threshold))
                    if (i < j) actual.Add((i, j));
            Assert.Equal(expected.Order(), actual.Order());
        }
    }
}
