using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommTestTool.Domain.Interfaces;
using CommTestTool.Domain.Models;

namespace CommTestTool.Presentation.ViewModels;

// ─── ViewModelBase ────────────────────────────────────────────────────────
public abstract class ViewModelBase : ObservableObject { }

// ─── ParameterEditItem ────────────────────────────────────────────────────
public partial class ParameterEditItem : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _type;

    public ParameterEditItem(ParameterModel m)
    { _name = m.Name; _label = m.Label; _type = m.Type; }

    public ParameterModel ToModel() =>
        new(Name.Trim(), Label.Trim(), Type.Trim());
}

// ─── MainViewModel ────────────────────────────────────────────────────────
public partial class MainViewModel : ViewModelBase
{
    public DeviceListViewModel DeviceListVM { get; }
    public ExecutionViewModel  ExecutionVM  { get; }
    public MonitorViewModel    MonitorVM    { get; }

    [ObservableProperty] private int           _selectedTabIndex;
    [ObservableProperty] private ViewModelBase? _detailViewModel;

    // 実行中・監視中のいずれかが動いていれば設定タブを無効化
    public bool IsSettingEnabled => !ExecutionVM.IsRunning && !MonitorVM.IsRunning;

    // 実行状態が変わったときに設定タブの有効/無効を更新
    public void NotifyRunningChanged()
        => OnPropertyChanged(nameof(IsSettingEnabled));

    private readonly IDeviceRepository _repo;
    private readonly IDialogService    _dialog;
    private readonly IAppPaths          _paths;

    // 設備ごとのDetailViewModelキャッシュ（切り替えても編集内容を保持）
    private readonly Dictionary<string, DeviceDetailViewModel> _deviceVmCache = new();

    public MainViewModel(
        IDeviceRepository repo,
        IDialogService    dialog,
        IAppPaths         paths,
        CommTestTool.Application.Services.LogService           log,
        CommTestTool.Application.Services.CommunicationManager comm)
    {
        _repo   = repo;
        _dialog = dialog;
        _paths  = paths;

        DeviceListVM = new DeviceListViewModel(repo, dialog, this);
        MonitorVM    = new MonitorViewModel(log, DeviceListVM, this);
        ExecutionVM  = new ExecutionViewModel(comm, log, DeviceListVM, MonitorVM, this);

        DeviceListVM.LoadAll();
    }

    // 右パネル表示（キャッシュがあれば再利用して編集内容を保持）
    public void ShowDeviceDetail(DeviceModel device)
    {
        if (!_deviceVmCache.TryGetValue(device.Id, out var vm))
            _deviceVmCache[device.Id] = vm = new DeviceDetailViewModel(device, _repo, _dialog, DeviceListVM);
        DetailViewModel = vm;
    }

    public void ClearDetail() => DetailViewModel = null;

    // 設備IDのキャッシュを無効化（削除時）
    public void InvalidateDeviceCache(string deviceId)
        => _deviceVmCache.Remove(deviceId);

    // 設備保存後：キャッシュのキーを付け替える（VMは維持→画面リセットしない）
    public void ReplaceDeviceCache(string oldId, DeviceModel updated)
    {
        if (_deviceVmCache.TryGetValue(oldId, out var vm))
        {
            _deviceVmCache.Remove(oldId);
            _deviceVmCache[updated.Id] = vm;
            // DeviceDetailViewModel内の_originalも更新
            vm.UpdateOriginal(updated);
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialog.OpenFileDialog("インポートするYAMLファイルを選択");
        if (path == null) return;

        if (!_dialog.Confirm(
            "現在の設定を選択したファイルで上書きします。\n全ての設備・コマンド・シナリオが置き換えられます。\nこの操作は元に戻せません。続行しますか？",
            "インポート確認"))
            return;

        try
        {
            var yaml = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
            System.IO.File.WriteAllText(_paths.DevicesYaml, yaml, System.Text.Encoding.UTF8);
            // 全データ置き換えのためキャッシュをクリア
            _deviceVmCache.Clear();
            DetailViewModel = null;
            DeviceListVM.LoadAll();
            _dialog.Info($"インポートが完了しました。\n元ファイル: {path}");
        }
        catch (Exception ex)
        {
            _dialog.Error($"インポートに失敗しました。\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void Export()
    {
        var savePath = _dialog.SaveFileDialog("エクスポート先を選択", "devices.yaml");
        if (savePath == null) return;

        try
        {
            // キャッシュ内の各DeviceDetailVMの現在状態をDevicesリストに反映してからファイルに書き出す。
            // 「コマンドの保存」済みだが「設備の保存」未実施のデータがファイルに漏れるのを防ぐ。
            // 「コマンドの保存」すら押していない編集中データはBuildCurrentModel()に含まれないため除外される。
            foreach (var vm in _deviceVmCache.Values)
                DeviceListVM.UpdateModelSilent(vm.BuildCurrentModel());
            DeviceListVM.Save();

            System.IO.File.Copy(_paths.DevicesYaml, savePath, overwrite: true);
            _dialog.Info($"エクスポートが完了しました。\n保存先: {savePath}");
        }
        catch (Exception ex)
        {
            _dialog.Error($"エクスポートに失敗しました。\n{ex.Message}");
        }
    }
}

// ─── DeviceListViewModel ──────────────────────────────────────────────────
public partial class DeviceListViewModel : ViewModelBase
{
    private readonly IDeviceRepository _repo;
    private readonly IDialogService    _dialog;
    private readonly MainViewModel     _main;

    public ObservableCollection<DeviceModel> Devices { get; } = [];
    [ObservableProperty] private DeviceModel? _selectedDevice;

    public DeviceListViewModel(IDeviceRepository repo, IDialogService dialog, MainViewModel main)
    { _repo = repo; _dialog = dialog; _main = main; }

    public void LoadAll()
    {
        Devices.Clear();
        foreach (var d in _repo.GetAll()) Devices.Add(d);
    }

    partial void OnSelectedDeviceChanged(DeviceModel? value)
    {
        if (value != null) _main.ShowDeviceDetail(value);
    }

    [RelayCommand]
    private void Add()
    {
        var device = new DeviceModel($"device_{DateTime.Now:yyyyMMddHHmmss}", "新しい設備", [], [], []);
        Devices.Add(device);
        Save();
        SelectedDevice = device;
    }

    [RelayCommand]
    private void Copy(DeviceModel? device)
    {
        if (device == null) return;
        var copy = device with { Id = device.Id + "_copy", Name = device.Name + " (コピー)" };
        Devices.Add(copy);
        Save();
        SelectedDevice = copy;
    }

    [RelayCommand]
    private void Delete(DeviceModel? device)
    {
        if (device == null) return;
        if (!_dialog.Confirm($"設備「{device.Name}」を削除しますか？\nコマンド・シナリオも全て削除されます。")) return;
        _main.InvalidateDeviceCache(device.Id);
        Devices.Remove(device);
        Save();
        _main.ClearDetail();
    }

    public void Save() => _repo.Save(Devices.ToList());

    /// <summary>
    /// エクスポート用：SelectedDevice通知を発火させずにDevices内のモデルをIDで差し替える。
    /// CollectionChanged（Replace）は発火するのでExecutionVM/MonitorVMのSelectedDeviceは自動更新される。
    /// </summary>
    internal void UpdateModelSilent(DeviceModel updated)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Id != updated.Id) continue;
            Devices[i] = updated;
            // 選択中だった場合のみフィールドを直接更新（OnSelectedDeviceChangedを発火させない）
            if (_selectedDevice?.Id == updated.Id)
                _selectedDevice = updated;
            return;
        }
    }

    public void Replace(DeviceModel old, DeviceModel updated)
    {
        var idx = Devices.IndexOf(old);
        if (idx >= 0) Devices[idx] = updated;
        Save();
        // キャッシュのキーを付け替える（ShowDeviceDetailは呼ばない→画面リセットしない）
        _main.ReplaceDeviceCache(old.Id, updated);
        // 選択状態のハイライトだけ更新（発火しないよう直接フィールド操作）
        _selectedDevice = updated;
        OnPropertyChanged(nameof(SelectedDevice));
    }
}

// ─── DeviceDetailViewModel ────────────────────────────────────────────────
public partial class DeviceDetailViewModel : ViewModelBase
{
    private DeviceModel                   _original;  // 保存時に更新
    private readonly IDeviceRepository   _repo;
    private readonly IDialogService      _dialog;
    private readonly DeviceListViewModel _list;

