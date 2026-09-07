using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Services;

/// <summary>easytier-core 进程生命周期管理。
/// 通过环境变量（ET_*）传参：命令行不出现口令，也避免任何命令拼接。</summary>
public sealed class EasyTierManager : IDisposable
{
    private Process? _core;

    public string CorePath => Path.Combine(NativeExtractor.BinDir, "easytier-core.exe");
    public string CliPath => Path.Combine(NativeExtractor.BinDir, "easytier-cli.exe");

    /// <summary>开服服务器虚拟 IP，用于连通性探测。</summary>
    public const string ServerVip = "10.144.0.1";

    public bool IsRunning => _core is { HasExited: false };

    /// <summary>结束本客户端释放目录下残留的 easytier-core 进程（版本升级后旧文件被锁等场景）。</summary>
    public void KillStray()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("easytier-core"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, CorePath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        SimpleLog.Info("已清理残留 easytier-core 进程");
                    }
                }
                catch
                {
                    // 无法读取模块信息的进程跳过
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"清理残留进程失败：{ex.Message}");
        }
    }

    public void Start(AllocInfo alloc)
    {
        KillStray();
        var psi = new ProcessStartInfo
        {
            FileName = CorePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = NativeExtractor.BinDir,
        };
        var env = psi.EnvironmentVariables;
        env["ET_IPV4"] = alloc.Ip;
        env["ET_NETWORK_NAME"] = alloc.NetworkName;
        env["ET_NETWORK_SECRET"] = alloc.NetworkSecret;
        env["ET_PEERS"] = string.Join(",", alloc.PeerUrls);
        env["ET_NO_LISTENER"] = "true";
        env["ET_RPC_PORTAL"] = "127.0.0.1:15888";
        env["ET_DEFAULT_PROTOCOL"] = "tcp";
        env["ET_HOSTNAME"] = Environment.MachineName;
        env["ET_ACCEPT_DNS"] = "false";
        env["ET_FILE_LOG_DIR"] = NativeExtractor.LogsDir;
        env["ET_FILE_LOG_LEVEL"] = "info";

        _core = Process.Start(psi);
        SimpleLog.Info($"easytier-core 已启动（本机虚拟IP {Mask(alloc.Ip)}）");
    }

    public void Stop()
    {
        try
        {
            if (_core is { HasExited: false })
            {
                _core.Kill(entireProcessTree: true);
                _core.WaitForExit(8000);
                SimpleLog.Info("easytier-core 已停止");
            }
        }
        catch (Exception ex)
        {
            SimpleLog.Warn($"停止 easytier-core 异常：{ex.Message}");
        }
        finally
        {
            _core?.Dispose();
            _core = null;
            KillStray();
        }
    }

    /// <summary>探测开服服务器连通性（ping 10.144.0.1）。</summary>
    public async Task<bool> PingServerAsync(int timeoutMs = 1500)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse(ServerVip), timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private static string Mask(string ip)
    {
        var i = ip.LastIndexOf('.');
        return i > 0 ? ip[..i] + ".*" : ip;
    }

    public void Dispose() => Stop();
}
