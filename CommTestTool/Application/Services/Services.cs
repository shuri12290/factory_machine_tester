using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using CommTestTool.Domain.Interfaces;
using CommTestTool.Domain.Models;
using CommTestTool.Infrastructure.Adapters;

namespace CommTestTool.Application.Services;

// ─── LogService ───────────────────────────────────────────────────────────
public class LogService(IAppPaths paths) : ILogService
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public event Action<LogEntry>? EntryAdded;

    public void Write(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            File.AppendAllText(paths.DailyLogFile(entry.Timestamp),
                               entry.ToString() + Environment.NewLine);
        }
        EntryAdded?.Invoke(entry);
    }

    public void Clear() { lock (_lock) _entries.Clear(); }

    // ヘルパー
    public void Info   (string dev, string action, string msg, string? raw = null)
        => Write(new LogEntry(DateTime.Now, dev, action, LogLevel.Info,    msg, raw));
    public void Success(string dev, string action, string msg, string? raw = null)
        => Write(new LogEntry(DateTime.Now, dev, action, LogLevel.Success, msg, raw));
    public void Error  (string dev, string action, string msg, string? detail = null)
        => Write(new LogEntry(DateTime.Now, dev, action, LogLevel.Error,   msg, ErrorDetail: detail));
}

// ─── StepExecutionEngine ──────────────────────────────────────────────────
public class StepExecutionEngine(LogService log)
{
    public async Task<CommandResult> ExecuteAsync(
        CommandModel command,
        DeviceModel  device,
        IReadOnlyDictionary<string,string> paramValues,
        Func<string, CancellationToken, Task<IProtocolAdapter>> getAdapter,
        IReadOnlyDictionary<string,string>? scenarioVars,
        IProgress<StepResult>? progress,
        CancellationToken ct)
    {
        // 変数辞書（パラメータ + シナリオ変数）
        var vars = new Dictionary<string, string>(paramValues);
        if (scenarioVars != null)
            foreach (var kv in scenarioVars) vars[kv.Key] = kv.Value;

        // ステップが0件は設定ミスとして扱う
        if (!command.Steps.Any())
            return CommandResult.Fail($"コマンド「{command.Name}」にステップが登録されていません。");

        var results = new List<StepResult>();

        for (int i = 0; i < command.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = command.Steps[i];
            var sr   = new StepResult(i, step.Description, StepStatus.Running);
            progress?.Report(sr);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var raw = await ExecuteStep(step, device, command, vars, getAdapter, ct);
                sw.Stop();
                sr = sr with { Status = StepStatus.Success, Duration = sw.Elapsed, ReceivedData = raw };
                log.Success(device.Id, step.Action, step.Description, raw);
            }
            catch (OperationCanceledException)
            {
                sr = sr with { Status = StepStatus.Error, ErrorMessage = "キャンセル" };
                results.Add(sr); progress?.Report(sr);
                // 残りをSkip
                for (int j = i + 1; j < command.Steps.Count; j++)
                    results.Add(new StepResult(j, command.Steps[j].Description, StepStatus.Skipped));
                return CommandResult.Fail("ユーザーキャンセル（設備は動作継続中の可能性あり）");
            }
            catch (Exception ex)
            {
                sw.Stop();
                sr = sr with { Status = StepStatus.Error, ErrorMessage = ex.Message };
                log.Error(device.Id, step.Action, step.Description, ex.Message);
                results.Add(sr); progress?.Report(sr);
                for (int j = i + 1; j < command.Steps.Count; j++)
                    results.Add(new StepResult(j, command.Steps[j].Description, StepStatus.Skipped));
                return new CommandResult(false, results, vars, ex.Message);
            }

            results.Add(sr);
            progress?.Report(sr);
        }

