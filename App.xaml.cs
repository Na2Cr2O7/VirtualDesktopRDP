using System.Windows.Threading;
using VirtualDesktopRDP.Dialogs;
using VirtualDesktopRDP.Services;

namespace VirtualDesktopRDP;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 没有安装器，启动时自行维护应用级防火墙放行规则。
        _ = FirewallService.EnsureInboundRule();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppMessageBox.Show(
            e.Exception.GetBaseException().Message,
            "VirtualDesktopRDP",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }
}
