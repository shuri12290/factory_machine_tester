using System.Windows;
using System.Windows.Controls;
using CommTestTool.Domain.Models;

namespace CommTestTool.Views;

public partial class ConnectionEditWindow : Window
{
    public ConnectionModel? Result { get; private set; }
    private bool _ready;

    public ConnectionEditWindow(ConnectionModel? existing = null)
    {
        InitializeComponent();
        _ready = false;

        // プロトコル選択を設定
        var proto = existing?.Protocol ?? "opcua";
        foreach (ComboBoxItem item in ProtocolCombo.Items)
            if (item.Content?.ToString() == proto) { ProtocolCombo.SelectedItem = item; break; }

        if (existing != null)
        {
            IdBox.Text       = existing.Id;
            TimeoutBox.Text  = existing.TimeoutMs.ToString();
            EndpointBox.Text = existing.Endpoint ?? "";
            BrokerBox.Text   = existing.Broker ?? "";
            MqttPortBox.Text = existing.Port?.ToString() ?? "1883";
            PubTopicBox.Text = existing.PublishTopic ?? "";
            SubTopicBox.Text = existing.SubscribeTopic ?? "";
            HostBox.Text     = existing.Host ?? "";
            PortBox.Text     = existing.Port?.ToString() ?? "";
            SlmpHostBox.Text   = existing.Host ?? "";
            SlmpPortBox.Text  = existing.Port?.ToString() ?? "";
        }

        _ready = true;
        UpdatePanels(proto);
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdatePanels((ProtocolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "");
    }

    private void UpdatePanels(string protocol)
    {
        if (EndpointPanel == null) return;
        EndpointPanel.Visibility = Visibility.Collapsed;
        MqttPanel.Visibility     = Visibility.Collapsed;
        HostPortPanel.Visibility = Visibility.Collapsed;
        SlmpPanel.Visibility   = Visibility.Collapsed;
        StubWarn.Visibility      = Visibility.Collapsed;

        switch (protocol)
        {
            case "opcua":
            case "mtconnect":
                EndpointPanel.Visibility = Visibility.Visible; break;
            case "mqtt":
                MqttPanel.Visibility = Visibility.Visible; break;
            case "tcp":
                HostPortPanel.Visibility = Visibility.Visible; break;
            case "slmp":
                SlmpPanel.Visibility = Visibility.Visible;
                StubWarn.Visibility    = Visibility.Visible; break;
            case "focas2":
            case "ospapi":
                HostPortPanel.Visibility = Visibility.Visible;
                StubWarn.Visibility      = Visibility.Visible; break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var proto  = (ProtocolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(IdBox.Text))  errors.Add("・接続口IDを入力してください。");
        if (string.IsNullOrWhiteSpace(proto))        errors.Add("・プロトコルを選択してください。");

        int? timeout = int.TryParse(TimeoutBox.Text, out var t) ? t : 5000;

        switch (proto)
        {
            case "opcua": case "mtconnect":
                if (string.IsNullOrWhiteSpace(EndpointBox.Text))
                    errors.Add("・エンドポイントURIを入力してください。"); break;
            case "mqtt":
                if (string.IsNullOrWhiteSpace(BrokerBox.Text))   errors.Add("・ブローカーを入力してください。");
                if (string.IsNullOrWhiteSpace(PubTopicBox.Text)) errors.Add("・送信トピックを入力してください。");
                if (string.IsNullOrWhiteSpace(SubTopicBox.Text)) errors.Add("・受信トピックを入力してください。");
                break;
            case "tcp": case "focas2": case "ospapi":
                if (string.IsNullOrWhiteSpace(HostBox.Text)) errors.Add("・ホストを入力してください。");
                if (string.IsNullOrWhiteSpace(PortBox.Text)) errors.Add("・ポートを入力してください。"); break;
            case "slmp":
                if (string.IsNullOrWhiteSpace(SlmpHostBox.Text)) errors.Add("・IPアドレスを入力してください。");
                if (string.IsNullOrWhiteSpace(SlmpPortBox.Text)) errors.Add("・局番号を入力してください。"); break;
        }

        if (errors.Any())
        {
            ErrText.Text = string.Join("\n", errors);
            ErrBanner.Visibility = Visibility.Visible;
            return;
        }

        Result = proto switch
        {
            "opcua" or "mtconnect" => new ConnectionModel(
                IdBox.Text.Trim(), proto, timeout ?? 5000,
                Endpoint: EndpointBox.Text.Trim()),
            "mqtt" => new ConnectionModel(
                IdBox.Text.Trim(), proto, timeout ?? 5000,
                Broker: BrokerBox.Text.Trim(),
                Port: int.TryParse(MqttPortBox.Text, out var mp) ? mp : 1883,
                PublishTopic: PubTopicBox.Text.Trim(),
                SubscribeTopic: SubTopicBox.Text.Trim()),
            "slmp" => new ConnectionModel(
                IdBox.Text.Trim(), proto, timeout ?? 5000,
                Host: SlmpHostBox.Text.Trim(),
                Port: int.TryParse(SlmpPortBox.Text, out var sn) ? sn : null),
            _ => new ConnectionModel(
                IdBox.Text.Trim(), proto, timeout ?? 5000,
                Host: HostBox.Text.Trim(),
                Port: int.TryParse(PortBox.Text, out var p) ? p : null)
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
