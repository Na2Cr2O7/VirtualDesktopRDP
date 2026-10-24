using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using VirtualDesktopRDP.Services;

namespace VirtualDesktopRDP.GameMouse;

/// <summary>
/// 游戏鼠标：采集本机 Raw Input 相对位移，通过命名管道转发到子会话中的输入代理，
/// 由代理调用 SendInput 模拟相对移动。只有 RDP 输入窗口获得焦点时才锁定并隐藏本机鼠标。
/// </summary>
internal sealed class GameMouseService : IDisposable
{
    private static readonly TimeSpan AgentConnectionTimeout = TimeSpan.FromSeconds(10);
    private const int FlushIntervalMilliseconds = 10;

    private readonly ChildSessionService _childSessionService;
    private readonly SettingsService _settingsService;
    private readonly RawMouseInput _rawMouseInput = new();
    private readonly LocalCursorCapture _cursorCapture = new();
    private readonly DispatcherTimer _focusTimer;
    private readonly System.Threading.Timer _flushTimer;
    private readonly SemaphoreSlim _agentSemaphore = new(1, 1);
    private readonly object _deltaLock = new();

    private InputPipeServer? _pipeServer;
    private bool _enabled;
    private bool _rawInputStarted;
    private bool _captureActive;
    private bool _disposed;
    private int _pendingDeltaX;
    private int _pendingDeltaY;
    private string? _lastError;

    public GameMouseService(
        ChildSessionService childSessionService,
        SettingsService settingsService)
    {
        _childSessionService = childSessionService;
        _settingsService = settingsService;
        _enabled = settingsService.Current.GameMouseModeEnabled;

        _focusTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _focusTimer.Tick += OnFocusTimerTick;
        _focusTimer.Start();

        _flushTimer = new System.Threading.Timer(
            _ => FlushPendingDelta(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        _childSessionService.StateChanged += OnChildSessionStateChanged;
    }

    public event EventHandler? StateChanged;

    public bool IsEnabled => _enabled;

    public string? LastError => _lastError;

    /// <summary>开启或关闭游戏鼠标。开启时会按需启动子会话里的输入代理。</summary>
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return true;
        }

        _enabled = enabled;
        _settingsService.Current.GameMouseModeEnabled = enabled;
        _settingsService.Save();
        _lastError = null;

        if (!enabled)
        {
            StopAgent();
            RaiseStateChanged();
            return true;
        }

        if (_childSessionService.ConnectedState == 1)
        {
            await EnsureAgentAsync();
        }

        RaiseStateChanged();
        return _enabled;
    }

    private void OnChildSessionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        if (_childSessionService.ConnectedState == 1)
        {
            _ = EnsureAgentFromStateChangeAsync();
            return;
        }