        return new CommandResult(true, results, vars);
    }

    private async Task<string?> ExecuteStep(
        StepModel step, DeviceModel device,
        CommandModel? command,
        Dictionary<string,string> vars,
        Func<string, CancellationToken, Task<IProtocolAdapter>> getAdapter,
        CancellationToken ct)
    {
        if (step.Action == "wait")
        {
            // DurationMsParamが設定されていればパラメータから値を取得、なければDurationMsを使用
            int duration = 1000;
            if (!string.IsNullOrEmpty(step.DurationMsParam) &&
                vars.TryGetValue(step.DurationMsParam, out var dStr) &&
                int.TryParse(dStr, out var dv))
                duration = dv;
            else if (step.DurationMs.HasValue)
                duration = step.DurationMs.Value;
            await Task.Delay(duration, ct);
            return null;
        }

        // 接続口IDが未設定のステップはエラー
        if (string.IsNullOrEmpty(step.ConnectionId))
            throw new InvalidOperationException(
                $"ステップ「{step.Description}」に接続口が設定されていません。コマンドを編集して接続口を選択してください。");

        var adapter = await getAdapter(step.ConnectionId, ct);
        // TimeoutMsParamが設定されていればパラメータから取得、なければTimeoutMs→接続口デフォルトの順
        var timeout =
            (!string.IsNullOrEmpty(step.TimeoutMsParam) &&
             vars.TryGetValue(step.TimeoutMsParam, out var toStr) &&
             int.TryParse(toStr, out var toParsed))
            ? toParsed
            : step.TimeoutMs
              ?? device.Connections.FirstOrDefault(c => c.Id == step.ConnectionId)?.TimeoutMs
              ?? 5000;

        // プロトコル判定用（SLMP複数対応で使用）
        var conn = device.Connections.FirstOrDefault(c => c.Id == step.ConnectionId);

        switch (step.Action == "focas_read" ? "read" : step.Action == "focas_write" ? "write" : step.Action)
        {
            case "send":
            {
                // Payload: キー名→パラメータ名のマッピング
                // 値はvarsからパラメータ名で引く。未入力（空文字）はnull送信
                var sendPayload = (step.Payload ?? new Dictionary<string,string>())
                    .ToDictionary(
                        kv => kv.Key,
                        kv => vars.TryGetValue(kv.Value, out var v) && !string.IsNullOrEmpty(v)
                              ? v : "null");
                await adapter.SendAsync(sendPayload, ct);
                log.Info(device.Id, "send", step.Description, JsonSerializer.Serialize(sendPayload));
                return null;
            }
            case "write":
            {
                string target;
                Dictionary<string,string> writePayload;

                if (step.Nodes != null && step.Nodes.Count > 1 && conn.Protocol == "slmp")
                {
                    // ── SLMP 複数アドレス順次書き込み ────────────────────────
                    foreach (var node in step.Nodes)
                    {
                        var pVal = !string.IsNullOrEmpty(node.Parameter) &&
                                   vars.TryGetValue(node.Parameter, out var pv) ? pv : "null";
                        var pType = command?.Parameters
                            .FirstOrDefault(p => p.Name == node.Parameter)?.Type ?? "string";
                        var slmpPayload = new Dictionary<string,string>
                            { ["value"] = pVal, ["type"] = pType };
                        await adapter.WriteAsync(node.NodeId, slmpPayload, ct);
                        log.Info(device.Id, "write", step.Description, $"{node.NodeId} = {pVal}");
                    }
                    return null;
                }

                if (step.Nodes != null && step.Nodes.Count > 1)
                {
                    // ── 複数ノード一括書き込み（OPC-UA 専用） ─────────────────
                    // Nodes リストの各エントリからパラメータ値を解決して
                    // WriteNodes API に渡す形式のpayloadを構築する
                    target = string.Join(", ", step.Nodes.Select(n => n.NodeId));
                    writePayload = new Dictionary<string,string>();
                    for (int ni = 0; ni < step.Nodes.Count; ni++)
                    {
                        var node  = step.Nodes[ni];
                        var pVal  = !string.IsNullOrEmpty(node.Parameter) &&
                                    vars.TryGetValue(node.Parameter, out var pv) ? pv : "null";
                        var pType = command?.Parameters
                            .FirstOrDefault(p => p.Name == node.Parameter)?.Type ?? "string";
                        writePayload[node.NodeId]    = pVal;
                        writePayload[$"type_{ni}"]   = pType;
                    }
                    log.Info(device.Id, "write", step.Description,
                        string.Join(", ", step.Nodes.Select(n => $"{n.NodeId}={vars.GetValueOrDefault(n.Parameter ?? "", "null")}")));
                }
                else
                {
                    // ── 単一ノード書き込み（従来動作） ──────────────────────────
                    target = step.Nodes?.Count == 1
                        ? step.Nodes[0].NodeId
                        : step.NodeId ?? step.Address ?? "";
                    var paramName  = step.Nodes?.Count == 1 ? step.Nodes[0].Parameter : step.Parameter;
                    var paramValue = !string.IsNullOrEmpty(paramName) &&
                                     vars.TryGetValue(paramName, out var pv) &&
                                     !string.IsNullOrEmpty(pv) ? pv : "null";
                    var paramType  = command?.Parameters
                        .FirstOrDefault(p => p.Name == paramName)?.Type ?? "string";
                    writePayload = new Dictionary<string,string>
                    {
                        ["value"] = paramValue,
                        ["type"]  = paramType
                    };
                    log.Info(device.Id, "write", step.Description, $"{target} = {paramValue} ({paramType})");
                }
                await adapter.WriteAsync(target, writePayload, ct);
                return null;
            }
            case "receive":
            {
                var raw = await adapter.ReceiveAsync(timeout, ct);
                log.Info(device.Id, "receive", step.Description, raw);
                EvalConditions(step, raw, vars, adapter.ConnectionId);
                DoCapture(step, raw, vars);
                return raw;
            }
            case "read":
            {
                string raw;
                if (step.Nodes?.Count > 1 && conn.Protocol == "slmp")
                {
                    // ── SLMP 複数アドレス順次読み取り → dict形式で返す ────────
                    var dict = new System.Collections.Generic.Dictionary<string, object?>();
                    foreach (var node in step.Nodes)
                    {
                        var val = await adapter.ReadAsync(node.NodeId, timeout, ct);
                        dict[node.NodeId] = val;
                    }
                    raw = System.Text.Json.JsonSerializer.Serialize(dict);
                }
                else
                {
                    // Nodes リストがある場合はカンマ結合してReadNodes APIへ渡す（OPC-UA）
                    var target = step.Nodes?.Count > 1
                        ? string.Join(", ", step.Nodes.Select(n => n.NodeId))
                        : step.NodeId ?? step.Address ?? "";
                    raw = await adapter.ReadAsync(target, timeout, ct);
                }
                log.Info(device.Id, "read", step.Description, raw);
                EvalConditions(step, raw, vars, adapter.ConnectionId);
                DoCapture(step, raw, vars);
                return raw;
            }
            case "poll":
            {
                // Nodes リストがある場合はカンマ結合（OPC-UA 複数ノード対応）
                var target = step.Nodes?.Count > 1
                    ? string.Join(", ", step.Nodes.Select(n => n.NodeId))
                    : step.NodeId ?? step.Address ?? "";
                // パラメータ名から実行時の値を取得
                var interval = vars.TryGetValue(step.IntervalMsParam ?? "", out var ivStr) &&
                               int.TryParse(ivStr, out var iv) ? iv : 1000;
                var readTimeout = vars.TryGetValue(step.ReadTimeoutParam ?? "", out var rtStr) &&
                               int.TryParse(rtStr, out var rt) ? rt : 3000;
                var pollTimeout = vars.TryGetValue(step.TimeoutParam ?? "", out var ptStr) &&
                               int.TryParse(ptStr, out var pt) ? pt : timeout;
                var deadline    = DateTime.UtcNow.AddMilliseconds(pollTimeout);
                int attempt     = 0;
                string? lastRaw = null;

                // 条件の説明文を生成（エラーメッセージ用）
                var condDesc = step.Conditions != null && step.Conditions.Any()
                    ? string.Join(" AND ", step.Conditions.Select(c =>
                        c.Operator == "bit_check"
                            ? $"bit{c.Bit}={c.Value}"
                            : $"{c.Field ?? "value"}={c.Value}"))
                    : "（条件なし）";

                log.Info(device.Id, "poll開始",
                    $"{step.Description} 間隔:{interval}ms 通信タイムアウト:{readTimeout}ms 制限時間:{pollTimeout}ms");

                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    try
                    {
                        // 通信タイムアウトは ReadTimeoutMs で制御
                        lastRaw = await adapter.ReadAsync(target, readTimeout, ct);
                        log.Info(device.Id, $"poll #{attempt}", lastRaw ?? "");

                        // 条件チェック（条件なしの場合は取得できれば達成）
                        if (step.Conditions == null || !step.Conditions.Any())
                        {
                            DoCapture(step, lastRaw ?? "", vars);
                            log.Info(device.Id, "poll完了",
                                $"{step.Description} ({attempt}回目で取得完了)");
                            return lastRaw;
                        }

                        EvalConditions(step, lastRaw ?? "", vars, adapter.ConnectionId);
                        DoCapture(step, lastRaw ?? "", vars);
                        log.Info(device.Id, "poll完了",
                            $"{step.Description} ({attempt}回目で条件達成: {condDesc})");
                        return lastRaw;
                    }
                    catch (ConditionException)
                    {
                        // 条件未達成 → インターバル待機して再試行
                        var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (remaining <= 0) break;
                        await Task.Delay(Math.Min(interval, remaining), ct);
                    }
                    catch (CommTimeoutException)
                    {
                        // 通信タイムアウト（1回の読み取りが ReadTimeoutMs を超えた）→ そのままスロー
                        throw;
                    }
                    catch (ConnectionException)
                    {
                        // 通信失敗 → そのままスロー
                        throw;
                    }
                }

                // ループを抜けた = 制限時間内に条件を満たさなかった
                throw new ConditionTimeoutException(adapter.ConnectionId, pollTimeout, condDesc);
            }
            case "opcua_method":
            {
                var objectId = step.NodeId ?? "";
                var methodId = step.MethodId ?? "";
                if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(methodId))
                    throw new InvalidOperationException("opcua_method: オブジェクトNodeIDとメソッドNodeIDを設定してください。");

                if (adapter is not CommTestTool.Infrastructure.Adapters.OpcUaAdapter opcAdapter)
                    throw new InvalidOperationException("opcua_method は OPC-UA 接続口にのみ使用できます。");

                // 入力引数: payload の順序通りに渡す
                // キー形式: "0:string"（idx:型名）または旧形式 "string"
                var resolvedPayload = (step.Payload ?? new Dictionary<string,string>())
                    .Select(kv => new
                    {
                        TypeName = kv.Key.Contains(':') ? kv.Key.Split(':')[1] : kv.Key,
                        Value    = vars.TryGetValue(kv.Value, out var pv) && !string.IsNullOrEmpty(pv) ? pv : ""
                    })
                    .ToDictionary(t => t.TypeName, t => t.Value);

                var raw = await opcAdapter.CallMethodAsync(
                    objectId, methodId, resolvedPayload, timeout, ct);

                log.Info(device.Id, "opcua_method",
                    step.Description, $"{methodId} → {raw}");
                DoCapture(step, raw, vars);
                return raw;
            }
            default:
                throw new InvalidOperationException($"未知のaction: {step.Action}");
        }
    }

    private static IReadOnlyDictionary<string,string> Resolve(
        IReadOnlyDictionary<string,string> payload,
        Dictionary<string,string> vars)
    {
        return payload.ToDictionary(
            kv => kv.Key,
            kv => Regex.Replace(kv.Value, @"\{(\w+)\}", m =>
                vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value));
    }

    public static string ResolveStr(string template, Dictionary<string,string> vars) =>
        Regex.Replace(template, @"\{(\w+)\}", m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private static void EvalConditions(StepModel step, string raw,
        Dictionary<string,string> vars, string connId)
    {
        foreach (var cond in step.Conditions ?? [])
        {
            var expected = ResolveStr(cond.Value, vars);
            if (cond.Operator == "bit_check")
            {
                if (!TryParseBinary(raw, out var intVal))
                    throw new ConditionException(connId, $"bit{cond.Bit}={expected}", $"parse失敗:{raw}");
                var actual = ((intVal >> (cond.Bit ?? 0)) & 1).ToString();
                if (actual != expected)
                    throw new ConditionException(connId, $"bit{cond.Bit}={expected}", $"bit{cond.Bit}={actual}");
            }
            else
            {
                var actual = ExtractField(raw, cond.Field, step.Parse) ?? raw;
                var ok = cond.Operator switch
                {
                    "not_equals" => actual != expected,
                    "contains"   => actual.Contains(expected, StringComparison.Ordinal),
                    _            => actual == expected,  // "equals" またはデフォルト
                };
                if (!ok)
                    throw new ConditionException(connId,
                        $"{cond.Operator}({expected})", actual);
            }
        }
    }

    private static void DoCapture(StepModel step, string raw, Dictionary<string,string> vars)
    {
        foreach (var cap in step.Capture ?? [])
        {
            var val = ExtractField(raw, cap.Field, step.Parse);
            if (val != null) vars[cap.As] = val;
        }
    }

    private static string? ExtractField(string raw, string? field, ParseConfig? parse)
    {
        if (parse == null || parse.Format == "plain") return raw;
        try
        {
            return parse.Format switch
            {
                "json" when field != null =>
                    JsonDocument.Parse(raw).RootElement
                        .TryGetProperty(field, out var el)
                        ? (el.GetString() ?? el.GetRawText())
                        : null,
                "xml" when parse.XPath != null =>
                    MTConnectAdapter.ExtractXPath(raw, parse.XPath),
                // binary: SLMPなどの数値文字列をそのまま返す。
                // フィールド指定がある場合はbit位置（field="0"ならbit0の値）を返す
                "binary" when field != null && int.TryParse(field, out var bit) =>
                    TryParseBinary(raw, out var bval)
                        ? (((bval >> bit) & 1).ToString())
                        : raw,
                "binary" => raw,  // フィールド指定なし: 数値文字列をそのまま返す
                _ => raw
            };
        }
        catch { return null; }
    }

    private static bool TryParseBinary(string raw, out int value)
    {
        value = 0;
        var s = raw.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out value)
            : int.TryParse(s, out value);
    }
}

