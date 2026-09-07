using EasyTierGroupClient.Models;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Services;

public enum ConnState
{
    Disconnected, // 未连接
    Connecting,   // 连接中（含自动重连等待）
    Connected,    // 已连接
}

/// <summary>连接总编排：申请虚拟 IP → 启动 easytier → 防火墙隔离 → 心跳保活 → 异常自动重连。
/// 并发与卡死防护设计：
///   - 每次连接会话有唯一递增「代际号」，旧循环的一切清理动作只在代际仍是当前时生效，
///     卡死的旧任务苏醒后不会停掉新会话的进程/防火墙/租约；
///   - 断开与退出路径全部后台执行且有界（15 秒兜底），绝不长时间阻塞 UI 线程；
///   - Connect() 永远可用：发现旧任务未收尾时换新代际直接开新循环，后台回收旧任务。
/// 口令仅在内存中流转，不落盘、不进日志。</summary>
public sealed class ConnectionOrchestrator : IDisposable
{
    private readonly ClientConfig _cfg;
    private readonly EasyTierManager _et = new();
    private readonly TrafficMonitor _traffic = new();
    private readonly object _gate = new();

    private CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private AllocApiClient? _api;
    private AllocInfo? _alloc;
    private string? _lastTriedCode;
    private int _generation;

    public ConnState State { get; private set; } = ConnState.Disconnected;
    public string? StatusText { get; private set; }
    public string? VirtualIp => _alloc?.Ip;
    public DateTime? ConnectedSince { get; private set; }
    public bool ServerReachable { get; private set; }
    public TrafficMonitor Traffic => _traffic;
    public bool CoreRunning => _et.IsRunning;

    /// <summary>状态或统计数据变化时触发（在后台线程，UI 需自行调度）。</summary>
    public event Action? Changed;

    public ConnectionOrchestrator(ClientConfig cfg) => _cfg = cfg;

    /// <summary>应用启动时清理上次会话残留（孤儿 core 进程、防火墙规则）。不发起连接。</summary>
    public void CleanStartupResidue()
    {
        Task.Run(() =>
        {
            try
            {
                _et.KillStray();
                FirewallManager.RemoveIsolation();
                SimpleLog.Info("启动残留清理完成");
            }
            catch (Exception ex)
            {
                SimpleLog.Error("启动残留清理失败", ex);
            }
        });
    }

