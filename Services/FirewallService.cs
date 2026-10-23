using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VirtualDesktopRDP.I18n;

namespace VirtualDesktopRDP.Services;

/// <summary>
/// 为当前程序维护 Windows 防火墙入站放行规则。
///
/// BetterGI 本身没有在应用代码里操作防火墙，它的“无需手动设置”来自安装包：
/// Build/micasetup.json 中的 IsAllowFirewall=true 会让 MicaSetup 安装器写入应用级防火墙规则。
/// 本项目没有安装器，因此在启动时自行完成等价操作。
/// </summary>
internal static class FirewallService
{
    private const string RuleName = "VirtualDesktopRDP";
    private const string RuleGrouping = "VirtualDesktopRDP";
    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;
    private const int ProtocolAny = 256;
    private const int ProfileAll = 0x7FFFFFFF;

    /// <summary>最近一次检查/写入的结果，供界面提示使用。</summary>
    public static string LastMessage { get; private set; } = "尚未检查防火墙规则";

    /// <summary>
    /// 确保当前可执行文件存在一条启用状态的入站放行规则。
    /// 已存在且路径一致时不重复写入；路径变化时替换旧规则。
    /// </summary>
    public static bool EnsureInboundRule()
    {
        var executablePath = ResolveExecutablePath();
        if (executablePath is null)
        {
            LastMessage = I18nService.T(
                "当前不是通过 VirtualDesktopRDP.exe 启动，跳过防火墙规则写入。");
            Trace.WriteLine(LastMessage);
            return false;
        }

        object? policyObject = null;
        object? rulesObject = null;
        object? ruleObject = null;
        object? existingObject = null;

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                LastMessage = I18nService.T("系统未提供 HNetCfg.FwPolicy2 防火墙 COM 接口。");
                Trace.WriteLine(LastMessage);
                return false;
            }

            policyObject = Activator.CreateInstance(policyType);
            if (policyObject is null)
            {
                LastMessage = I18nService.T("无法创建 Windows 防火墙策略对象。");
                Trace.WriteLine(LastMessage);
                return false;
            }

            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            dynamic rules = rulesObject;

            try
            {
                existingObject = rules.Item(RuleName);
            }
            catch (COMException)
            {
                // 规则不存在时 Item 会抛错，继续创建。
            }

            if (existingObject is not null)
            {
                dynamic existingRule = existingObject;
                var existingRuleIsUsable = false;
                try
                {
                    existingRuleIsUsable =
                        string.Equals(
                            existingRule.ApplicationName as string,
                            executablePath,
                            StringComparison.OrdinalIgnoreCase)
                        && (bool)existingRule.Enabled
                        && (int)existingRule.Direction == DirectionInbound
                        && (int)existingRule.Action == ActionAllow
                        && (int)existingRule.Protocol == ProtocolAny
                        && (int)existingRule.Profiles == ProfileAll;
                }
                catch (COMException)
                {
                    // 规则属性不可读时按不可用处理，删除后重建。
                }

                if (existingRuleIsUsable)
                {
                    LastMessage = I18nService.F("防火墙规则已存在：{0}", executablePath);
                    Trace.WriteLine(LastMessage);
                    return true;
                }

                rules.Remove(RuleName);
                ReleaseComObject(existingObject);
                existingObject = null;
            }

            var ruleType = Type.GetTypeFromProgID("HNetCfg.FwRule");
            if (ruleType is null)
            {
                LastMessage = I18nService.T("系统未提供 HNetCfg.FwRule 防火墙 COM 接口。");
                Trace.WriteLine(LastMessage);
                return false;
            }

            ruleObject = Activator.CreateInstance(ruleType);
            if (ruleObject is null)
            {
                LastMessage = I18nService.T("无法创建 Windows 防火墙规则对象。");
                Trace.WriteLine(LastMessage);
                return false;
            }

            dynamic rule = ruleObject;
            rule.Name = RuleName;
            rule.Description = I18nService.T(
                "允许 VirtualDesktopRDP 通过 Windows 防火墙");
            rule.Grouping = RuleGrouping;
            rule.ApplicationName = executablePath;
            rule.Direction = DirectionInbound;
            rule.Action = ActionAllow;
            rule.Enabled = true;
            rule.Protocol = ProtocolAny;
            rule.Profiles = ProfileAll;

            rules.Add(rule);
            LastMessage = I18nService.F("已写入防火墙规则：{0}", executablePath);
            Trace.WriteLine(LastMessage);
            return true;
        }
        catch (Exception exception)
        {
            // 防火墙规则属于辅助能力，任何失败都不应阻止程序启动。
            LastMessage = I18nService.F(
                "写入防火墙规则失败：{0}",
                exception.GetBaseException().Message);
            Trace.WriteLine(LastMessage);
            return false;
        }
        finally
        {
            ReleaseComObject(existingObject);
            ReleaseComObject(ruleObject);
            ReleaseComObject(rulesObject);
            ReleaseComObject(policyObject);
        }
    }

    /// <summary>
    /// 只对真正的 VirtualDesktopRDP.exe 进程写规则，避免测试时给 dotnet.exe 放行。
    /// </summary>
    private static string? ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !File.Exists(processPath)
            || string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(processPath);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
