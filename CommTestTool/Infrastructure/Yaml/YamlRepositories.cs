using System.IO;
using CommTestTool.Domain.Interfaces;
using CommTestTool.Domain.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CommTestTool.Infrastructure.Yaml;

// ─── YAML DTO ─────────────────────────────────────────────────────────────
internal sealed class DevicesRoot         { public List<DeviceYaml>?   Devices { get; set; } }

internal sealed class DeviceYaml
{
    public string?                    Id          { get; set; }
    public string?                    Name        { get; set; }
    public List<ConnectionYaml>?      Connections { get; set; }
    public List<CommandYaml>?         Commands    { get; set; }
    public List<ScenarioYaml>?        Scenarios   { get; set; }
}
internal sealed class ConnectionYaml
{
    public string? Id              { get; set; }
    public string? Protocol        { get; set; }
    public int?    TimeoutMs       { get; set; }
    public string? Endpoint        { get; set; }
    public string? Host            { get; set; }
    public int?    Port            { get; set; }
    public string? Broker          { get; set; }
    public string? PublishTopic    { get; set; }
    public string? SubscribeTopic  { get; set; }
    public int?    StationNo       { get; set; }
    // 認証設定（OPC-UA専用。anonymous省略可）
    public string? OpcAuthMode     { get; set; }
    public string? OpcUserName     { get; set; }
    public string? OpcPassword     { get; set; }
    public string? OpcCertFile     { get; set; }
    public string? OpcCertPassword { get; set; }
}
internal sealed class CommandYaml
{
    public string?              Id         { get; set; }
    public string?              Name       { get; set; }
    public List<ParameterYaml>? Parameters { get; set; }
    public List<StepYaml>?      Steps      { get; set; }
}
internal sealed class ParameterYaml
{
    public string? Name  { get; set; }
    public string? Label { get; set; }
    public string? Type  { get; set; }
}
internal sealed class StepYaml
{
    public string?                    Action       { get; set; }
    public string?                    Description  { get; set; }
    public string?                    ConnectionId { get; set; }
    public string?                    NodeId       { get; set; }
    public string?                    Address      { get; set; }
    public int?                       TimeoutMs        { get; set; }
    public string?                    TimeoutMsParam   { get; set; }
    public int?                       DurationMs       { get; set; }
    public string?                    DurationMsParam  { get; set; }
    public string?                    IntervalMsParam  { get; set; }
    public string?                    ReadTimeoutParam { get; set; }
    public string?                    TimeoutParam     { get; set; }
    public string?                    Parameter        { get; set; }
    public Dictionary<string,string>? Payload      { get; set; }
    public ParseYaml?                 Parse        { get; set; }
    public List<ConditionYaml>?       Conditions   { get; set; }
    public List<CaptureYaml>?         Capture      { get; set; }
    public List<NodeEntryYaml>?       Nodes        { get; set; }  // OPC-UA複数ノード
}
internal sealed class NodeEntryYaml
{
    public string? NodeId    { get; set; }
    public string? Parameter { get; set; }
}
internal sealed class ParseYaml
{
    public string? Format { get; set; }
    public string? Xpath  { get; set; }
}
internal sealed class ConditionYaml
{
    public string? Operator { get; set; }
    public string? Field    { get; set; }
    public string? Value    { get; set; }
    public int?    Bit      { get; set; }
}
internal sealed class CaptureYaml
{
    public string? Field { get; set; }
    public string? As    { get; set; }
}
internal sealed class ScenarioYaml
{
    public string?                    Id    { get; set; }
    public string?                    Name  { get; set; }
    public List<ScenarioStepYaml>?    Steps { get; set; }
}
internal sealed class ScenarioStepYaml
{
    public string?                    Type        { get; set; }
    public string?                    Description { get; set; }
    public string?                    CommandId   { get; set; }
    public Dictionary<string,string>? Parameters  { get; set; }
    public List<CaptureYaml>?         Capture     { get; set; }
    public string?                    OnSuccess   { get; set; }
    public string?                    OnError     { get; set; }
    public int?                       DurationMs  { get; set; }
}

