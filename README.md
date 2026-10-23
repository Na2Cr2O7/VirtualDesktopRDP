# VirtualDesktopRDP

从 BetterGI 的“桌面分身”功能中提取的独立 WPF 示例程序。它不做远程桌面协议实现，而是组合 Windows 自带能力：

- `WTSEnableChildSessions` / `WTSGetChildSessionId` / `WTSLogoffSession`
- 本机 RDP ActiveX `MsRdpClient10` 的 `ConnectToChildSession`
- 任务计划程序 `RunEx(..., TASK_RUN_USE_SESSION_ID, ...)` 投递进程
- RDP 输入窗口 `Input Capture Window` 的 `EnableWindow` 控制（观看模式）

## 环境要求

- Windows 10 / 11 非家庭版（家庭版可能可用，但不受微软支持）
- 以管理员权限运行（项目清单已设置 `requireAdministrator`）
- 系统 RDP 未被关闭、未被 RDP Wrapper 接管
- 不要在桌面分身运行期间直接关机/重启；先关闭本程序并注销会话

## 功能

保留：

- 启动 / 连接本机 Child Session
- 隐藏到托盘（RDP 连接保持）
- 关闭并注销 Child Session
- 观看模式（屏蔽本机键鼠进入子会话）
- 自适应 / 1:1、保持 16:9
- 小窗模式
- 系统组合键发送到虚拟桌面
- 音频静音
- 发送 Win+D
- 以管理员权限在子会话中启动任意 `.exe`
- 新建会话后按列表自动启动程序

已去掉：

- 游戏鼠标 / 相对鼠标转发
- 运行 BetterGI
- 切换窗口 / Win+Tab
- BetterGI 双实例 IPC、任务调度等业务

## 防火墙

BetterGI 的应用代码没有防火墙处理；它之所以通常不需要手动设置，是因为安装包配置了 `IsAllowFirewall: true`，由 MicaSetup 安装器写入应用级防火墙规则。

本项目没有安装器，因此启动时会自动检查并写入一条针对当前 `VirtualDesktopRDP.exe` 的入站放行规则（所有网络配置文件）。如果失败，可以在“控制中心 → 更多 → 修复防火墙规则”中重试。

## 自动启动列表

工具栏“设置”或托盘菜单“自动启动设置”可以维护 `.exe` 列表。新建 Child Session 并完成 RDP 登录后，列表中的程序会按顺序以管理员权限启动；重新连接已有会话不会重复启动。单个程序失败会被跳过，其余程序继续启动。

配置保存在：

```text
%LOCALAPPDATA%\VirtualDesktopRDP\settings.json
```

## 构建

```powershell
dotnet build .\VirtualDesktopRDP.csproj
```

生成的程序位于 `bin\Debug\net10.0-windows\VirtualDesktopRDP.exe`。直接双击会请求管理员权限。

## 代码结构

```text
Services/
  ChildSessionNativeMethods.cs      WTS / 注册表 / RDP 输入窗口
  ChildSessionEnvironmentCheck.cs   启动前环境检查
  ChildSessionService.cs            连接、重试、设置、自动启动、注销
  ChildSessionProcessLauncher.cs    计划任务投递
  FirewallService.cs                应用级防火墙入站规则
Rdp/
  RdpActiveXHost.cs                 MsRdpClient10 AxHost 封装
ViewModels/
  MainWindowViewModel.cs            主窗口状态与命令
Infrastructure/
  SettingsService.cs / AppSettings.cs
  ObservableObject.cs / RelayCommand.cs
MainWindow.xaml / MainWindow.xaml.cs
SettingsWindow.xaml / SettingsWindow.xaml.cs
```

## 说明

RDP 的登录凭据由 Windows 的 RDP ActiveX 登录流程处理，本程序不保存用户名或密码。首次连接时可能会要求输入当前 Windows 用户的凭据；PIN、Windows Hello、微软账户密码不同步等都可能导致登录失败。
