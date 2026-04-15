using System.Windows;
using System.Windows.Controls;
using CommTestTool.Domain.Models;

namespace CommTestTool.Views;

public partial class ScenarioStepPickWindow : Window
{
    public ScenarioStepModel? Result { get; private set; }

    public ScenarioStepPickWindow(List<CommandModel> commands)
    {
        InitializeComponent();
        CommandCombo.ItemsSource = commands;
        if (commands.Any()) CommandCombo.SelectedIndex = 0;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (CommandCombo.SelectedItem is not CommandModel cmd)
        { MessageBox.Show("コマンドを選択してください。"); return; }
        var onError = (OnErrorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "stop";
        var desc    = DescBox.Text.Trim();
        Result = new ScenarioStepModel("command",
            string.IsNullOrEmpty(desc) ? cmd.Name : desc,
            cmd.Id, OnError: onError);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
