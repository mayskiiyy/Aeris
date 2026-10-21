using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aeris;

/// <summary>A picture in the side-by-side strip. The thumbnail is decoded small, off the UI thread.</summary>
public class ImagePreview(SimilarImage image) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public SimilarImage Image { get; } = image;
    public ImageSource? Thumbnail { get; private set; }

    public async Task LoadAsync()
    {
        Thumbnail = await Task.Run(() =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Image.Path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (Image.Width >= Image.Height) bitmap.DecodePixelWidth = 400; else bitmap.DecodePixelHeight = 280;
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource?)bitmap;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { return null; }
        });
        PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
    }
}

public partial class MainWindow
{
    private List<SimilarImage> similar = [];
    private int previewGroup;
    private bool SimilarMode => DuplicateMode.SelectedIndex == 1;

    private DataGrid ActiveDuplicateGrid => SimilarMode ? SimilarGrid : DuplicatesGrid;

    private void DuplicateModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DuplicatesGrid == null) return;
        bool similarMode = SimilarMode;
        DuplicatesGrid.Visibility = similarMode ? Visibility.Collapsed : Visibility.Visible;
        SimilarGrid.Visibility = SimilarityPanel.Visibility = similarMode ? Visibility.Visible : Visibility.Collapsed;
        SimilarStrip.Visibility = similarMode && SimilarPreviews.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicateFooter.Text = Loc.Get(similarMode ? "Dup.SimilarFooter" : "Dup.Footer");
        RefreshSimilarityLabel();
        SetBusy(busy);
    }

    private void SimilarityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshSimilarityLabel();

    private void RefreshSimilarityLabel()
    {
        if (SimilarityLabel != null) SimilarityLabel.Text = Loc.Format("Dup.SimilarityValue", (int)SimilaritySlider.Value);
    }

    private async Task FindSimilar()
    {
        if (!Storage.SafePath(folder)) { Status.Text = Loc.Get("Dup.ChooseFolder"); return; }
        SaveSettings();
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        var exclusions = data.Exclusions.ToArray();
        int threshold = (int)SimilaritySlider.Value;
        string root = folder;
        SetBusy(true);
        Status.Text = Loc.Get("Similar.Searching");
        int skipped = 0;
        var progress = new Progress<string>(text => { if (busy && token == cancellation?.Token && !token.IsCancellationRequested) Status.Text = text; });
        try
        {
            var groups = await Task.Run(() => SimilarImageSearch.Find([root], exclusions, threshold, 0, token, ((IProgress<string>)progress).Report, () => Interlocked.Increment(ref skipped), HashCache.Load("dhash")), token);
            token.ThrowIfCancellationRequested();
            similar = groups.SelectMany(g => g).ToList();
            SimilarGrid.ItemsSource = similar;
            previewGroup = -1;
            ShowPreviews(similar.FirstOrDefault()?.Group ?? 0);
            Status.Text = Loc.Format("Similar.Result", groups.Count, skipped);
        }
        catch (OperationCanceledException) { Status.Text = Loc.Get("Dup.Cancelled"); }
        catch (Exception ex) { Status.Text = Loc.Format("Dup.Error", ex.Message); }
        finally { SetBusy(false); }
    }

    private void SimilarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SimilarGrid.SelectedItem is SimilarImage image) ShowPreviews(image.Group);
    }

    private void ShowPreviews(int group)
    {
        if (group == previewGroup && SimilarPreviews.Items.Count > 0) return;
        previewGroup = group;
        var previews = similar.Where(i => i.Group == group).Select(i => new ImagePreview(i)).ToList();
        SimilarPreviews.ItemsSource = previews;
        SimilarStrip.Visibility = SimilarMode && previews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var preview in previews) _ = preview.LoadAsync();
    }

    private void SelectNonBestImages()
    {
        SimilarGrid.SelectedItems.Clear();
        foreach (var image in similar.Where(i => !i.Best)) SimilarGrid.SelectedItems.Add(image);
        Status.Text = Loc.Format("Dup.SelectedExtra", similar.Count(i => !i.Best), Storage.Format(similar.Where(i => !i.Best).Sum(i => i.Size)));
        SimilarGrid.Focus();
    }

    private bool TrySimilarPlan(out CleanupPlan? plan)
    {
        plan = null;
        var selected = SimilarGrid.SelectedItems.OfType<SimilarImage>().ToArray();
        if (selected.Length == 0) { Status.Text = Loc.Get("Recycle.SelectFiles"); return false; }
        try { plan = Duplicates.Plan(selected, similar); return true; }
        catch (InvalidOperationException) { Status.Text = Loc.Get("Recycle.KeepOne"); return false; }
    }

    private void RevealSimilar()
    {
        if (SimilarGrid.SelectedItem is not SimilarImage image) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{image.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Status.Text = ex.Message; }
    }
}
