using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using CommTestTool.Domain.Interfaces;
using CommTestTool.Domain.Models;
using CommTestTool.Infrastructure.Adapters;
using MQTTnet;
using OpcUaHelper;

namespace CommTestTool.Infrastructure.Adapters;

// ─── TCP/IP ───────────────────────────────────────────────────────────────
public sealed class TcpAdapter(ConnectionModel conn) : IProtocolAdapter
{
    private TcpClient?     _client;
    private NetworkStream? _stream;

    public string ConnectionId => conn.Id;
    public bool   IsConnected  => _client?.Connected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(conn.Host!, conn.Port!.Value, ct);
            _stream = _client.GetStream();
        }
        catch (Exception ex)
        { throw new ConnectionException(conn.Id, $"TCP接続失敗: {conn.Host}:{conn.Port}", ex); }
    }

    public async Task DisconnectAsync()
    {
        _stream?.Close(); _client?.Close();
        _stream = null;   _client = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); }

    public async Task SendAsync(IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
    {
        Guard();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await _stream!.WriteAsync(bytes, ct);
    }

    public async Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default)
    {
        Guard();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            var buf = new byte[65536];
            var sb  = new StringBuilder();
            while (true)
            {
                var n = await _stream!.ReadAsync(buf, cts.Token);
                if (n == 0) throw new ConnectionException(conn.Id, "接続が切断されました。");
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) return sb.ToString().TrimEnd('\n', '\r');
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CommTimeoutException(conn.Id, timeoutMs); }
    }

    public Task WriteAsync(string target, IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
        => SendAsync(payload, ct);

    public Task<string> ReadAsync(string target, int timeoutMs, CancellationToken ct = default)
        => ReceiveAsync(timeoutMs, ct);

    private void Guard()
    {
        if (!IsConnected)
            throw new ConnectionException(conn.Id, "TCP接続が切断されています。");
    }
}

// ─── OPC-UA ───────────────────────────────────────────────────────────────
public sealed class OpcUaAdapter(ConnectionModel conn, IAppPaths? paths = null) : IProtocolAdapter
{
    private OpcUaClient? _client;
    private bool         _connected;

