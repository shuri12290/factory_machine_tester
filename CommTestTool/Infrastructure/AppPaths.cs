using System.IO;
using System.Reflection;
using CommTestTool.Domain.Interfaces;

namespace CommTestTool.Infrastructure;

public class AppPaths : IAppPaths
{
    public string AppDir     { get; }
    public string ConfigDir  { get; }
    public string LogsDir    { get; }
    public string CertsDir   { get; }
    public string DevicesYaml { get; }

    public AppPaths()
    {
        AppDir    = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                    ?? AppDomain.CurrentDomain.BaseDirectory;
        ConfigDir = Path.Combine(AppDir, "config");
        LogsDir   = Path.Combine(AppDir, "logs");
        CertsDir  = Path.Combine(AppDir, "certs");
        DevicesYaml = Path.Combine(ConfigDir, "devices.yaml");

        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CertsDir);
    }

    public string DailyLogFile(DateTime date) =>
        Path.Combine(LogsDir, $"{date:yyyyMMdd}.log");

    public string CertFilePath(string fileName) =>
        Path.Combine(CertsDir, fileName);
}
