// SPDX-License-Identifier: GPL-3.0-only
// 源自 BetterGI 的桌面分身实现，已针对本项目裁剪。

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VirtualDesktopRDP.Services;

/// <summary>
/// 通过任务计划程序把进程投递到指定 Windows Session，并以最高权限运行。
/// 直接 Process.Start 只能启动到当前 Session，也无法绕开子会话中的 UAC 同意界面。
/// </summary>
internal static class ChildSessionProcessLauncher
{
    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskRunUseSessionId = 0x4;

    /// <summary>
    /// 校验程序路径并在目标 Child Session 中以管理员权限启动。
    /// </summary>
    internal static Task LaunchElevatedAsync(
        uint childSessionId,
        string executablePath,
        string arguments = "",
        string? workingDirectory = null)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var actualWorkingDirectory = workingDirectory
                                     ?? Path.GetDirectoryName(fullPath)
                                     ?? AppContext.BaseDirectory;
        return Task.Run(() => LaunchWithTemporaryTask(
            childSessionId,
            fullPath,
            arguments,
            actualWorkingDirectory));
    }

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        var actualChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (actualChildSessionId != childSessionId)
        {
            throw new InvalidOperationException(
                $"目标 Child Session 已发生变化。请求会话为 {childSessionId}，当前会话为 "
                + (actualChildSessionId?.ToString(CultureInfo.InvariantCulture) ?? "无")
                + "。");
        }

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");
        var taskName = $"VirtualDesktopRDP-ElevatedLaunch-{Guid.NewGuid():N}";
        string accountName;
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            accountName = currentIdentity.Name;
        }

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        object? runningTaskObject = null;
        var taskRegistered = false;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;
            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject;

            taskDefinition.RegistrationInfo.Author = "VirtualDesktopRDP";
            taskDefinition.RegistrationInfo.Description =
                $"临时启动 {Path.GetFileName(executablePath)} 到 Child Session {childSessionId}";

            taskDefinition.Settings.Enabled = true;
            taskDefinition.Settings.Hidden = true;
            taskDefinition.Settings.AllowDemandStart = true;
            taskDefinition.Settings.DisallowStartIfOnBatteries = false;
            taskDefinition.Settings.StopIfGoingOnBatteries = false;
            taskDefinition.Settings.ExecutionTimeLimit = "PT0S";

            taskDefinition.Principal.UserId = accountName;
            taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
            taskDefinition.Principal.RunLevel = TaskRunLevelHighest;

            actionObject = taskDefinition.Actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = executablePath;
            action.Arguments = arguments;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                taskName,
                taskDefinition,
                TaskCreate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;

            dynamic registeredTask = registeredTaskObject;
            runningTaskObject = registeredTask.RunEx(
                null,
                TaskRunUseSessionId,
                checked((int)childSessionId),
                null);

            if (runningTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回运行实例。");
            }
        }
        finally
        {
            if (taskRegistered && rootFolderObject is not null)
            {
                try
                {
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                catch (COMException)
                {
                    // 程序已经启动成功，清理临时任务失败不应中断流程。
                }
            }

            ReleaseComObject(runningTaskObject);
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>校验路径存在且为 .exe。</summary>
    internal static string ValidateExecutablePath(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只允许选择 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
