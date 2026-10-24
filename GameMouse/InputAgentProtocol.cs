namespace VirtualDesktopRDP.GameMouse;

/// <summary>
/// VirtualDesktopRDP 输入代理的命令行与管道协议。
/// </summary>
internal static class InputAgentProtocol
{
    public const string AgentArgument = "--input-agent";
    public const string PipeArgument = "--input-pipe";
    public const int DeltaSize = sizeof(int) * 2;

    public static string CreatePipeName()
    {
        return $"VirtualDesktopRDP.Input.{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 从启动参数中解析输入代理模式。非代理模式返回 null。
    /// </summary>
    public static string? TryGetPipeName(string[] args)
    {
        if (!args.Any(argument => string.Equals(
                argument,
                AgentArgument,
                StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(PipeArgument + "=", StringComparison.OrdinalIgnoreCase))
            {
                return argument[(PipeArgument.Length + 1)..];
            }

            if (string.Equals(argument, PipeArgument, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
