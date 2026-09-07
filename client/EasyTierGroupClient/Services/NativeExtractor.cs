using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Services;

/// <summary>把内嵌的 easytier 原生文件解压到 %LOCALAPPDATA%\EasyTierGroupClient\bin\，按内容哈希检测变更。</summary>
public static class NativeExtractor
{
    private static readonly string[] Names =
    {
        "easytier-core.exe", "easytier-cli.exe", "wintun.dll", "Packet.dll", "WinDivert64.sys"
    };

    public static string BinDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyTierGroupClient", "bin");

    private static string CoreLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyTierGroupClient", "logs");

    public static string LogsDir => CoreLogDir;

    public static void ExtractAll()
    {
        var asm = Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(BinDir);
        foreach (var name in Names)
        {
            using var stream = asm.GetManifestResourceStream($"native/{name}")
                ?? throw new InvalidOperationException(
                    $"安装包不完整：缺少内嵌组件 {name}。请向管理员重新获取客户端。");
            string hash;
            using (var sha = SHA256.Create())
                hash = Convert.ToHexString(sha.ComputeHash(stream));
            stream.Position = 0;

            var target = Path.Combine(BinDir, name);
            if (File.Exists(target) && Sha256File(target) == hash)
                continue;

            var tmp = target + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                stream.CopyTo(fs);
            File.Move(tmp, target, overwrite: true);
            SimpleLog.Info($"已释放组件 {name}");
        }
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
