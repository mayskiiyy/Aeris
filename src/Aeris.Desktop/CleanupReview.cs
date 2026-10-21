using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;

namespace Aeris.Desktop;

internal sealed class CleanupReview : Window
{
    internal CleanupReview(CleanupPlan plan)
    {
        Title = Loc.Get("Caches.CleanSelected");
        Width = 760; Height = 580; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var summary = new TextBlock { Text = Loc.Format("Desktop.ReviewSummary", plan.Count, Storage.Format(plan.Bytes)), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var accept = new Button { Content = Loc.Get("Desktop.Quarantine"), HorizontalAlignment = HorizontalAlignment.Right };
        void Refresh() { summary.Text = Loc.Format("Desktop.ReviewSummary", plan.Count, Storage.Format(plan.Bytes)); accept.IsEnabled = plan.Count > 0; }
        var list = new ListBox
        {
            ItemsSource = plan.Included.ToList(),
            ItemTemplate = new FuncDataTemplate<PlanItem>((item, _) =>
            {
                if (item == null) return new TextBlock();
                var check = new CheckBox { Content = $"{item.DisplaySize}   {item.Path}" };
                check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(PlanItem.Included)) { Source = item, Mode = BindingMode.TwoWay });
                check.IsCheckedChanged += (_, _) => Refresh();
                return check;
            }),
        };
        var cancel = new Button { Content = Loc.Get("Desktop.Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        accept.Click += (_, _) => Close(true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Children = { cancel, accept } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(24), RowSpacing = 18 };
        var warnings = new TextBlock { Text = string.Join("\n", plan.Categories.Where(c => c.HasWarning).Select(c => c.Warning)), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Grid.SetRow(warnings, 1); Grid.SetRow(list, 2); Grid.SetRow(actions, 3);
        grid.Children.Add(summary); grid.Children.Add(warnings); grid.Children.Add(list); grid.Children.Add(actions);
        Content = grid;
    }
}
