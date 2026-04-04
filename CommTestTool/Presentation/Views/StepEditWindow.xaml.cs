using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommTestTool.Domain.Models;

namespace CommTestTool.Views;

public class CondRow    { public string Field { get; set; } = ""; public string Operator { get; set; } = "equals"; public string Value { get; set; } = ""; }
public class CaptureRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _field = "", _as = "";
    public string Field { get => _field; set { _field = value; Notify(nameof(Field)); } }
    public string As    { get => _as;    set { _as    = value; Notify(nameof(As));    } }
    /// <summary>変数名コンボのソース（パラメータ一覧 + 空欄）</summary>
    public List<string> ParamOptions { get; set; } = new();
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}
/// <summary>OPC-UA ノードリストの1行エントリ（TwoWayバインド用）</summary>
public class OpcNodeRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _nodeId = "", _paramName = "";
    public string NodeId    { get => _nodeId;    set { _nodeId    = value; Notify(nameof(NodeId));   } }
    public string ParamName { get => _paramName; set { _paramName = value; Notify(nameof(ParamName));} }
    /// <summary>一覧から選択コンボのソース（コンストラクタで _paramNames を渡す）</summary>
    public List<string> ParamOptions { get; set; } = new();
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public partial class StepEditWindow : Window
{
    public StepModel? Result { get; private set; }

    private readonly List<ConnectionModel> _connections;
    private readonly List<string>          _paramNames;
    private readonly ObservableCollection<CondRow>    _condRows     = [];
    private readonly ObservableCollection<CaptureRow> _captureRows  = [];
    private readonly ObservableCollection<CondRow>    _pollCondRows = [];   // pollの達成条件
    private readonly ObservableCollection<CaptureRow> _pollCapRows  = [];   // pollの変数化
    private readonly ObservableCollection<OpcNodeRow> _opcWriteNodes = [];
    private readonly ObservableCollection<OpcNodeRow> _opcReadNodes  = [];
    private readonly ObservableCollection<OpcNodeRow> _pollNodes     = [];
    private bool _ready;

    // プロトコルごとに使用できるアクション定義
    private static readonly Dictionary<string, string[]> ProtoActions = new()
    {
        ["tcp"]        = ["send", "receive"],
        ["mqtt"]       = ["send", "receive"],
        ["opcua"]      = ["write", "read", "poll"],
        ["mtconnect"]  = ["read", "poll"],       // 読み取り専用
        ["slmp"]       = ["write", "read", "poll"],
        // focas2/ospapi = 未実装 → 空 = 表示なし
        // focas2: 専用アクション（target指定で関数を切り替える）
        ["focas2"]     = ["focas_read", "focas_write", "poll"],
        ["ospapi"]     = [],
    };

    private static readonly Dictionary<string, string> ActionHints = new()
    {
        ["send"]    = "TCP/IP・MQTT へ JSON データを送信します。",
        ["receive"] = "TCP/IP・MQTT からデータを受信するまで待機します。",
        ["write"]      = "OPC-UA のノード / SLMP のアドレスにパラメータの値を書き込みます。",
        ["focas_read"]  = "FOCAS2 からデータを読み取ります。target に読み取り種別を指定します。",
        ["focas_write"] = "FOCAS2 にデータを書き込みます。target に書き込み種別を指定します。",
        ["read"]    = "OPC-UA・MTConnect・SLMP からデータを1回読み取ります。",
        ["poll"]    = "条件を満たすまで定期的に読み取り続けます。全時間はパラメータで指定します。",
        ["wait"]    = "指定時間だけ処理を停止します。待機時間はパラメータで指定します。",
    };

    public StepEditWindow(StepModel step, List<ConnectionModel> connections, List<string> paramNames)
    {
        _connections = connections;
        _paramNames  = paramNames;
        InitializeComponent();

        CondList.ItemsSource      = _condRows;
        CaptureList.ItemsSource   = _captureRows;
        PollCondList.ItemsSource  = _pollCondRows;
        PollCapList.ItemsSource   = _pollCapRows;
        // OPC-UA ノードリストの初期バインド（既存ステップ読み込み時に再設定される）
        OpcWriteNodeList.ItemsSource = _opcWriteNodes;
        OpcReadNodeList.ItemsSource  = _opcReadNodes;
        PollNodeList.ItemsSource     = _pollNodes;

        // 接続口コンボを構築（常に表示。waitのみ接続口不要だがリストには出す）
        foreach (var c in connections)
            ConnCombo.Items.Add(new ComboBoxItem { Content = $"{c.Id}  [{c.Protocol}]", Tag = c });

        // poll用接続口コンボ
        foreach (var c in connections)
            PollConnCombo.Items.Add(new ComboBoxItem { Content = $"{c.Id}  [{c.Protocol}]", Tag = c });

        BuildParamCombos();
        LoadExistingConditions(step.Conditions);
        LoadExistingCaptures(step.Capture);

        // 新規ステップ用の初期空行（既存ステップ読み込み時は後でクリアして再設定）
        if (_opcWriteNodes.Count == 0) _opcWriteNodes.Add(new OpcNodeRow { ParamOptions = _paramNames });
        if (_opcReadNodes.Count == 0)  _opcReadNodes.Add(new OpcNodeRow());
        if (_pollNodes.Count == 0)     _pollNodes.Add(new OpcNodeRow());

        _ready = false;

        // 初期値復元
        DescBox.Text            = step.Description ?? "";
        DurationBox.Text        = step.DurationMsParam ?? "";
        TimeoutBox.Text         = step.TimeoutMsParam ?? step.TimeoutMs?.ToString() ?? "";
        // OPC-UA 書き込みノードリスト初期化
        _opcWriteNodes.Clear();   // コンストラクタで追加した空行をクリア
        OpcWriteNodeList.ItemsSource = _opcWriteNodes;
        if (step.Nodes?.Count > 1)
        {
            foreach (var n in step.Nodes)
                _opcWriteNodes.Add(new OpcNodeRow { NodeId = n.NodeId, ParamName = n.Parameter ?? "", ParamOptions = _paramNames });
        }
        else
        {
            _opcWriteNodes.Add(new OpcNodeRow { NodeId = step.NodeId ?? "", ParamName = step.Parameter ?? "", ParamOptions = _paramNames });
        }
        SlmpWriteAddrBox.Text   = step.Address ?? "";
        SlmpWriteValueBox.Text  = step.Parameter ?? "";
        // OPC-UA 読み取りノードリスト初期化
        _opcReadNodes.Clear();   // コンストラクタで追加した空行をクリア
        OpcReadNodeList.ItemsSource = _opcReadNodes;
        if (step.Nodes?.Count > 1)
        {
            foreach (var n in step.Nodes)
                _opcReadNodes.Add(new OpcNodeRow { NodeId = n.NodeId });
        }
        else
        {
            _opcReadNodes.Add(new OpcNodeRow { NodeId = step.NodeId ?? "" });
        }
        MtcPathBox.Text         = step.NodeId ?? "";
        MtcXPathBox.Text        = step.Parse?.XPath ?? "";
        SlmpReadAddrBox.Text    = step.Address ?? "";
        XPathBox.Text           = step.Parse?.XPath ?? "";
        // pollノードリスト初期化
        _pollNodes.Clear();
        if (step.Nodes?.Count > 1)
        {
            foreach (var n in step.Nodes)
                _pollNodes.Add(new OpcNodeRow { NodeId = n.NodeId });
        }
        else
        {
            _pollNodes.Add(new OpcNodeRow { NodeId = step.NodeId ?? step.Address ?? "" });
        }
        // FOCAS2 read: targetからコンボ選択を復元
        var focasReadTarget = step.Action is "focas_read" or "poll" ? step.NodeId ?? "" : "";
        RestoreFocasReadCombo(focasReadTarget);

        // FOCAS2 write: targetからコンボ選択を復元
        var focasWriteTarget = step.Action == "focas_write" ? step.NodeId ?? "" : "";
        RestoreFocasWriteCombo(focasWriteTarget);
        FocasWriteValueBox.Text = step.Action == "focas_write"
            ? (step.Payload?.TryGetValue("value", out var fv) == true ? fv : "") : "";
        PollIntervalBox.Text    = step.IntervalMsParam  ?? "";
        PollReadTimeoutBox.Text = step.ReadTimeoutParam ?? "";
        PollTimeoutBox.Text     = step.TimeoutParam ?? "";
        // poll条件・変数化をリストに復元
        _pollCondRows.Clear();
        foreach (var c in step.Conditions ?? [])
            _pollCondRows.Add(c.Operator == "bit_check"
                ? new CondRow { Field = "", Operator = "bit_check", Value = c.Bit?.ToString() ?? "0" }
                : new CondRow { Field = c.Field ?? "", Operator = c.Operator, Value = c.Value });
        _pollCapRows.Clear();
        foreach (var c in step.Capture ?? [])
            _pollCapRows.Add(new CaptureRow { Field = c.Field, As = c.As, ParamOptions = _paramNames });

        if (step.Payload != null && step.Payload.Count > 0)
            SendPayloadBox.Text = string.Join("\n", step.Payload.Select(kv => $"{kv.Key}: {kv.Value}"));

        SetCombo(ParseCombo, step.Parse?.Format ?? "plain");
        SelectConn(ConnCombo,     step.ConnectionId);
        SelectConn(PollConnCombo, step.ConnectionId);

        // 接続口IDが未設定（新規ステップ）かつ接続口が存在する場合は最初を自動選択
        if (string.IsNullOrEmpty(step.ConnectionId) && connections.Count > 0)
        {
            foreach (ComboBoxItem item in ConnCombo.Items)
                if (item.Tag is ConnectionModel) { ConnCombo.SelectedItem = item; break; }
            foreach (ComboBoxItem item in PollConnCombo.Items)
                if (item.Tag is ConnectionModel) { PollConnCombo.SelectedItem = item; break; }
        }

        _ready = true;

        // 接続口に応じてアクションコンボを構築してから選択
        UpdateActionCombo(SelectedConn(ConnCombo));
        SetCombo(ActionCombo, string.IsNullOrEmpty(step.Action) ? "" : step.Action);
        UpdatePanels(SelectedConn(ConnCombo), SelectedAction());
    }

    // ── アクションコンボをプロトコルに応じて構築 ──
    private void UpdateActionCombo(ConnectionModel? conn)
    {
        ActionCombo.Items.Clear();
        StubNotice.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;

        if (conn == null)
        {
            // 接続口未選択 → waitのみ
            ActionCombo.Items.Add(new ComboBoxItem { Content = "wait", Tag = "wait" });
            ActionPanel.Visibility = Visibility.Visible;
            ActionCombo.SelectedIndex = 0;
            return;
        }

        var proto = conn.Protocol;
        if (!ProtoActions.TryGetValue(proto, out var actions) || actions.Length == 0)
        {
            // 未実装プロトコル
            StubNoticeText.Text = $"⚠️ プロトコル「{proto}」は現在未実装です。\nSDK/DLLを入手後に実装予定です。ステップを登録することはできません。";
            StubNotice.Visibility = Visibility.Visible;
            return;
        }

        ActionPanel.Visibility = Visibility.Visible;
        // waitは接続口不要なので常に追加
        ActionCombo.Items.Add(new ComboBoxItem { Content = "wait", Tag = "wait" });
        foreach (var a in actions)
            ActionCombo.Items.Add(new ComboBoxItem { Content = a, Tag = a });
        ActionCombo.SelectedIndex = 0;
    }

    // ── ハンドラー ──
    private void ConnCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var conn = SelectedConn(ConnCombo);
        ConnSelectPanel.Visibility = Visibility.Visible;
        UpdateActionCombo(conn);
        UpdatePanels(conn, SelectedAction());
    }

    private void ActionCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var action = SelectedAction();
        ActionHint.Text = action != null ? ActionHints.GetValueOrDefault(action, "") : "";
        UpdatePanels(SelectedConn(ConnCombo), action);
    }

    private void PollConnCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        var conn = SelectedConn(PollConnCombo);
        PollTargetHint.Text = conn?.Protocol switch
        {
            "opcua"  => "例: ns=2;s=MachineStatus",
            "slmp"   => "例: D100（データレジスタ）/ W200（リンクレジスタ）",
            _        => ""
        };
    }

    private void ParseCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_ready || XPathPanel == null) return;
        XPathPanel.Visibility = (ParseCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "xml"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DurationParamCombo_Changed(object s, SelectionChangedEventArgs e)      => ApplyParamCombo(DurationParamCombo, DurationBox);
    private void TimeoutParamCombo_Changed(object s, SelectionChangedEventArgs e)        => ApplyParamCombo(TimeoutParamCombo, TimeoutBox);
    private void SlmpWriteParamCombo_Changed(object s, SelectionChangedEventArgs e)      => ApplyParamCombo(SlmpWriteParamCombo, SlmpWriteValueBox);
    private void PollIntervalParamCombo_Changed(object s, SelectionChangedEventArgs e)   => ApplyParamCombo(PollIntervalParamCombo, PollIntervalBox);
    private void PollReadTimeoutParamCombo_Changed(object s, SelectionChangedEventArgs e)=> ApplyParamCombo(PollReadTimeoutParamCombo, PollReadTimeoutBox);
    private void PollTimeoutParamCombo_Changed(object s, SelectionChangedEventArgs e)    => ApplyParamCombo(PollTimeoutParamCombo, PollTimeoutBox);

    private void AddCond_Click(object s, RoutedEventArgs e)    => _condRows.Add(new CondRow());
    private void RemoveCond_Click(object s, RoutedEventArgs e) { if ((s as Button)?.Tag is CondRow r) _condRows.Remove(r); }
    private void CaptureAsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string param
            && combo.Tag is CaptureRow row && !string.IsNullOrEmpty(param))
            row.As = param;
    }

    private void AddCapture_Click(object s, RoutedEventArgs e)    => _captureRows.Add(new CaptureRow { ParamOptions = _paramNames });
    private void RemoveCapture_Click(object s, RoutedEventArgs e) { if ((s as Button)?.Tag is CaptureRow r) _captureRows.Remove(r); }

    // ── パネル切り替え ──
    private void UpdatePanels(ConnectionModel? conn, string? action)
    {
        foreach (var p in new FrameworkElement[]
            { WaitPanel, PollPanel, OpcWritePanel, SlmpWritePanel, TcpSendPanel,
              OpcReadPanel, MtcReadPanel, SlmpReadPanel, TcpRecvPanel,
              TimeoutPanel, CondPanel, CapturePanel,
              FocasReadPanel, FocasWritePanel })   // ← 追加
            p.Visibility = Visibility.Collapsed;

        if (action == null) return;
        var proto = conn?.Protocol ?? "";

        switch (action)
        {
            case "wait":
                WaitPanel.Visibility = Visibility.Visible;
                break;
            case "send":
                TcpSendPanel.Visibility = Visibility.Visible;
                break;
            case "write":
                (proto == "slmp" ? (FrameworkElement)SlmpWritePanel : OpcWritePanel).Visibility = Visibility.Visible;
                break;
            case "receive":
                TcpRecvPanel.Visibility = TimeoutPanel.Visibility =
                    CondPanel.Visibility = CapturePanel.Visibility = Visibility.Visible;
                break;
            case "read":
                (proto switch
                {
                    "opcua"     => (FrameworkElement)OpcReadPanel,
                    "mtconnect" => MtcReadPanel,
                    "slmp"      => SlmpReadPanel,
                    _           => OpcReadPanel
                }).Visibility = Visibility.Visible;
                TimeoutPanel.Visibility = CondPanel.Visibility = CapturePanel.Visibility = Visibility.Visible;
                break;
            case "focas_read":
                FocasReadPanel.Visibility = TimeoutPanel.Visibility =
                    CondPanel.Visibility = CapturePanel.Visibility = Visibility.Visible;
                break;
            case "focas_write":
                FocasWritePanel.Visibility = Visibility.Visible;
                break;
            case "poll":
                if (proto == "focas2")
                    FocasReadPanel.Visibility = Visibility.Visible;
                PollPanel.Visibility = Visibility.Visible;
                break;
        }

        // 接続口は常に表示
        ConnSelectPanel.Visibility = Visibility.Visible;
    }

    // ── 保存 ──

    // ── FOCAS2 コンボ変更ハンドラ ─────────────────────────────
    private void FocasReadTypeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateFocasReadPanels();
        BuildFocasReadTarget();
    }

    private void FocasWriteTypeCombo_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateFocasWritePanels();
        BuildFocasWriteTarget();
    }

    private void UpdateFocasReadPanels()
    {
        var tag = (FocasReadTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FocasMacroPanel.Visibility   = tag == "macro" ? Visibility.Visible : Visibility.Collapsed;
        FocasParamPanel.Visibility   = tag == "param" ? Visibility.Visible : Visibility.Collapsed;
        FocasPmcReadPanel.Visibility = tag == "pmc"   ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFocasWritePanels()
    {
        var tag = (FocasWriteTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FocasWriteMacroPanel.Visibility = tag == "macro" ? Visibility.Visible : Visibility.Collapsed;
        FocasWritePmcPanel.Visibility   = tag == "pmc"   ? Visibility.Visible : Visibility.Collapsed;
    }

    // FocasTargetBoxに完成したtarget文字列を組み立てて入れる
    private void BuildFocasReadTarget()
    {
        var tag = (FocasReadTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FocasTargetBox.Text = tag switch
        {
            "macro" => $"macro:{FocasMacroNoBox.Text.Trim()}",
            "param" => $"param:{FocasParamNoBox.Text.Trim()}",
            "pmc"   => $"pmc:{FocasPmcReadAddrBox.Text.Trim()}",
            _       => tag
        };
    }

    private void BuildFocasWriteTarget()
    {
        var tag = (FocasWriteTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FocasWriteTargetBox.Text = tag switch
        {
            "macro" => $"macro:{FocasWriteMacroNoBox.Text.Trim()}",
            "pmc"   => $"pmc:{FocasWritePmcAddrBox.Text.Trim()}",
            _       => tag
        };
    }

    // テキストボックス変更時もtargetを更新
    private void FocasNoBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (!_ready) return;
        BuildFocasReadTarget();
    }

    private void FocasWriteNoBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (!_ready) return;
        BuildFocasWriteTarget();
    }

    private void RestoreFocasReadCombo(string target)
    {
        if (string.IsNullOrEmpty(target)) { FocasReadTypeCombo.SelectedIndex = 0; return; }
        string tag;
        string num = "";
        if (target.StartsWith("macro:"))      { tag = "macro"; num = target[6..]; }
        else if (target.StartsWith("param:")) { tag = "param"; num = target[6..]; }
        else if (target.StartsWith("pmc:"))   { tag = "pmc";   num = target[4..]; }
        else                                   { tag = target; }
        foreach (ComboBoxItem item in FocasReadTypeCombo.Items)
        {
            if ((item.Tag as string) == tag) { FocasReadTypeCombo.SelectedItem = item; break; }
        }
        FocasMacroNoBox.Text    = tag == "macro" ? num : "";
        FocasParamNoBox.Text    = tag == "param" ? num : "";
        FocasPmcReadAddrBox.Text= tag == "pmc"   ? num : "";
        FocasTargetBox.Text     = target;
        UpdateFocasReadPanels();
    }

    private void RestoreFocasWriteCombo(string target)
    {
        if (string.IsNullOrEmpty(target)) { FocasWriteTypeCombo.SelectedIndex = 0; return; }
        string tag;
        string num = "";
        if (target.StartsWith("macro:"))    { tag = "macro"; num = target[6..]; }
        else if (target.StartsWith("pmc:")) { tag = "pmc";   num = target[4..]; }
        else                                 { tag = target; }
        foreach (ComboBoxItem item in FocasWriteTypeCombo.Items)
        {
            if ((item.Tag as string) == tag) { FocasWriteTypeCombo.SelectedItem = item; break; }
        }
        FocasWriteMacroNoBox.Text  = tag == "macro" ? num : "";
        FocasWritePmcAddrBox.Text  = tag == "pmc"   ? num : "";
        FocasWriteTargetBox.Text   = target;
        UpdateFocasWritePanels();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var action = SelectedAction();
        var conn   = SelectedConn(ConnCombo);
        if (action == null) { ShowError("アクションを選択してください。"); return; }

        var errs = Validate(action, conn);
        if (errs.Any()) { ShowError(string.Join("\n", errs)); return; }
        ErrBanner.Visibility = Visibility.Collapsed;

        var conditions = BuildConditions();
        var captures   = BuildCaptures();

        Result = action switch
        {
            "wait" => new StepModel("wait", DescBox.Text.Trim(),
                DurationMsParam: DurationBox.Text.Trim().NullIfEmpty()),

            "poll" => new StepModel("poll", DescBox.Text.Trim(),
                ConnectionId:    SelectedConn(PollConnCombo)?.Id,
                // pollノードリスト → 単一/複数対応
                NodeId: SelectedConn(PollConnCombo)?.Protocol is not "slmp"
                    ? (_pollNodes.Count(r => !string.IsNullOrWhiteSpace(r.NodeId)) == 1
                        ? _pollNodes.First(r => !string.IsNullOrWhiteSpace(r.NodeId)).NodeId.Trim()
                        : null)
                    : null,
                Address: SelectedConn(PollConnCombo)?.Protocol == "slmp"
                    ? _pollNodes.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.NodeId))?.NodeId.Trim()
                    : null,
                Nodes: SelectedConn(PollConnCombo)?.Protocol is not "slmp" && _pollNodes.Count(r => !string.IsNullOrWhiteSpace(r.NodeId)) > 1
                    ? _pollNodes.Where(r => !string.IsNullOrWhiteSpace(r.NodeId))
                                .Select(r => new NodeEntry(r.NodeId.Trim())).ToList()
                    : null,
                IntervalMsParam:  PollIntervalBox.Text.Trim().NullIfEmpty(),
                ReadTimeoutParam: PollReadTimeoutBox.Text.Trim().NullIfEmpty(),
                TimeoutParam:     PollTimeoutBox.Text.Trim().NullIfEmpty(),
                Conditions: BuildPollConditions(),
                Capture:    BuildPollCaptures()),

            "write" => BuildWriteStep(conn, DescBox.Text.Trim()),

            "send" => new StepModel("send", DescBox.Text.Trim(),
                ConnectionId: conn?.Id,
                Payload: ParseKeyValue(SendPayloadBox?.Text ?? "")),

            "focas_read" => new StepModel("focas_read", DescBox.Text.Trim(),
                ConnectionId: conn?.Id,
                NodeId:   FocasTargetBox.Text.Trim(),  // targetをNodeIdで保持
                TimeoutMs: int.TryParse(TimeoutBox.Text, out var ft) ? ft : (int?)null,
                TimeoutMsParam: int.TryParse(TimeoutBox.Text, out _) ? null : TimeoutBox.Text.Trim().NullIfEmpty(),
                Conditions: conditions,
                Capture:    captures),

            "focas_write" => new StepModel("focas_write", DescBox.Text.Trim(),
                ConnectionId: conn?.Id,
                NodeId:   FocasWriteTargetBox.Text.Trim(),
                Payload:  string.IsNullOrWhiteSpace(FocasWriteValueBox.Text) ? null
                    : new Dictionary<string,string>{{ "value", FocasWriteValueBox.Text.Trim() }}),

            _ => new StepModel(action, DescBox.Text.Trim(),
                ConnectionId: conn?.Id,
                NodeId:   conn?.Protocol is "opcua"     ? (_opcReadNodes.Count(r => !string.IsNullOrWhiteSpace(r.NodeId)) == 1 ? _opcReadNodes.First(r => !string.IsNullOrWhiteSpace(r.NodeId)).NodeId.Trim() : null)
                        : conn?.Protocol is "mtconnect" ? MtcPathBox.Text.Trim() : null,
                Address:  conn?.Protocol == "slmp" ? SlmpReadAddrBox.Text.Trim() : null,
                TimeoutMs: int.TryParse(TimeoutBox.Text, out var t) ? t : (int?)null,
                TimeoutMsParam: int.TryParse(TimeoutBox.Text, out _) ? null : TimeoutBox.Text.Trim().NullIfEmpty(),
                Nodes: conn?.Protocol == "opcua" && _opcReadNodes.Count(r => !string.IsNullOrWhiteSpace(r.NodeId)) > 1
                    ? _opcReadNodes.Where(r => !string.IsNullOrWhiteSpace(r.NodeId))
                                   .Select(r => new NodeEntry(r.NodeId.Trim())).ToList()
                    : null,
                Parse: conn?.Protocol switch
                {
                    "opcua"     => new ParseConfig("json"),
                    "mtconnect" => new ParseConfig("xml", MtcXPathBox.Text.Trim().NullIfEmpty()),
                    "slmp"      => new ParseConfig("binary"),
                    _           => new ParseConfig(
                        (ParseCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "plain",
                        XPathBox.Text.Trim().NullIfEmpty())
                },
                Conditions: conditions,
                Capture:    captures)
        };
        DialogResult = true;
    }

    private List<string> Validate(string action, ConnectionModel? conn)
    {
        var e = new List<string>();
        if (action == "wait")   { if (string.IsNullOrWhiteSpace(DurationBox.Text)) e.Add("・待機時間のパラメータ名を入力してください。"); return e; }
        if (action == "poll")
        {
            if (SelectedConn(PollConnCombo) == null) e.Add("・接続口を選択してください。");
            if (_pollNodes.All(r => string.IsNullOrWhiteSpace(r.NodeId))) e.Add("・ノードID / アドレスを入力してください。");
            else if (_pollNodes.Any(r => string.IsNullOrWhiteSpace(r.NodeId))) e.Add("・空のノードID行があります。保存時に除外されます。入力するか × で削除してください。");
            if (string.IsNullOrWhiteSpace(PollIntervalBox.Text)) e.Add("・ポーリング間隔を入力してください。");
            return e;
        }
        if (conn == null)       { e.Add("・接続口を選択してください。"); return e; }
        if (action == "write")
        {
            if (conn.Protocol == "opcua")
            {
                if (_opcWriteNodes.All(r => string.IsNullOrWhiteSpace(r.NodeId)))
                    e.Add("・ノードIDを1つ以上入力してください。");
                else if (_opcWriteNodes.Any(r => string.IsNullOrWhiteSpace(r.NodeId)))
                    e.Add("・空のノードID行があります。保存時に除外されます。入力するか × で削除してください。");
            }
            if (conn.Protocol == "slmp" && string.IsNullOrWhiteSpace(SlmpWriteAddrBox.Text))
                e.Add("・アドレスを入力してください。");
        }
        if (action is "read")
        {
            if (conn.Protocol == "opcua")
            {
                if (_opcReadNodes.All(r => string.IsNullOrWhiteSpace(r.NodeId)))
                    e.Add("・ノードIDを1つ以上入力してください。");
                else if (_opcReadNodes.Any(r => string.IsNullOrWhiteSpace(r.NodeId)))
                    e.Add("・空のノードID行があります。保存時に除外されます。入力するか × で削除してください。");
            }
            if (conn.Protocol == "slmp" && string.IsNullOrWhiteSpace(SlmpReadAddrBox.Text))
                e.Add("・アドレスを入力してください。");
        }
        return e;
    }

    private void ShowError(string msg) { ErrText.Text = msg; ErrBanner.Visibility = Visibility.Visible; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ── ユーティリティ ──
    private void BuildParamCombos()
    {
        foreach (var combo in new[] { DurationParamCombo, TimeoutParamCombo,
            SlmpWriteParamCombo, PollIntervalParamCombo, PollReadTimeoutParamCombo, PollTimeoutParamCombo })
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "（一覧から選択）", Tag = "" });
            foreach (var n in _paramNames)
                combo.Items.Add(new ComboBoxItem { Content = n, Tag = n });
            combo.SelectedIndex = 0;
        }
    }

    private void LoadExistingConditions(IReadOnlyList<ConditionModel>? conds)
    {
        _condRows.Clear();
        foreach (var c in conds ?? [])
            _condRows.Add(c.Operator == "bit_check"
                ? new CondRow { Field = "", Operator = "bit_check", Value = c.Bit?.ToString() ?? "0" }
                : new CondRow { Field = c.Field ?? "", Operator = c.Operator, Value = c.Value });
    }

    private void LoadExistingCaptures(IReadOnlyList<CaptureModel>? caps)
    {
        _captureRows.Clear();
        foreach (var c in caps ?? []) _captureRows.Add(new CaptureRow { Field = c.Field, As = c.As, ParamOptions = _paramNames });
    }

    private IReadOnlyList<ConditionModel> BuildPollConditions() =>
        _pollCondRows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => r.Operator == "bit_check"
                ? new ConditionModel("bit_check", r.Value.Trim(), Bit: int.TryParse(r.Value.Trim(), out var b2) ? b2 : 0)
                : new ConditionModel(r.Operator, r.Value.Trim(), Field: string.IsNullOrWhiteSpace(r.Field) ? null : r.Field.Trim()))
            .ToList();

    private IReadOnlyList<CaptureModel> BuildPollCaptures() =>
        _pollCapRows.Where(r => !string.IsNullOrWhiteSpace(r.Field) && !string.IsNullOrWhiteSpace(r.As))
            .Select(r => new CaptureModel(r.Field.Trim(), r.As.Trim())).ToList();

    private IReadOnlyList<ConditionModel> BuildConditions() =>
        _condRows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => r.Operator == "bit_check"
                ? new ConditionModel("bit_check", r.Value.Trim(), Bit: int.TryParse(r.Value.Trim(), out var b) ? b : 0)
                : new ConditionModel(r.Operator, r.Value.Trim(), Field: string.IsNullOrWhiteSpace(r.Field) ? null : r.Field.Trim()))
            .ToList();

    private IReadOnlyList<CaptureModel> BuildCaptures() =>
        _captureRows.Where(r => !string.IsNullOrWhiteSpace(r.Field) && !string.IsNullOrWhiteSpace(r.As))
            .Select(r => new CaptureModel(r.Field.Trim(), r.As.Trim())).ToList();

    private static ConnectionModel? SelectedConn(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as ConnectionModel;

    private string? SelectedAction()
        => (ActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static void SelectConn(ComboBox combo, string? connId)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Tag is ConnectionModel c && c.Id == connId) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static void SetCombo(ComboBox combo, string? value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Content?.ToString() == value || item.Tag?.ToString() == value)
            { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static void ApplyParamCombo(ComboBox combo, TextBox box)
    {
        var tag = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        if (!string.IsNullOrEmpty(tag)) box.Text = tag;
    }

    private static IReadOnlyDictionary<string,string>? ParseKeyValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string,string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            dict[line[..idx].Trim()] = line[(idx+1)..].Trim();
        }
        return dict.Any() ? dict : null;
    }

    private static IReadOnlyList<ConditionModel> ParseConditions(string text)
    {
        var list = new List<ConditionModel>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|');
            if (p.Length < 3) continue;
            list.Add(p[0].Trim() == "bit_check"
                ? new ConditionModel("bit_check", p[2].Trim(), Bit: int.TryParse(p[1].Trim(), out var b) ? b : 0)
                : new ConditionModel(p[1].Trim(), p[2].Trim(), Field: p[0].Trim().NullIfEmpty()));
        }
        return list;
    }

    private static IReadOnlyList<CaptureModel> ParseCapture(string text)
    {
        var list = new List<CaptureModel>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|');
            if (p.Length < 2) continue;
            list.Add(new CaptureModel(p[0].Trim(), p[1].Trim()));
        }
        return list;
    }

    private static string BuildCondText(IReadOnlyList<ConditionModel>? list)
        => string.Join("\n", (list ?? []).Select(c =>
            c.Operator == "bit_check" ? $"bit_check|{c.Bit}|{c.Value}" : $"{c.Field ?? ""}|{c.Operator}|{c.Value}"));

    private static string BuildCapText(IReadOnlyList<CaptureModel>? list)
        => string.Join("\n", (list ?? []).Select(c => $"{c.Field}|{c.As}"));
    // ── OPC-UA ノードリスト操作 ─────────────────────────────────────────────

    /// <summary>writeパネルの「一覧から選択」コンボ変更 → OpcNodeRow.ParamName に反映</summary>
    private void OpcWriteParamRow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string param
            && combo.Tag is OpcNodeRow row && !string.IsNullOrEmpty(param))
            row.ParamName = param;
    }

    private void OpcWriteNodeAdd_Click(object sender, RoutedEventArgs e)
        => _opcWriteNodes.Add(new OpcNodeRow { ParamOptions = _paramNames });

    private void OpcWriteNodeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is OpcNodeRow row && _opcWriteNodes.Count > 1)
            _opcWriteNodes.Remove(row);
    }

    private void OpcReadNodeAdd_Click(object sender, RoutedEventArgs e)
        => _opcReadNodes.Add(new OpcNodeRow());

    private void OpcReadNodeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is OpcNodeRow row && _opcReadNodes.Count > 1)
            _opcReadNodes.Remove(row);
    }

    private void PollNodeAdd_Click(object sender, RoutedEventArgs e)
        => _pollNodes.Add(new OpcNodeRow());

    private void PollNodeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is OpcNodeRow row && _pollNodes.Count > 1)
            _pollNodes.Remove(row);
    }

    private void AddPollCond_Click(object s, RoutedEventArgs e)     => _pollCondRows.Add(new CondRow());
    private void RemovePollCond_Click(object s, RoutedEventArgs e)  { if ((s as Button)?.Tag is CondRow r)    _pollCondRows.Remove(r); }
    private void AddPollCap_Click(object s, RoutedEventArgs e)      => _pollCapRows.Add(new CaptureRow { ParamOptions = _paramNames });
    private void RemovePollCap_Click(object s, RoutedEventArgs e)   { if ((s as Button)?.Tag is CaptureRow r) _pollCapRows.Remove(r); }
    private void PollCapAsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string param
            && combo.Tag is CaptureRow row && !string.IsNullOrEmpty(param))
            row.As = param;
    }

    /// <summary>write ステップを Nodes リストから構築する</summary>
    private StepModel BuildWriteStep(ConnectionModel? conn, string desc)
    {
        if (conn?.Protocol == "opcua")
        {
            var validRows = _opcWriteNodes.Where(r => !string.IsNullOrWhiteSpace(r.NodeId)).ToList();
            if (validRows.Count > 1)
            {
                return new StepModel("write", desc,
                    ConnectionId: conn.Id,
                    Nodes: validRows.Select(r => new NodeEntry(r.NodeId.Trim(), r.ParamName.Trim().NullIfEmpty())).ToList());
            }
            else
            {
                var row = validRows.FirstOrDefault() ?? _opcWriteNodes.FirstOrDefault();
                return new StepModel("write", desc,
                    ConnectionId: conn.Id,
                    NodeId:    row?.NodeId.Trim(),
                    Parameter: row?.ParamName.Trim().NullIfEmpty());
            }
        }
        else if (conn?.Protocol == "slmp")
        {
            return new StepModel("write", desc,
                ConnectionId: conn.Id,
                Address:   SlmpWriteAddrBox.Text.Trim(),
                Parameter: SlmpWriteValueBox.Text.Trim().NullIfEmpty());
        }
        return new StepModel("write", desc, ConnectionId: conn?.Id);
    }
}

file static class StringExt
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}