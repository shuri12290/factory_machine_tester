namespace CommTestTool.Domain.Models;

// ── 設備 ──────────────────────────────────────────
public record DeviceModel(
    string Id,
    string Name,
    IReadOnlyList<ConnectionModel> Connections,
    IReadOnlyList<CommandModel>    Commands,
    IReadOnlyList<ScenarioModel>   Scenarios)
{
    public DeviceModel() : this("", "", [], [], []) { }
}

/// <summary>
/// 認証モード。現在は OPC-UA のみ使用。
/// anonymous: 匿名接続（デフォルト）
/// username:  ユーザー名・パスワード認証
/// </summary>
public static class AuthMode
{
    public const string Anonymous   = "anonymous";
    public const string Username    = "username";
    public const string Certificate = "certificate";
}

public record ConnectionModel(
    string Id,
    string Protocol,
    int    TimeoutMs      = 5000,
    string? Endpoint      = null,
    string? Host          = null,
    int?    Port          = null,
    string? Broker        = null,
    string? PublishTopic  = null,
    string? SubscribeTopic= null,
    int?    StationNo     = null,
    // 認証設定（現在はOPC-UAのみ使用）
    string? OpcAuthMode   = null,   // "anonymous"（省略時）/ "username" / "certificate"
    string? OpcUserName   = null,   // ユーザー名（username認証時）
    string? OpcPassword   = null,   // パスワード（username認証時）※平文保存
    string? OpcCertFile     = null,   // 証明書ファイル名（certificate認証時）※certs/フォルダに配置
    string? OpcCertPassword = null)   // 証明書パスワード（certificate認証時）※平文保存
{
    public ConnectionModel() : this("", "") { }
    public bool IsImplemented =>
        Protocol is "opcua" or "mqtt" or "tcp" or "mtconnect" or "slmp" or "focas2";
}

// ── コマンド ──────────────────────────────────────
public record CommandModel(
    string Id,
    string Name,
    IReadOnlyList<ParameterModel> Parameters,
    IReadOnlyList<StepModel>      Steps)
{
    public CommandModel() : this("", "", [], []) { }
}

/// <summary>
/// パラメータ定義。スキーマ（項目名・型）のみ。値は実行時に入力する。
/// </summary>
public record ParameterModel(
    string Name,
    string Label,
    string Type = "string")
{
    public ParameterModel() : this("", "") { }
}

/// <summary>
/// ステップ定義。
/// Payload: キー名→パラメータ名のマッピング（send用）
///   例: { "work_id": "work_id_param" } → 送信JSONの work_id キーに パラメータ work_id_param の値を入れる
/// Parameter: 単一パラメータ名（write用）
///   例: "program_no" → node_idに パラメータ program_no の値を書き込む
/// </summary>
/// <summary>
/// OPC-UA 複数ノード用エントリ。read: NodeId のみ / write: NodeId + Parameter
/// </summary>
public record NodeEntry(string NodeId, string? Parameter = null);

public record StepModel(
    string  Action,
    string  Description   = "",
    string? ConnectionId  = null,
    string? NodeId        = null,   // 単一ノード（後方互換）/ opcua_method: オブジェクトNodeID
    string? MethodId      = null,   // opcua_method: メソッドNodeID
    string? Address       = null,
    int?    TimeoutMs        = null,
    string? TimeoutMsParam   = null,      // receive/read用：タイムアウトのパラメータ名
    int?    DurationMs       = null,
    string? DurationMsParam  = null,      // wait用：待機時間のパラメータ名
    string? IntervalMsParam  = null,      // poll用：ポーリング間隔のパラメータ名
    string? ReadTimeoutParam = null,      // poll用：通信タイムアウトのパラメータ名
    string? TimeoutParam     = null,      // poll用：条件タイムアウトのパラメータ名
    string? Parameter        = null,      // write用：書き込むパラメータ名
    IReadOnlyDictionary<string,string>? Payload    = null, // send用：キー名→パラメータ名
    ParseConfig?                         Parse      = null,
    IReadOnlyList<ConditionModel>?        Conditions = null,
    IReadOnlyList<CaptureModel>?          Capture    = null,
    IReadOnlyList<NodeEntry>?             Nodes      = null)  // OPC-UA複数ノード（listUI用）
{
    public StepModel() : this("send") { }
}

public record ParseConfig(
    string  Format = "plain",
    string? XPath  = null)
{
    public ParseConfig() : this("plain") { }
}

public record ConditionModel(
    string  Operator,
    string  Value,
    string? Field = null,
    int?    Bit   = null)
{
    public ConditionModel() : this("equals", "") { }
}

public record CaptureModel(string Field, string As)
{
    public CaptureModel() : this("", "") { }
}

// ── シナリオ ──────────────────────────────────────
public record ScenarioModel(
    string Id,
    string Name,
    IReadOnlyList<ScenarioStepModel> Steps)
{
    public ScenarioModel() : this("", "", []) { }
}

/// <summary>
/// シナリオステップ。
/// Parameters: 実行時に入力した値をそのまま渡す。
/// YAMLには {変数名} 参照は書かない。値は全て実行時に人間またはシステムが入力する。
/// </summary>
public record ScenarioStepModel(
    string  Type,
    string  Description = "",
    string? CommandId   = null,
    IReadOnlyDictionary<string,string>? Parameters = null,
    IReadOnlyList<CaptureModel>? Capture = null,
    string  OnSuccess   = "next",
    string  OnError     = "stop",
    int?    DurationMs  = null)
{
    public ScenarioStepModel() : this("command") { }
}

// ── 実行結果 ──────────────────────────────────────
public enum StepStatus { Pending, Running, Success, Error, Skipped }
public enum LogLevel   { Info, Success, Warning, Error }

public record LogEntry(
    DateTime  Timestamp,
    string    DeviceId,
    string    Action,
    LogLevel  Level,
    string    Message,
    string?   RawData      = null,
    string?   ErrorDetail  = null)
{
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} [{Level,-7}] [{DeviceId}] {Action}: {Message}" +
        (RawData     != null ? $"\n  → {RawData}"      : "") +
        (ErrorDetail != null ? $"\n  ❌ {ErrorDetail}" : "");
}

public record StepResult(
    int        StepIndex,
    string     Description,
    StepStatus Status       = StepStatus.Pending,
    string?    ReceivedData = null,
    string?    ErrorMessage = null,
    TimeSpan?  Duration     = null);

public record CommandResult(
    bool                               IsSuccess,
    IReadOnlyList<StepResult>          StepResults,
    IReadOnlyDictionary<string,string> CapturedVars,
    string?                            ErrorMessage = null)
{
    public static CommandResult Fail(string msg) =>
        new(false, [], new Dictionary<string, string>(), msg);
}
