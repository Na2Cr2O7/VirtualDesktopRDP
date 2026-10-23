// SPDX-License-Identifier: GPL-3.0-only
// 源自 BetterGI 的桌面分身实现，已针对本项目裁剪。

namespace VirtualDesktopRDP.Services;

/// <summary>
/// RDP 连接失败事件参数。
/// </summary>
internal sealed class ChildSessionConnectionFailedEventArgs(
    string message,
    int errorCode,
    int? extendedErrorCode = null) : EventArgs
{
    public string Message { get; } = message;

    public int ErrorCode { get; } = errorCode;

    public int? ExtendedErrorCode { get; } = extendedErrorCode;
}
