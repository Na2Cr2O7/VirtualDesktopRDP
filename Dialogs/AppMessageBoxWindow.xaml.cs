using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WpfButton = System.Windows.Controls.Button;

namespace VirtualDesktopRDP.Dialogs;

/// <summary>
/// 与主界面风格统一的直角消息对话框：3px 边框、无圆角。
/// </summary>
public partial class AppMessageBoxWindow : Window
{
    private MessageBoxResult? _result;
    private MessageBoxResult _cancelResult = MessageBoxResult.Cancel;

    public AppMessageBoxWindow()
    {
        InitializeComponent();
    }

    public MessageBoxResult Result => _result ?? _cancelResult;

    internal void Configure(
        string caption,
        string message,
        MessageBoxButton button,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        Title = caption;
        TitleText.Text = caption;
        MessageText.Text = message;
        _cancelResult = button switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel
        };

        ApplyIcon(image);
        BuildButtons(button, defaultResult);
    }

    private void ApplyIcon(MessageBoxImage image)
    {
        switch (image)
        {
            case MessageBoxImage.Error:
                IconText.Text = "×";
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4A, 0x25, 0x28));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x7A));
                IconText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x9A, 0x9A));
                break;

            case MessageBoxImage.Warning:
                IconText.Text = "!";
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4A, 0x3D, 0x20));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x5A));
                IconText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xD1, 0x7A));
                break;

            case MessageBoxImage.Question:
                IconText.Text = "?";
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0x3A, 0x4E));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x7F, 0xBF, 0xFF));
                IconText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x9F, 0xD1, 0xFF));
                break;

            case MessageBoxImage.Information:
                IconText.Text = "i";
                IconBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0x3A, 0x4E));
                IconBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x7F, 0xBF, 0xFF));
                IconText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x9F, 0xD1, 0xFF));
                break;

            default:
                IconBorder.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void BuildButtons(MessageBoxButton button, MessageBoxResult defaultResult)
    {
        ButtonsPanel.Children.Clear();
        foreach (var (result, text) in GetButtonSpecs(button))
        {
            var isPrimary = result == defaultResult;
            var dialogButton = new WpfButton
            {
                Content = text,
                MinWidth = 92,
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = isPrimary,
                Style = (Style)FindResource(
                    isPrimary
                        ? "AppDialogPrimaryButtonStyle"
                        : "AppDialogButtonStyle")
            };
            var buttonResult = result;
            dialogButton.Click += (_, _) => CloseWithResult(buttonResult);
            ButtonsPanel.Children.Add(dialogButton);
        }
    }

    private static IReadOnlyList<(MessageBoxResult Result, string Text)> GetButtonSpecs(
        MessageBoxButton button)
    {
        return button switch
        {
            MessageBoxButton.OK =>
            [
                (MessageBoxResult.OK, "确定")
            ],
            MessageBoxButton.OKCancel =>
            [
                (MessageBoxResult.OK, "确定"),
                (MessageBoxResult.Cancel, "取消")
            ],
            MessageBoxButton.YesNo =>
            [
                (MessageBoxResult.Yes, "是"),
                (MessageBoxResult.No, "否")
            ],
            MessageBoxButton.YesNoCancel =>
            [
                (MessageBoxResult.Yes, "是"),
                (MessageBoxResult.No, "否"),
                (MessageBoxResult.Cancel, "取消")
            ],
            _ =>
            [
                (MessageBoxResult.OK, "确定")
            ]
        };
    }

    private void CloseWithResult(MessageBoxResult result)
    {
        if (_result is not null)
        {
            return;
        }

        _result = result;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(_cancelResult);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWithResult(_cancelResult);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _result ??= _cancelResult;
    }
}