    public string ConnectionId => conn.Id;
    public bool   IsConnected  => _connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        try
        {
            _client = new OpcUaClient();

            // 認証モード設定（接続口の OpcAuthMode で切り替え）
            // OpcUaHelperはデフォルトで Anonymous かつ BadCertificateUntrusted を自動Accept済み
            if (conn.OpcAuthMode == AuthMode.Username
                && !string.IsNullOrEmpty(conn.OpcUserName))
            {
                // ユーザー名・パスワード認証
                _client.UserIdentity = new Opc.Ua.UserIdentity(
                    conn.OpcUserName,
                    conn.OpcPassword ?? string.Empty);
            }
            else if (conn.OpcAuthMode == AuthMode.Certificate
                && !string.IsNullOrEmpty(conn.OpcCertFile)
                && paths != null)
            {
                // X.509証明書認証（certs/フォルダに配置したファイルを使用）
                var certPath = paths.CertFilePath(conn.OpcCertFile);
                if (!System.IO.File.Exists(certPath))
                    throw new ConnectionException(conn.Id,
                        $"証明書ファイルが見つかりません: {certPath}\n" +
                        $"アプリフォルダの certs/ フォルダに「{conn.OpcCertFile}」を配置してください。");
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    certPath,
                    conn.OpcCertPassword ?? string.Empty,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet
                    | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
                _client.UserIdentity = new Opc.Ua.UserIdentity(cert);
            }
            // それ以外（anonymous またはフィールド未設定）はライブラリデフォルトの Anonymous

            // タイムアウト付き非同期接続（await + WaitAsync でキャンセルを正しく伝播）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(conn.TimeoutMs);
            await _client.ConnectServer(conn.Endpoint!).WaitAsync(cts.Token);
            _connected = true;
        }
        catch (ConnectionException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectionException(conn.Id,
                $"OPC-UA接続タイムアウト ({conn.TimeoutMs}ms): {conn.Endpoint}");
        }
        catch (OperationCanceledException)
        {
            throw new ConnectionException(conn.Id, $"OPC-UA接続キャンセル: {conn.Endpoint}");
        }
        catch (Exception ex)
        {
            throw new ConnectionException(conn.Id,
                $"OPC-UA接続失敗: {conn.Endpoint}\n詳細: {ex.Message}", ex);
        }
    }

    public async Task DisconnectAsync()
    {
        try { _client?.Disconnect(); } catch { }
        _client    = null;
        _connected = false;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); }

    public Task SendAsync(IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
        => throw new NotSupportedException("OPC-UAはWriteAsyncを使用してください。");

    public Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default)
        => throw new NotSupportedException("OPC-UAはReadAsyncを使用してください。");

    public async Task WriteAsync(string nodeId, IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
    {
        Guard();

        // カンマ区切りで複数ノードIDを検出（単一ノードは従来通り）
        var nodeIds = nodeId.Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries);

        if (nodeIds.Length == 1)
        {
            // ── 単一ノード書き込み（従来動作） ──────────────────────────────
            var strVal   = payload.GetValueOrDefault("value", "");
            var type     = payload.GetValueOrDefault("type", "string");
            var writeVal = ConvertValue(strVal, type);
            await Task.Run(() => _client!.WriteNode(nodeIds[0], writeVal), ct);
        }
        else
        {
            // ── 複数ノード一括書き込み ───────────────────────────────────────
            // payload の各エントリは "nodeId: paramValue" または
            // node_id カンマ区切りに対応するキー "0","1",... / nodeId文字列 の2方式に対応
            // WriteNodes(string[] nodeIds, object[] values) を使用
            var values = new object[nodeIds.Length];
            for (int i = 0; i < nodeIds.Length; i++)
            {
                // キーの優先順: nodeId文字列 → インデックス文字列
                var raw  = (payload.TryGetValue(nodeIds[i], out var rv) ? rv : null)
                        ?? (payload.TryGetValue(i.ToString(), out var ri) ? ri : "");
                var type = payload.TryGetValue($"type_{i}", out var rt) ? rt
                         : payload.TryGetValue("type", out var rtt) ? rtt : "string";
                values[i] = ConvertValue(raw, type);
            }
            await Task.Run(() =>
            {
                bool ok = _client!.WriteNodes(nodeIds, values);
                if (!ok)
                    throw new InvalidOperationException("OPC-UA 複数ノード書き込みに失敗しました。");
            }, ct);
        }
    }

    /// <summary>文字列値を OPC-UA の型に変換する共通ヘルパー</summary>
    private static object ConvertValue(string strVal, string type) => type switch
    {
        "boolean"  => bool.TryParse(strVal, out var b)    ? b                : (object)strVal,
        "integer"  => int.TryParse(strVal, out var i)     ? i                : (object)strVal,
        "float"    => float.TryParse(strVal, System.Globalization.NumberStyles.Any,
                          System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : (object)strVal,
        "double"   => double.TryParse(strVal, System.Globalization.NumberStyles.Any,
                          System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (object)strVal,
        "long"     => long.TryParse(strVal, out var l)    ? l                : (object)strVal,
        "uint"     => uint.TryParse(strVal, out var u)    ? u                : (object)strVal,
        "byte"     => byte.TryParse(strVal, out var by)   ? by               : (object)strVal,
        "word"     => ushort.TryParse(strVal, out var w)  ? w                : (object)strVal,
        "dword"    => uint.TryParse(strVal, out var dw)   ? dw               : (object)strVal,
        "datetime" => DateTime.TryParse(strVal, null,
                          System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : (object)strVal,
        _          => strVal
    };

    public async Task<string> ReadAsync(string nodeId, int timeoutMs, CancellationToken ct = default)
    {
        Guard();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            // カンマ区切りで複数ノードIDを検出（単一ノードは従来通り）
            var nodeIds = nodeId.Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries);

            if (nodeIds.Length == 1)
            {
                // ── 単一ノード読み取り（従来動作）──────────────────────────
                // 戻り値: 値をそのままJSONシリアライズ（例: "IDLE" / 1234）
                var result = await Task.Run(() => _client!.ReadNode(nodeIds[0]), cts.Token);
                return JsonSerializer.Serialize(result);
            }
            else
            {
                // ── 複数ノード一括読み取り ─────────────────────────────────
                // ReadNodes(NodeId[]) を使用してPLCとの往復を1回に削減
                // 戻り値: { "ns=2;s=Node1": 値, "ns=2;s=Node2": 値, ... }
                var dataValues = await Task.Run(
                    () => _client!.ReadNodes(nodeIds.Select(n => new Opc.Ua.NodeId(n)).ToArray()),
                    cts.Token);

                // DataValue[] → Dictionary<nodeId, value> → JSON
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < nodeIds.Length; i++)
                    dict[nodeIds[i]] = dataValues[i]?.Value;
                return JsonSerializer.Serialize(dict);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CommTimeoutException(conn.Id, timeoutMs); }
    }

    private void Guard() { if (!_connected) throw new ConnectionException(conn.Id, "OPC-UA未接続。"); }

    /// <summary>
    /// OPC-UA メソッドを呼び出す。
    /// payload: 型名→パラメータ値 の辞書（順序付き）
    /// 戻り値: 出力引数を {"0": 値, "1": 値, ...} のJSON形式で返す
    /// </summary>
    public async Task<string> CallMethodAsync(
        string objectNodeId, string methodNodeId,
        IReadOnlyDictionary<string,string> inputArgs,
        int timeoutMs, CancellationToken ct = default)
    {
        Guard();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var inputs = inputArgs.Select(kv => ConvertValue(kv.Value, kv.Key)).ToArray();

        // OpcUaHelper の Session プロパティ経由で OPC UA 標準の Call サービスを呼ぶ
        // Session.Call(NodeId objectId, NodeId methodId, params object[] args) -> IList<object>
        var outputs = await Task.Run(() =>
        {
            var session = _client!.Session
                ?? throw new InvalidOperationException("OPC-UA セッションが存在しません。");

            var objectId = new Opc.Ua.NodeId(objectNodeId);
            var methodId = new Opc.Ua.NodeId(methodNodeId);

            // Session.Call は params object[] を受け取り IList<object> を返す
            IList<object> result = session.Call(objectId, methodId, inputs);
            return result ?? (IList<object>)Array.Empty<object>();
        }, cts.Token);

        if (outputs == null || outputs.Count == 0)
            return "{}";

        var dict = new System.Collections.Generic.Dictionary<string, object?>();
        for (int i = 0; i < outputs.Count; i++)
            dict[i.ToString()] = outputs[i]?.ToString() ?? "";

        return JsonSerializer.Serialize(dict);
    }
}