    [ObservableProperty] private string  _id;
    [ObservableProperty] private string  _name;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool    _hasError;

    public ObservableCollection<ConnectionModel> Connections { get; }
    public ObservableCollection<CommandModel>    Commands    { get; }
    public ObservableCollection<ScenarioModel>   Scenarios   { get; }

    // リスト選択
    [ObservableProperty] private ConnectionModel? _selectedConnection;
    [ObservableProperty] private CommandModel?  _selectedCommand;
    [ObservableProperty] private ScenarioModel? _selectedScenario;

    // サブVM（コマンド詳細・シナリオ詳細）
    [ObservableProperty] private ViewModelBase? _subDetailViewModel;

    // 編集中VMのキャッシュ（切り替えても内容を保持する）
    private readonly Dictionary<object, ViewModelBase> _vmCache = new();

    public DeviceDetailViewModel(DeviceModel device, IDeviceRepository repo,
        IDialogService dialog, DeviceListViewModel list)
    {
        _original   = device;
        _repo       = repo;
        _dialog     = dialog;
        _list       = list;
        _id         = device.Id;
        _name       = device.Name;
        Connections = new(device.Connections);
        Commands    = new(device.Commands);
        Scenarios   = new(device.Scenarios);
    }

    // 設備名・IDをリアルタイムで左リスト（DeviceListVM.Devices）に反映
    private int FindDeviceIndex()
    {
        for (int i = 0; i < _list.Devices.Count; i++)
            if (_list.Devices[i].Id == _original.Id) return i;
        return -1;
    }

    partial void OnNameChanged(string value)
    {
        var idx = FindDeviceIndex();
        if (idx < 0) return;
        var updated = _list.Devices[idx] with { Name = value };
        _original = updated;
        _list.Devices[idx] = updated;
        _list.SelectedDevice = updated;
    }

    partial void OnIdChanged(string value)
    {
        var idx = FindDeviceIndex();
        if (idx < 0) return;
        var updated = _list.Devices[idx] with { Id = value };
        _original = updated;
        _list.Devices[idx] = updated;
        _list.SelectedDevice = updated;
    }

    // キャッシュキーを付け替える（CommandDetailVM/ScenarioDetailVMから呼ばれる）
    // 名前変更時にコマンド/シナリオ選択状態を更新（OnSelectedXxxChangedを発火させない）
    internal void SetSelectedCommandSilent(CommandModel? value) => _selectedCommand = value;
    internal void SetSelectedScenarioSilent(ScenarioModel? value) => _selectedScenario = value;

    // Commands/Scenariosの変更をDeviceListのDevicesコレクションに同期する
    // （実行画面のAvailableCommandsはDevicesのCollectionChangedで更新されるため）
    internal void SyncDeviceToList()
    {
        // IDで検索（record型はインスタンス参照が変わるためIndexOfが一致しない）
        var devIdx = -1;
        for (int i = 0; i < _list.Devices.Count; i++)
            if (_list.Devices[i].Id == _original.Id) { devIdx = i; break; }
        if (devIdx < 0) return;
        // Devicesに入っているインスタンスをベースに最新のCommands/Scenariosで更新
        var syncedDevice = _list.Devices[devIdx] with
        {
            Name      = _name,
            Commands  = Commands.ToList().AsReadOnly(),
            Scenarios = Scenarios.ToList().AsReadOnly()
        };
        _original = syncedDevice;
        // []代入でReplace通知を発火させる
        _list.Devices[devIdx] = syncedDevice;
        _list.SelectedDevice = syncedDevice;
    }

    internal void MoveCacheKey(object oldKey, object newKey)
    {
        if (_vmCache.TryGetValue(oldKey, out var vm))
        {
            _vmCache.Remove(oldKey);
            _vmCache[newKey] = vm;
        }
    }

    private List<ConnectionModel> GetConnectionsList() => Connections.ToList();

    partial void OnSelectedCommandChanged(CommandModel? value)
    {
        if (value == null) return;
        // シナリオ選択をリセット（フィールド直接操作＋通知）
        _selectedScenario = null;
        OnPropertyChanged(nameof(SelectedScenario));
        if (!_vmCache.TryGetValue(value, out var vm))
            _vmCache[value] = vm = new CommandDetailViewModel(value, _dialog, this);
        SubDetailViewModel = vm;
    }

    partial void OnSelectedScenarioChanged(ScenarioModel? value)
    {
        if (value == null) return;
        // コマンド選択をリセット
        _selectedCommand = null;
        OnPropertyChanged(nameof(SelectedCommand));
        if (!_vmCache.TryGetValue(value, out var vm))
            _vmCache[value] = vm = new ScenarioDetailViewModel(value, _dialog, this);
        SubDetailViewModel = vm;
    }

    // _originalを更新（設備保存後にキャッシュキー付け替え時に呼ばれる）
    public void UpdateOriginal(DeviceModel updated)
    {
        _original = updated;
        // Commands/Scenarios/Connectionsも最新状態に同期
        // （既にUIで編集中のものはそのまま残す）
    }

    /// <summary>
    /// エクスポート用：現在のCommands/Scenarios/Connectionsからモデルを構築する（副作用なし）
    /// 「コマンドの保存」済みだが「設備の保存」未実施のデータもここで拾う。
    /// 「コマンドの保存」すら押していない編集中データは含めない。
    /// </summary>
    internal DeviceModel BuildCurrentModel() => _original with
    {
        Name        = _name,
        Connections = Connections.ToList().AsReadOnly(),
        Commands    = Commands.ToList().AsReadOnly(),
        Scenarios   = Scenarios.ToList().AsReadOnly()
    };

