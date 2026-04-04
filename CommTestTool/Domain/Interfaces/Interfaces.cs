using CommTestTool.Domain.Models;

namespace CommTestTool.Domain.Interfaces;

// 設備リポジトリ（コマンド・シナリオも設備に内包）
public interface IDeviceRepository
{
    IReadOnlyList<DeviceModel> GetAll();
    void Save(IReadOnlyList<DeviceModel> devices);
}

// 通信アダプター
public interface IProtocolAdapter : IAsyncDisposable
{
    string ConnectionId { get; }
    bool   IsConnected  { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task SendAsync(IReadOnlyDictionary<string,string> payload, CancellationToken ct = default);
    Task<string> ReceiveAsync(int timeoutMs, CancellationToken ct = default);
    Task WriteAsync(string target, IReadOnlyDictionary<string,string> payload, CancellationToken ct = default);
    Task<string> ReadAsync(string target, int timeoutMs, CancellationToken ct = default);
}

// ログ
public interface ILogService
{
    IReadOnlyList<LogEntry> Entries { get; }
    event Action<LogEntry>  EntryAdded;
    void Write(LogEntry entry);
    void Clear();
}

// ダイアログ
public interface IDialogService
{
    bool Confirm(string message, string title = "確認");
    void Info(string message, string title = "完了");
    void Error(string message, string title = "エラー");
    T? ShowDialog<T>(Func<T> create) where T : class;
    /// <summary>ファイルを開くダイアログ。キャンセル時はnullを返す。</summary>
    string? OpenFileDialog(string title, string filter = "YAMLファイル|*.yaml|全てのファイル|*.*");
    /// <summary>ファイルを保存するダイアログ。キャンセル時はnullを返す。</summary>
    string? SaveFileDialog(string title, string defaultFileName, string filter = "YAMLファイル|*.yaml|全てのファイル|*.*");
}

// パス解決
public interface IAppPaths
{
    string AppDir     { get; }
    string ConfigDir  { get; }
    string LogsDir    { get; }
    string CertsDir   { get; }   // 証明書フォルダ（certs/）
    string DevicesYaml { get; }
    string DailyLogFile(DateTime date);
    /// <summary>証明書ファイル名からフルパスを解決する</summary>
    string CertFilePath(string fileName);
}