// ─── リポジトリ実装 ───────────────────────────────────────────────────────
public class YamlDeviceRepository(IAppPaths paths) : IDeviceRepository
{
    private static readonly IDeserializer Des = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties().Build();
    private static readonly ISerializer Ser = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build();

    public IReadOnlyList<DeviceModel> GetAll()
    {
        if (!File.Exists(paths.DevicesYaml)) return [];
        try
        {
            var root = Des.Deserialize<DevicesRoot>(File.ReadAllText(paths.DevicesYaml));
            return root?.Devices?.Select(ToModel).ToList() ?? [];
        }
        catch { return []; }
    }

    public void Save(IReadOnlyList<DeviceModel> devices)
    {
        var root = new DevicesRoot { Devices = devices.Select(ToYaml).ToList() };
        File.WriteAllText(paths.DevicesYaml, Ser.Serialize(root));
    }

    // ─── Domain → YAML DTO ───
    private static DeviceYaml ToYaml(DeviceModel m) => new()
    {
        Id          = m.Id,
        Name        = m.Name,
        Connections = m.Connections.Any() ? m.Connections.Select(ToYaml).ToList() : null,
        Commands    = m.Commands.Any()    ? m.Commands.Select(ToYaml).ToList()    : null,
        Scenarios   = m.Scenarios.Any()   ? m.Scenarios.Select(ToYaml).ToList()   : null,
    };
    private static ConnectionYaml ToYaml(ConnectionModel m) => new()
    {
        Id = m.Id, Protocol = m.Protocol,
        TimeoutMs = m.TimeoutMs == 5000 ? null : m.TimeoutMs,
        Endpoint = m.Endpoint, Host = m.Host, Port = m.Port,
        Broker = m.Broker, PublishTopic = m.PublishTopic, SubscribeTopic = m.SubscribeTopic,
        StationNo = m.StationNo,
        // 認証設定（anonymous = 省略）
        OpcAuthMode = m.OpcAuthMode == AuthMode.Anonymous ? null : m.OpcAuthMode,
        OpcUserName = m.OpcUserName,
        OpcPassword = m.OpcPassword,
        OpcCertFile     = m.OpcCertFile,
        OpcCertPassword = m.OpcCertPassword,
    };
    private static CommandYaml ToYaml(CommandModel m) => new()
    {
        Id = m.Id, Name = m.Name,
        Parameters = m.Parameters.Any() ? m.Parameters.Select(ToYaml).ToList() : null,
        Steps      = m.Steps.Select(ToYaml).ToList()
    };
    private static ParameterYaml ToYaml(ParameterModel m) => new()
    { Name = m.Name, Label = m.Label, Type = m.Type };
    private static StepYaml ToYaml(StepModel m) => new()
    {
        Action = m.Action, Description = string.IsNullOrEmpty(m.Description) ? null : m.Description,
        ConnectionId = m.ConnectionId, NodeId = m.NodeId, Address = m.Address,
        Nodes = m.Nodes?.Count > 1
            ? m.Nodes.Select(n => new NodeEntryYaml { NodeId = n.NodeId, Parameter = n.Parameter }).ToList()
            : null,  // 単一ノードは従来通り NodeId/Parameter フィールドに保存
        TimeoutMs = m.TimeoutMs, TimeoutMsParam = m.TimeoutMsParam, DurationMs = m.DurationMs, DurationMsParam = m.DurationMsParam,
        IntervalMsParam = m.IntervalMsParam, ReadTimeoutParam = m.ReadTimeoutParam, TimeoutParam = m.TimeoutParam,
        Parameter = m.Parameter,
        Payload    = m.Payload?.ToDictionary(k => k.Key, k => k.Value),
        Parse      = m.Parse != null ? new ParseYaml { Format = m.Parse.Format, Xpath = m.Parse.XPath } : null,
        Conditions = m.Conditions?.Select(c => new ConditionYaml { Operator = c.Operator, Field = c.Field, Value = c.Value, Bit = c.Bit }).ToList(),
        Capture    = m.Capture?.Select(c => new CaptureYaml { Field = c.Field, As = c.As }).ToList()
    };
    private static ScenarioYaml ToYaml(ScenarioModel m) => new()
    {
        Id = m.Id, Name = m.Name,
        Steps = m.Steps.Select(ToYaml).ToList()
    };
    private static ScenarioStepYaml ToYaml(ScenarioStepModel m) => new()
    {
        Type = m.Type, Description = string.IsNullOrEmpty(m.Description) ? null : m.Description,
        CommandId  = m.CommandId,
        Parameters = m.Parameters?.ToDictionary(k => k.Key, k => k.Value),
        Capture    = m.Capture?.Select(c => new CaptureYaml { Field = c.Field, As = c.As }).ToList(),
        OnSuccess  = m.OnSuccess == "next" ? null : m.OnSuccess,
        OnError    = m.OnError   == "stop" ? null : m.OnError,
        DurationMs = m.DurationMs
    };