// ─── MQTT ─────────────────────────────────────────────────────────────────
public sealed class MqttAdapter(ConnectionModel conn) : IProtocolAdapter
{
    private IMqttClient?           _client;
    private readonly SemaphoreSlim _sem = new(0, 1);
    private string?                _lastMsg;

    public string ConnectionId => conn.Id;
    public bool   IsConnected  => _client?.IsConnected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;
        try
        {
            // v5: MqttFactory → MqttClientFactory
            var factory = new MqttClientFactory();
            _client = factory.CreateMqttClient();
            var opts = new MqttClientOptionsBuilder()
                .WithTcpServer(conn.Broker, conn.Port ?? 1883)
                .WithCleanStart(true)
                .Build();
            _client.ApplicationMessageReceivedAsync += e =>
            {
                _lastMsg = e.ApplicationMessage.ConvertPayloadToString();
                if (_sem.CurrentCount == 0) _sem.Release();
                return Task.CompletedTask;
            };
            var result = await _client.ConnectAsync(opts, ct);
            // v5: 接続失敗でも例外を投げず ResultCode で返す場合があるため確認
            if (result.ResultCode != MqttClientConnectResultCode.Success)
                throw new ConnectionException(conn.Id,
                    $"MQTT接続失敗: {conn.Broker}:{conn.Port ?? 1883} コード:{result.ResultCode}");
            // v5: SubscribeAsync はオプションビルダー形式
            var subOpts = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(conn.SubscribeTopic!)
                .Build();
            await _client.SubscribeAsync(subOpts, ct);
        }
        catch (ConnectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ConnectionException(conn.Id,
                $"MQTT接続失敗: {conn.Broker}:{conn.Port ?? 1883}\n詳細: {ex.Message}", ex);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client?.IsConnected == true)
            await _client.DisconnectAsync();
        _client = null;
    }

    public async ValueTask DisposeAsync() { await DisconnectAsync(); }

    public async Task SendAsync(IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(conn.PublishTopic!)
            .WithPayload(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
            .Build();
        await _client!.PublishAsync(msg, ct);
    }

    public async Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default)
    {
        _lastMsg = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await _sem.WaitAsync(cts.Token); return _lastMsg ?? ""; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CommTimeoutException(conn.Id, timeoutMs); }
    }

    public Task WriteAsync(string target, IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
        => SendAsync(payload, ct);
    public Task<string> ReadAsync(string target, int timeoutMs, CancellationToken ct = default)
        => ReceiveAsync(timeoutMs, ct);
}

// ─── MTConnect ────────────────────────────────────────────────────────────
public sealed class MTConnectAdapter(ConnectionModel conn) : IProtocolAdapter
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string ConnectionId => conn.Id;
    public bool   IsConnected  => true;

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task SendAsync(IReadOnlyDictionary<string,string> p, CancellationToken ct = default)
        => throw new NotSupportedException("MTConnectは読み取り専用です。");
    public Task<string> ReceiveAsync(int t, CancellationToken ct = default)
        => throw new NotSupportedException("MTConnectはReadAsyncを使用してください。");
    public Task WriteAsync(string target, IReadOnlyDictionary<string,string> p, CancellationToken ct = default)
        => throw new NotSupportedException("MTConnectは読み取り専用です。");

    public async Task<string> ReadAsync(string path, int timeoutMs, CancellationToken ct = default)
    {
        var url = $"{conn.Endpoint!.TrimEnd('/')}/{path.TrimStart('/')}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { return await Http.GetStringAsync(url, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new CommTimeoutException(conn.Id, timeoutMs); }
        catch (Exception ex)
        { throw new ConnectionException(conn.Id, $"MTConnect取得失敗: {url}", ex); }
    }

    public static string? ExtractXPath(string xml, string xpath)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.XPathSelectElement(xpath)?.Value;
        }
        catch { return null; }
    }
}

// ─── スタブ ───────────────────────────────────────────────────────────────

// ── SLMP アダプター ─────────────────────────────────────────────────────────
// SLMP（Seamless Message Protocol）: 三菱電機 iQ-R/Q/L/FX シリーズ対応
// 外部SDKなし。TCP/IPソケットで3Eフレームを自前組み立てして送受信。
public sealed class SlmpAdapter(ConnectionModel conn) : IProtocolAdapter
{
    private System.Net.Sockets.TcpClient? _tcp;
    private System.Net.Sockets.NetworkStream? _stream;
    private bool _connected;

    public string ConnectionId => conn.Id;
    public bool   IsConnected  => _connected;

    // ── SLMP 3Eフレーム定数 ──
    private const ushort SUBHEADER    = 0x5000;  // 3Eフレーム
    private const ushort NETWORK_NO   = 0x00;
    private const ushort PC_NO        = 0xFF;
    private const ushort IO_NO        = 0x03FF;
    private const ushort CHANNEL_NO   = 0x00;
    private const ushort CPU_TIMER_MS = 4;       // 応答待ちタイマ（×250ms単位）

