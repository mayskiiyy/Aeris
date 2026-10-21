using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Aeris;

/// <summary>
/// Shows a <see cref="CleanupPlan"/> before anything is deleted: categories, every file, search, sorting and
/// per-file exclusion. The plan is changed in place; nothing touches the disk here.
/// </summary>
public partial class PreviewWindow : Window
{
    private readonly CleanupPlan plan;
    private readonly int largeFiles;
    private readonly long largeBytes;
    private readonly int quarantineDays;
    private readonly DispatcherTimer refresh;
    private ICollectionView? view;

    public PreviewWindow(CleanupPlan plan, IEnumerable<string> warnings, int largeFiles, long largeBytes, int quarantineDays)
    {
        InitializeComponent();
        this.plan = plan;
        this.largeFiles = largeFiles;
        this.largeBytes = largeBytes;
        this.quarantineDays = quarantineDays;
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => RefreshTotals(), Dispatcher) { IsEnabled = false };
        bool recycle = plan.Mode == CleanupMode.Recycle;
        Heading.Text = Loc.Get(recycle ? "Preview.TitleRecycle" : "Preview.TitleDelete");
        Subheading.Text = plan.Cutoff == DateTime.MinValue ? Loc.Get("Preview.HintRecycle") : Loc.Format("Preview.HintCache", plan.Cutoff.ToLocalTime().ToString("g", Loc.Culture));
        var lines = warnings.Concat(plan.Categories.Where(c => c.HasWarning).Select(c => c.Warning)).Distinct().ToList();
        Warnings.Text = string.Join("\n", lines);
        WarningBox.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModePicker.Visibility = recycle ? Visibility.Collapsed : Visibility.Visible;
        ModePicker.SelectedIndex = plan.Mode == CleanupMode.Quarantine ? 1 : 0;
        CategoryList.ItemsSource = plan.Categories;
        CategoryList.SelectedIndex = 0;
        SourceInitialized += (_, _) =>
        {
            int dark = ((App)Application.Current).DarkTheme ? 1 : 0;
            MainWindow.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += (_, _) => ConfirmButton.Focus();
        RefreshTotals();
    }

    private void CategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not PlanCategory category) { ItemsGrid.ItemsSource = null; return; }
        view = new ListCollectionView(category.Items);
        ApplySort();
        ApplyFilter();
        ItemsGrid.ItemsSource = view;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (view == null) return;
        string text = Search.Text.Trim();
        view.Filter = text.Length == 0 ? null : o => o is PlanItem item && item.Path.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void SortChanged(object sender, SelectionChangedEventArgs e) => ApplySort();

    private void ApplySort()
    {
        if (view == null) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(SortOrder.SelectedIndex switch
            {
                1 => new SortDescription(nameof(PlanItem.Name), ListSortDirection.Ascending),
                2 => new SortDescription(nameof(PlanItem.Modified), ListSortDirection.Descending),
                _ => new SortDescription(nameof(PlanItem.Size), ListSortDirection.Descending),
            });
        }
    }

    private void SetShown(bool included)
    {
        if (view == null) return;
        foreach (var item in view.OfType<PlanItem>()) item.Included = included;
        Changed();
    }

    private void IncludeShown(object sender, RoutedEventArgs e) => SetShown(true);
    private void ExcludeShown(object sender, RoutedEventArgs e) => SetShown(false);

    private void GridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || ItemsGrid.SelectedItems.Count == 0) return;
        var items = ItemsGrid.SelectedItems.OfType<PlanItem>().ToList();
        bool include = !items.All(i => i.Included);
        foreach (var item in items) item.Included = include;
        Changed();
        e.Handled = true;
    }

    private void CategoryToggled(object sender, RoutedEventArgs e) => Changed();

    private void ItemToggled(object sender, RoutedEventArgs e) => Changed();

    private void Changed()
    {
        refresh.Stop();
        refresh.Start();
    }

    private void RefreshTotals()
    {
        refresh.Stop();
        foreach (var category in plan.Categories) category.Refresh();
        int count = plan.Count;
        long bytes = plan.Bytes;
        bool folders = plan.Included.Any(i => i.IsFolder);
        Totals.Text = count == 0 ? Loc.Get("Preview.NothingSelected")
            : folders ? Loc.Format("Preview.TotalsItems", count, Storage.Format(bytes)) : Loc.Format("Preview.Totals", count, Storage.Format(bytes));
        ModeHint.Text = plan.Mode switch
        {
            CleanupMode.Quarantine => Loc.Plural("Preview.ModeQuarantine", quarantineDays),
            CleanupMode.Recycle => Loc.Get("Preview.ModeRecycle"),
            _ => Loc.Get("Preview.ModePermanent"),
        };
        ConfirmButton.Content = Loc.Get(plan.Mode switch { CleanupMode.Quarantine => "Preview.ConfirmQuarantine", CleanupMode.Recycle => "Preview.ConfirmRecycle", _ => "Preview.ConfirmDelete" });
        ConfirmButton.IsEnabled = count > 0;
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModePicker.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<CleanupMode>(tag, out var mode)) plan.Mode = mode;
        if (IsLoaded) RefreshTotals();
    }

    private void RevealItem(object sender, MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not PlanItem item || e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) != null) return;
        string target = item.IsFolder ? item.Path : item.Directory;
        if (!Directory.Exists(target)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", item.IsFolder ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        RefreshTotals();
        if (plan.Count == 0) return;
        if (plan.Mode == CleanupMode.Recycle && !Duplicates.KeepsOne(plan)) { MessageBox.Show(this, Loc.Get("Recycle.KeepOne"), Heading.Text); return; }
        if (plan.IsLarge(largeFiles, largeBytes))
        {
            string message = Loc.Format("Preview.LargeConfirm", plan.Count, Storage.Format(plan.Bytes), Loc.Get($"Mode.{plan.Mode}"));
            if (MessageBox.Show(this, message, Heading.Text, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }
}
