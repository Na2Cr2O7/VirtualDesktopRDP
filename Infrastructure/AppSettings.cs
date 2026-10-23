namespace VirtualDesktopRDP.Infrastructure;

/// <summary>
/// 桌面分身本机配置，保存到用户本地应用数据目录。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 新建子会话登录成功后自动启动的程序列表，按顺序以管理员权限启动。
    /// </summary>
    public List<string> AutoStartPrograms { get; set; } = [];

    /// <summary>窗口是否置顶。</summary>
    public bool TopmostEnabled { get; set; }

    /// <summary>RDP 画面是否随窗口缩放。</summary>
    public bool SmartSizingEnabled { get; set; } = true;

    /// <summary>窗口是否保持 16:9 内容宽高比。</summary>
    public bool KeepAspectRatio { get; set; } = true;

    /// <summary>Alt+Tab、Windows 键等系统组合键是否发送到子会话。</summary>
    public bool SendSystemShortcutsToRemote { get; set; } = true;

    /// <summary>是否禁用子会话的远程音频。</summary>
    public bool AudioMuted { get; set; }

    /// <summary>是否屏蔽本机进入子会话的键鼠输入。</summary>
    public bool WatchModeEnabled { get; set; }

    /// <summary>普通窗口宽度，用于恢复上次窗口尺寸。</summary>
    public double NormalWidth { get; set; } = 1280;

    /// <summary>普通窗口高度。</summary>
    public double NormalHeight { get; set; } = 752;

    /// <summary>主窗口上一次所在的屏幕坐标。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>主窗口上一次所在的屏幕坐标。</summary>
    public double? WindowTop { get; set; }

    /// <summary>
    /// 创建一份用于设置窗口编辑的副本，取消时不影响当前配置。
    /// </summary>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            AutoStartPrograms = [.. AutoStartPrograms],
            TopmostEnabled = TopmostEnabled,
            SmartSizingEnabled = SmartSizingEnabled,
            KeepAspectRatio = KeepAspectRatio,
            SendSystemShortcutsToRemote = SendSystemShortcutsToRemote,
            AudioMuted = AudioMuted,
            WatchModeEnabled = WatchModeEnabled,
            NormalWidth = NormalWidth,
            NormalHeight = NormalHeight,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop
        };
    }
}