    // コマンド定義
    private const ushort CMD_BATCH_READ_WORD  = 0x0401;  // ワード一括読み出し
    private const ushort CMD_BATCH_WRITE_WORD = 0x1401;  // ワード一括書き込み
    private const ushort SUBCOMMAND_WORD      = 0x0000;  // ワード単位

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        try
        {
            _tcp = new System.Net.Sockets.TcpClient();
            await _tcp.ConnectAsync(conn.Host!, conn.Port ?? 5007, ct);
            _stream = _tcp.GetStream();
            _connected = true;
        }
        catch (Exception ex)
        { throw new ConnectionException(conn.Id, $"SLMP接続失敗: {conn.Host}:{conn.Port ?? 5007}", ex); }
    }

    public Task DisconnectAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _connected = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
        => throw new NotSupportedException("SLMPはWriteAsyncを使用してください。");

    public Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default)
        => throw new NotSupportedException("SLMPはReadAsyncを使用してください。");

    /// <summary>
    /// デバイス書き込み。target例: "D100"（ワードデバイス）
    /// payload の最初の値をワード値として書き込む。
    /// </summary>
    public async Task WriteAsync(string target, IReadOnlyDictionary<string,string> payload, CancellationToken ct = default)
    {
        Guard();
        var value = payload.Values.FirstOrDefault() ?? "0";
        if (!short.TryParse(value, out var wordVal))
            throw new ArgumentException($"SLMPはワード（整数）値のみ書き込み可能です: {value}");
        var address = target;

        var (deviceCode, deviceNo) = ParseAddress(address);
        // コマンドデータ
        var cmdData = new byte[6];
        Array.Copy(BitConverter.GetBytes((uint)deviceNo), cmdData, 3); // 先頭番号3バイト
        cmdData[3] = deviceCode;
        Array.Copy(BitConverter.GetBytes((ushort)1), 0, cmdData, 4, 2); // 点数=1
        // 書き込み値
        var writeData = BitConverter.GetBytes(wordVal);

        var request = Build3ERequest(CMD_BATCH_WRITE_WORD, SUBCOMMAND_WORD,
            cmdData.Concat(writeData).ToArray());

        _stream!.Write(request, 0, request.Length);
        var response = await ReadResponseAsync(ct);
        CheckEndCode(response);
    }

    /// <summary>
    /// デバイス読み取り。address例: "D100"
    /// </summary>
    public async Task<string> ReadAsync(string address, int timeoutMs, CancellationToken ct = default)
    {
        Guard();
        var (deviceCode, deviceNo) = ParseAddress(address);

        var cmdData = new byte[6];
        Array.Copy(BitConverter.GetBytes((uint)deviceNo), cmdData, 3);
        cmdData[3] = deviceCode;
        Array.Copy(BitConverter.GetBytes((ushort)1), 0, cmdData, 4, 2);

        var request = Build3ERequest(CMD_BATCH_READ_WORD, SUBCOMMAND_WORD, cmdData);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        _stream!.Write(request, 0, request.Length);
        var response = await ReadResponseAsync(cts.Token);
        CheckEndCode(response);

        // 応答データ部: ヘッダ(9) + エンドコード(2) = オフセット11から
        const int dataOffset = 11;
        if (response.Length < dataOffset + 2)
            throw new ConnectionException(conn.Id, "SLMPレスポンスが短すぎます。");
        var wordVal = BitConverter.ToInt16(response, dataOffset);
        return wordVal.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── ヘルパー ──────────────────────────────────────────────────────────
    private static byte[] Build3ERequest(ushort command, ushort subcommand, byte[] cmdData)
    {
        // 3Eフレーム リクエスト構造（バイナリコード）:
        // [0-1]   サブヘッダ    (0x5000)
        // [2]     ネットワーク番号
        // [3]     PC番号
        // [4-5]   要求先I/O番号
        // [6]     要求先局番号
        // [7-8]   データ長      (タイマ以降のバイト数 = 2+2+2+cmdData.Length)
        // [9-10]  監視タイマ    (2バイト・250ms単位)
        // [11-12] コマンド
        // [13-14] サブコマンド
        // [15+]   コマンドデータ
        ushort dataLen = (ushort)(6 + cmdData.Length); // timer(2)+cmd(2)+subcmd(2)+cmdData
        var buf = new byte[15 + cmdData.Length];        // 9(ヘッダ)+6(timer+cmd+subcmd)+cmdData
        int i = 0;
        buf[i++] = 0x50; buf[i++] = 0x00;                           // サブヘッダ
        buf[i++] = (byte)NETWORK_NO;                                 // ネットワーク番号
        buf[i++] = (byte)PC_NO;                                      // PC番号
        buf[i++] = (byte)(IO_NO & 0xFF); buf[i++] = (byte)(IO_NO >> 8); // I/O番号
        buf[i++] = (byte)CHANNEL_NO;                                 // 局番号
        buf[i++] = (byte)(dataLen & 0xFF); buf[i++] = (byte)(dataLen >> 8); // データ長
        buf[i++] = (byte)(CPU_TIMER_MS & 0xFF);                      // 監視タイマ (Low)
        buf[i++] = (byte)(CPU_TIMER_MS >> 8);                        // 監視タイマ (High) ← 追加
        Array.Copy(BitConverter.GetBytes(command),    0, buf, i, 2); i += 2;
        Array.Copy(BitConverter.GetBytes(subcommand), 0, buf, i, 2); i += 2;
        Array.Copy(cmdData, 0, buf, i, cmdData.Length);
        return buf;
    }

    private async Task<byte[]> ReadResponseAsync(CancellationToken ct)
    {
        // 3Eフレーム レスポンス構造:
        // [0-1]   サブヘッダ (0xD000)
        // [2]     ネットワーク番号
        // [3]     PC番号
        // [4-5]   I/O番号
        // [6]     局番号
        // [7-8]   データ長 (エンドコード2バイト + 応答データのバイト数)
        // [9-10]  エンドコード (0x0000=正常)
        // [11+]   応答データ
        //
        // ヘッダは [0-8] の9バイト。データ長フィールドはエンドコード以降の全バイト数。
        var header = new byte[9];
        int read = 0;
        while (read < 9)
            read += await _stream!.ReadAsync(header, read, 9 - read, ct);
        ushort dataLen = BitConverter.ToUInt16(header, 7);
        var data = new byte[dataLen]; // エンドコード(2) + 応答データ
        read = 0;
        while (read < dataLen)
            read += await _stream!.ReadAsync(data, read, dataLen - read, ct);
        return header.Concat(data).ToArray();
        // response[0-8] = ヘッダ, response[9-10] = エンドコード, response[11+] = 応答データ
    }

    private static void CheckEndCode(byte[] response)
    {
        // エンドコードは response[9-10] (ヘッダ9バイトの直後)
        if (response.Length < 11) return;
        ushort endCode = BitConverter.ToUInt16(response, 9);
        if (endCode != 0x0000)
            throw new InvalidOperationException($"SLMPエラーコード: 0x{endCode:X4}");
    }

    private static (byte deviceCode, int deviceNo) ParseAddress(string address)
    {
        // 対応デバイス記号 → コード（三菱 SLMP仕様書 付録参照）
        var deviceMap = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            {"D",  0xA8}, // データレジスタ
            {"W",  0xB4}, // リンクレジスタ
            {"R",  0xAF}, // ファイルレジスタ
            {"M",  0x90}, // 内部リレー
            {"X",  0x9C}, // 入力
            {"Y",  0x9D}, // 出力
            {"B",  0xA0}, // リンクリレー
            {"SD", 0xA9}, // 特殊レジスタ
            {"SM", 0x91}, // 特殊リレー
        };
        // "D100" → ("D", 100), "SD10" → ("SD", 10)
        var sym = System.Text.RegularExpressions.Regex.Match(address, @"^([A-Za-z]+)(\d+)$");
        if (!sym.Success)
            throw new ArgumentException($"SLMPアドレス書式が不正です: {address}（例: D100, W200）");
        var key = sym.Groups[1].Value.ToUpper();
        if (!deviceMap.TryGetValue(key, out var code))
            throw new ArgumentException($"未対応デバイス記号: {key}（対応: D W R M X Y B SD SM）");
        return (code, int.Parse(sym.Groups[2].Value));
    }

    private void Guard()
    { if (!_connected) throw new ConnectionException(conn.Id, "SLMP未接続。"); }
}


