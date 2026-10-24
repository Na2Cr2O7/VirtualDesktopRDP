using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualDesktopRDP.Dialogs;
using VirtualDesktopRDP.GameMouse;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using VirtualDesktopRDP.Services;
using VirtualDesktopRDP.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VirtualDesktopRDP;

public partial class MainWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;

    private readonly SettingsService _settingsService;
    private readonly ChildSessionService _childSessionService;
    private readonly GameMouseService _gameMouseService;
    private readonly MainWindowViewModel _viewModel;
    private readonly Forms.NotifyIcon _trayIcon;

    private bool _allowClose;
    private bool _closeInProgress;
    private bool _adjustingAspectRatio;
    private double _lastWidth;
    private double _lastHeight;
    private bool _trayHintShown;

    public MainWindow()
    {
        _settingsService = new SettingsService();
        I18nService.Instance.ChangeLanguage(_settingsService.Current.Language);

        InitializeComponent();

        _childSessionService = new ChildSessionService(_settingsService);
        _gameMouseService = new GameMouseService(_childSessionService, _settingsService);
        _viewModel = new MainWindowViewModel(_childSessionService, _gameMouseService);
        DataContext = _viewModel;
        RdpHostContainer.Child = _childSessionService.RdpHost;
        RdpHostContainer.IsVisibleChanged += OnRdpHostVisibilityChanged;
        ControlCenterPanel.AddHandler(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnControlCenterActionClick));

        _viewModel.HideRequested += OnHideRequested;
        _viewModel.SettingsRequested += OnSettingsRequested;
        _viewModel.ResolutionRequested += OnResolutionRequested;
        _viewModel.ResizeRequested += OnResizeRequested;

        RestoreWindowPlacement();
        _trayIcon = CreateTrayIcon();

        _lastWidth = Width;
        _lastHeight = Height;
        SizeChanged += OnWindowSizeChanged;
        TitleBar.SizeChanged += OnTitleBarSizeChanged;
        I18nService.Instance.LanguageChanged += OnLanguageChanged;
        UpdateMaximizeRestoreButton();
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCaptionHeight();
    }

    /// <summary>
    /// 窄窗口下工具栏会换行，标题栏高度随之增加；同步 WindowChrome 的拖动区域，
    /// 让整块顶部区域都可以拖动窗口。
    /// </summary>
    private void UpdateCaptionHeight()
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CaptionHeight = Math.Max(40, TitleBar.ActualHeight);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeRestoreButton();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = maximized ? "❐" : "□";
        MaximizeRestoreButton.ToolTip = maximized
            ? I18nService.T("还原")
            : I18nService.T("最大化");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeRestoreButton();
        UpdateTrayMenuText();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (!_childSessionService.HasActiveChildSession())
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);

        if (_closeInProgress)
        {
            return;
        }

        var result = AppMessageBox.Show(
            I18nService.T("关闭会断开 RDP 并注销虚拟桌面，其中所有正在运行的程序都会被关闭，未保存的数据会丢失。是否继续？"),
            I18nService.T("关闭 VirtualDesktopRDP"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No,
            this);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _closeInProgress = true;
        _ = CloseAfterLogoffAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        SizeChanged -= OnWindowSizeChanged;
        TitleBar.SizeChanged -= OnTitleBarSizeChanged;
        RdpHostContainer.IsVisibleChanged -= OnRdpHostVisibilityChanged;
        I18nService.Instance.LanguageChanged -= OnLanguageChanged;

        SaveWindowPlacement();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        RdpHostContainer.Child = null;
        _viewModel.Dispose();
        _gameMouseService.Dispose();
        _childSessionService.Dispose();

        base.OnClosed(e);
    }

    private async Task CloseAfterLogoffAsync()
    {
        try
        {
            await _viewModel.CloseSessionAsync();
        }
        catch (Exception exception)
        {
            // 注销失败时保留窗口，避免用户误以为会话已经关闭而继续关机或重启。
            _closeInProgress = false;
            AppMessageBox.Show(
                exception.GetBaseException().Message,
                I18nService.T("注销虚拟桌面失败"),
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                this);
            return;
        }

        _closeInProgress = false;
        _allowClose = true;
        Close();
    }

    private void OnHideRequested(object? sender, EventArgs e)
    {
        Hide();
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "VirtualDesktopRDP";
        _trayIcon.BalloonTipText = I18nService.T(
            "虚拟桌面仍在运行，双击托盘图标可重新打开窗口。");
        _trayIcon.ShowBalloonTip(2500);
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        var window = new SettingsWindow(_settingsService.Current.Clone())
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            _settingsService.Update(window.Settings);
            I18nService.Instance.ChangeLanguage(window.Settings.Language);
        }
    }

    private void OnResolutionRequested(object? sender, EventArgs e)
    {
        var window = new ResolutionWindow(
            _childSessionService.DesktopWidth,
            _childSessionService.DesktopHeight)
        {
            Owner = this
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _childSessionService.SetDesktopResolution(
                window.SelectedWidth,
                window.SelectedHeight);
            _viewModel.RefreshResolutionHeader();
        }
        catch (Exception exception)
        {
            AppMessageBox.Show(
                exception.GetBaseException().Message,
                I18nService.T("输入分辨率"),
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                this);
        }
    }

    private void OnResizeRequested(object? sender, int width)
    {
        ResizeToWidth(width);
    }

    private void OnControlCenterButtonClick(object sender, RoutedEventArgs e)
    {
        ControlCenterPopup.IsOpen = ControlCenterButton.IsChecked == true;
    }

    private void OnControlCenterPopupClosed(object? sender, EventArgs e)
    {
        ControlCenterButton.IsChecked = false;
    }

    private void OnControlCenterActionClick(object sender, RoutedEventArgs e)
    {
        // 任何菜单项执行后立即收起面板，避免 Popup 停留在画面上。
        ControlCenterPopup.IsOpen = false;
        ControlCenterButton.IsChecked = false;
    }

    private void OnRdpHostVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (!RdpHostContainer.IsVisible)
        {
            return;
        }

        // 等布局完成后再按实际可用高度校正一次内容比例。
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_viewModel.KeepAspectRatio && WindowState == WindowState.Normal)
                {
                    ResizeToWidth(Width);
                }
            }),
            DispatcherPriority.Loaded);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_adjustingAspectRatio
            || !_viewModel.KeepAspectRatio
            || WindowState != WindowState.Normal
            || !RdpHostContainer.IsVisible
            || RdpHostContainer.ActualWidth <= 0
            || RdpHostContainer.ActualHeight <= 0)
        {
            _lastWidth = Width;
            _lastHeight = Height;
            return;
        }

        var widthDelta = Math.Abs(e.NewSize.Width - _lastWidth);
        var heightDelta = Math.Abs(e.NewSize.Height - _lastHeight);
        var chromeHeight = ActualHeight - RdpHostContainer.ActualHeight;

        try
        {
            _adjustingAspectRatio = true;
            if (widthDelta >= heightDelta)
            {
                var targetHeight = RdpHostContainer.ActualWidth * 9d / 16d + chromeHeight;
                if (Math.Abs(Height - targetHeight) > 1)
                {
                    Height = Math.Max(MinHeight, targetHeight);
                }
            }
            else
            {
                var targetWidth = RdpHostContainer.ActualHeight * 16d / 9d;
                if (Math.Abs(Width - targetWidth) > 1)
                {
                    Width = Math.Max(MinWidth, targetWidth);
                }
            }
        }
        finally
        {
            _adjustingAspectRatio = false;
            _lastWidth = Width;
            _lastHeight = Height;
        }
    }

    private void ResizeToWidth(double width)
    {
        var chromeHeight = RdpHostContainer.IsVisible && RdpHostContainer.ActualHeight > 0
            ? ActualHeight - RdpHostContainer.ActualHeight
            : 104;
        var targetHeight = width * 9d / 16d + chromeHeight;

        _adjustingAspectRatio = true;
        try
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            Width = Math.Max(MinWidth, width);
            Height = Math.Max(MinHeight, targetHeight);
        }
        finally
        {
            _adjustingAspectRatio = false;
            _lastWidth = Width;
            _lastHeight = Height;
        }
    }

    private void RestoreWindowPlacement()
    {
        var settings = _settingsService.Current;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(
            Math.Max(MinWidth, settings.NormalWidth),
            Math.Max(MinWidth, workArea.Width * 0.94));
        Height = Math.Min(
            Math.Max(MinHeight, settings.NormalHeight),
            Math.Max(MinHeight, workArea.Height * 0.94));

        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top)
        {
            return;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        if (left < virtualLeft - 50
            || top < virtualTop - 50
            || left > virtualRight - 100
            || top > virtualBottom - 100)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SaveWindowPlacement()
    {
        var settings = _settingsService.Current;
        if (WindowState == WindowState.Normal)
        {
            settings.NormalWidth = Width;
            settings.NormalHeight = Height;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
        }

        _settingsService.Save();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(
            I18nService.T("显示主窗口"),
            null,
            (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(
            I18nService.T("自动启动设置"),
            null,
            (_, _) => Dispatcher.Invoke(ShowSettingsFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(
            I18nService.T("退出"),
            null,
            (_, _) => Dispatcher.Invoke(Close));

        var icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "VirtualDesktopRDP",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private void UpdateTrayMenuText()
    {
        var items = _trayIcon.ContextMenuStrip?.Items;
        if (items is null || items.Count < 4)
        {
            return;
        }

        items[0].Text = I18nService.T("显示主窗口");
        items[1].Text = I18nService.T("自动启动设置");
        items[3].Text = I18nService.T("退出");
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (_viewModel.IsConnected
            && _viewModel.KeepAspectRatio
            && WindowState == WindowState.Normal)
        {
            ResizeToWidth(Width);
        }
    }

    private void ShowSettingsFromTray()
    {
        ShowFromTray();
        OnSettingsRequested(this, EventArgs.Empty);
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            _ = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 旧系统没有 DwmSetWindowAttribute，保留系统默认标题栏。
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);
}
