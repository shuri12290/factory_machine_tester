namespace CommTestTool.Domain.Models;

public class ConnectionException(string connectionId, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ConnectionId { get; } = connectionId;
}

public class CommTimeoutException(string connectionId, int timeoutMs)
    : Exception($"タイムアウト ({timeoutMs}ms) - 接続口: {connectionId}")
{
    public string ConnectionId { get; } = connectionId;
    public int    TimeoutMs    { get; } = timeoutMs;
}

public class ConditionException(string connectionId, string expected, string actual)
    : Exception($"条件不一致 - 期待値: {expected} / 実際値: {actual}")
{
    public string ConnectionId { get; } = connectionId;
    public string Expected     { get; } = expected;
    public string Actual       { get; } = actual;
}

public class NotImplementedProtocolException(string protocol)
    : Exception($"プロトコル '{protocol}' は未実装です。")
{
    public string Protocol { get; } = protocol;
}

/// <summary>poll アクションで制限時間内に条件を満たさなかった</summary>
public class ConditionTimeoutException(string connectionId, int timeoutMs, string conditionDesc)
    : Exception($"条件タイムアウト ({timeoutMs}ms 経過) - 条件: {conditionDesc} - 接続口: {connectionId}")
{
    public string ConnectionId   { get; } = connectionId;
    public int    TimeoutMs      { get; } = timeoutMs;
    public string ConditionDesc  { get; } = conditionDesc;
}