    // ─── 保存 ───
    [RelayCommand]
    private void Save()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))   errors.Add("・設備IDを入力してください。");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("・設備名を入力してください。");
        if (!Connections.Any())              errors.Add("・接続口を1つ以上追加してください。");
        if (errors.Any()) { HasError = true; ErrorMessage = string.Join("\n", errors); return; }
        HasError = false;

        var updated = _original with
        {
            Id          = Id.Trim(),
            Name        = Name.Trim(),
            Connections = Connections.ToList().AsReadOnly(),
            Commands    = Commands.ToList().AsReadOnly(),
            Scenarios   = Scenarios.ToList().AsReadOnly()
        };
        _list.Replace(_original, updated);
        _dialog.Info(
            "設定を保存しました。\n\n" +
            "⚠️ この保存はアプリ内部（config/devices.yaml）への書き込みです。\n" +
            "アプリを削除・再インストールすると失われます。\n\n" +
            "📤 大切な設定はヘッダーの「エクスポート」で\n" +
            "　　安全な場所にバックアップしてください。",
            "保存しました");
    }

    // ─── 接続口 ───
    public void ReplaceConnection(ConnectionModel old, ConnectionModel @new)
    {
        var idx = Connections.IndexOf(old);
        if (idx >= 0) Connections[idx] = @new;
    }

    [ObservableProperty] private ConnectionEditItem? _selectedConnectionItem;

    partial void OnSelectedConnectionChanged(ConnectionModel? value)
    {
        if (value == null)
        {
            SubDetailViewModel = null;
            return;
        }
        _selectedCommand  = null; OnPropertyChanged(nameof(SelectedCommand));
        _selectedScenario = null; OnPropertyChanged(nameof(SelectedScenario));
        // 接続口はキャッシュしない（適用ボタンでモデルが置き換わるため）
        var item = new ConnectionEditItem(this, value);
        SelectedConnectionItem = item;
        SubDetailViewModel = item;
    }

    [RelayCommand]
    private void AddConnection()
    {
        var newConn = new ConnectionModel(
            $"conn_{DateTime.Now:yyyyMMddHHmmss}", "opcua", 5000);
        Connections.Add(newConn);
        SelectedConnection = newConn;
    }

    [RelayCommand]
    private void EditConnection(ConnectionModel? conn)
    {
        if (conn == null) return;
        // 同じものをクリックしたらトグル
        SelectedConnection = SelectedConnection == conn ? null : conn;
    }

    [RelayCommand]
    private void DeleteConnection(ConnectionModel? conn)
    {
        if (conn != null && _dialog.Confirm($"接続口「{conn.Id}」を削除しますか？"))
            Connections.Remove(conn);
    }

    // ─── コマンド ───
    [RelayCommand]
    private void AddCommand()
    {
        var cmd = new CommandModel($"command_{DateTime.Now:yyyyMMddHHmmss}", "新しいコマンド", [], []);
        Commands.Add(cmd);
        _vmCache[cmd] = new CommandDetailViewModel(cmd, _dialog, this);
        SelectedCommand  = cmd;
        SelectedScenario = null;
    }



    [RelayCommand]
    private void CopyCommand(CommandModel? cmd)
    {
        if (cmd == null) return;
        var copy = cmd with { Id = cmd.Id + "_copy", Name = cmd.Name + " (コピー)" };
        Commands.Add(copy);
        _vmCache[copy] = new CommandDetailViewModel(copy, _dialog, this);
        SelectedCommand = copy;
    }

    [RelayCommand]
    private void DeleteCommand(CommandModel? cmd)
    {
        if (cmd == null) return;
        if (!_dialog.Confirm($"コマンド「{cmd.Name}」を削除しますか？")) return;
        _vmCache.Remove(cmd);
        Commands.Remove(cmd);
        SelectedCommand = null;
        SubDetailViewModel = null;
    }

    // リアルタイム名前・ID更新（保存前でも左リストに即反映）
    public void UpdateCommandName(CommandModel original, string name)
    {
        var idx = Commands.IndexOf(original);
        if (idx < 0) return;
        Commands[idx] = original with { Name = name };
    }
    public void UpdateCommandId(CommandModel original, string id)
    {
        var idx = Commands.IndexOf(original);
        if (idx < 0) return;
        Commands[idx] = original with { Id = id };
    }
    public void UpdateScenarioName(ScenarioModel original, string name)
    {
        var idx = Scenarios.IndexOf(original);
        if (idx < 0) return;
        Scenarios[idx] = original with { Name = name };
    }
    public void UpdateScenarioId(ScenarioModel original, string id)
    {
        var idx = Scenarios.IndexOf(original);
        if (idx < 0) return;
        Scenarios[idx] = original with { Id = id };
    }

    public void ReplaceCommand(CommandModel old, CommandModel updated)
    {
        // record型はインスタンス参照が変わるとIndexOfが-1を返すため、IDで検索する
        var idx = -1;
        for (int i = 0; i < Commands.Count; i++)
            if (Commands[i].Id == old.Id) { idx = i; break; }

        if (idx >= 0)
        {
            // record型はINotifyPropertyChangedを実装しないため、
            // Commands[idx] = updated では表示が更新されない。
            // RemoveAt + Insert で強制的にAdd/Remove通知を発火させる。
            Commands.RemoveAt(idx);
            Commands.Insert(idx, updated);
        }
        // キャッシュのキーを old → updated に付け替える（VMは再作成しない）
        if (_vmCache.TryGetValue(old, out var vm))
        {
            _vmCache.Remove(old);
            _vmCache[updated] = vm;
        }
        // _selectedCommandをupdatedに差し替えるが OnPropertyChanged は呼ばない
        // （OnSelectedCommandChanged が発火すると SubDetailViewModel がリセットされる）
        _selectedCommand = updated;

        // Devicesを即時更新し_originalも同期する（実行画面のAvailableCommandsにも反映される）
        SyncDeviceToList();
    }

    // ─── シナリオ ───
    [RelayCommand]
    private void AddScenario()
    {
        var s = new ScenarioModel($"scenario_{DateTime.Now:yyyyMMddHHmmss}", "新しいシナリオ", []);
        Scenarios.Add(s);
        _vmCache[s] = new ScenarioDetailViewModel(s, _dialog, this);
        SelectedScenario = s;
        SelectedCommand  = null;
    }



    [RelayCommand]
    private void CopyScenario(ScenarioModel? s)
    {
        if (s == null) return;
        var copy = s with { Id = s.Id + "_copy", Name = s.Name + " (コピー)" };
        Scenarios.Add(copy);
        SubDetailViewModel = new ScenarioDetailViewModel(copy, _dialog, this);
    }

    [RelayCommand]
    private void DeleteScenario(ScenarioModel? s)
    {
        if (s == null) return;
        if (!_dialog.Confirm($"シナリオ「{s.Name}」を削除しますか？")) return;
        _vmCache.Remove(s);
        Scenarios.Remove(s);
        SelectedScenario = null;
        SubDetailViewModel = null;
    }

    public void ReplaceScenario(ScenarioModel old, ScenarioModel updated)
    {
        // record型はインスタンス参照が変わるとIndexOfが-1を返すため、IDで検索する
        var idx = -1;
        for (int i = 0; i < Scenarios.Count; i++)
            if (Scenarios[i].Id == old.Id) { idx = i; break; }

        if (idx >= 0)
        {
            Scenarios.RemoveAt(idx);
            Scenarios.Insert(idx, updated);
        }
        if (_vmCache.TryGetValue(old, out var vm))
        {
            _vmCache.Remove(old);
            _vmCache[updated] = vm;
        }
        _selectedScenario = updated;

        // Devicesを即時更新し_originalも同期する（実行画面のAvailableScenarioにも反映される）
        SyncDeviceToList();
    }
}

// ─── CommandDetailViewModel ───────────────────────────────────────────────
public partial class CommandDetailViewModel : ViewModelBase
{
    private CommandModel                   _original;   // 保存時に更新
    private readonly IDialogService        _dialog;
    private readonly DeviceDetailViewModel _deviceDetail;

    [ObservableProperty] private string  _id;
    [ObservableProperty] private string  _name;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool    _hasError;

    public ObservableCollection<ParameterEditItem> Parameters { get; }
    public ObservableCollection<StepModel>         Steps      { get; }