// ─── CommunicationManager ─────────────────────────────────────────────────
public class CommunicationManager(LogService log, CommTestTool.Domain.Interfaces.IAppPaths? paths = null) : IAsyncDisposable
{
    private readonly StepExecutionEngine _engine = new(log);
    private readonly Dictionary<string, IProtocolAdapter> _pool = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<CommandResult> RunCommandAsync(
        CommandModel command, DeviceModel device,
        IReadOnlyDictionary<string,string> paramValues,
        IReadOnlyDictionary<string,string>? scenarioVars = null,
        IProgress<StepResult>? progress = null,
        CancellationToken ct = default)
    {
        log.Info(device.Id, "コマンド開始", command.Name);
        try
        {
            var result = await _engine.ExecuteAsync(
                command, device, paramValues,
                (connId, token) => GetOrConnect(connId, device, token),
                scenarioVars, progress, ct);

            await DisconnectNonTcp(device);

            if (result.IsSuccess) log.Success(device.Id, "コマンド完了", command.Name);
            else                  log.Error  (device.Id, "コマンド失敗", command.Name, result.ErrorMessage);
            return result;
        }
        catch (Exception ex)
        {
            await DisconnectNonTcp(device);
            log.Error(device.Id, "コマンドエラー", command.Name, ex.Message);
            return CommandResult.Fail(ex.Message);
        }
    }

