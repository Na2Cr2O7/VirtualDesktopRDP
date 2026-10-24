// SPDX-License-Identifier: GPL-3.0-only
// 源自 BetterGI 的桌面分身实现，已针对本项目裁剪。

using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using VirtualDesktopRDP.Rdp;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace VirtualDesktopRDP.Services;

/// <summary>
/// 桌面分身的核心编排：启用 Child Session、建立 RDP 连接、应用设置、失败重试、
/// 登录后启动程序以及注销会话。
/// </summary>
internal sealed class ChildSessionService : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialConnectionRetryCount = 3;
    private const int RdpSettingsReconnectRetryCount = 2;
    private const int ErrorTimeout = 1460;

    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _statusTimer;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private bool _autoStartPending;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;
    private RdpSettingChange _pendingRdpSettingChanges;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _statusTickInProgress;
    private bool _disposed;
    private string? _lastOperationMessage;
    private IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _environmentIssues = [];

    public ChildSessionService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        RdpHost = new RdpActiveXHost();
        RdpHost.SetSmartSizing(Settings.SmartSizingEnabled);
        RdpHost.SetSendSystemShortcutsToRemote(Settings.SendSystemShortcutsToRemote);
        RdpHost.SetAudioMuted(Settings.AudioMuted);
        RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        RdpHost.LoginCompleted += OnRdpLoginCompleted;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
        RefreshState();
    }

    public event EventHandler? StateChanged;

    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    public event EventHandler? SystemShortcutsReconnectCompleted;

    public event EventHandler? AudioReconnectCompleted;

    public RdpActiveXHost RdpHost { get; }

    public string StatusText { get; private set; } = I18nService.T("虚拟桌面尚未启动");

    public int ConnectedState { get; private set; }

    public uint? ChildSessionId { get; private set; }

    public bool SendSystemShortcutsToRemote => Settings.SendSystemShortcutsToRemote;

    public bool TopmostEnabled => Settings.TopmostEnabled;

    public bool SmartSizingEnabled => Settings.SmartSizingEnabled;

    public bool KeepAspectRatio => Settings.KeepAspectRatio;

    public bool AudioMuted => Settings.AudioMuted;

    public bool WatchModeEnabled => Settings.WatchModeEnabled;

    public int DesktopWidth => ResolutionConstraints.Normalize(
        Settings.DesktopWidth,
        Settings.DesktopHeight).Width;

    public int DesktopHeight => ResolutionConstraints.Normalize(
        Settings.DesktopWidth,
        Settings.DesktopHeight).Height;

    private DrawingSize CurrentDesktopSize => new(DesktopWidth, DesktopHeight);

    public bool HasEnvironmentIssue => _environmentIssues.Count > 0;

    public string EnvironmentIssueSummary =>
        ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues);

    private AppSettings Settings => _settingsService.Current;

    /// <summary>最近一次连接前环境预检是否存在风险项。</summary>
    public void RefreshEnvironmentCheck()
    {
        ThrowIfDisposed();
        _environmentIssues = ChildSessionEnvironmentCheck.Collect();
    }

    /// <summary>系统是否安装了 RDP Wrapper。它与 Child Session 不兼容。</summary>
    public static bool IsRdpWrapperEnabled()
    {
        return ChildSessionNativeMethods.IsRdpWrapperEnabled();
    }

    /// <summary>当前是否存在活动的 Child Session，用于关闭前确认与状态展示。</summary>
    public bool HasActiveChildSession()
    {
        if (_disposed)
        {
            return false;
        }

        RefreshState();
        return ChildSessionId is not null;
    }

    /// <summary>RDP 输入窗口当前是否拥有键盘焦点。</summary>
    public bool IsRdpInputWindowFocused()
    {
        return RdpHost.IsInputWindowFocused();
    }

    /// <summary>RDP 画面在屏幕上的矩形区域，用于约束本机鼠标。</summary>
    public bool TryGetRdpClientBounds(out DrawingRectangle bounds)
    {
        bounds = RdpHost.RectangleToScreen(RdpHost.ClientRectangle);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    /// <summary>启用 Child Session，建立 RDP 连接，并等待登录完成。</summary>
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        RefreshEnvironmentCheck();
        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1)
        {
            return;
        }

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;

            try
            {
                ConnectCore(I18nService.T("正在启动虚拟桌面"));
            }
            catch
            {
                _autoStartPending = false;
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                throw;
            }
        }

        try
        {
            await completionSource.Task.WaitAsync(
                ConnectionTimeout,
                _disposeCancellationTokenSource.Token);
        }
        catch (TimeoutException)
        {
            if (!completionSource.Task.IsCompleted)
            {
                _autoStartPending = false;
                _initialConnectionRetriesRemaining = 0;
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                TryDisconnectRdpHost();
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    /// <summary>
    /// 只断开 RDP 连接，保持 Child Session 和窗口中运行的程序不变。
    /// 之后可以再次通过 StartAsync 恢复连接。
    /// </summary>
    public void DisconnectRdp()
    {
        ThrowIfDisposed();
        if (RdpHost.ConnectedState == 0)
        {
            return;
        }

        // 用户主动断开时取消尚未完成的重连/设置切换，避免断开后又被自动连上。
        _autoStartPending = false;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);

        RdpHost.DisconnectSession();
        RefreshState(I18nService.T("已断开 RDP 连接，虚拟桌面会话仍在运行"));
    }

    /// <summary>
    /// 修改虚拟桌面分辨率。已连接时会自动重连 RDP；没有会话时保存后下次连接生效。
    /// 返回是否启动了重连。
    /// </summary>
    public bool SetDesktopResolution(int width, int height)
    {
        ThrowIfDisposed();
        if (ResolutionConstraints.IsBelowMinimum(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                I18nService.T("分辨率不得低于 800×600。"));
        }

        if (ResolutionConstraints.IsAboveMaximum(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                I18nService.T("分辨率不能超过 8192×8192。"));
        }

        if (Settings.DesktopWidth == width && Settings.DesktopHeight == height)
        {
            return false;
        }

        Settings.DesktopWidth = width;
        Settings.DesktopHeight = height;
        _settingsService.Save();
        var message = I18nService.F(
            "分辨率已改为 {0}×{1}",
            width,
            height);
        return ReconnectForRdpSettingChange(RdpSettingChange.Resolution, message);
    }

    /// <summary>让 RDP 控件重新贴合当前 WindowsFormsHost 大小。</summary>
    public void RefreshRdpDisplayBounds()
    {
        ThrowIfDisposed();
        RdpHost.RefreshDisplayBounds();
    }

    /// <summary>切换 RDP 画面自适应 / 1:1。</summary>
    public void SetSmartSizing(bool enabled)
    {
        ThrowIfDisposed();
        RdpHost.SetSmartSizing(enabled);
        Settings.SmartSizingEnabled = enabled;
        _settingsService.Save();
        RefreshState(enabled
            ? I18nService.T("窗口显示模式已切换为自适应")
            : I18nService.T("窗口显示模式已切换为 1:1"));
    }

    /// <summary>切换窗口是否保持 16:9。</summary>
    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        Settings.KeepAspectRatio = enabled;
        _settingsService.Save();
        RefreshState(enabled
            ? I18nService.T("窗口将保持 16:9 宽高比")
            : I18nService.T("窗口不再保持宽高比"));
    }

    /// <summary>切换窗口置顶。</summary>
    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        Settings.TopmostEnabled = enabled;
        _settingsService.Save();
        RefreshState(enabled
            ? I18nService.T("窗口已置顶")
            : I18nService.T("窗口已取消置顶"));
    }

    /// <summary>
    /// 切换系统组合键发送位置。该设置只在 RDP 连接建立时生效，因此需要自动重连。
    /// 返回是否启动了重连。
    /// </summary>
    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (Settings.SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        Settings.SendSystemShortcutsToRemote = enabled;
        _settingsService.Save();
        RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled
            ? I18nService.T("虚拟桌面")
            : I18nService.T("本机");
        return ReconnectForRdpSettingChange(
            RdpSettingChange.SystemShortcuts,
            I18nService.F("系统组合键已改为在{0}生效", target));
    }

    /// <summary>
    /// 切换子会话音频静音。该设置只在 RDP 连接建立时生效，因此需要自动重连。
    /// 返回是否启动了重连。
    /// </summary>
    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (Settings.AudioMuted == muted)
        {
            return false;
        }

        Settings.AudioMuted = muted;
        _settingsService.Save();
        RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            RdpSettingChange.Audio,
            muted
                ? I18nService.T("虚拟桌面声音已关闭")
                : I18nService.T("虚拟桌面声音已开启"));
    }

    /// <summary>观看模式：画面正常显示，但本机键鼠不会进入子会话。</summary>
    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        Settings.WatchModeEnabled = enabled;
        _settingsService.Save();
        RdpHost.SetInputBlocked(enabled);
        RefreshState(enabled
            ? I18nService.T("已进入观看模式，本机鼠标和键盘不会进入虚拟桌面")
            : I18nService.T("已退出观看模式，可以操作虚拟桌面"));
    }

    /// <summary>向子会话发送 Win+D。</summary>
    public void SendShowDesktopShortcut()
    {
        ThrowIfDisposed();
        RdpHost.SendShowDesktopShortcut();
        RefreshState(I18nService.T("已向虚拟桌面发送 Win+D"));
    }

    /// <summary>以管理员权限在子会话中启动指定程序。</summary>
    public async Task LaunchExecutableAsync(string executablePath)
    {
        ThrowIfDisposed();
        var childSessionId = GetRequiredChildSessionId();

        await _launchSemaphore.WaitAsync();
        try
        {
            RefreshState(I18nService.F(
                "正在以管理员权限启动 {0}",
                Path.GetFileName(executablePath)));
            await ChildSessionProcessLauncher.LaunchElevatedAsync(childSessionId, executablePath);
            RefreshState(I18nService.F(
                "已在虚拟桌面（会话 {0}）中以管理员权限启动 {1}",
                childSessionId,
                Path.GetFileName(executablePath)));
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>断开 RDP、注销 Child Session。可选校验预期会话 ID，避免误伤新会话。</summary>
    public async Task LogoffAsync(uint? expectedSessionId = null)
    {
        ThrowIfDisposed();

        await _launchSemaphore.WaitAsync();
        try
        {
            var activeSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            if (expectedSessionId is { } expected
                && activeSessionId is { } active
                && active != expected)
            {
                throw new InvalidOperationException(I18nService.F(
                    "虚拟桌面会话已从 {0} 变更为 {1}，已取消注销操作。",
                    expected,
                    active));
            }

            _autoStartPending = false;
            _initialConnectionRetriesRemaining = 0;
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _connectionAttemptCompletionSource?.TrySetResult(false);

            TryDisconnectRdpHost();
            RefreshState(I18nService.T("正在断开 RDP 并注销虚拟桌面"));

            var terminatedSessionId = await Task.Run(() =>
                ChildSessionNativeMethods.TerminateChildSession(expectedSessionId, wait: true));
            RefreshState(terminatedSessionId is null
                ? I18nService.T("当前没有虚拟桌面会话")
                : I18nService.F(
                    "虚拟桌面会话 {0} 已注销",
                    terminatedSessionId.Value));
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>语言切换后丢弃旧语言的操作提示，并按当前连接状态重新生成状态文本。</summary>
    public void RefreshLanguage()
    {
        _lastOperationMessage = null;
        RefreshState();
    }

    /// <summary>刷新连接状态、Child Session ID 与状态文本。</summary>
    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = RdpHost.ConnectedState;

            var connectionText = ConnectedState switch
            {
                0 => I18nService.T("未连接"),
                1 => I18nService.T("已连接"),
                2 => I18nService.T("正在连接"),
                _ => I18nService.F("未知连接状态 {0}", ConnectedState)
            };
            var sessionText = ChildSessionId?.ToString() ?? I18nService.T("无");
            var mainText = _lastOperationMessage ?? connectionText;

            StatusText =
                $"{mainText} | {I18nService.T("RDP：")}{connectionText} | "
                + $"{I18nService.T("虚拟桌面会话：")}{sessionText} | "
                + $"{I18nService.T("RDP功能已启用：")}{enabled}";
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _autoStartPending = false;
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
        RdpHost.LoginCompleted -= OnRdpLoginCompleted;
        TryDisconnectRdpHost();

        try
        {
            _ = ChildSessionNativeMethods.TerminateChildSession(expectedSessionId: null, wait: false);
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            // 应用正在退出，Child Session 清理失败不应阻止主程序关闭。
        }

        RdpHost.Dispose();
        _launchSemaphore.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var existingSessionId = ChildSessionNativeMethods.TryGetChildSessionId();

        if (RdpHost.ConnectedState == 0)
        {
            _autoStartPending = existingSessionId is null;
            _initialConnectionRetriesRemaining = _autoStartPending
                ? InitialConnectionRetryCount
                : 0;
            RdpHost.ConnectToChildSession(CurrentDesktopSize);
        }

        RefreshState(operationMessage);
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
        }
    }

    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        RdpHost.SetInputBlocked(Settings.WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState(I18nService.T("自动重新连接完成，RDP 设置已生效"));

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState(I18nService.T("虚拟桌面登录初始化完成"));
        }

        if (!_autoStartPending)
        {
            return;
        }

        _autoStartPending = false;
        await Task.Yield();
        if (_disposed)
        {
            return;
        }

        try
        {
            RefreshState();
            if (ConnectedState == 1 && ChildSessionId is not null)
            {
                await LaunchAutoStartProgramsAsync(ChildSessionId.Value);
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(I18nService.F(
                "自动启动程序失败：{0}",
                exception.GetBaseException().Message));
        }
    }

    /// <summary>
    /// 新建子会话登录成功后，按设置列表依次启动全部程序。
    /// 单个程序失败不会影响后续程序，也不会断开已经建立的 RDP 连接。
    /// </summary>
    private async Task LaunchAutoStartProgramsAsync(uint childSessionId)
    {
        var programs = Settings.AutoStartPrograms
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (programs.Length == 0)
        {
            RefreshState(I18nService.T("虚拟桌面登录完成"));
            return;
        }

        var launchedCount = 0;
        await _launchSemaphore.WaitAsync();
        try
        {
            for (var index = 0; index < programs.Length; index++)
            {
                var program = programs[index];
                var fileName = Path.GetFileName(program);
                RefreshState(I18nService.F(
                    "正在自动启动 {0}（{1}/{2}）",
                    fileName,
                    index + 1,
                    programs.Length));

                try
                {
                    await ChildSessionProcessLauncher.LaunchElevatedAsync(
                        childSessionId,
                        program);
                    launchedCount++;
                }
                catch (Exception exception) when (IsExpectedChildSessionException(exception))
                {
                    RefreshState(I18nService.F(
                        "自动启动 {0} 失败：{1}",
                        fileName,
                        exception.GetBaseException().Message));
                }
            }

            RefreshState(launchedCount == programs.Length
                ? I18nService.F(
                    "虚拟桌面登录完成，已启动 {0} 个自动启动程序",
                    launchedCount)
                : I18nService.F(
                    "虚拟桌面登录完成，自动启动成功 {0}/{1} 个",
                    launchedCount,
                    programs.Length));
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        _lastConnectionFailure = e;

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
        }

        if (_autoStartPending
            && _initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);

        RefreshState(I18nService.F(
            "RDP 自动重连暂未成功，{0} 秒后重试（{1}/{2}）",
            retryDelay.TotalSeconds,
            retryNumber,
            RdpSettingsReconnectRetryCount));

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(I18nService.F(
                "正在重试 RDP 自动连接（{0}/{1}）",
                retryNumber,
                RdpSettingsReconnectRetryCount));
            _rdpSettingsReconnectRetryInProgress = false;
            RdpHost.ReconnectToChildSession(CurrentDesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                I18nService.F("RDP 自动重新连接失败：{0}", actualException.Message),
                errorCode);
        }
        finally
        {
            _rdpSettingsReconnectRetryInProgress = false;
            RefreshState(I18nService.F(
               ""));
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;
        var retryNumber = InitialConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << (retryNumber - 1));

        RefreshState(I18nService.F(
            "虚拟桌面首次连接尚未完成，{0} 秒后自动重试（{1}/{2}）",
            retryDelay.TotalSeconds,
            retryNumber,
            InitialConnectionRetryCount));

        try
        {
            await Task.Delay(retryDelay, _disposeCancellationTokenSource.Token);
            if (_disposed || !_autoStartPending)
            {
                return;
            }

            RefreshState();
            RefreshState(I18nService.F(
                "正在重试虚拟桌面连接（{0}/{1}）",
                retryNumber,
                InitialConnectionRetryCount));
            _connectionRetryInProgress = false;
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            RdpHost.ReconnectToChildSession(CurrentDesktopSize);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                I18nService.F("重试虚拟桌面连接失败：{0}", actualException.Message),
                errorCode);
        }
        finally
        {
            _connectionRetryInProgress = false;
        }

        if (retryFailure is not null)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    /// <summary>连接失败的统一收尾，附加会话与环境诊断后通知 UI。</summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _autoStartPending = false;
        _initialConnectionRetriesRemaining = 0;
        _connectionAttemptCompletionSource?.TrySetResult(false);
        RefreshState(diagnosed.Message);
        ConnectionFailed?.Invoke(this, diagnosed);
    }

    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage = I18nService.F(
            "虚拟桌面连接及登录初始化未能在 {0} 秒内完成。",
            ConnectionTimeout.TotalSeconds);
        var lastDiagnostic =
            RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                $"{timeoutMessage}\n\n"
                + I18nService.T("RDP ActiveX 未报告更具体的失败原因。"),
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            $"{timeoutMessage}\n\n"
            + I18nService.F("RDP ActiveX 最后报告：{0}", lastDiagnostic.Message),
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    /// <summary>连接失败后的补充诊断：是否存在 Child Session，以及环境预检结论。</summary>
    private string BuildFailureDiagnosis()
    {
        var builder = new StringBuilder();

        if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(I18nService.T(
                "诊断：当前没有可用的虚拟桌面会话，说明 Windows 未能建立 RDP 会话。"
                + "该状态本身无法确定原因——系统无法创建会话与凭据未通过验证都会得到这个结果，"
                + "请结合上面的 RDP ActiveX 错误信息一并判断。"));
        }

        if (_environmentIssues.Count > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append(ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues));
        }

        return builder.ToString();
    }

    private uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException(
                I18nService.T("当前没有可用的虚拟桌面，请先启动虚拟桌面。"));
        }

        return childSessionId.Value;
    }

    private bool ReconnectForRdpSettingChange(
        RdpSettingChange settingChange,
        string operationMessage)
    {
        RefreshState();
        if (RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState(I18nService.F(
                "{0}，将在下次 RDP 连接后应用",
                operationMessage));
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        try
        {
            RdpHost.ReconnectToChildSession(CurrentDesktopSize);
        }
        catch
        {
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState(I18nService.F(
            "{0}，正在自动重新连接 RDP",
            operationMessage));
        return true;
    }

    private void TryDisconnectRdpHost()
    {
        try
        {
            RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，忽略后仍继续注销 Child Session。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or IOException
            or NotSupportedException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2,
        Resolution = 4
    }
}