// ── FOCAS2 アダプター ────────────────────────────────────────────────────────
// FANUC FOCAS2 (CNC/PMC Data Window Library) - x64 対応実装
// 使用DLL: fwlib0DN64.dll（Focasフォルダに配置・ビルド時にbinへ自動コピー）
// ポート: 8193（FOCAS2標準）
// 設計: コマンドごとにトークン（ハンドル）を取得・解放する（使い回しなし）
public sealed class Focas2Adapter(ConnectionModel conn) : IProtocolAdapter
{
    public string ConnectionId => conn.Id;
    // ハンドルは保持しない設計（コマンドごとに取得・解放）
    public bool   IsConnected  => _verified;

    private bool _verified = false;  // 一度でも接続成功したか
    private readonly string _ip      = conn.Host ?? "127.0.0.1";
    private readonly ushort _port    = (ushort)(conn.Port ?? 8193);
    private readonly int    _timeout = Math.Max(1, conn.TimeoutMs / 1000);

    // ── トークン取得ヘルパー ────────────────────────────────
    // コマンドごとに毎回取得し、使用後は必ず解放する
    private ushort AcquireHandle()
    {
        short ret;
        try
        {
            ret = Focas.cnc_allclibhndl3(_ip, _port, _timeout, out ushort h);
            if (ret != Focas.EW_OK)
                throw new ConnectionException(conn.Id,
                    $"FOCAS2 ハンドル取得失敗 (IP:{_ip}:{_port}, " +
                    $"コード:{ret} {FocasErrMsg(ret)})");
            return h;
        }
        catch (DllNotFoundException)
        {
            throw new ConnectionException(conn.Id,
                "fwlib0DN64.dll が見つかりません。\n" +
                "アプリの bin フォルダに以下を配置してください：\n" +
                "  fwlib0DN64.dll（FANUC FOCAS2 Library）");
        }
    }

    private static void ReleaseHandle(ushort h)
    {
        if (h != 0) Focas.cnc_freelibhndl(h);
    }

