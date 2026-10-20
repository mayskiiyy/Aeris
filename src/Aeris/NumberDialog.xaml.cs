using System.Globalization;
using System.Windows;
using System.Windows.Interop;

namespace Aeris;

public partial class NumberDialog : Window
{
    private readonly int minimum;
    private readonly int maximum;
    public int Value { get; private set; }

    public NumberDialog(string prompt, int initial, int minimum, int maximum)
    {
        InitializeComponent();
        this.minimum = minimum;
        this.maximum = maximum;
        Prompt.Text = prompt;
        Input.Text = initial.ToString(CultureInfo.InvariantCulture);
        Error.Text = Loc.Format("Number.Range", minimum, maximum);
        SourceInitialized += (_, _) =>
        {
            int dark = ((App)Application.Current).DarkTheme ? 1 : 0;
            MainWindow.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        if (!TryValue(Input.Text, minimum, maximum, out int value))
        { Error.Text = Loc.Format("Number.Invalid", minimum, maximum); Input.Focus(); return; }
        Value = value;
        DialogResult = true;
    }

    public static bool TryValue(string text, int minimum, int maximum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
}
