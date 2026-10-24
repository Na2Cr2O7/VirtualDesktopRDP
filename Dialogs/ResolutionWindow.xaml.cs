using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace VirtualDesktopRDP.Dialogs;

/// <summary>
/// 输入虚拟桌面分辨率，并对常见误操作做前端校验。
/// </summary>
public partial class ResolutionWindow : Window
{
    private static readonly Regex NumberRegex = new("^[0-9]+$", RegexOptions.Compiled);

    public ResolutionWindow(int currentWidth, int currentHeight)
    {
        InitializeComponent();
        WidthTextBox.Text = currentWidth.ToString();
        HeightTextBox.Text = currentHeight.ToString();
        WidthTextBox.SelectAll();
        WidthTextBox.Focus();
    }

    public int SelectedWidth { get; private set; }

    public int SelectedHeight { get; private set; }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        WidthTextBox.Text = parts[0];
        HeightTextBox.Text = parts[1];
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var title = I18nService.T("输入分辨率");
        if (!int.TryParse(WidthTextBox.Text, out var width)
            || !int.TryParse(HeightTextBox.Text, out var height)
            || width <= 0
            || height <= 0)
        {
            AppMessageBox.Show(
                I18nService.T("请输入有效的宽度和高度。"),
                title,
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning,
                WpfMessageBoxResult.OK,
                this);
            return;
        }

        if (ResolutionConstraints.IsBelowMinimum(width, height))
        {
            AppMessageBox.Show(
                I18nService.T("分辨率不得低于 800×600。"),
                title,
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning,
                WpfMessageBoxResult.OK,
                this);
            return;
        }

        if (ResolutionConstraints.IsAboveMaximum(width, height))
        {
            AppMessageBox.Show(
                I18nService.T("分辨率不能超过 8192×8192。"),
                title,
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning,
                WpfMessageBoxResult.OK,
                this);
            return;
        }

        if (ResolutionConstraints.IsAboveWarningThreshold(width, height))
        {
            var result = AppMessageBox.Show(
                I18nService.F(
                    "分辨率 {0}×{1} 超过 2560×1440，可能导致画面缩放、性能下降或显示异常，是否继续？",
                    width,
                    height),
                I18nService.T("分辨率提醒"),
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Warning,
                WpfMessageBoxResult.No,
                this);
            if (result != WpfMessageBoxResult.Yes)
            {
                return;
            }
        }

        SelectedWidth = width;
        SelectedHeight = height;
        DialogResult = true;
    }

    private void OnNumberPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !NumberRegex.IsMatch(e.Text);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
