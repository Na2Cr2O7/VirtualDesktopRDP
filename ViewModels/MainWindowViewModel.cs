using System.IO;
using System.Windows.Input;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfApplication = System.Windows.Application;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;
using VirtualDesktopRDP.Dialogs;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using VirtualDesktopRDP.Services;

namespace VirtualDesktopRDP.ViewModels;

/// <summary>
/// 主窗口状态与命令。窗口显示/隐藏等纯 UI 动作通过事件交给 MainWindow 处理。
/// </summary>
internal sealed class MainWindowViewModel : ObservableObject
{
    private readonly ChildSessionService _childSessionService;

    private string _statusTitle = "虚拟桌面尚未启动";
    private string _statusDescription = "点击“启动”，创建一个独立的 Windows 会话。";
    private string _rdpStatusText = "RDP 未连接";
    private string _childSessionStatusText = "虚拟桌面会话未创建";
    private string _statusTooltip = "虚拟桌面尚未启动";
    private MediaBrush _statusBrush = MediaBrushes.IndianRed;
    private bool _isConnected;
    private bool _isConnecting;
    private bool _isPromptVisible = true;
    private bool _isSessionClosing;
    private bool _isTopmost;
    private bool _isAdaptive = true;
    private bool _isOneToOne;
    private bool _keepAspectRatio = true;
    private bool _isWatchModeEnabled;
    private bool _isAudioMuted;
    private bool _sendSystemShortcutsToRemote = true;
    private bool _isSmallWindowMode;
    private bool _startRequested;

    public MainWindowViewModel(ChildSessionService childSessionService)
    {
        _childSessionService = childSessionService;

        _isTopmost = _childSessionService.TopmostEnabled;
        _isAdaptive = _childSessionService.SmartSizingEnabled;
        _isOneToOne = !_isAdaptive;
        _keepAspectRatio = _childSessionService.KeepAspectRatio;
        _isWatchModeEnabled = _childSessionService.WatchModeEnabled;
        _isAudioMuted = _childSessionService.AudioMuted;
        _sendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;

        _childSessionService.StateChanged += OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed += OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted +=
            OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted += OnAudioReconnectCompleted;
        I18nService.Instance.LanguageChanged += OnLanguageChanged;

        StartCommand = new RelayCommand(
            ConnectOrDisconnect,
            () => CanConnectOrDisconnect);
        HideCommand = new RelayCommand(() => HideRequested?.Invoke(this, EventArgs.Empty));
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ToggleWatchModeCommand = new RelayCommand(ToggleWatchMode);
        ToggleAudioMutedCommand = new RelayCommand(ToggleAudioMuted);
        ToggleSendSystemShortcutsCommand = new RelayCommand(ToggleSendSystemShortcuts);
        ToggleTopmostCommand = new RelayCommand(ToggleTopmost);
        UseAdaptiveCommand = new RelayCommand(UseAdaptive);
        UseOneToOneCommand = new RelayCommand(UseOneToOne);
        ToggleKeepAspectRatioCommand = new RelayCommand(ToggleKeepAspectRatio);
        ToggleSmallWindowCommand = new RelayCommand(ToggleSmallWindow);
        LaunchExecutableCommand = new AsyncRelayCommand(LaunchExecutableAsync);
        SendShowDesktopCommand = new RelayCommand(SendShowDesktop);
        RepairFirewallCommand = new RelayCommand(RepairFirewall);

        UpdateConnectionStatus();
    }

    public event EventHandler? HideRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler<int>? ResizeRequested;

    public ICommand StartCommand { get; }

    public ICommand HideCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand ToggleWatchModeCommand { get; }

    public ICommand ToggleAudioMutedCommand { get; }

    public ICommand ToggleSendSystemShortcutsCommand { get; }

    public ICommand ToggleTopmostCommand { get; }

    public ICommand UseAdaptiveCommand { get; }

    public ICommand UseOneToOneCommand { get; }

    public ICommand ToggleKeepAspectRatioCommand { get; }

    public ICommand ToggleSmallWindowCommand { get; }

    public ICommand LaunchExecutableCommand { get; }

    public ICommand SendShowDesktopCommand { get; }

    public ICommand RepairFirewallCommand { get; }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDescription
    {
        get => _statusDescription;
        private set => SetProperty(ref _statusDescription, value);
    }

    public string RdpStatusText
    {
        get => _rdpStatusText;
        private set => SetProperty(ref _rdpStatusText, value);
    }

    public string ChildSessionStatusText
    {
        get => _childSessionStatusText;
        private set => SetProperty(ref _childSessionStatusText, value);
    }

    public string StatusTooltip
    {
        get => _statusTooltip;
        private set => SetProperty(ref _statusTooltip, value);
    }

