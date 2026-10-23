using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualDesktopRDP.Infrastructure;
using VirtualDesktopRDP.Services;
using VirtualDesktopRDP.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace VirtualDesktopRDP;

public partial class MainWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;

    private readonly SettingsService _settingsService;
    private readonly ChildSessionService _childSessionService;
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
        InitializeComponent();

        _settingsService = new SettingsService();
        _childSessionService = new ChildSessionService(_settingsService);
        _viewModel = new MainWindowViewModel(_childSessionService);
        DataContext = _viewModel;
        RdpHostContainer.Child = _childSessionService.RdpHost;
        RdpHostContainer.IsVisibleChanged += OnRdpHostVisibilityChanged;

        _viewModel.HideRequested += OnHideRequested;
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.SettingsRequested += OnSettingsRequested;
        _viewModel.ResizeRequested += OnResizeRequested;

        RestoreWindowPlacement();
        _trayIcon = CreateTrayIcon();

        _lastWidth = Width;
        _lastHeight = Height;
        SizeChanged += OnWindowSizeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
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

        var result = WpfMessageBox.Show(
            "关闭会断开 RDP 并注销虚拟桌面，其中所有正在运行的程序都会被关闭，未保存的数据会丢失。是否继续？",
            "关闭 VirtualDesktopRDP",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
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
        RdpHostContainer.IsVisibleChanged -= OnRdpHostVisibilityChanged;

        SaveWindowPlacement();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        RdpHostContainer.Child = null;
        _viewModel.Dispose();
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
            WpfMessageBox.Show(
                exception.GetBaseException().Message,
                "注销虚拟桌面失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        _trayIcon.BalloonTipText = "虚拟桌面仍在运行，双击托盘图标可重新打开窗口。";
        _trayIcon.ShowBalloonTip(2500);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
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
        }
    }

    private void OnResizeRequested(object? sender, int width)
    {
        ResizeToWidth(width);
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
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(
            "自动启动设置",
            null,
            (_, _) => Dispatcher.Invoke(ShowSettingsFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));

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
