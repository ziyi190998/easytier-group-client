using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EasyTierGroupClient.Models;
using EasyTierGroupClient.Services;
using EasyTierGroupClient.Util;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace EasyTierGroupClient;

public partial class MainWindow : Window
{
    private readonly ClientConfig _cfg;
    private readonly ConnectionOrchestrator _orch;
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer = new()
    { Interval = TimeSpan.FromSeconds(2) };

    private WinForms.NotifyIcon? _tray;
    private readonly Dictionary<ConnState, System.Drawing.Icon?> _trayIcons = new();
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _cfg = ClientConfig.Load();
        _orch = new ConnectionOrchestrator(_cfg);
        _orch.Changed += OnOrchestratorChanged;
        _uiTimer.Tick += (_, _) => UpdateUi();

        InviteBox.Text = _cfg.InviteCode;
        SvcInfoText.Text = $"服务地址：{_cfg.ServiceUrl}";

        InitTray();
        Closed += (_, _) => Cleanup();
        UpdateUi();
        _uiTimer.Start();

        // 傻瓜式体验：已保存邀请码则启动即连
        if (!string.IsNullOrWhiteSpace(_cfg.InviteCode))
            _orch.Connect();
        else
            DetailText.Text = "首次使用：请展开「设置」，填入管理员发给你的邀请码后点击连接。";
    }

    // ------------------------------------------------------------------ 托盘

    private void InitTray()
    {
        _trayIcons[ConnState.Connected] = MakeIcon("#2ECC71");
        _trayIcons[ConnState.Connecting] = MakeIcon("#F39C12");
        _trayIcons[ConnState.Disconnected] = MakeIcon("#8A8F9C");

        _tray = new WinForms.NotifyIcon
        {
            Text = "EasyTier 群友客户端",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("连接", null, (_, _) => Dispatcher.Invoke(ConnectFromUi));
        menu.Items.Add("断开", null, (_, _) => Dispatcher.Invoke(async () => await DisconnectFromUi()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAppAsync());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        UpdateTrayIcon();
    }

    private static System.Drawing.Icon? MakeIcon(string hex)
    {
        try
        {
            var color = (System.Drawing.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    private void UpdateTrayIcon()
    {
        if (_tray is null) return;
        _tray.Icon = _trayIcons.GetValueOrDefault(_orch.State);
        _tray.Text = _orch.State switch
        {
            ConnState.Connected => $"已连接（{_orch.VirtualIp ?? "—"}）",
            ConnState.Connecting => "连接中…",
            _ => "未连接",
        };
    }

    // ------------------------------------------------------------------ UI 刷新

    private void OnOrchestratorChanged() => Dispatcher.BeginInvoke(UpdateUi);

    private void UpdateUi()
    {
        var st = _orch.State;
        StateDot.Fill = st switch
        {
            ConnState.Connected => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
            ConnState.Connecting => new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)),
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9C)),
        };
        StateText.Text = st switch
        {
            ConnState.Connected => "已连接",
            ConnState.Connecting => "连接中…",
            _ => "未连接",
        };
        DetailText.Text = _orch.StatusText ?? string.Empty;

        IpText.Text = _orch.VirtualIp ?? "—";
        ServerText.Text = st == ConnState.Connected
            ? (_orch.ServerReachable ? "可达（10.144.0.1）" : "ping 未通（可能被限流，不影响使用）")
            : "—";
        DurationText.Text = _orch.ConnectedSince is { } since
            ? Fmt.Duration(DateTime.Now - since)
            : "—";

        if (st == ConnState.Connected)
        {
            var tr = _orch.Traffic;
            RxText.Text = $"{Fmt.Bytes(tr.SessionRx)}（{Fmt.Rate(tr.RateRx)}）";
            TxText.Text = $"{Fmt.Bytes(tr.SessionTx)}（{Fmt.Rate(tr.RateTx)}）";
        }
        else
        {
            RxText.Text = "—";
            TxText.Text = "—";
        }

        var hasCode = !string.IsNullOrWhiteSpace(_cfg.InviteCode);
        ConnectBtn.IsEnabled = st == ConnState.Disconnected && hasCode;
        DisconnectBtn.IsEnabled = st != ConnState.Disconnected;
        UpdateTrayIcon();
    }

    // ------------------------------------------------------------------ 交互

    private void ConnectBtn_Click(object sender, RoutedEventArgs e) => ConnectFromUi();

    private void ConnectFromUi()
    {
        SaveInviteCode(silent: true);
        if (string.IsNullOrWhiteSpace(_cfg.InviteCode))
        {
            DetailText.Text = "请先在「设置」里填入邀请码。";
            return;
        }
        _orch.Connect();
    }

    private async void DisconnectBtn_Click(object sender, RoutedEventArgs e) => await DisconnectFromUi();

    private async Task DisconnectFromUi() => await _orch.DisconnectAsync();

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveInviteCode(silent: false);
        if (!string.IsNullOrWhiteSpace(_cfg.InviteCode) && _orch.State == ConnState.Disconnected)
            _orch.Connect();
    }

    private void SaveInviteCode(bool silent)
    {
        var code = InviteBox.Text.Trim();
        if (code == _cfg.InviteCode) return;
        _cfg.InviteCode = code;
        _cfg.Save();
        if (!silent)
            MessageBox.Show("邀请码已保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ExitAppAsync()
    {
        _reallyExit = true;
        await Dispatcher.InvokeAsync(async () =>
        {
            Hide();
            await _orch.DisconnectAsync();
        });
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        _uiTimer.Stop();
        _orch.Changed -= OnOrchestratorChanged;
        try { _orch.Dispose(); } catch { }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        foreach (var icon in _trayIcons.Values)
        {
            try { icon?.Dispose(); } catch { }
        }
    }
}