    public MediaBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(CanConnectOrDisconnect));
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(ConnectButtonTooltip));
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(CanConnectOrDisconnect));
            }
        }
    }

    public bool IsPromptVisible
    {
        get => _isPromptVisible;
        private set => SetProperty(ref _isPromptVisible, value);
    }

    public bool IsSessionClosing
    {
        get => _isSessionClosing;
        private set
        {
            if (SetProperty(ref _isSessionClosing, value))
            {
                OnPropertyChanged(nameof(CanConnectOrDisconnect));
            }
        }
    }

    public bool IsTopmost
    {
        get => _isTopmost;
        private set
        {
            if (SetProperty(ref _isTopmost, value))
            {
                OnPropertyChanged(nameof(TopmostMenuHeader));
            }
        }
    }

    public bool IsAdaptive
    {
        get => _isAdaptive;
        private set => SetProperty(ref _isAdaptive, value);
    }

    public bool IsOneToOne
    {
        get => _isOneToOne;
        private set => SetProperty(ref _isOneToOne, value);
    }

    public bool KeepAspectRatio
    {
        get => _keepAspectRatio;
        private set => SetProperty(ref _keepAspectRatio, value);
    }

    public bool IsWatchModeEnabled
    {
        get => _isWatchModeEnabled;
        private set
        {
            if (SetProperty(ref _isWatchModeEnabled, value))
            {
                OnPropertyChanged(nameof(WatchModeMenuHeader));
            }
        }
    }

    public bool IsAudioMuted
    {
        get => _isAudioMuted;
        private set => SetProperty(ref _isAudioMuted, value);
    }

    public bool SendSystemShortcutsToRemote
    {
        get => _sendSystemShortcutsToRemote;
        private set => SetProperty(ref _sendSystemShortcutsToRemote, value);
    }

    public bool IsSmallWindowMode
    {
        get => _isSmallWindowMode;
        private set
        {
            if (SetProperty(ref _isSmallWindowMode, value))
            {
                OnPropertyChanged(nameof(SmallWindowMenuHeader));
            }
        }
    }

    /// <summary>
    /// 关闭会话期间禁用按钮；连接过程中禁用，但已经连上时即使仍处于连接收尾阶段
    /// 也允许立即断开。
    /// </summary>
    public bool CanConnectOrDisconnect =>
        !IsSessionClosing && (!IsConnecting || IsConnected);

    /// <summary>未连接时显示“启动”，已连接时显示“断开”。</summary>
    public string ConnectButtonText => IsConnected
        ? I18nService.T("断开")
        : I18nService.T("启动");

    public string ConnectButtonTooltip => IsConnected
        ? I18nService.T("断开 RDP 连接，虚拟桌面会话和窗口保持运行")
        : I18nService.T("启动并连接 RDP");

    public string TopmostMenuHeader => IsTopmost
        ? I18nService.T("取消置顶")
        : I18nService.T("置顶");

    public string WatchModeMenuHeader => IsWatchModeEnabled
        ? I18nService.T("退出观看模式")
        : I18nService.T("观看模式");

    public string SmallWindowMenuHeader => IsSmallWindowMode
        ? I18nService.T("还原窗口")
        : I18nService.T("小窗模式");

    public string AudioMenuHeader => IsAudioMuted
        ? I18nService.T("开启声音")
        : I18nService.T("静音");

    /// <summary>关闭会话并释放服务；由窗口在确认关闭后调用。</summary>
    public async Task CloseSessionAsync()
    {
        if (IsSessionClosing)
        {
            return;
        }

        IsSessionClosing = true;
        UpdateConnectionStatus();
        try
        {
            await _childSessionService.LogoffAsync();
        }
        finally
        {
            IsSessionClosing = false;
            UpdateConnectionStatus();
        }
    }

    public void Dispose()
    {
        _childSessionService.StateChanged -= OnChildSessionStateChanged;
        _childSessionService.ConnectionFailed -= OnChildSessionConnectionFailed;
        _childSessionService.SystemShortcutsReconnectCompleted -=
            OnSystemShortcutsReconnectCompleted;
        _childSessionService.AudioReconnectCompleted -= OnAudioReconnectCompleted;
        I18nService.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonTooltip));
        OnPropertyChanged(nameof(TopmostMenuHeader));
        OnPropertyChanged(nameof(WatchModeMenuHeader));
        OnPropertyChanged(nameof(SmallWindowMenuHeader));
        OnPropertyChanged(nameof(AudioMenuHeader));
        _childSessionService.RefreshLanguage();
        UpdateConnectionStatus();
    }

    /// <summary>
    /// 同一个按钮承担连接和断开：未连接时启动/恢复 RDP，已连接时只断开 RDP。
    /// 这里使用同步命令，避免异步连接尚未收尾时命令自身一直处于“执行中”而禁用按钮。
    /// </summary>
    private void ConnectOrDisconnect()
    {
        if (_childSessionService.ConnectedState == 1)
        {
            DisconnectRdp();
            return;
        }

        _ = StartAsync();
    }

    private void DisconnectRdp()
    {
        try
        {
            _childSessionService.DisconnectRdp();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task StartAsync()
    {
        if (_startRequested)
        {
            return;
        }

        if (_childSessionService.ConnectedState != 1
            && ChildSessionService.IsRdpWrapperEnabled())
        {
            var result = AppMessageBox.Show(
                I18nService.T("检测到系统已安装并启用 RDP Wrapper。")
                + "\n\n"
                + I18nService.T("RDP Wrapper 提供了更强大的远程多用户支持，但与当前虚拟桌面功能不兼容，可能导致无法正常启动。")
                + "\n\n"
                + I18nService.T("是否仍要继续？"),
                I18nService.T("RDP Wrapper 兼容性提醒"),
                WpfMessageBoxButton.YesNo,
                WpfMessageBoxImage.Warning,
                WpfMessageBoxResult.No);
            if (result != WpfMessageBoxResult.Yes)
            {
                return;
            }
        }

        if (_childSessionService.ConnectedState != 1)
        {
            _childSessionService.RefreshEnvironmentCheck();
            if (_childSessionService.HasEnvironmentIssue)
            {
                // 这些条件只是风险提示，不应该挡住本来可以连上的用户。
                var result = AppMessageBox.Show(
                    _childSessionService.EnvironmentIssueSummary
                    + "\n\n"
                    + I18nService.T("检测到可能影响虚拟桌面的环境设置。是否仍要继续？"),
                    I18nService.T("虚拟桌面环境检查"),
                    WpfMessageBoxButton.YesNo,
                    WpfMessageBoxImage.Warning,
                    WpfMessageBoxResult.Yes);
                if (result != WpfMessageBoxResult.Yes)
                {
                    return;
                }
            }
        }

        _startRequested = true;
        IsConnecting = true;
        UpdateConnectionStatus();

        try
        {
            await _childSessionService.StartAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _startRequested = false;
            IsConnecting = false;
            UpdateConnectionStatus();
        }
    }

    private void ToggleWatchMode()
    {
        try
        {
            _childSessionService.SetWatchModeEnabled(!IsWatchModeEnabled);
            IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ToggleAudioMuted()
    {
        try
        {
            _childSessionService.SetAudioMuted(!IsAudioMuted);
            IsAudioMuted = _childSessionService.AudioMuted;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ToggleSendSystemShortcuts()
    {
        try
        {
            _childSessionService.SetSendSystemShortcutsToRemote(
                !SendSystemShortcutsToRemote);
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ToggleTopmost()
    {
        try
        {
            _childSessionService.SetTopmost(!IsTopmost);
            IsTopmost = _childSessionService.TopmostEnabled;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void UseAdaptive()
    {
        try
        {
            _childSessionService.SetSmartSizing(true);
            IsAdaptive = true;
            IsOneToOne = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void UseOneToOne()
    {
        try
        {
            _childSessionService.SetSmartSizing(false);
            IsAdaptive = false;
            IsOneToOne = true;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ToggleKeepAspectRatio()
    {
        try
        {
            _childSessionService.SetKeepAspectRatio(!KeepAspectRatio);
            KeepAspectRatio = _childSessionService.KeepAspectRatio;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ToggleSmallWindow()
    {
        IsSmallWindowMode = !IsSmallWindowMode;
        ResizeRequested?.Invoke(this, IsSmallWindowMode ? 500 : 1280);
    }

    private async Task LaunchExecutableAsync()
    {
        var fileDialog = new Win32OpenFileDialog
        {
            Title = I18nService.T("选择要在虚拟桌面中以管理员权限启动的程序"),
            Filter = I18nService.T("Windows 程序 (*.exe)|*.exe"),
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = false
        };

        if (fileDialog.ShowDialog() != true)
        {
            return;
        }

        var executablePath = Path.GetFullPath(fileDialog.FileName);
        try
        {
            await _childSessionService.LaunchExecutableAsync(executablePath);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void SendShowDesktop()
    {
        try
        {
            _childSessionService.SendShowDesktopShortcut();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static void RepairFirewall()
    {
        var success = FirewallService.EnsureInboundRule();
        AppMessageBox.Show(
            FirewallService.LastMessage,
            I18nService.T("防火墙规则"),
            WpfMessageBoxButton.OK,
            success ? WpfMessageBoxImage.Information : WpfMessageBoxImage.Warning);
    }

    private void OnChildSessionStateChanged(object? sender, EventArgs e)
    {
        if (WpfApplication.Current.Dispatcher.CheckAccess())
        {
            UpdateConnectionStatus();
            return;
        }

        _ = WpfApplication.Current.Dispatcher.BeginInvoke(UpdateConnectionStatus);
    }

    private void OnChildSessionConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateConnectionStatus();
            AppMessageBox.Show(
                e.Message,
                I18nService.T("RDP 连接失败"),
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Error);
        }));
    }

    private void OnSystemShortcutsReconnectCompleted(object? sender, EventArgs e)
    {
        _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
            UpdateConnectionStatus();
        }));
    }

    private void OnAudioReconnectCompleted(object? sender, EventArgs e)
    {
        _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            IsAudioMuted = _childSessionService.AudioMuted;
            UpdateConnectionStatus();
        }));
    }

    private void UpdateConnectionStatus()
    {
        var connectedState = _childSessionService.ConnectedState;
        var childSessionId = _childSessionService.ChildSessionId;

        IsTopmost = _childSessionService.TopmostEnabled;
        IsAdaptive = _childSessionService.SmartSizingEnabled;
        IsOneToOne = !IsAdaptive;
        KeepAspectRatio = _childSessionService.KeepAspectRatio;
        SendSystemShortcutsToRemote = _childSessionService.SendSystemShortcutsToRemote;
        IsWatchModeEnabled = _childSessionService.WatchModeEnabled;
        IsAudioMuted = _childSessionService.AudioMuted;
        IsConnected = connectedState == 1;
        IsConnecting = _startRequested || connectedState == 2;
        IsPromptVisible = !IsConnected;
        StatusTooltip = _childSessionService.StatusText;

        if (IsSessionClosing)
        {
            StatusBrush = MediaBrushes.Orange;
            StatusTitle = I18nService.T("正在关闭虚拟桌面");
            StatusDescription = I18nService.T("正在断开 RDP 并注销会话，此过程可能需要一些时间。");
            RdpStatusText = connectedState == 0
                ? I18nService.T("RDP 已断开")
                : I18nService.T("正在断开 RDP");
            ChildSessionStatusText = childSessionId is null
                ? I18nService.T("正在确认会话已注销")
                : I18nService.F("正在注销会话 {0}", childSessionId.Value);
            return;
        }

        if (IsConnected)
        {
            StatusBrush = MediaBrushes.LimeGreen;
            StatusTitle = I18nService.T("虚拟桌面已连接");
            StatusDescription = I18nService.T("RDP 连接正常，可以直接在虚拟桌面中操作。");
        }
        else if (connectedState == 2 || _startRequested)
        {
            StatusBrush = childSessionId is null ? MediaBrushes.IndianRed : MediaBrushes.DodgerBlue;
            StatusTitle = I18nService.T("正在连接虚拟桌面");
            StatusDescription = I18nService.T("正在创建或恢复 RDP 连接，请稍候。");
        }
        else if (childSessionId is not null)
        {
            StatusBrush = MediaBrushes.DodgerBlue;
            StatusTitle = I18nService.T("虚拟桌面已启动，等待连接");
            StatusDescription = I18nService.T("会话仍在运行，点击“启动”可重新建立 RDP 连接。");
        }
        else
        {
            StatusBrush = MediaBrushes.IndianRed;
            StatusTitle = I18nService.T("虚拟桌面尚未启动");
            StatusDescription = I18nService.T("点击“启动”，创建一个独立的 Windows 会话。");
        }

        RdpStatusText = connectedState switch
        {
            0 => I18nService.T("RDP 未连接"),
            1 => I18nService.T("RDP 已连接"),
            2 => I18nService.T("RDP 正在连接"),
            _ => I18nService.F("RDP 状态未知（{0}）", connectedState)
        };
        ChildSessionStatusText = childSessionId is null
            ? I18nService.T("虚拟桌面会话未创建")
            : I18nService.F("虚拟桌面会话 {0}", childSessionId.Value);

        OnPropertyChanged(nameof(CanConnectOrDisconnect));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonTooltip));
        OnPropertyChanged(nameof(TopmostMenuHeader));
        OnPropertyChanged(nameof(WatchModeMenuHeader));
        OnPropertyChanged(nameof(AudioMenuHeader));
        CommandManager.InvalidateRequerySuggested();
    }

    private static void ShowError(Exception exception)
    {
        var actualException = exception.GetBaseException();
        var suggestion = actualException is System.ComponentModel.Win32Exception
        {
            NativeErrorCode: 5
        }
            ? "\n\n" + I18nService.T("操作被系统拒绝，请确认本程序正在以管理员权限运行。")
            : string.Empty;

        AppMessageBox.Show(
            $"{actualException.Message}{suggestion}",
            "VirtualDesktopRDP",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Error);
    }
}