    public CommandDetailViewModel(CommandModel cmd, IDialogService dialog,
        DeviceDetailViewModel deviceDetail)
    {
        _original     = cmd;
        _dialog       = dialog;
        _deviceDetail = deviceDetail;
        _id           = cmd.Id;
        _name         = cmd.Name;
        Parameters    = new(cmd.Parameters.Select(p => new ParameterEditItem(p)));
        Steps         = new(cmd.Steps);
    }

    // コマンド名・IDをリアルタイムで左リスト（Commands）に反映
    partial void OnNameChanged(string value) => RefreshCommandInList(_original with { Name = value });
    partial void OnIdChanged(string value)   => RefreshCommandInList(_original with { Id   = value });

    private void RefreshCommandInList(CommandModel updated)
    {
        var idx = _deviceDetail.Commands.IndexOf(_original);
        if (idx < 0) return;
        _deviceDetail.MoveCacheKey(_original, updated);
        _original = updated;  // CommandDetailVMの_original（CommandModel）を更新
        _deviceDetail.Commands.RemoveAt(idx);
        _deviceDetail.Commands.Insert(idx, updated);
        _deviceDetail.SetSelectedCommandSilent(updated);
        _deviceDetail.SyncDeviceToList();
    }

    [RelayCommand]
    private void Save()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))   errors.Add("・コマンドIDを入力してください。");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("・コマンド名を入力してください。");
        if (!Steps.Any())                    errors.Add("・ステップを1つ以上追加してください。");
        if (errors.Any()) { HasError = true; ErrorMessage = string.Join("\n", errors); return; }
        HasError = false;

        var updated = _original with
        {
            Id         = Id.Trim(),
            Name       = Name.Trim(),
            Parameters = Parameters.Select(p => p.ToModel()).ToList().AsReadOnly(),
            Steps      = Steps.ToList().AsReadOnly()
        };
        _deviceDetail.ReplaceCommand(_original, updated);
        _original = updated;  // 次の保存のために_originalを更新
        _dialog.Info("保存しました。");
    }

    [RelayCommand]
    private void AddParameter()
        => Parameters.Add(new ParameterEditItem(new ParameterModel("param", "パラメータ", "string")));

    [RelayCommand]
    private void DeleteParameter(ParameterEditItem? p)
    {
        if (p != null)
            Parameters.Remove(p);           // 行の × ボタンから呼ばれた場合
        else if (Parameters.Count > 0)
            Parameters.RemoveAt(Parameters.Count - 1);  // CommandParameter未指定（「最後を削除」ボタン）の場合
    }

    private List<string> GetParamNames() =>
        Parameters.Select(p => p.Name).ToList();

    [RelayCommand]
    private void AddStep()
    {
        var step = new StepModel("send", "新しいステップ");
        var conns = _deviceDetail.Connections.ToList();
        var pnames = GetParamNames();
        var dlg = _dialog.ShowDialog(() => new Views.StepEditWindow(step, conns, pnames));
        if (dlg is Views.StepEditWindow w && w.DialogResult == true && w.Result != null)
            Steps.Add(w.Result);
    }

    [RelayCommand]
    private void EditStep(StepModel? step)
    {
        if (step == null) return;
        var conns = _deviceDetail.Connections.ToList();
        var pnames = GetParamNames();
        var dlg = _dialog.ShowDialog(() => new Views.StepEditWindow(step, conns, pnames));
        if (dlg is Views.StepEditWindow w && w.DialogResult == true && w.Result != null)
        {
            var idx = Steps.IndexOf(step);
            if (idx >= 0) Steps[idx] = w.Result;
        }
    }

    [RelayCommand]
    private void DeleteStep(StepModel? step)
    { if (step != null) Steps.Remove(step); }

    [RelayCommand]
    private void MoveStepUp(StepModel? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx > 0) Steps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveStepDown(StepModel? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx >= 0 && idx < Steps.Count - 1) Steps.Move(idx, idx + 1);
    }
}

// ─── ScenarioDetailViewModel ──────────────────────────────────────────────
public partial class ScenarioDetailViewModel : ViewModelBase
{
    private ScenarioModel                  _original;   // 保存時に更新
    private readonly IDialogService        _dialog;
    private readonly DeviceDetailViewModel _deviceDetail;

    [ObservableProperty] private string  _id;
    [ObservableProperty] private string  _name;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool    _hasError;

    public ObservableCollection<ScenarioStepModel> Steps { get; }

    public ScenarioDetailViewModel(ScenarioModel scenario, IDialogService dialog,
        DeviceDetailViewModel deviceDetail)
    {
        _original     = scenario;
        _dialog       = dialog;
        _deviceDetail = deviceDetail;
        _id           = scenario.Id;
        _name         = scenario.Name;
        Steps         = new(scenario.Steps);
    }

    // シナリオ名・IDをリアルタイムで左リスト（Scenarios）に反映
    partial void OnNameChanged(string value) => RefreshScenarioInList(_original with { Name = value });
    partial void OnIdChanged(string value)   => RefreshScenarioInList(_original with { Id   = value });

    private void RefreshScenarioInList(ScenarioModel updated)
    {
        var idx = _deviceDetail.Scenarios.IndexOf(_original);
        if (idx < 0) return;
        _deviceDetail.MoveCacheKey(_original, updated);
        _original = updated;
        _deviceDetail.Scenarios.RemoveAt(idx);
        _deviceDetail.Scenarios.Insert(idx, updated);
        _deviceDetail.SetSelectedScenarioSilent(updated);
        _deviceDetail.SyncDeviceToList();
    }

    [RelayCommand]
    private void Save()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))   errors.Add("・シナリオIDを入力してください。");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("・シナリオ名を入力してください。");
        if (!Steps.Any())                    errors.Add("・ステップを1つ以上追加してください。");
        if (errors.Any()) { HasError = true; ErrorMessage = string.Join("\n", errors); return; }
        HasError = false;

        var updated = _original with
        {
            Id    = Id.Trim(),
            Name  = Name.Trim(),
            Steps = Steps.ToList().AsReadOnly()
        };
        _deviceDetail.ReplaceScenario(_original, updated);
        _original = updated;  // 次の保存のために_originalを更新
        _dialog.Info("保存しました。");
    }

    [RelayCommand]
    private void AddCommandStep()
    {
        var commands = _deviceDetail.Commands.ToList();
        if (!commands.Any()) { _dialog.Error("先にコマンドを登録してください。"); return; }
        var dlg = _dialog.ShowDialog(() => new Views.ScenarioStepPickWindow(commands));
        if (dlg is Views.ScenarioStepPickWindow w && w.DialogResult == true && w.Result != null)
            Steps.Add(w.Result);
    }

    [RelayCommand]
    private void AddWaitStep()
    {
        var dlg = _dialog.ShowDialog(() => new Views.WaitStepWindow());
        if (dlg is Views.WaitStepWindow w && w.DialogResult == true)
            Steps.Add(new ScenarioStepModel("wait", $"{w.DurationMs}ms 待機", DurationMs: w.DurationMs));
    }

    [RelayCommand]
    private void DeleteStep(ScenarioStepModel? step)
    { if (step != null) Steps.Remove(step); }

    [RelayCommand]
    private void MoveStepUp(ScenarioStepModel? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx > 0) Steps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveStepDown(ScenarioStepModel? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx >= 0 && idx < Steps.Count - 1) Steps.Move(idx, idx + 1);
    }
}

// ─── ExecutionViewModel ───────────────────────────────────────────────────
public partial class ExecutionViewModel : ViewModelBase
{
    private readonly CommTestTool.Application.Services.CommunicationManager _comm;
    private readonly CommTestTool.Application.Services.LogService            _log;
    private readonly DeviceListViewModel _devices;
    private MainViewModel? _mainVM;
    // 単体実行用
    [ObservableProperty] private DeviceModel?   _selectedDevice;
    [ObservableProperty] private CommandModel?  _selectedCommand;
    public ObservableCollection<CommandModel>   AvailableCommands  { get; } = [];