    public async Task<List<(ScenarioStepModel Step, CommandResult? Result)>> RunScenarioAsync(
        ScenarioModel scenario,
        DeviceModel   device,
        Dictionary<string,string>? paramValues = null,
        IProgress<(int, ScenarioStepModel, StepStatus)>? scenarioProgress = null,
        IProgress<StepResult>? stepProgress = null,
        CancellationToken ct = default)
    {
        var results  = new List<(ScenarioStepModel, CommandResult?)>();
        // 実行時パラメータを初期値としてseenVarsに設定
        var scenVars = new Dictionary<string, string>(paramValues ?? []);
        log.Info(device.Id, "シナリオ開始", scenario.Name);

        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = scenario.Steps[i];
            scenarioProgress?.Report((i, step, StepStatus.Running));

            if (step.Type == "wait")
            {
                await Task.Delay(step.DurationMs ?? 0, ct);
                scenarioProgress?.Report((i, step, StepStatus.Success));
                results.Add((step, null));
                continue;
            }

            var command = device.Commands.FirstOrDefault(c => c.Id == step.CommandId);
            if (command == null)
            {
                var err = CommandResult.Fail($"コマンド不明: {step.CommandId}");
                scenarioProgress?.Report((i, step, StepStatus.Error));
                results.Add((step, err));
                if (step.OnError == "stop") break;
                continue;
            }

            // ステップに明示されたパラメータ + シナリオの実行時パラメータをマージ
            var stepParams = new Dictionary<string,string>(scenVars);
            foreach (var kv in step.Parameters ?? new Dictionary<string,string>())
                stepParams[kv.Key] = kv.Value;

            var cmdResult = await RunCommandAsync(command, device, stepParams,
                                                  scenVars, stepProgress, ct);

            foreach (var cap in step.Capture ?? [])
                if (cmdResult.CapturedVars.TryGetValue(cap.Field, out var v))
                    scenVars[cap.As] = v;

            var status = cmdResult.IsSuccess ? StepStatus.Success : StepStatus.Error;
            scenarioProgress?.Report((i, step, status));
            results.Add((step, cmdResult));

            if (!cmdResult.IsSuccess && step.OnError == "stop") break;
        }