    // ── IProtocolAdapter 実装 ─────────────────────────────
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // 疎通確認：ハンドル取得→即解放
        ushort h = 0;
        try
        {
            await Task.Run(() => { h = AcquireHandle(); }, ct);
            _verified = true;
        }
        finally { ReleaseHandle(h); }
    }

    public Task DisconnectAsync()
    {
        _verified = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(IReadOnlyDictionary<string, string> payload,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "FOCAS2 は send をサポートしません。read / poll を使用してください。");

    public Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default)
        => throw new NotSupportedException(
            "FOCAS2 は receive をサポートしません。read / poll を使用してください。");

    /// <summary>
    /// FOCAS2 書き込み（コマンドごとにハンドルを取得・解放）
    ///
    /// targetとpayloadの指定方法：
    ///   【マクロ変数書き込み】
    ///     target: macro:&lt;変数番号&gt;  例) macro:100
    ///     payload: { "value": "12345" }
    ///   【PMCアドレス書き込み】
    ///     target: pmc:G&lt;アドレス&gt;  例) pmc:G0
    ///     payload: { "value": "1" }
    /// </summary>
    public async Task WriteAsync(string target,
        IReadOnlyDictionary<string, string> payload,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ushort h = 0;
            try
            {
                h = AcquireHandle();
                DispatchWrite(h, target, payload);
            }
            finally { ReleaseHandle(h); }
        }, ct);
    }

    /// <summary>
    /// FOCAS2 読み取り（コマンドごとにハンドルを取得・解放）
    ///
    /// targetの指定方法：
    ///   【稼働状態】
    ///     status        → 稼働状態(run/mode/alarm/emergency)
    ///     alarm         → アラーム情報
    ///     opmsg         → オペレーターメッセージ
    ///   【プログラム】
    ///     program       → 実行中プログラム番号
    ///     exeprgname    → 実行中プログラム名
    ///   【位置・速度】
    ///     position      → 現在位置(絶対/機械/相対)
    ///     feedrate      → 実際の送り速度(F)
    ///     spindle       → スピンドル速度(S)
    ///   【マクロ】
    ///     macro:&lt;変数番号&gt;  例) macro:100
    ///   【パラメータ】
    ///     param:&lt;番号&gt;      例) param:6750
    ///   【PMC】
    ///     pmc:G&lt;アドレス&gt;  例) pmc:G0  (Gレジスタ)
    ///     pmc:D&lt;アドレス&gt;  例) pmc:D100 (Dレジスタ)
    ///   【システム情報】
    ///     sysinfo       → システム情報
    /// </summary>
    public async Task<string> ReadAsync(string target, int timeoutMs,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ushort h = 0;
            try
            {
                h = AcquireHandle();
                return DispatchRead(h, target);
            }
            finally { ReleaseHandle(h); }
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        _verified = false;
        return ValueTask.CompletedTask;
    }

    // ── 読み取り実装 ─────────────────────────────────────

    private static string ReadStatus(ushort h)
    {
        var st  = new Focas.ODBST();
        short r = Focas.cnc_statinfo(h, st);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException(
                $"cnc_statinfo 失敗 (コード:{r} {FocasErrMsg(r)})");

        var run = st.run switch
        {
            0 => "STOP", 1 => "HOLD", 2 => "START",
            3 => "MSTR", 4 => "RESTART", 5 => "PRSR",
            _ => $"UNKNOWN({st.run})"
        };
        var mode = st.aut switch
        {
            0 => "MDI", 1 => "MEM", 3 => "EDIT",
            4 => "HND", 5 => "JOG", 7 => "REF",
            _ => $"UNKNOWN({st.aut})"
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            run,
            mode,
            motion    = st.motion,
            alarm     = st.alarm != 0,
            emergency = st.emergency != 0,
            edit      = st.edit
        });
    }

    private static string ReadProgram(ushort h)
    {
        var prg = new Focas.ODBPRO();
        short r = Focas.cnc_rdprgnum(h, prg);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException(
                $"cnc_rdprgnum 失敗 (コード:{r} {FocasErrMsg(r)})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            program      = prg.data,
            main_program = prg.mdata
        });
    }

    // ── ディスパッチ ─────────────────────────────────────────
    private static string DispatchRead(ushort h, string target)
    {
        var t = target.ToLower();
        // 直接マッチ
        if (t == "status")     return ReadStatus(h);
        if (t == "alarm")      return ReadAlarm(h);
        if (t == "opmsg")      return ReadOpMsg(h);
        if (t == "program")    return ReadProgram(h);
        if (t == "exeprgname") return ReadExePrgName(h);
        if (t == "position")   return ReadPosition(h);
        if (t == "feedrate")   return ReadFeedrate(h);
        if (t == "spindle")    return ReadSpindle(h);
        if (t == "sysinfo")    return ReadSysInfo(h);
        // プレフィックスマッチ
        if (t.StartsWith("macro:"))
        {
            if (!short.TryParse(target[6..], out var varNo))
                throw new ArgumentException($"マクロ変数番号が不正: {target[6..]}");
            return ReadMacro(h, varNo);
        }
        if (t.StartsWith("param:"))
        {
            if (!short.TryParse(target[6..], out var paramNo))
                throw new ArgumentException($"パラメータ番号が不正: {target[6..]}");
            return ReadParam(h, paramNo);
        }
        if (t.StartsWith("pmc:"))
            return ReadPmc(h, target[4..]);

        throw new ArgumentException(
            $"FOCAS2 の read でサポートされていない target: '{target}'\n" +
            "対応: status / alarm / opmsg / program / exeprgname / " +
            "position / feedrate / spindle / sysinfo / " +
            "macro:番号 / param:番号 / pmc:G番号 / pmc:D番号");
    }

    private static void DispatchWrite(ushort h, string target,
        IReadOnlyDictionary<string, string> payload)
    {
        var t = target.ToLower();
        var val = payload.TryGetValue("value", out var v) ? v : payload.Values.FirstOrDefault() ?? "0";

        if (t.StartsWith("macro:"))
        {
            if (!short.TryParse(target[6..], out var varNo))
                throw new ArgumentException($"マクロ変数番号が不正: {target[6..]}");
            WriteMacro(h, varNo, val);
            return;
        }
        if (t.StartsWith("pmc:"))
        {
            WritePmc(h, target[4..], val);
            return;
        }
        throw new ArgumentException(
            $"FOCAS2 の write でサポートされていない target: '{target}'\n" +
            "対応: macro:番号 / pmc:G番号 / pmc:D番号");
    }

    // ── 読み取り実装 ─────────────────────────────────────────

    private static string ReadAlarm(ushort h)
    {
        short r = Focas.cnc_alarm2(h, out int alm);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_alarm2 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            servo   = (alm & 0x001) != 0,
            pmc     = (alm & 0x002) != 0,
            io      = (alm & 0x004) != 0,
            program = (alm & 0x010) != 0,
            spindle = (alm & 0x020) != 0,
            overheat= (alm & 0x040) != 0,
            ps      = (alm & 0x100) != 0,
            raw     = alm
        });
    }

    private static string ReadOpMsg(ushort h)
    {
        var msg = new Focas.OPMSG3();
        short type = -1; // 全タイプ
        short r = Focas.cnc_rdopmsg3(h, -1, ref type, msg);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_rdopmsg3 失敗 (コード:{r})");
        string GetMsg(Focas.OPMSG3_data d)
            => d.datano != 0
                ? new string(d.data).TrimEnd('\0').Trim()
                : "";
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            msg1 = GetMsg(msg.msg1),
            msg2 = GetMsg(msg.msg2),
            msg3 = GetMsg(msg.msg3),
            msg4 = GetMsg(msg.msg4)
        });
    }

    private static string ReadExePrgName(ushort h)
    {
        var info = new Focas.ODBEXEPRG();
        short r = Focas.cnc_exeprgname2(h, info);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_exeprgname2 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            name   = new string(info.name).TrimEnd('\0').Trim(),
            o_num  = info.o_num
        });
    }

    private static string ReadPosition(ushort h)
    {
        short axes = -1; // 全軸
        var pos = new Focas.ODBPOS();
        short r = Focas.cnc_rdposition(h, -1, ref axes, pos);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_rdposition 失敗 (コード:{r})");
        // POSELMALL.abs/mach/rel はそれぞれ POSELM 型（data, dec フィールドを持つ）
        static double ToMm(Focas.POSELM e) => e.data * Math.Pow(10, e.dec * -1);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            absolute = new { x = ToMm(pos.p1.abs), y = ToMm(pos.p2.abs), z = ToMm(pos.p3.abs) },
            machine  = new { x = ToMm(pos.p1.mach), y = ToMm(pos.p2.mach), z = ToMm(pos.p3.mach) },
            relative = new { x = ToMm(pos.p1.rel), y = ToMm(pos.p2.rel), z = ToMm(pos.p3.rel) }
        });
    }

    private static string ReadFeedrate(ushort h)
    {
        var act = new Focas.ODBACT();
        short r = Focas.cnc_actf(h, act);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_actf 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new { feedrate = act.data });
    }

    private static string ReadSpindle(ushort h)
    {
        var act = new Focas.ODBACT2();
        short r = Focas.cnc_acts2(h, -1, act);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_acts2 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            spindle_no = act.datano,
            speed      = act.data?[0] ?? 0
        });
    }

    private static string ReadMacro(ushort h, short varNo)
    {
        var mac = new Focas.ODBM();
        short r = Focas.cnc_rdmacro(h, varNo, 8, mac);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_rdmacro (#{varNo}) 失敗 (コード:{r})");
        double val = mac.mcr_val * Math.Pow(10, mac.dec_val * -1);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            variable_no = varNo,
            value       = val,
            raw         = mac.mcr_val,
            dec         = mac.dec_val
        });
    }

    private static string ReadParam(ushort h, short paramNo)
    {
        var prm = new Focas.IODBPSD_1();
        short r = Focas.cnc_rdparam(h, paramNo, -1, 8, prm);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_rdparam ({paramNo}) 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            param_no = paramNo,
            value    = prm.data.ldata  // IODBPSD_1.data は IODBPSD_1_UNION 型
        });
    }

    private static string ReadPmc(ushort h, string addr)
    {
        // addr 例: "G0", "D100"
        if (addr.Length < 2)
            throw new ArgumentException($"PMCアドレスの形式が不正: {addr}  例) G0 / D100");
        short adrType = addr[0].ToString().ToUpper() switch
        {
            "G" => 0, "F" => 1, "Y" => 2, "X" => 3,
            "A" => 4, "R" => 5, "T" => 6, "K" => 7,
            "C" => 8, "D" => 9, "M" => 10, "N" => 11,
            "Z" => 14, "L" => 15, "V" => 22, "B" => 26,
            _   => throw new ArgumentException($"未対応のPMCアドレス種別: {addr[0]}")
        };
        if (!ushort.TryParse(addr[1..], out ushort adrNo))
            throw new ArgumentException($"PMCアドレス番号が不正: {addr[1..]}");

        var pmc = new Focas.IODBPMC0();
        short r = Focas.pmc_rdpmcrng(h, adrType, 0, adrNo, adrNo, 6, pmc);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"pmc_rdpmcrng ({addr}) 失敗 (コード:{r})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            address = addr,
            value   = pmc.cdata?[0] ?? 0
        });
    }

    // ── 書き込み実装 ─────────────────────────────────────────

    private static void WriteMacro(ushort h, short varNo, string val)
    {
        if (!double.TryParse(val, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double dval))
            throw new ArgumentException($"マクロ変数の値が不正: {val}");
        // 小数点以下の桁数を判定
        var parts = val.Split('.');
        short dec = parts.Length > 1 ? (short)parts[1].Length : (short)0;
        int rawVal = (int)(dval * Math.Pow(10, dec));
        short r = Focas.cnc_wrmacro(h, varNo, 8, rawVal, dec);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"cnc_wrmacro (#{varNo}) 失敗 (コード:{r})");
    }

    private static void WritePmc(ushort h, string addr, string val)
    {
        if (addr.Length < 2)
            throw new ArgumentException($"PMCアドレスの形式が不正: {addr}");
        short adrType = addr[0].ToString().ToUpper() switch
        {
            "G" => 0, "F" => 1, "Y" => 2, "X" => 3,
            "A" => 4, "R" => 5, "T" => 6, "K" => 7,
            "C" => 8, "D" => 9, _ => throw new ArgumentException($"未対応: {addr[0]}")
        };
        if (!ushort.TryParse(addr[1..], out ushort adrNo))
            throw new ArgumentException($"PMCアドレス番号が不正: {addr[1..]}");
        if (!byte.TryParse(val, out byte byteVal))
            throw new ArgumentException($"PMC書き込み値が不正(0-255): {val}");
        var pmc = new Focas.IODBPMC0();
        pmc.type_a   = adrType;
        pmc.type_d   = 0;          // byte型
        pmc.datano_s = (short)adrNo;
        pmc.datano_e = (short)adrNo;
        if (pmc.cdata == null) pmc.cdata = new byte[5];
        pmc.cdata[0] = byteVal;
        // datalen = ヘッダ(8バイト) + データ数(1バイト) = 9
        short r = Focas.pmc_wrpmcrng(h, 9, pmc);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException($"pmc_wrpmcrng ({addr}) 失敗 (コード:{r})");
    }

    private static string ReadSysInfo(ushort h)
    {
        var sys = new Focas.ODBSYS();
        short r = Focas.cnc_sysinfo(h, sys);
        if (r != Focas.EW_OK)
            throw new InvalidOperationException(
                $"cnc_sysinfo 失敗 (コード:{r} {FocasErrMsg(r)})");
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            cnc_type = sys.cnc_type != null ? new string(sys.cnc_type).TrimEnd('\0').Trim() : "",
            mt_type  = sys.mt_type  != null ? new string(sys.mt_type).TrimEnd('\0').Trim()  : "",
            series   = sys.series   != null ? new string(sys.series).TrimEnd('\0').Trim()   : "",
            version  = sys.version  != null ? new string(sys.version).TrimEnd('\0').Trim()  : "",
            axes     = sys.axes     != null ? new string(sys.axes).TrimEnd('\0').Trim()     : ""
        });
    }

    private static string FocasErrMsg(short code) => code switch
    {
        -17 => "(EW_PROTOCOL: プロトコルエラー)",
        -16 => "(EW_SOCKET: ソケットエラー)",
        -15 => "(EW_NODLL: DLL が存在しない)",
        -9  => "(EW_HSSB: HSSB通信エラー)",
        -8  => "(EW_HANDLE: ハンドルエラー)",
        -7  => "(EW_VERSION: バージョン不一致)",
        -6  => "(EW_UNEXP: 異常エラー)",
        -5  => "(EW_SYSTEM: システムエラー)",
        -1  => "(EW_BUSY: ビジー)",
        1   => "(EW_FUNC: 機能エラー)",
        2   => "(EW_LENGTH: 長さエラー)",
        3   => "(EW_NUMBER: 番号エラー)",
        6   => "(EW_NOOPT: オプションなし)",
        12  => "(EW_MODE: モードエラー)",
        13  => "(EW_REJECT: 実行拒否)",
        15  => "(EW_ALARM: アラーム発生中)",
        _   => ""
    };
}

