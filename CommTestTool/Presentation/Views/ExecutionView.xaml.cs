using System.Windows.Controls;

namespace CommTestTool.Views;

public partial class ExecutionView : UserControl
{
    public ExecutionView()
    {
        InitializeComponent();

        // DataContextが設定されたタイミングでMonitorGridのDataContextを設定
        // TabItemの内側ではElementNameもRelativeSourceも効かないためcode-behindで解決
        DataContextChanged += (_, _) =>
        {
            if (DataContext is Presentation.ViewModels.ExecutionViewModel vm)
                MonitorGrid.DataContext = vm.MonitorVM;
        };
    }
}
