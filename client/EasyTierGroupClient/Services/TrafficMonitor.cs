using System.Net.NetworkInformation;

namespace EasyTierGroupClient.Services;

/// <summary>轮询本机虚拟网卡的 IPv4 收发字节数，计算会话累计流量与实时速率。</summary>
public sealed class TrafficMonitor : IDisposable
{
    private NetworkInterface? _nic;
    private long _lastRx, _lastTx;
    private DateTime _lastSample = DateTime.UtcNow;

    public ulong SessionRx { get; private set; }
    public ulong SessionTx { get; private set; }
    public double RateRx { get; private set; } // 字节/秒
    public double RateTx { get; private set; }

    /// <summary>按本机虚拟 IP 找到 easytier 的 TUN 网卡并开始统计。</summary>
    public void Bind(string localVip)
    {
        _nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        {
            try
            {
                return n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.OperationalStatus == OperationalStatus.Up
                    && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == localVip);
            }
            catch
            {
                return false;
            }
        });
        if (_nic is null) return;

        var s = _nic.GetIPv4Statistics();
        _lastRx = s.BytesReceived;
        _lastTx = s.BytesSent;
        SessionRx = 0;
        SessionTx = 0;
        _lastSample = DateTime.UtcNow;
    }

    public void Sample()
    {
        if (_nic is null) return;
        try
        {
            var s = _nic.GetIPv4Statistics();
            long rx = s.BytesReceived;
            long tx = s.BytesSent;
            var now = DateTime.UtcNow;
            var dt = Math.Max(0.2, (now - _lastSample).TotalSeconds);
            var dRx = rx >= _lastRx ? (ulong)(rx - _lastRx) : 0;
            var dTx = tx >= _lastTx ? (ulong)(tx - _lastTx) : 0;
            SessionRx += dRx;
            SessionTx += dTx;
            RateRx = dRx / dt;
            RateTx = dTx / dt;
            _lastRx = rx;
            _lastTx = tx;
            _lastSample = now;
        }
        catch
        {
            // 网卡消失（断开中）时忽略本次采样
        }
    }

    public void Dispose() => _nic = null;
}
