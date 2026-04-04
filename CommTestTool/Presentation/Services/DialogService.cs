using System.Windows;
using Microsoft.Win32;
using CommTestTool.Domain.Interfaces;

namespace CommTestTool.Presentation.Services;

public class DialogService : IDialogService
{
    public bool Confirm(string message, string title = "確認") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public void Info(string message, string title = "完了") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string title = "エラー") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public T? ShowDialog<T>(Func<T> create) where T : class
    {
        var dlg = create();
        if (dlg is Window win)
        {
            win.Owner = System.Windows.Application.Current.MainWindow;
            win.ShowDialog();
        }
        return dlg;
    }

    public string? OpenFileDialog(string title,
        string filter = "YAMLファイル|*.yaml|全てのファイル|*.*")
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? SaveFileDialog(string title, string defaultFileName,
        string filter = "YAMLファイル|*.yaml|全てのファイル|*.*")
    {
        var dlg = new SaveFileDialog
        {
            Title    = title,
            FileName = defaultFileName,
            Filter   = filter,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}