        log.Info(device.Id, "シナリオ完了", scenario.Name);
        return results;
    }

    private async Task<IProtocolAdapter> GetOrConnect(
        string connId, DeviceModel device, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_pool.TryGetValue(connId, out var existing) && existing.IsConnected)
                return existing;

            var conn = device.Connections.FirstOrDefault(c => c.Id == connId)
                ?? throw new InvalidOperationException($"接続口不明: {connId}");

            var adapter = AdapterFactory.Create(conn, paths);
            await adapter.ConnectAsync(ct);
            _pool[connId] = adapter;
            log.Info(device.Id, "接続", $"{connId} ({conn.Protocol})");
            return adapter;
        }
        finally { _lock.Release(); }
    }

    private async Task DisconnectNonTcp(DeviceModel device)
    {
        foreach (var conn in device.Connections.Where(c => c.Protocol != "tcp"))
        {
            if (_pool.TryGetValue(conn.Id, out var adapter))
            {
                await adapter.DisconnectAsync();
                _pool.Remove(conn.Id);
                log.Info(device.Id, "切断", conn.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in _pool.Values) await a.DisposeAsync();
        _pool.Clear();
        _lock.Dispose();
    }
}

// ─── MonitorService（独立監視機能）────────────────────────────────────────
public class MonitorEntry(
    DateTime Timestamp,
    string   DeviceId,
    string   ConnectionId,
    string   Target,
    string?  Value,
    bool     IsError,
    string?  ErrorMessage = null)
{
    public DateTime Timestamp     { get; } = Timestamp;
    public string   DeviceId      { get; } = DeviceId;
    public string   ConnectionId  { get; } = ConnectionId;
    public string   Target        { get; } = Target;
    public string?  Value         { get; } = Value;
    public bool     IsError       { get; } = IsError;
    public string?  ErrorMessage  { get; } = ErrorMessage;

    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} [{DeviceId}] {ConnectionId}/{Target} = " +
        (IsError ? $"ERROR: {ErrorMessage}" : Value ?? "");
}

