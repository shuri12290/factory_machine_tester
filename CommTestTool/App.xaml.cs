using System.Globalization;
using System.Windows.Data;

using CommTestTool.Domain.Interfaces;
using CommTestTool.Infrastructure;
using CommTestTool.Infrastructure.Yaml;
using CommTestTool.Presentation.Services;
using CommTestTool.Presentation.ViewModels;

namespace CommTestTool;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        IAppPaths paths = new AppPaths();
        var log    = new CommTestTool.Application.Services.LogService(paths);
        var comm   = new CommTestTool.Application.Services.CommunicationManager(log, paths);
        var dialog = new DialogService();
        IDeviceRepository deviceRepo = new YamlDeviceRepository(paths);

        var mainVM = new MainViewModel(deviceRepo, dialog, paths, log, comm);
        var win = new Views.MainWindow { DataContext = mainVM };

        // アプリ終了時にエクスポートを促す
        win.Closing += (_, closeArgs) =>
        {
            var result = System.Windows.MessageBox.Show(
                "アプリを終了します。\n\n" +
                "⚠️ 設定（devices.yaml）はアプリフォルダ内に保存されています。\n" +
                "　 アプリの削除・PCの交換・再インストール時に失われる可能性があります。\n\n" +
                "📤 終了前にエクスポートしてバックアップを取りますか？",
                "終了確認",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Cancel)
            {
                closeArgs.Cancel = true;  // 終了キャンセル
                return;
            }
            if (result == System.Windows.MessageBoxResult.Yes)
            {
                mainVM.ExportCommand.Execute(null);  // エクスポートダイアログを開く
                // エクスポート後も終了（キャンセルしても終了する）
            }
        };

        win.Closed += async (_, _) =>
        {
            await mainVM.MonitorVM.DisposeAsync();
            await comm.DisposeAsync();
        };
        win.Show();
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is bool b && !b;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => v is bool b && !b;
}
public class BoolToVisConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
        => v is bool b && b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
        => v is System.Windows.Visibility vis && vis == System.Windows.Visibility.Visible;  // OneWayのみ使用
}
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
        => v == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
        => System.Windows.DependencyProperty.UnsetValue;  // OneWayのみ使用
}
public class StringEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
        => string.IsNullOrEmpty(v as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
        => System.Windows.DependencyProperty.UnsetValue;  // OneWayのみ使用
}
