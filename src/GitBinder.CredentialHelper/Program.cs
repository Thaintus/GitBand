using GitBinder.Application;
using GitBinder.Application.Accounts;
using GitBinder.Application.Bindings;
using GitBinder.Application.Security;
using GitBinder.Domain.Accounts;
using GitBinder.Infrastructure;
using GitBinder.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace GitBinder.CredentialHelper;

/// <summary>
/// Git Credential Helper：实现 Git Credential Protocol，从 Secret Store 提供 HTTPS 认证数据。
/// 用法（由 GUI 写入仓库 config）：
///   credential.helper = "!&lt;绝对路径&gt;/gitbinder-credential[.exe] --account-id &lt;账号 Id&gt;"
/// 指定账号时不按主机或默认账号回退，确保认证与当前选中的账号一致。
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 打印调试信息到 stderr，避免污染 stdout 协议输出。
        // Git 会把 get/store/erase 追加到 credential.helper 配置的命令之后。
        var operation = args.FirstOrDefault(arg => arg is "get" or "store" or "erase") ?? "get";

        try
        {
            var (services, accountRepository, secretStore) = Build();

            switch (operation)
            {
                case "get":
                    return await GetAsync(accountRepository, secretStore, args);
                case "store":
                    return 0; // V1 不实现 store（凭据由 GUI 管理）。
                case "erase":
                    return 0;
                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"gitbinder-credential: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> GetAsync(
        IAccountRepository accountRepository,
        ISecretStore secretStore,
        IReadOnlyList<string> args)
    {
        // 读取 stdin 的 key=value 对（Git Credential Protocol）。
        var request = ParseStdin();
        var host = GetValue(request, "host");

        // 指定账号。
        var accountId = GetAccountId(args);
        var account = accountId is Guid id
            ? await accountRepository.GetByIdAsync(id)
            : await ResolveByHostAsync(accountRepository, host);

        if (account is null || !account.Enabled
            || account.AuthenticationType is not (AuthenticationType.Https or AuthenticationType.Both))
        {
            return 0;
        }

        var username = string.IsNullOrWhiteSpace(account.HttpsUsername)
            ? account.Username
            : account.HttpsUsername;

        string? credential = null;
        if (!string.IsNullOrWhiteSpace(account.HttpsSecretId))
        {
            credential = await secretStore.GetAsync(account.HttpsSecretId);
        }

        if (credential is null)
        {
            return 0;
        }

        // 输出协议。
        Console.WriteLine($"username={username}");
        Console.WriteLine($"password={credential}");
        return 0;
    }

    private static Dictionary<string, string> ParseStdin()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }

        return result;
    }

    private static string GetValue(Dictionary<string, string> request, string key)
        => request.TryGetValue(key, out var value) ? value : string.Empty;

    private static Guid? GetAccountId(IReadOnlyList<string> args)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], "--account-id", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(args[index + 1], out var id))
            {
                return id;
            }
        }

        // 兼容早期仅通过环境变量指定账号的配置。
        var raw = Environment.GetEnvironmentVariable("GITBINDER_ACCOUNT_ID");
        return Guid.TryParse(raw, out var environmentAccountId) ? environmentAccountId : null;
    }

    private static async Task<Account?> ResolveByHostAsync(
        IAccountRepository accountRepository,
        string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return await accountRepository.GetDefaultAsync();
        }

        var accounts = await accountRepository.GetAllAsync();
        return accounts.FirstOrDefault(a =>
            string.Equals(a.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    private static (IServiceProvider, IAccountRepository, ISecretStore) Build()
    {
        // 必须与桌面端共享平台路径；macOS 使用 Library/Application Support。
        var dataPath = PlatformPaths.CreateApplicationDataPath();

        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(dataPath.DatabasePath);
        services.AddPlatform();

        var provider = services.BuildServiceProvider();
        var initializer = provider.GetRequiredService<DatabaseInitializer>();
        initializer.Initialize();

        var accountRepository = provider.GetRequiredService<IAccountRepository>();
        var secretStore = provider.GetRequiredService<ISecretStore>();
        return (provider, accountRepository, secretStore);
    }
}
