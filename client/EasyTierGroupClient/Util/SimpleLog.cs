using System.IO;

namespace EasyTierGroupClient.Util;

/// <summary>极简本地日志：滚动写入 %LOCALAPPDATA%\EasyTierGroupClient\logs\client.log。
/// 注意脱敏：不要传入网络口令、邀请码完整值。</summary>
public static class SimpleLog
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyTierGroupClient", "logs");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, "client.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}\r\n");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
