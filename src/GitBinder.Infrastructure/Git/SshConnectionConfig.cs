using System.Globalization;
using System.Text;
using GitBinder.Application.Common;
using GitBinder.Domain.Common;

namespace GitBinder.Infrastructure.Git;

/// <summary>从本机 SSH 解析结果中复制连接设置，目标主机仍只使用软件选定的私钥</summary>
internal static class SshConnectionConfig
{
    private static readonly HashSet<string> ConnectionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "hostname", "user", "port", "addressfamily", "proxycommand", "proxyjump", "proxyusefdpass",
        "hostkeyalias", "userknownhostsfile", "globalknownhostsfile", "identityagent",
        "connecttimeout", "connectionattempts", "serveraliveinterval", "serveralivecountmax",
        "tcpkeepalive", "compression", "ciphers", "macs", "kexalgorithms", "hostkeyalgorithms",
        "pubkeyacceptedalgorithms", "hostbasedacceptedalgorithms", "bindaddress", "bindinterface",
    };

    internal static async Task<Result<EmptySshConfig>> CreateAsync(ICommandExecutor executor,
        string sshExecutable, string url, string? workingDirectory, CancellationToken ct)
    {
        string host;
        string user;
        int port = -1;
        if (url.StartsWith("git@", StringComparison.Ordinal))
        {
            host = url[4..url.IndexOf(':')];
            user = "git";
        }
        else
        {
            var uri = new Uri(url);
            host = uri.IdnHost;
            user = Uri.UnescapeDataString(uri.UserInfo);
            port = uri.Port;
        }
        if (host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')))
            return Result<EmptySshConfig>.Failure(new DomainError("TRANSFER_URL_INVALID"));
        var args = new List<string> { "-G", "-o", "BatchMode=yes" };
        if (!string.IsNullOrEmpty(user)) args.AddRange(["-l", user]);
        if (port > 0) args.AddRange(["-p", port.ToString(CultureInfo.InvariantCulture)]);
        args.Add(host);
        var resolved = await executor.ExecuteWithOptionsAsync(sshExecutable, args, workingDirectory,
            GitTransfer.TransferOptions(timeout: TimeSpan.FromSeconds(60)), ct);
        resolved.ThrowIfTerminationUnconfirmed();
        ct.ThrowIfCancellationRequested();
        if (!resolved.IsSuccess)
            return Result<EmptySshConfig>.Failure(new DomainError("TRANSFER_SSH_CONFIG_FAILED",
                new[] { "ssh -G", resolved.ExitCode.ToString(CultureInfo.InvariantCulture) }));
        var content = new StringBuilder().Append("Host ").AppendLine(host);
        var hasHostName = false;
        foreach (var line in resolved.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var setting = line.Trim();
            var separator = setting.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0) continue;
            var key = setting[..separator];
            var value = setting[(separator + 1)..].Trim();
            if (!ConnectionKeys.Contains(key) || string.IsNullOrEmpty(value) || value.Any(char.IsControl)) continue;
            if (key.Equals("hostname", StringComparison.OrdinalIgnoreCase)) hasHostName = true;
            content.Append("    ").Append(key).Append(' ').AppendLine(SerializeValue(key, value));
        }
        if (!hasHostName)
            return Result<EmptySshConfig>.Failure(new DomainError("TRANSFER_SSH_CONFIG_FAILED", new[] { "ssh -G", "0" }));
        // ProxyJump 子进程可读取跳板自身的设置，目标主机不 Include 用户配置，防止叠加其他私钥
        content.Append("Host * !").AppendLine(host).AppendLine("    Include ~/.ssh/config");
        var systemConfigs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            systemConfigs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "ssh_config"));
            if (System.IO.Path.IsPathFullyQualified(sshExecutable))
                systemConfigs.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sshExecutable)!, "..", "..", "etc", "ssh", "ssh_config")));
        }
        else systemConfigs.Add("/etc/ssh/ssh_config");
        foreach (var systemConfig in systemConfigs.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
            content.Append("    Include \"").Append(GitCommandPaths.ToShellPath(systemConfig).Replace("\"", "\\\"")).AppendLine("\"");
        return Result<EmptySshConfig>.Success(new EmptySshConfig(content.ToString()));
    }

    private static string SerializeValue(string key, string value)
    {
        // 命令尾部和多路径列表不是单值，不把整行加引号或猜测 ssh -G 已丢失的路径边界
        if (key.Equals("proxycommand", StringComparison.OrdinalIgnoreCase)
            || key.Equals("userknownhostsfile", StringComparison.OrdinalIgnoreCase)
            || key.Equals("globalknownhostsfile", StringComparison.OrdinalIgnoreCase))
            return value;
        if (!value.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\' or '#'))
            return value;

        // SSH 配置词法会消费引号和转义符，单值中的空格、引号及反斜线需要完整往返
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
