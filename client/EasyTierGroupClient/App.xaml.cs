using System.Windows;
using EasyTierGroupClient.Util;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EasyTierGroupClient;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Global\EasyTierGroupClient-SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("EasyTier 群友客户端已在运行，请查看右下角托盘图标。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            SimpleLog.Error("未处理UI异常", ex.Exception);
            ex.Handled = true;
        };

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
