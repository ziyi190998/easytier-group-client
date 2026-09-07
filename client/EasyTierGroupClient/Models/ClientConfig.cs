using System.IO;
using System.Text.Json;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Models;

/// <summary>客户端本地配置。注意：网络口令不落盘，每次连接时凭邀请码向服务端换取、仅存内存。</summary>
public sealed class ClientConfig
{
    /// <summary>群友个人邀请码（由管理员分发）。</summary>
    public string InviteCode { get; set; } = "";

    /// <summary>IP 分配服务地址。</summary>
    public string ServiceUrl { get; set; } = "https://101.43.121.186:11020";

    /// <summary>服务端 TLS 证书 SHA-256 指纹（内置默认值为生产指纹，防中间人）。</summary>
    public string CertSha256 { get; set; } = "7F43EB81879782AB24A155946FF4AA347F06014AE25744CAD8C9051D8ED4BFBF";

    /// <summary>点击窗口 × 时的行为：ask（每次询问）/ hide（隐藏到托盘）/ exit（退出程序）。</summary>
    public string CloseAction { get; set; } = "ask";

    /// <summary>本机持久标识（首启自动生成），服务端用于「一码一机」绑定。</summary>
    public string MachineId { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyTierGroupClient");

    private static string FilePath => Path.Combine(Dir, "config.json");

    public static ClientConfig Load()
    {
        ClientConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new ClientConfig()
                : new ClientConfig();
        }
        catch (Exception ex)
        {
            SimpleLog.Error("读取配置失败，使用默认配置", ex);
            cfg = new ClientConfig();
        }
        if (string.IsNullOrWhiteSpace(cfg.MachineId))
        {
            cfg.MachineId = Guid.NewGuid().ToString("N");
            cfg.Save(); // 立即落盘，保证后续连接都用同一标识
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            SimpleLog.Error("保存配置失败", ex);
        }
    }
}
