using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Services;

/// <summary>easytier-core 进程生命周期管理。
/// 启动方式：写入 TOML 配置文件后以 `-c <固定路径>` 参数启动（ArgumentList 参数数组，无字符串拼接、无环境变量传参），
/// 口令只存在于用户配置目录下的配置文件中，不进命令行。</summary>
public sealed class EasyTierManager : IDisposable
{
    private Process? _core;
    private StreamWriter? _coreLog;

    public string CorePath => Path.Combine(NativeExtractor.BinDir, "easytier-core.exe");
    public string CliPath => Path.Combine(NativeExtractor.BinDir, "easytier-cli.exe");

    /// <summary>配置文件目录（用户档案下，继承用户级访问控制）。</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyTierGroupClient", "config");

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
        var (vip, netName, netSecret, peers) = SanitizeAlloc(alloc);
        KillStray();

        var cfgPath = WriteConfig(vip, netName, netSecret, alloc.PeerUrls.Select(KeepPeer).ToList());

        var psi = new ProcessStartInfo
        {
            FileName = CorePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = NativeExtractor.BinDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(cfgPath);

        Directory.CreateDirectory(NativeExtractor.LogsDir);
        _coreLog?.Dispose();
        _coreLog = new StreamWriter(
            Path.Combine(NativeExtractor.LogsDir, $"core-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
            false, System.Text.Encoding.UTF8)
        { AutoFlush = true };

        _core = new Process { StartInfo = psi };
        _core.OutputDataReceived += (_, e) => WriteCoreLog(e?.Data);
        _core.ErrorDataReceived += (_, e) => WriteCoreLog(e?.Data);
        _core.Start();
        _core.BeginOutputReadLine();
        _core.BeginErrorReadLine();
        SimpleLog.Info($"easytier-core 已启动（本机虚拟IP {Mask(vip)}）");
    }

    private void WriteCoreLog(string? line)
    {
        if (line is null) return;
        try
        {
            lock (this)
            {
                _coreLog?.WriteLine(line);
            }
        }
        catch
        {
            // 日志写失败不影响运行
        }
    }

    /// <summary>生成 easytier TOML 配置（每次连接覆盖写入）。值均已经白名单净化并做 TOML 转义。</summary>
    private static string WriteConfig(string vip, string networkName, string networkSecret, List<string> peerUrls)
    {
        Directory.CreateDirectory(ConfigDir);
        var path = Path.Combine(ConfigDir, "easytier.toml");
        var lines = new List<string>
        {
            "# EasyTier 群友客户端自动生成，每次连接覆盖",
            $"ipv4 = \"{vip}\"",
            "",
            "[network_identity]",
            $"network_name = \"{TomlEscape(networkName)}\"",
            $"network_secret = \"{TomlEscape(networkSecret)}\"",
            "",
            "[flags]",
            "default_protocol = \"tcp\"",
            "no_listener = true",
            "rpc_portal = \"127.0.0.1:15888\"",
            $"hostname = \"{TomlEscape(KeepOnly(Environment.MachineName, static c => char.IsAsciiLetterOrDigit(c) || "-_".Contains(c), "主机名"))}\"",
        };
        foreach (var uri in peerUrls)
        {
            lines.Add("");
            lines.Add("[[peer]]");
            lines.Add($"uri = \"{TomlEscape(uri)}\"");
        }
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return path;
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
            try { _coreLog?.Dispose(); } catch { }
            _coreLog = null;
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

    /// <summary>
    /// 净化来自分配服务的全部字段：仅按白名单字符重建字符串后才允许进入配置文件。
    /// 任何字符被过滤掉（与原始长度不符）即视为非法，直接拒绝启动。
    /// </summary>
    private static (string Vip, string NetworkName, string NetworkSecret, string Peers) SanitizeAlloc(AllocInfo alloc)
    {
        var vip = KeepOnly(alloc.Ip, static c => char.IsAsciiDigit(c) || c == '.', "虚拟 IP");
        if (!System.Net.IPAddress.TryParse(vip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("服务端返回的虚拟 IP 无效");

        var netName = KeepOnly(alloc.NetworkName, static c => !char.IsControl(c) && c != 0x7F, "网络名");
        var netSecret = KeepOnly(alloc.NetworkSecret, static c => !char.IsControl(c) && c != 0x7F, "口令");
        if (netName.Length == 0 || netName.Length > 256) throw new InvalidOperationException("网络名不合法");
        if (netSecret.Length == 0 || netSecret.Length > 256) throw new InvalidOperationException("口令不合法");

        if (alloc.PeerUrls.Count == 0) throw new InvalidOperationException("服务端未返回对端地址");
        foreach (var u in alloc.PeerUrls)
        {
            var s = KeepPeer(u);
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    s, @"^(tcp|udp|wg|quic|ws|wss|faketcp)://[A-Za-z0-9._:\-]+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("对端地址协议不合法");
        }
        var peers = string.Join(",", alloc.PeerUrls.Select(KeepPeer));
        return (vip, netName, netSecret, peers);
    }

    /// <summary>对端地址白名单：仅字母数字与 . _ : -（不含引号、空格等 TOML/路径特殊字符）。</summary>
    private static string KeepPeer(string url) =>
        KeepOnly(url, static c => char.IsAsciiLetterOrDigit(c) || "._:-".Contains(c), "对端地址");

    /// <summary>按白名单字符重建字符串；若有字符被丢弃则抛出异常（拒绝而非截断）。</summary>
    private static string KeepOnly(string input, Func<char, bool> allowed, string what)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (allowed(c)) sb.Append(c);
        }
        if (sb.Length != input.Length)
            throw new InvalidOperationException($"服务端返回的{what}包含非法字符");
        return sb.ToString();
    }

    private static string TomlEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Mask(string ip)
    {
        var i = ip.LastIndexOf('.');
        return i > 0 ? ip[..i] + ".*" : ip;
    }

    public void Dispose() => Stop();
}
