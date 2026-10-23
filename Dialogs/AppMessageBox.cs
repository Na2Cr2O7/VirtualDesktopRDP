using System.Windows;

namespace VirtualDesktopRDP.Dialogs;

/// <summary>
/// 替换系统 MessageBox 的统一入口，使用项目自己的直角边框对话框。
/// </summary>
public static class AppMessageBox
{
    public static MessageBoxResult Show(
        string message,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.OK,
        Window? owner = null)
    {
        var dialog = new AppMessageBoxWindow();
        dialog.Configure(caption, message, button, image, defaultResult);

        owner ??= System.Windows.Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}
