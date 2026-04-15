using System.Windows;

namespace CommTestTool.Views;

public partial class WaitStepWindow : Window
{
    public int DurationMs { get; private set; } = 2000;
    public WaitStepWindow() => InitializeComponent();

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DurationBox.Text, out var ms) || ms <= 0)
        { MessageBox.Show("1以上の数値を入力してください。"); return; }
        DurationMs = ms;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