    // シナリオ実行用（設備選択を分離）
    [ObservableProperty] private DeviceModel?   _selectedScenarioDevice;
    [ObservableProperty] private ScenarioModel? _selectedScenario;
    public ObservableCollection<ScenarioModel>  AvailableScenarios { get; } = [];

    // キャンセルトークンを用途別に分離
    private CancellationTokenSource? _commandCts;
    private CancellationTokenSource? _scenarioCts;

    public ObservableCollection<ParamValueItem>    ParamValues    { get; } = [];
    public ObservableCollection<ParamValueItem>    ScenParamValues { get; } = [];  // シナリオ実行用
    public ObservableCollection<StepProgressItem>  StepProgresses { get; } = [];
    public ObservableCollection<ScenProgressItem>  ScenProgresses { get; } = [];
    public ObservableCollection<LogEntry>          LogEntries     { get; } = [];

    public ObservableCollection<DeviceModel> Devices => _devices.Devices;
    public MonitorViewModel MonitorVM { get; }

    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusMessage = "待機中";

    public ExecutionViewModel(
        CommTestTool.Application.Services.CommunicationManager comm,
        CommTestTool.Application.Services.LogService log,
        DeviceListViewModel devices,
        MonitorViewModel monitorVM,
        MainViewModel? mainVM = null)
    {
        _comm    = comm;
        _log     = log;
        _devices = devices;
        MonitorVM = monitorVM;
        _mainVM  = mainVM;

        _log.EntryAdded += entry =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() => LogEntries.Add(entry));

