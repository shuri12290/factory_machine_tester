using System.Windows;
using System.Windows.Controls;
using CommTestTool.Presentation.ViewModels;

namespace CommTestTool.Views;

public partial class ExecutionView : UserControl
{
    public ExecutionView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExecutionViewModel vm)
                MonitorGrid.DataContext = vm.MonitorVM;
        };
    }

    // ── integer/long UpDown ──────────────────────────────────────────────────
    private void ParamIncrement_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamValueItem item)
            item.Increment();
    }
    private void ParamDecrement_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamValueItem item)
            item.Decrement();
    }

    // ── byte/word/dword/uint UpDown ─────────────────────────────────────────
    private void ParamIncrementUint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamValueItem item)
            item.IncrementUint();
    }
    private void ParamDecrementUint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamValueItem item)
            item.DecrementUint();
    }

    // ── datetime: 現在時刻ボタン ────────────────────────────────────────────
    private void ParamNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParamValueItem item)
            item.Value = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    }
}