public sealed class StubAdapter(ConnectionModel conn) : IProtocolAdapter
{
    public string ConnectionId => conn.Id;
    public bool   IsConnected  => false;
    public Task ConnectAsync(CancellationToken ct = default) => Throw();
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task SendAsync(IReadOnlyDictionary<string,string> p, CancellationToken ct = default) => Throw();
    public Task<string> ReceiveAsync(int t, CancellationToken ct = default) => ThrowT<string>();
    public Task WriteAsync(string n, IReadOnlyDictionary<string,string> p, CancellationToken ct = default) => Throw();
    public Task<string> ReadAsync(string n, int t, CancellationToken ct = default) => ThrowT<string>();
    private Task Throw() => throw new NotImplementedProtocolException(conn.Protocol);
    private Task<T> ThrowT<T>() => throw new NotImplementedProtocolException(conn.Protocol);
}

// ─── ファクトリー ─────────────────────────────────────────────────────────
public static class AdapterFactory
{
    public static IProtocolAdapter Create(ConnectionModel conn,
        CommTestTool.Domain.Interfaces.IAppPaths? paths = null) => conn.Protocol switch
    {
        "tcp"       => new TcpAdapter(conn),
        "opcua"     => new OpcUaAdapter(conn, paths),
        "mqtt"      => new MqttAdapter(conn),
        "mtconnect" => new MTConnectAdapter(conn),
        "slmp"      => new SlmpAdapter(conn),
        "focas2"    => new Focas2Adapter(conn),
        _           => new StubAdapter(conn)
    };
}