        StopAgent();
    }

    private async Task EnsureAgentFromStateChangeAsync()
    {
        await EnsureAgentAsync();
    }

    private async Task EnsureAgentAsync()
    {
        if (_disposed
            || !_enabled
            || _childSessionService.ConnectedState != 1
            || _pipeServer?.IsConnected == true)
        {
            return;
        }

        await _agentSemaphore.WaitAsync();
        try
        {
            if (_disposed
                || !_enabled
                || _childSessionService.ConnectedState != 1
                || _pipeServer?.IsConnected == true)
            {
                return;
            }

            var sessionId = _childSessionService.ChildSessionId;
            if (sessionId is null)
            {
                return;
            }

            var pipeName = InputAgentProtocol.CreatePipeName();
            InputPipeServer server;
            try
            {
                server = new InputPipeServer(pipeName);
            }
            catch (Exception exception)
            {
                SetError(I18nService.F(
                    "启动游戏鼠标输入代理失败：{0}",
                    exception.GetBaseException().Message));
                return;
            }

            var connectionTask = server.WaitForConnectionAsync(AgentConnectionTimeout);

            try
            {
                await ChildSessionProcessLauncher.LaunchElevatedAsync(
                    sessionId.Value,
                    ResolveAgentExecutablePath(),
                    CreateAgentArguments(pipeName),
                    AppContext.BaseDirectory);
            }
            catch (Exception exception)
            {
                server.Dispose();
                SetError(I18nService.F(
                    "启动游戏鼠标输入代理失败：{0}",
                    exception.GetBaseException().Message));
                return;
            }

            bool connected;
            try
            {
                connected = await connectionTask;
            }
            catch (Exception exception)
            {
                server.Dispose();
                SetError(I18nService.F(
                    "启动游戏鼠标输入代理失败：{0}",
                    exception.GetBaseException().Message));
                return;
            }

            if (!connected)
            {
                server.Dispose();
                SetError(I18nService.T("游戏鼠标输入代理连接超时。"));
                return;
            }

            _pipeServer?.Dispose();
            _pipeServer = server;
            _lastError = null;
            StartRawInput();
        }
        finally
        {
            _agentSemaphore.Release();
        }
    }

    private static string ResolveAgentExecutablePath()
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException(
                              I18nService.T("无法取得当前程序路径。"));
        return processPath;
    }

    private static string CreateAgentArguments(string pipeName)
    {
        var processPath = Environment.ProcessPath ?? string.Empty;
        var pipeArguments =
            $"{InputAgentProtocol.AgentArgument} {InputAgentProtocol.PipeArgument} \"{pipeName}\"";

        // 通过 dotnet VirtualDesktopRDP.dll 启动时，任务计划程序需要使用 dotnet 作为入口。
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location
                                ?? throw new InvalidOperationException(
                                    I18nService.T("无法取得程序入口路径。"));
            return $"\"{entryAssembly}\" {pipeArguments}";
        }

        return pipeArguments;
    }

    private void StartRawInput()
    {
        if (_rawInputStarted)
        {
            return;
        }

        try
        {
            _rawMouseInput.Moved += OnRawMouseMoved;
            _rawMouseInput.Start();
            _rawInputStarted = true;
        }
        catch (Exception exception)
        {
            _rawMouseInput.Moved -= OnRawMouseMoved;
            SetError(I18nService.F(
                "启动 Raw Input 鼠标采集失败：{0}",
                exception.GetBaseException().Message));
        }
    }

    private void StopRawInput()
    {
        if (!_rawInputStarted)
        {
            return;
        }

        _rawMouseInput.Moved -= OnRawMouseMoved;
        _rawMouseInput.Stop();
        _rawInputStarted = false;
    }

    private void OnRawMouseMoved(int deltaX, int deltaY)
    {
        if (_disposed || !_captureActive || _pipeServer?.IsConnected != true)
        {
            return;
        }

        lock (_deltaLock)
        {
            _pendingDeltaX += deltaX;
            _pendingDeltaY += deltaY;
        }

        _flushTimer.Change(FlushIntervalMilliseconds, Timeout.Infinite);
    }

    private void FlushPendingDelta()
    {
        if (_disposed)
        {
            return;
        }

        int deltaX;
        int deltaY;
        lock (_deltaLock)
        {
            deltaX = _pendingDeltaX;
            deltaY = _pendingDeltaY;
            _pendingDeltaX = 0;
            _pendingDeltaY = 0;
        }

        if (deltaX != 0 || deltaY != 0)
        {
            _pipeServer?.Enqueue(deltaX, deltaY);
        }
    }

    private void OnFocusTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_enabled
            || _pipeServer?.IsConnected != true
            || !_childSessionService.IsRdpInputWindowFocused()
            || !_childSessionService.TryGetRdpClientBounds(out var bounds))
        {
            StopCapture();
            return;
        }

        // 按住 Alt 时临时释放鼠标，松开后重新锁定。
        if (IsAltPressed())
        {
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            lock (_deltaLock)
            {
                _pendingDeltaX = 0;
                _pendingDeltaY = 0;
            }

            if (_captureActive)
            {
                try
                {
                    _cursorCapture.ReleaseTemporarily();
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception)
                {
                    Debug.WriteLine(exception.Message);
                }

                _captureActive = false;
            }

            return;
        }

        try
        {
            _cursorCapture.Capture(bounds);
            _captureActive = true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or ArgumentException)
        {
            _captureActive = false;
            _cursorCapture.Release();
            Debug.WriteLine(exception.Message);
        }
    }

    private void StopCapture()
    {
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_deltaLock)
        {
            _pendingDeltaX = 0;
            _pendingDeltaY = 0;
        }

        if (_captureActive)
        {
            _cursorCapture.Release();
            _captureActive = false;
        }
    }

    private void StopAgent()
    {
        StopCapture();
        StopRawInput();
        _pipeServer?.Dispose();
        _pipeServer = null;
    }

    private void SetError(string message)
    {
        _lastError = message;
        Debug.WriteLine(message);
        _enabled = false;
        _settingsService.Current.GameMouseModeEnabled = false;
        _settingsService.Save();
        StopAgent();
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsAltPressed()
    {
        return (GetAsyncKeyState((int)Keys.LeftMenu) & 0x8000) != 0
               || (GetAsyncKeyState((int)Keys.RightMenu) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _focusTimer.Stop();
        _focusTimer.Tick -= OnFocusTimerTick;
        _childSessionService.StateChanged -= OnChildSessionStateChanged;
        StopAgent();
        _flushTimer.Dispose();
        _cursorCapture.Dispose();
        _rawMouseInput.Dispose();
        _agentSemaphore.Dispose();
    }

    private enum Keys
    {
        LeftMenu = 0xA4,
        RightMenu = 0xA5
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