    // ─── YAML DTO → Domain ───
    private static DeviceModel ToModel(DeviceYaml y) => new(
        y.Id ?? "", y.Name ?? "",
        y.Connections?.Select(ToModel).ToList() ?? [],
        y.Commands?.Select(ToModel).ToList() ?? [],
        y.Scenarios?.Select(ToModel).ToList() ?? []);

    private static ConnectionModel ToModel(ConnectionYaml y) => new(
        y.Id ?? "", y.Protocol ?? "", y.TimeoutMs ?? 5000,
        y.Endpoint, y.Host, y.Port, y.Broker, y.PublishTopic, y.SubscribeTopic, y.StationNo,
        OpcAuthMode: y.OpcAuthMode, OpcUserName: y.OpcUserName, OpcPassword: y.OpcPassword,
        OpcCertFile: y.OpcCertFile, OpcCertPassword: y.OpcCertPassword);

    private static CommandModel ToModel(CommandYaml y) => new(
        y.Id ?? "", y.Name ?? "",
        y.Parameters?.Select(ToModel).ToList() ?? [],
        y.Steps?.Select(ToModel).ToList() ?? []);

    private static ParameterModel ToModel(ParameterYaml y) => new(
        y.Name ?? "", y.Label ?? "", y.Type ?? "string");

    private static StepModel ToModel(StepYaml y) => new(
        y.Action ?? "send", y.Description ?? "", y.ConnectionId,
        y.NodeId, y.Address, y.TimeoutMs, y.TimeoutMsParam, y.DurationMs, y.DurationMsParam, y.IntervalMsParam, y.ReadTimeoutParam, y.TimeoutParam, y.Parameter, y.Payload,
        y.Parse != null ? new ParseConfig(y.Parse.Format ?? "plain", y.Parse.Xpath) : null,
        y.Conditions?.Select(c => new ConditionModel(c.Operator ?? "equals", c.Value ?? "", c.Field, c.Bit)).ToList(),
        y.Capture?.Select(c => new CaptureModel(c.Field ?? "", c.As ?? "")).ToList(),
        Nodes: y.Nodes?.Select(n => new NodeEntry(n.NodeId ?? "", n.Parameter)).ToList());

    private static ScenarioModel ToModel(ScenarioYaml y) => new(
        y.Id ?? "", y.Name ?? "",
        y.Steps?.Select(ToModel).ToList() ?? []);

    private static ScenarioStepModel ToModel(ScenarioStepYaml y) => new(
        y.Type ?? "command", y.Description ?? "",
        y.CommandId, y.Parameters,
        y.Capture?.Select(c => new CaptureModel(c.Field ?? "", c.As ?? "")).ToList(),
        y.OnSuccess ?? "next", y.OnError ?? "stop", y.DurationMs);
}