        // Devicesコレクションの変更を監視して実行側も自動更新（Replace/Add両対応）
        _devices.Devices.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
                e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
            var updated = e.NewItems?[0] as DeviceModel;
            if (updated == null) return;
            if (SelectedDevice?.Id == updated.Id)
                SelectedDevice = updated;
            if (SelectedScenarioDevice?.Id == updated.Id)
                SelectedScenarioDevice = updated;
        };
    }

    // 単体実行：設備 → コマンド絞り込み
    partial void OnSelectedDeviceChanged(DeviceModel? value)
    {
        // 以前選択していたコマンドIDを記憶（デバイス更新後に再選択するため）
        var prevCommandId = SelectedCommand?.Id;
        AvailableCommands.Clear();
        SelectedCommand = null;
        if (value == null) return;
        foreach (var c in value.Commands) AvailableCommands.Add(c);
        // 以前と同じコマンドIDがあれば再選択してパラメータを維持
        if (prevCommandId != null)
        {
            var match = AvailableCommands.FirstOrDefault(c => c.Id == prevCommandId);
            if (match != null) SelectedCommand = match;
        }
    }

    // シナリオ実行：設備 → シナリオ絞り込み
    partial void OnSelectedScenarioDeviceChanged(DeviceModel? value)
    {
        var prevScenarioId = SelectedScenario?.Id;
        AvailableScenarios.Clear();
        SelectedScenario = null;
        ScenParamValues.Clear();
        if (value == null) return;
        foreach (var s in value.Scenarios) AvailableScenarios.Add(s);
        // 以前と同じシナリオIDがあれば再選択
        if (prevScenarioId != null)
        {
            var match = AvailableScenarios.FirstOrDefault(s => s.Id == prevScenarioId);
            if (match != null) SelectedScenario = match;
        }
    }

    partial void OnSelectedCommandChanged(CommandModel? value)
    {
        ParamValues.Clear();
        if (value == null) return;
        foreach (var p in value.Parameters)
            ParamValues.Add(new ParamValueItem(p, ""));
    }

    partial void OnSelectedScenarioChanged(ScenarioModel? value)
    {
        ScenParamValues.Clear();
        if (value == null || SelectedScenarioDevice == null) return;
        // シナリオの全ステップが使うコマンドのパラメータを収集（同名は1つにまとめる）
        var seen = new HashSet<string>();
        foreach (var step in value.Steps.Where(s => s.Type == "command"))
        {
            var cmd = SelectedScenarioDevice.Commands.FirstOrDefault(c => c.Id == step.CommandId);
            if (cmd == null) continue;
            foreach (var p in cmd.Parameters)
            {
                if (seen.Add(p.Name))
                    ScenParamValues.Add(new ParamValueItem(p, ""));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCommand()
    {
        if (SelectedDevice == null || SelectedCommand == null) return;
        IsRunning = true; StatusMessage = $"実行中: {SelectedCommand.Name}";
        StepProgresses.Clear();
        for (int i = 0; i < SelectedCommand.Steps.Count; i++)
            StepProgresses.Add(new StepProgressItem(i, SelectedCommand.Steps[i].Description));

        _commandCts = new CancellationTokenSource();
        // 空欄の場合は空文字のまま渡す（Services側でnull扱い）
        var paramValues = ParamValues.ToDictionary(p => p.Parameter.Name, p => p.Value);
        var progress = new Progress<StepResult>(sr =>
        {
            var item = StepProgresses.FirstOrDefault(s => s.Index == sr.StepIndex);
            if (item != null)
            {
                item.Status       = sr.Status;
                item.ErrorMessage = sr.ErrorMessage;
                item.ReceivedData = sr.ReceivedData;
                item.Duration     = sr.Duration.HasValue
                    ? $"{sr.Duration.Value.TotalMilliseconds:F0} ms"
                    : null;
            }
        });

        try
        {
            var result = await _comm.RunCommandAsync(
                SelectedCommand, SelectedDevice, paramValues, progress: progress, ct: _commandCts.Token);
            StatusMessage = result.IsSuccess ? "✅ 完了" : $"❌ {result.ErrorMessage}";
        }
        catch (OperationCanceledException) { StatusMessage = "⚠️ キャンセル"; }
        finally
        {
            IsRunning = false;
            _commandCts?.Dispose();   // CancellationTokenSource のリーク防止
            _commandCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunScenario()
    {
        if (SelectedScenarioDevice == null || SelectedScenario == null) return;
        IsRunning = true; StatusMessage = $"シナリオ実行中: {SelectedScenario.Name}";
        ScenProgresses.Clear();
        foreach (var s in SelectedScenario.Steps)
            ScenProgresses.Add(new ScenProgressItem(s));

        // シナリオの全パラメータ値を収集
        var scenParamDict = ScenParamValues.ToDictionary(p => p.Parameter.Name, p => p.Value);

        _scenarioCts = new CancellationTokenSource();
        var scenProgress = new Progress<(int, ScenarioStepModel, StepStatus)>(t =>
        {
            var item = ScenProgresses.FirstOrDefault(s => s.Step == t.Item2);
            if (item != null) item.Status = t.Item3;
        });

        // ステップ進捗のエラーメッセージをScenProgressItemに反映
        var stepProgressForScen = new Progress<StepResult>(sr =>
        {
            // 最後に実行中のScenProgressItemのエラーを更新
            var running = ScenProgresses.FirstOrDefault(s => s.Status == StepStatus.Running);
            if (running != null && sr.Status == StepStatus.Error)
                running.ErrorMessage = sr.ErrorMessage;
        });

        try
        {
            var results = await _comm.RunScenarioAsync(
                SelectedScenario, SelectedScenarioDevice, scenParamDict,
                scenarioProgress: scenProgress, stepProgress: stepProgressForScen,
                ct: _scenarioCts.Token);

            var ok = results.Count(r => r.Result?.IsSuccess != false);
            StatusMessage = $"✅ シナリオ完了: {ok}/{results.Count} ステップ";
        }
        catch (OperationCanceledException) { StatusMessage = "⚠️ キャンセル"; }
        finally
        {
            IsRunning = false;
            _scenarioCts?.Dispose();   // CancellationTokenSource のリーク防止
            _scenarioCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopExecution() { _commandCts?.Cancel(); _scenarioCts?.Cancel(); }

    private bool CanStop() => IsRunning;

    [RelayCommand]
    private void ClearLog() { LogEntries.Clear(); _log.Clear(); }

    private bool CanRun() => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommandCommand.NotifyCanExecuteChanged();
        RunScenarioCommand.NotifyCanExecuteChanged();
        StopExecutionCommand.NotifyCanExecuteChanged();
        _mainVM?.NotifyRunningChanged();
    }
}

// ─── 補助クラス ───────────────────────────────────────────────────────────
public partial class ParamValueItem : ObservableObject
{
    public ParameterModel Parameter { get; }

    [ObservableProperty] private string _value;
    [ObservableProperty] private string? _validationError;  // バリデーションエラーメッセージ

    // ── 型別UI表示切替 ────────────────────────────────────────────────────
    public bool IsBoolType     => Parameter.Type == "boolean";
    public bool IsBitType      => Parameter.Type == "bit";
    public bool IsJsonType     => Parameter.Type == "json";
    public bool IsDateTimeType => Parameter.Type == "datetime";
    public bool IsIntType      => Parameter.Type is "integer" or "long";
    public bool IsUnsignedType => Parameter.Type is "byte" or "word" or "dword" or "uint";
    public bool IsFloatType    => Parameter.Type is "float" or "double";
    public bool IsStringType   => Parameter.Type == "string";
    public bool IsTextType     => IsStringType;  // 後方互換

    // ── 型ごとの入力範囲 ─────────────────────────────────────────────────
    public long   IntMin  => Parameter.Type == "long" ? long.MinValue : int.MinValue;
    public long   IntMax  => Parameter.Type == "long" ? long.MaxValue : int.MaxValue;
    public double UintMin => 0;
    public double UintMax => Parameter.Type switch
    {
        "byte"  => 255,
        "word"  => 65535,
        "dword" => 4294967295,
        "uint"  => 4294967295,
        _       => 4294967295
    };

    // ── TypeHint ──────────────────────────────────────────────────────────
    public string TypeHint => Parameter.Type switch
    {
        "integer"  => $"整数（{int.MinValue}〜{int.MaxValue}）",
        "long"     => "64bit整数（-9223372036854775808〜9223372036854775807）",
        "float"    => "単精度小数（例: 3.14）",
        "double"   => "倍精度小数（例: 3.141592653589793）",
        "byte"     => "0〜255",
        "word"     => "0〜65535",
        "dword"    => "0〜4294967295",
        "uint"     => "符号なし整数 0〜4294967295",
        "string"   => "テキスト",
        "datetime" => "ISO 8601形式 例: 2024-01-01T12:00:00",
        _          => ""
    };

    // ── boolean ───────────────────────────────────────────────────────────
    public bool IsBoolValue
    {
        get => Value?.ToLower() == "true";
        set { Value = value ? "true" : "false"; }
    }

    // ── bit ───────────────────────────────────────────────────────────────
    public bool IsBitZero { get => Value != "1"; set { if (value) Value = "0"; } }
    public bool IsBitOne  { get => Value == "1";  set { if (value) Value = "1"; } }

    // ── integer/long: UpDown ─────────────────────────────────────────────
    public void Increment()
    {
        if (long.TryParse(Value, out var v) && v < IntMax) Value = (v + 1).ToString();
    }
    public void Decrement()
    {
        if (long.TryParse(Value, out var v) && v > IntMin) Value = (v - 1).ToString();
    }

    // ── byte/word/dword/uint: Increment/Decrement ─────────────────────────
    public void IncrementUint()
    {
        if (ulong.TryParse(Value, out var v) && v < (ulong)UintMax) Value = (v + 1).ToString();
    }
    public void DecrementUint()
    {
        if (ulong.TryParse(Value, out var v) && v > 0) Value = (v - 1).ToString();
    }

    // ── 値変更時のバリデーション ──────────────────────────────────────────
    partial void OnValueChanged(string value)
    {
        ValidationError = Validate(value);
    }

    public bool HasError => !string.IsNullOrEmpty(ValidationError);

    private string? Validate(string v)
    {
        if (string.IsNullOrEmpty(v)) return null;  // 空は許容
        return Parameter.Type switch
        {
            "integer" => int.TryParse(v, out _)    ? null : $"整数を入力してください（{int.MinValue}〜{int.MaxValue}）",
            "long"    => long.TryParse(v, out _)   ? null : "64bit整数を入力してください",
            "float"   => float.TryParse(v, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out _)
                             ? null : "小数を入力してください（例: 3.14）",
            "double"  => double.TryParse(v, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out _)
                             ? null : "小数を入力してください（例: 3.14）",
            "byte"    => byte.TryParse(v, out _)   ? null : "0〜255の整数を入力してください",
            "word"    => ushort.TryParse(v, out _) ? null : "0〜65535の整数を入力してください",
            "dword"   => uint.TryParse(v, out _)   ? null : "0〜4294967295の整数を入力してください",
            "uint"    => uint.TryParse(v, out _)   ? null : "0〜4294967295の整数を入力してください",
            "datetime"=> DateTime.TryParse(v, out _) ? null : "日時を入力してください（例: 2024-01-01T12:00:00）",
            _         => null
        };
    }

    public ParamValueItem(ParameterModel parameter, string value)
    {
        Parameter = parameter;
        _value    = value;
    }
}

public partial class StepProgressItem(int index, string description) : ObservableObject
{
    public int    Index       { get; } = index;
    public string Description { get; } = description;
    [ObservableProperty] private StepStatus _status       = StepStatus.Pending;
    [ObservableProperty] private string?    _errorMessage;
    [ObservableProperty] private string     _icon         = "⬜";
    [ObservableProperty] private string?    _receivedData;  // 実行結果の生データ
    [ObservableProperty] private string?    _duration;      // 実行時間

    public bool HasReceivedData => !string.IsNullOrEmpty(ReceivedData);

    partial void OnStatusChanged(StepStatus value)
    {
        Icon = value switch
        {
            StepStatus.Running => "⏳", StepStatus.Success => "✅",
            StepStatus.Error   => "❌", StepStatus.Skipped => "⏭",
            _                  => "⬜"
        };
    }

    partial void OnReceivedDataChanged(string? value)
        => OnPropertyChanged(nameof(HasReceivedData));
}

public partial class ScenProgressItem(ScenarioStepModel step) : ObservableObject
{
    public ScenarioStepModel Step { get; } = step;
    [ObservableProperty] private StepStatus _status       = StepStatus.Pending;
    [ObservableProperty] private string     _icon         = "⬜";
    [ObservableProperty] private string?    _errorMessage;

    partial void OnStatusChanged(StepStatus value)
    {
        Icon = value switch
        {
            StepStatus.Running => "⏳", StepStatus.Success => "✅",
            StepStatus.Error   => "❌", _                  => "⬜"
        };
    }
}

// ─── MonitorViewModel ─────────────────────────────────────────────────────
public partial class MonitorViewModel : ViewModelBase
{
    private readonly CommTestTool.Application.Services.LogService _log;
    private readonly DeviceListViewModel _devices;
    private CommTestTool.Application.Services.MonitorChannel? _channel;

    public ObservableCollection<DeviceModel>  Devices             => _devices.Devices;
    public ObservableCollection<CommandModel> AvailableCommands   { get; } = [];
    public ObservableCollection<StepModel>    AvailablePollSteps  { get; } = [];
    public ObservableCollection<CommTestTool.Application.Services.MonitorEntry> History { get; } = [];

    [ObservableProperty] private DeviceModel?  _selectedDevice;
    [ObservableProperty] private CommandModel? _selectedCommand;
    [ObservableProperty] private StepModel?    _selectedPollStep;

    // 選択されたpollステップから自動入力（変更可）
    [ObservableProperty] private string  _target        = "";
    [ObservableProperty] private string  _connectionId  = "";

    // パラメータ入力（コマンドのパラメータ一覧から）
    public ObservableCollection<ParamValueItem> ParamValues { get; } = [];

    // 複数ノード最新値表示
    public ObservableCollection<MonitorNodeValue> NodeValues { get; } = [];
    [ObservableProperty] private bool _hasMultipleNodes;

    [ObservableProperty] private bool     _isRunning;
    [ObservableProperty] private string?  _currentValue;
    [ObservableProperty] private string?  _currentError;
    [ObservableProperty] private bool     _hasError;
    [ObservableProperty] private DateTime? _lastUpdated;
    [ObservableProperty] private string   _statusMessage = "停止中";

    private MainViewModel? _mainVM;

    public MonitorViewModel(CommTestTool.Application.Services.LogService log,
        DeviceListViewModel devices,
        MainViewModel? mainVM = null)
    {
        _log     = log;
        _devices = devices;
        _mainVM  = mainVM;
        _ = _devices.Devices;

        // Devicesコレクションの変更を監視して監視側も自動更新
        _devices.Devices.CollectionChanged += (_, e) =>
        {
            DeviceModel? updated = null;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace ||
                e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                updated = e.NewItems?[0] as DeviceModel;
            if (updated == null) return;
            if (SelectedDevice?.Id == updated.Id)
                SelectedDevice = updated;
        };
    }

    partial void OnSelectedDeviceChanged(DeviceModel? value)
    {
        var prevCommandId  = SelectedCommand?.Id;
        var prevPollStepId = SelectedPollStep?.Description;
        AvailableCommands.Clear();
        AvailablePollSteps.Clear();
        SelectedCommand  = null;
        SelectedPollStep = null;
        if (value == null) return;
        foreach (var c in value.Commands.Where(c => c.Steps.Any(s => s.Action == "poll")))
            AvailableCommands.Add(c);
        // 以前と同じコマンドIDがあれば再選択
        if (prevCommandId != null)
        {
            var match = AvailableCommands.FirstOrDefault(c => c.Id == prevCommandId);
            if (match != null) SelectedCommand = match;
        }
    }

    partial void OnSelectedCommandChanged(CommandModel? value)
    {
        AvailablePollSteps.Clear();
        SelectedPollStep = null;
        ParamValues.Clear();
        if (value == null) return;
        foreach (var s in value.Steps.Where(s => s.Action == "poll"))
            AvailablePollSteps.Add(s);
        foreach (var p in value.Parameters)
            ParamValues.Add(new ParamValueItem(p, ""));
    }

    partial void OnSelectedPollStepChanged(StepModel? value)
    {
        if (value == null) return;
        // pollステップの設定を自動入力
        var nodeIds = value.Nodes?.Count > 1
            ? string.Join(", ", value.Nodes.Select(n => n.NodeId))
            : value.NodeId ?? value.Address ?? "";
        Target       = nodeIds;
        ConnectionId = value.ConnectionId ?? "";

        // 複数ノード表示エリアを更新
        NodeValues.Clear();
        if (value.Nodes?.Count > 1)
        {
            foreach (var n in value.Nodes)
                NodeValues.Add(new MonitorNodeValue(n.NodeId));
            HasMultipleNodes = true;
        }
        else
        {
            HasMultipleNodes = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (SelectedDevice == null || string.IsNullOrWhiteSpace(ConnectionId) ||
            string.IsNullOrWhiteSpace(Target)) return;

        var conn = SelectedDevice.Connections.FirstOrDefault(c => c.Id == ConnectionId);
        if (conn == null) return;

        // パラメータ値を辞書に変換
        var paramVals = ParamValues.ToDictionary(p => p.Parameter.Name, p => p.Value);

        // 間隔・タイムアウトをパラメータ名 or 直接値から解決
        var step = SelectedPollStep;
        int intervalMs = ResolveIntParam(step?.IntervalMsParam, paramVals, 1000);
        int readTimeoutMs = ResolveIntParam(step?.ReadTimeoutParam, paramVals, 3000);

        // 複数ノード対応
        var targets = step?.Nodes?.Count > 1
            ? step.Nodes.Select(n => n.NodeId).ToList()
            : null;

        var config = new CommTestTool.Application.Services.MonitorConfig(
            SelectedDevice.Id, SelectedDevice.Name,
            ConnectionId, Target.Trim(),
            intervalMs, readTimeoutMs, targets);

        var adapter = CommTestTool.Infrastructure.Adapters.AdapterFactory.Create(conn);
        _channel = new CommTestTool.Application.Services.MonitorChannel(config, adapter, _log);

        _channel.EntryReceived += entry =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                CurrentValue  = entry.IsError ? null : entry.Value;
                CurrentError  = entry.ErrorMessage;
                HasError      = entry.IsError;
                LastUpdated   = entry.Timestamp;
                StatusMessage = entry.IsError
                    ? $"❌ {entry.ErrorMessage}"
                    : $"✅ 監視中  最終取得: {entry.Timestamp:HH:mm:ss.fff}";

                // 接続失敗（MonitorChannel が return して IsRunning=false になった場合）
                // → ViewModel 側の IsRunning も false にしてボタンを復帰させる
                if (entry.IsError && !(_channel?.IsRunning ?? false))
                {
                    IsRunning = false;
                    StartCommand.NotifyCanExecuteChanged();
                    StopCommand.NotifyCanExecuteChanged();
                    _mainVM?.NotifyRunningChanged();
                }

                // 複数ノードの場合は各ノードの最新値を更新
                if (HasMultipleNodes && !entry.IsError && entry.Value != null)
                {
                    try
                    {
                        var dict = System.Text.Json.JsonSerializer
                            .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(entry.Value);
                        if (dict != null)
                            foreach (var nv in NodeValues)
                                if (dict.TryGetValue(nv.NodeId, out var el))
                                    nv.Value = el.ToString();
                    }
                    catch { /* JSON解析失敗時は CurrentValue のみ更新 */ }
                }
                else if (HasMultipleNodes && entry.IsError)
                {
                    foreach (var nv in NodeValues)
                    { nv.HasError = true; nv.ErrorMessage = entry.ErrorMessage; }
                }

                History.Insert(0, entry);
                if (History.Count > 1000) History.RemoveAt(History.Count - 1);
            });

        IsRunning     = true;
        StatusMessage = "監視開始中...";
        await _channel.StartAsync();
    }

    /// <summary>パラメータ名→値辞書から int を解決。パラメータ名が空か変換失敗時は fallback を返す</summary>
    private static int ResolveIntParam(string? paramName, Dictionary<string, string> vals, int fallback)
    {
        if (string.IsNullOrEmpty(paramName)) return fallback;
        if (vals.TryGetValue(paramName, out var str) && int.TryParse(str, out var v)) return v;
        return fallback;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (_channel == null) return;
        await _channel.StopAsync();
        await _channel.DisposeAsync();
        _channel      = null;
        IsRunning     = false;
        StatusMessage = "停止しました";
    }

    [RelayCommand]
    private void ClearHistory() => History.Clear();

    private bool CanStart() => !IsRunning;
    private bool CanStop()  => IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        _mainVM?.NotifyRunningChanged();
    }

    public async Task DisposeAsync()
    {
        if (_channel != null)
        {
            await _channel.StopAsync();
            await _channel.DisposeAsync();
        }
    }
}

// ─── MonitorNodeValue（複数ノード監視用 最新値表示アイテム）────────────────────
public partial class MonitorNodeValue : ObservableObject
{
    public string NodeId { get; }
    [ObservableProperty] private string? _value;
    [ObservableProperty] private bool    _hasError;
    [ObservableProperty] private string? _errorMessage;
    public MonitorNodeValue(string nodeId) { NodeId = nodeId; }
}

// ─── ConnectionEditItem（接続口インライン編集用ObservableObject）─────────────
public partial class ConnectionEditItem : ViewModelBase
{
    private readonly DeviceDetailViewModel _parent;
    private readonly ConnectionModel?      _original;

    [ObservableProperty] private string  _id;
    [ObservableProperty] private string  _protocol;
    [ObservableProperty] private string  _timeoutMs;
    [ObservableProperty] private string  _endpoint;
    [ObservableProperty] private string  _broker;
    [ObservableProperty] private string  _mqttPort;
    [ObservableProperty] private string  _publishTopic;
    [ObservableProperty] private string  _subscribeTopic;
    [ObservableProperty] private string  _host;
    [ObservableProperty] private string  _port;
    [ObservableProperty] private string  _stationNo;
    // OPC-UA 認証設定
    [ObservableProperty] private string  _opcAuthMode = Domain.Models.AuthMode.Anonymous;
    [ObservableProperty] private string  _opcUserName = "";
    [ObservableProperty] private string  _opcCertFile = "";  // 証明書ファイル名（certs/フォルダ基準）
    private string _opcPassword     = "";  // PasswordBoxはバインド不可のためcode-behind経由で設定
    private string _opcCertPassword = "";  // 証明書パスワード（同上）
    public  string OpcPassword     { get => _opcPassword;     set => SetProperty(ref _opcPassword, value); }
    public  string OpcCertPassword { get => _opcCertPassword; set => SetProperty(ref _opcCertPassword, value); }

    // パネル表示制御（プロトコル変更時に通知）
    public bool ShowEndpoint     => Protocol is "opcua" or "mtconnect";
    public bool ShowMqtt         => Protocol == "mqtt";
    public bool ShowHostPort     => Protocol is "tcp" or "focas2" or "ospapi";
    public bool ShowSlmp         => Protocol == "slmp";
    public bool IsStub           => Protocol is "ospapi";  // focas2は実装済み
    public bool IsMtConnect      => Protocol == "mtconnect";
    public bool ShowOpcAuth      => Protocol == "opcua";
    public bool ShowUsernameAuth => Protocol == "opcua" && OpcAuthMode == Domain.Models.AuthMode.Username;
    public bool ShowCertAuth     => Protocol == "opcua" && OpcAuthMode == Domain.Models.AuthMode.Certificate;

    partial void OnProtocolChanged(string value)
    {
        OnPropertyChanged(nameof(ShowEndpoint));
        OnPropertyChanged(nameof(ShowMqtt));
        OnPropertyChanged(nameof(ShowHostPort));
        OnPropertyChanged(nameof(ShowSlmp));
        OnPropertyChanged(nameof(IsStub));
        OnPropertyChanged(nameof(IsMtConnect));
        OnPropertyChanged(nameof(ShowOpcAuth));
        OnPropertyChanged(nameof(ShowUsernameAuth));
        OnPropertyChanged(nameof(ShowCertAuth));
    }

    partial void OnOpcAuthModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowUsernameAuth));
        OnPropertyChanged(nameof(ShowCertAuth));
    }

    public ConnectionEditItem(DeviceDetailViewModel parent, ConnectionModel conn)
    {
        _parent   = parent;
        _original = conn;
        _id             = conn.Id;
        _protocol       = conn.Protocol;
        _timeoutMs      = conn.TimeoutMs.ToString();
        _endpoint       = conn.Endpoint ?? "";
        _broker         = conn.Broker ?? "";
        _mqttPort       = conn.Port?.ToString() ?? "1883";
        _publishTopic   = conn.PublishTopic ?? "";
        _subscribeTopic = conn.SubscribeTopic ?? "";
        _host           = conn.Host ?? "";
        _port           = conn.Port?.ToString() ?? "";
        _stationNo      = conn.StationNo?.ToString() ?? "";
        // 認証設定（OPC-UA）
        _opcAuthMode = conn.OpcAuthMode ?? Domain.Models.AuthMode.Anonymous;
        _opcUserName = conn.OpcUserName ?? "";
        _opcPassword = conn.OpcPassword ?? "";
        _opcCertFile     = conn.OpcCertFile     ?? "";
        _opcCertPassword = conn.OpcCertPassword ?? "";
    }

    public ConnectionModel ToModel()
    {
        int timeout = int.TryParse(TimeoutMs, out var t) ? t : 5000;
        return Protocol switch
        {
            "opcua" => new ConnectionModel(Id.Trim(), Protocol, timeout,
                Endpoint: Endpoint.Trim(),
                OpcAuthMode:  OpcAuthMode == Domain.Models.AuthMode.Anonymous ? null : OpcAuthMode,
                OpcUserName:  OpcAuthMode == Domain.Models.AuthMode.Username  ? OpcUserName.Trim() : null,
                OpcPassword:  OpcAuthMode == Domain.Models.AuthMode.Username  ? OpcPassword : null,
                OpcCertFile:     OpcAuthMode == Domain.Models.AuthMode.Certificate ? OpcCertFile.Trim()     : null,
                OpcCertPassword: OpcAuthMode == Domain.Models.AuthMode.Certificate ? OpcCertPassword : null),
            "mtconnect" => new ConnectionModel(Id.Trim(), Protocol, timeout,
                Endpoint: Endpoint.Trim()),
            "mqtt" => new ConnectionModel(Id.Trim(), Protocol, timeout,
                Broker: Broker.Trim(),
                Port: int.TryParse(MqttPort, out var mp) ? mp : 1883,
                PublishTopic: PublishTopic.Trim(),
                SubscribeTopic: SubscribeTopic.Trim()),
            "slmp" => new ConnectionModel(Id.Trim(), Protocol, timeout,
                Host: Host.Trim(),
                Port: int.TryParse(Port, out var sp) ? sp : 5007),
            _ => new ConnectionModel(Id.Trim(), Protocol, timeout,
                Host: Host.Trim(),
                Port: int.TryParse(Port, out var p) ? p : null)
        };
    }

    [RelayCommand]
    private void Apply()
    {
        var newConn = ToModel();
        if (_original != null)
            _parent.ReplaceConnection(_original, newConn);
        else
            _parent.Connections.Add(newConn);
        _parent.SelectedConnection     = null;
        _parent.SelectedConnectionItem = null;
        _parent.SubDetailViewModel     = null;
    }

    [RelayCommand]
    private void Discard()
    {
        _parent.SelectedConnection     = null;
        _parent.SelectedConnectionItem = null;
        _parent.SubDetailViewModel     = null;
    }
}