public record MonitorConfig(
    string              DeviceId,
    string              DeviceName,
    string              ConnectionId,
    string              Target,          // 単一または複数ノード（カンマ区切り）
    int                 IntervalMs,
    int                 ReadTimeoutMs,
    IReadOnlyList<string>? Targets = null); // 複数ノード時に使用（Targetと同期）

public class MonitorChannel : IAsyncDisposable
{
    public MonitorConfig Config  { get; }
    public bool IsRunning        { get; private set; }
    public string? LastValue     { get; private set; }
    public string? LastError     { get; private set; }
    public DateTime? LastUpdated { get; private set; }

    public event Action<MonitorEntry>? EntryReceived;

    private CancellationTokenSource? _cts;
    private readonly IProtocolAdapter _adapter;
    private readonly LogService _log;

    public MonitorChannel(MonitorConfig config, IProtocolAdapter adapter, LogService log)
    {
        Config   = config;
        _adapter = adapter;
        _log     = log;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();   // CancellationTokenSource のリーク防止
        _cts = null;
        await _adapter.DisconnectAsync();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // 接続失敗（ConnectionException等）もエラーとして通知する
            try
            {
                await _adapter.ConnectAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // 接続失敗をUIに通知してループを抜ける
                LastError   = ex.Message;
                LastUpdated = DateTime.Now;
                var connEntry = new MonitorEntry(DateTime.Now, Config.DeviceId,
                    Config.ConnectionId, Config.Target, null, true, $"接続失敗: {ex.Message}");
                _log.Write(new LogEntry(DateTime.Now, Config.DeviceId,
                    "監視エラー", LogLevel.Error, $"接続失敗: {ex.Message}", ErrorDetail: ex.Message));
                EntryReceived?.Invoke(connEntry);
                return;
            }

            _log.Info(Config.DeviceId, "監視開始",
                $"{Config.ConnectionId}/{Config.Target} 間隔:{Config.IntervalMs}ms");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var value = await _adapter.ReadAsync(Config.Target, Config.ReadTimeoutMs, ct);
                    LastValue   = value;
                    LastError   = null;
                    LastUpdated = DateTime.Now;

                    var entry = new MonitorEntry(DateTime.Now, Config.DeviceId,
                        Config.ConnectionId, Config.Target, value, false);
                    _log.Write(new LogEntry(DateTime.Now, Config.DeviceId,
                        "監視取得", LogLevel.Info, $"{Config.Target} = {value}", value));
                    EntryReceived?.Invoke(entry);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError   = ex.Message;
                    LastUpdated = DateTime.Now;
                    var entry = new MonitorEntry(DateTime.Now, Config.DeviceId,
                        Config.ConnectionId, Config.Target, null, true, ex.Message);
                    _log.Write(new LogEntry(DateTime.Now, Config.DeviceId,
                        "監視エラー", LogLevel.Error, ex.Message, ErrorDetail: ex.Message));
                    EntryReceived?.Invoke(entry);
                }

                await Task.Delay(Config.IntervalMs, ct).ContinueWith(_ => { });
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsRunning = false;
            _log.Info(Config.DeviceId, "監視停止", $"{Config.ConnectionId}/{Config.Target}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _adapter.DisposeAsync();
    }
}
