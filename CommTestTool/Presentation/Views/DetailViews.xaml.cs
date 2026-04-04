using System.Windows.Controls;

namespace CommTestTool.Views;

public partial class DeviceDetailView : UserControl
{
    public DeviceDetailView() => InitializeComponent();

}
public partial class CommandDetailView  : UserControl { public CommandDetailView()  => InitializeComponent(); }
public partial class ScenarioDetailView : UserControl { public ScenarioDetailView() => InitializeComponent(); }

public class EqualityConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, System.Globalization.CultureInfo c)
        => values.Length == 2 && Equals(values[0], values[1]);
    public object[] ConvertBack(object v, Type[] t, object p, System.Globalization.CultureInfo c)
        => throw new NotImplementedException();
}
public partial class ConnectionDetailView : System.Windows.Controls.UserControl
{
    public ConnectionDetailView()
    {
        InitializeComponent();
        // DataContextが設定された後にPasswordBoxの初期値を反映する
        DataContextChanged += (_, _) => SyncPasswordFromVm();
    }

    private void SyncPasswordFromVm()
    {
        if (DataContext is CommTestTool.Presentation.ViewModels.ConnectionEditItem vm)
        {
            OpcPasswordBox.Password = vm.OpcPassword;
            // PasswordBox変更をVMに反映（PasswordChangedはXAMLでバインドできないためcode-behindで対応）
            OpcPasswordBox.PasswordChanged -= OnPasswordChanged;
            OpcPasswordBox.PasswordChanged += OnPasswordChanged;
            OpcCertPasswordBox.Password = vm.OpcCertPassword;
            OpcCertPasswordBox.PasswordChanged -= OnCertPasswordChanged;
            OpcCertPasswordBox.PasswordChanged += OnCertPasswordChanged;
        }
    }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CommTestTool.Presentation.ViewModels.ConnectionEditItem vm)
            vm.OpcPassword = OpcPasswordBox.Password;
    }

    private void OnCertPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CommTestTool.Presentation.ViewModels.ConnectionEditItem vm)
            vm.OpcCertPassword = OpcCertPasswordBox.Password;
    }

    private void BrowseCert_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "証明書ファイルを選択",
            Filter = "証明書ファイル|*.pfx;*.p12;*.pem;*.cer;*.der|全てのファイル|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        // ファイル名のみ取得（フルパスは保存しない）
        if (DataContext is CommTestTool.Presentation.ViewModels.ConnectionEditItem vm)
            vm.OpcCertFile = System.IO.Path.GetFileName(dlg.FileName);
    }
}
