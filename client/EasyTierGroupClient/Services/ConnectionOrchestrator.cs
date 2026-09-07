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
/// 口令仅在内存中流转，不落盘、不进日志。</summary>
public sealed class ConnectionOrchestrator : IDisposable
{
    private readonly ClientConfig _cfg;
    private readonly EasyTierManager _et = new();
    private readonly TrafficMonitor _traffic = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Task? _loopTask;
    private AllocApiClient? _api;
    private AllocInfo? _alloc;

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

    public void Connect()
    {
        lock (_gate)
        {
            if (_loopTask is not null) return;
            _loopTask = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async Task DisconnectAsync()
    {
        Task? loop;
        lock (_gate) loop = _loopTask;
        if (loop is null)
        {
            CleanupRuntime();
            RaiseChanged();
            return;
        }
        _cts.Cancel();
        try { await loop; } catch { /* 忽略收尾异常 */ }
        lock (_gate) _loopTask = null;
        RaiseChanged();
    }

    // ------------------------------------------------------------------ 主循环

    private async Task RunAsync(CancellationToken ct)
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
                    // Maintain 正常返回 = 连接已丢失，需要重连
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
                if (ex.IsInvalidCode || ex.IsPoolFull)
                {
                    // 邀请码无效或满员：重试无意义，等待用户处理
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

            CleanupRuntime();
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

        CleanupRuntime();
        SetState(ConnState.Disconnected, null);
    }

    private async Task<bool> ConnectOnceAsync(CancellationToken ct)
    {
        _api?.Dispose();
        _api = new AllocApiClient(_cfg.ServiceUrl, _cfg.CertSha256);
        SetState(ConnState.Connecting, "正在申请虚拟 IP…");
        _alloc = await _api.AllocAsync(_cfg.InviteCode, ct);
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
                    alive = await _api!.HeartbeatAsync(_cfg.InviteCode, _alloc.Ip, ct);
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

    /// <summary>邀请码无效/满员时：停 60 秒等待用户修改后重试（用户断开则退出）。</summary>
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

    private string? _lastTriedCode;

    // ------------------------------------------------------------------ 清理

    private void CleanupRuntime()
    {
        try
        {
            if (_api is not null && _alloc is not null)
                _api.ReleaseAsync(_cfg.InviteCode, _alloc.Ip).Wait(TimeSpan.FromSeconds(8));
        }
        catch { /* 释放尽力而为 */ }
        _et.Stop();
        FirewallManager.RemoveIsolation();
        ConnectedSince = null;
        ServerReachable = false;
        _alloc = null;
    }

    // ------------------------------------------------------------------ 杂项

    private static string DescribeApiError(AllocApiException ex) => ex switch
    {
        _ when ex.IsInvalidCode => "邀请码无效或已被停用，请打开「设置」检查邀请码。",
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
        try { _loopTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        _api?.Dispose();
        _et.Dispose();
        _traffic.Dispose();
        _cts.Dispose();
    }
}