    /// <summary>启动连接。始终可用：正在连接/已连接时忽略重复调用；
    /// 存在未收尾的旧任务（上次断开卡住等罕见情况）时换新代际立即开新循环，旧任务后台自然消亡。</summary>
    public void Connect()
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false } && State is ConnState.Connecting or ConnState.Connected)
                return; // 已在连接或已连接
            _generation++;
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var gen = _generation;
            _loopTask = Task.Run(() => RunAsync(ct, gen));
        }
    }

    /// <summary>断开连接。立即置为未连接状态并允许再次连接；实际清理在后台有界完成。</summary>
    public async Task DisconnectAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _generation++; // 旧循环（若仍存活）的清理从此失效
            loop = _loopTask;
            _loopTask = null;
            try { _cts.Cancel(); } catch { }
        }
        SetState(ConnState.Disconnected, null);

        await Task.Run(async () =>
        {
            // 等旧循环退出，最多 15 秒；卡死则放弃等待，直接强制收尾
            if (loop is not null)
            {
                try { await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(15))); }
                catch { }
                if (!loop.IsCompleted)
                    SimpleLog.Warn("旧连接任务未按时退出，强制收尾");
            }

            AllocApiClient? api;
            string? ip;
            lock (_gate)
            {
                api = _api;
                ip = _alloc?.Ip;
                _api = null;
                _alloc = null;
            }
            if (api is not null && ip is not null)
            {
                try { await api.ReleaseAsync(_cfg.InviteCode, ip, _cfg.MachineId); }
                catch { /* 释放尽力而为：服务端心跳超时也会回收 */ }
                api.Dispose();
            }
            _et.Stop();
            FirewallManager.RemoveIsolation();
            ConnectedSince = null;
            ServerReachable = false;
        });
        RaiseChanged();
    }

    // ------------------------------------------------------------------ 主循环

    private async Task RunAsync(CancellationToken ct, int gen)
    {
        SetState(ConnState.Connecting, "正在连接…");
        try
        {
            _et.KillStray(); // 先清残留进程，避免旧文件占用无法更新组件
            NativeExtractor.ExtractAll();
        }
        catch (Exception ex)
        {
            SimpleLog.Error("释放内嵌组件失败", ex);
            SetState(ConnState.Connecting, $"客户端组件异常：{ex.Message}");
            await WaitForUserActionAsync(ct);
            if (ct.IsCancellationRequested)
            {
                SetState(ConnState.Disconnected, null);
                return;
            }
        }
        var backoff = TimeSpan.FromSeconds(5);
        var maxBackoff = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            _lastTriedCode = _cfg.InviteCode;
            try
            {
                var ok = await ConnectOnceAsync(ct);
                if (ok)
                {
                    backoff = TimeSpan.FromSeconds(5); // 成功后重置退避
                    await MaintainAsync(ct);
                    if (!ct.IsCancellationRequested)
                    {
                        SetState(ConnState.Connecting, "连接中断，正在自动重连…");
                        SimpleLog.Warn("连接丢失，准备重连");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AllocApiException ex)
            {
                SimpleLog.Error("连接失败（服务端拒绝）", ex);
                SetState(ConnState.Connecting, DescribeApiError(ex));
                if (ex.IsInvalidCode || ex.IsCodeInUse || ex.IsPoolFull)
                {
                    // 邀请码无效/被占用/满员：重试无意义，等待用户处理
                    await WaitForUserActionAsync(ct);
                    if (ct.IsCancellationRequested) break;
                    continue;
                }
            }
            catch (Exception ex)
            {
                SimpleLog.Error("连接流程异常", ex);
                SetState(ConnState.Connecting, $"连接失败：{ex.Message}");
            }

            await CleanupAsync(gen);
            if (ct.IsCancellationRequested) break;

            try
            {
                SetState(ConnState.Connecting, $"{Math.Ceiling(backoff.TotalSeconds)} 秒后自动重试…");
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        await CleanupAsync(gen);
        SetState(ConnState.Disconnected, null);
    }

    private async Task<bool> ConnectOnceAsync(CancellationToken ct)
    {
        _api?.Dispose();
        _api = new AllocApiClient(_cfg.ServiceUrl, _cfg.CertSha256);
        SetState(ConnState.Connecting, "正在申请虚拟 IP…");
        _alloc = await _api.AllocAsync(_cfg.InviteCode, _cfg.MachineId, ct);
        SimpleLog.Info($"已分配虚拟IP {MaskIp(_alloc.Ip)}");

        SetState(ConnState.Connecting, "正在建立组网…");
        _et.Start(_alloc);

        FirewallManager.ApplyIsolation();

        // 等待虚拟网就绪：最多 30 秒，期间 ping 开服服务器
        SetState(ConnState.Connecting, "正在接入服务器…");
        var reachable = false;
        for (var i = 0; i < 15 && !ct.IsCancellationRequested; i++)
        {
            if (!_et.IsRunning)
                throw new InvalidOperationException(
                    "组网组件启动失败。若首次运行时系统弹窗询问是否安装网络驱动，请选择「允许」后重试。");
            reachable = await _et.PingServerAsync();
            if (reachable) break;
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { throw; }
        }

        _traffic.Bind(_alloc.Ip);
        ConnectedSince = DateTime.Now;
        ServerReachable = reachable;
        SetState(ConnState.Connected, reachable ? null : "已连入组网（服务器暂未响应 ping，可稍候重试）");
        return true;
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        var hbPeriod = TimeSpan.FromSeconds(Math.Max(10, _alloc!.HeartbeatIntervalSec));
        using var hbTimer = new PeriodicTimer(hbPeriod);
        using var probeTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var hbTask = hbTimer.WaitForNextTickAsync(ct).AsTask();
        var probeTask = probeTimer.WaitForNextTickAsync(ct).AsTask();
        var misses = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_et.IsRunning)
            {
                SimpleLog.Warn("easytier-core 进程退出，准备重连");
                return;
            }

            var done = await Task.WhenAny(hbTask, probeTask);
            if (done == hbTask)
            {
                try { await hbTask; } catch (OperationCanceledException) { throw; }
                hbTask = hbTimer.WaitForNextTickAsync(ct).AsTask();
                bool alive;
                try
                {
                    alive = await _api!.HeartbeatAsync(_cfg.InviteCode, _alloc.Ip, _cfg.MachineId, ct);
                    misses = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 网络抖动：容忍 3 次连续失败（服务端 TTL 90s）
                    misses++;
                    SimpleLog.Warn($"心跳失败 {misses} 次：{ex.Message}");
                    if (misses >= 3) { SimpleLog.Warn("心跳连续失败，准备重连"); return; }
                    continue;
                }
                if (!alive)
                {
                    SimpleLog.Warn("IP 租约已丢失，重新申请");
                    return;
                }
            }
            else
            {
                try { await probeTask; } catch (OperationCanceledException) { throw; }
                probeTask = probeTimer.WaitForNextTickAsync(ct).AsTask();
                ServerReachable = await _et.PingServerAsync();
                RaiseChanged();
            }
        }
    }

    /// <summary>邀请码无效/占用/满员时：停 60 秒等待用户修改后重试（用户断开则退出）。</summary>
    private async Task WaitForUserActionAsync(CancellationToken ct)
    {
        var waited = 0;
        while (waited < 60 && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { throw; }
            waited += 5;
            // 用户改了邀请码后立即重试
            if (!string.Equals(_cfg.InviteCode, _lastTriedCode, StringComparison.Ordinal))
            {
                _lastTriedCode = _cfg.InviteCode;
                return;
            }
        }
    }

    // ------------------------------------------------------------------ 清理

    /// <summary>会话收尾：释放租约、停进程、移除防火墙规则。
    /// 仅当代际仍是当前会话时才执行——被新连接/断开接管后，这里的任何动作都可能
    /// 杀掉新会话的进程或释放新会话的租约，必须整体跳过。</summary>
    private async Task CleanupAsync(int gen)
    {
        AllocApiClient? api;
        string? ip;
        lock (_gate)
        {
            if (gen != _generation) return;
            api = _api;
            ip = _alloc?.Ip;
            _api = null;
            _alloc = null;
        }
        if (api is not null && ip is not null)
        {
            try { await api.ReleaseAsync(_cfg.InviteCode, ip, _cfg.MachineId); }
            catch { /* 释放尽力而为 */ }
            api.Dispose();
        }
        _et.Stop();
        FirewallManager.RemoveIsolation();
        ConnectedSince = null;
        ServerReachable = false;
    }

    // ------------------------------------------------------------------ 杂项

    private static string DescribeApiError(AllocApiException ex) => ex switch
    {
        _ when ex.IsInvalidCode => "邀请码无效或已被停用，请打开「设置」检查邀请码。",
        _ when ex.IsCodeInUse => "这个邀请码正在另一台电脑上使用（一人一码一机）。请勿共用邀请码，或联系管理员换新码。",
        _ when ex.IsPoolFull => "当前在线人数已满（最多 10 人），请稍后再试。",
        _ => $"服务端返回错误：{ex.Message}",
    };

    private static string MaskIp(string ip)
    {
        var i = ip.LastIndexOf('.');
        return i > 0 ? ip[..i] + ".*" : ip;
    }

    private void SetState(ConnState state, string? text)
    {
        State = state;
        StatusText = text;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _generation++; } catch { }
        _api?.Dispose();
        _et.Dispose();
        _traffic.Dispose();
        try { _cts.Dispose(); } catch { }
    }
}
