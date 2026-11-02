using AiResume.Worker.Probes;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// DeepSeek 余额行读 OpenCode 自己的凭据。夹具形状照抄 2026-10-30 本机文件结构
/// (<c>~/.local/share/opencode/auth.json</c> 的 <c>{"deepseek":{"type":"api","key":…}}</c>,
/// <c>~/.config/opencode/opencode.jsonc</c> 带注释与尾逗号),值全部是假的。
/// 所有路径都落在 TestTemp 下，绝不碰真机 OpenCode 目录。
/// </summary>
public sealed class OpenCodeCredentialsTests
{
    private const string FakeKey = "fake-deepseek-company-key-0001";
    private const string AuthJson = "{\"deepseek\":{\"type\":\"api\",\"key\":\"" + FakeKey + "\"}}";

    private static string Home(string? authJson = null, string? configJsonc = null)
    {
        string home = TestTemp.NewDir("opencode-home");
        if (authJson is not null)
        {
            string data = Path.Combine(home, ".local", "share", "opencode");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "auth.json"), authJson);
        }

        if (configJsonc is not null)
        {
            string config = Path.Combine(home, ".config", "opencode");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "opencode.jsonc"), configJsonc);
        }

        return home;
    }

    private static Func<string, string?> Env(Dictionary<string, string>? values = null) =>
        name => values is not null && values.TryGetValue(name, out string? value) ? value : null;

    [Fact]
    public void 读OpenCode登录文件里的DeepSeek密钥并压过旧环境变量()
    {
        string home = Home(authJson: "{\"deepseek\":{\"type\":\"api\",\"key\":\"" + FakeKey + "\"},\"anthropic\":{\"type\":\"api\",\"key\":\"other\"}}");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(
            Env(new() { ["DEEPSEEK_API_KEY"] = "personal-legacy-key-0002" }), home);

        Assert.True(key.Ok);
        Assert.Equal(FakeKey, key.Key);
        Assert.Equal(OpenCodeCredentials.SourceAuth, key.Source);
    }

    [Fact]
    public void 配置显式apiKey优先并解析环境变量引用()
    {
        string home = Home(
            authJson: AuthJson,
            configJsonc: """
                {
                  // 注释与尾逗号都是 OpenCode 合法写法
                  "provider": { "deepseek": { "options": { "apiKey": "{env:COMPANY_DS_KEY}", }, }, },
                }
                """);

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(new() { ["COMPANY_DS_KEY"] = "fake-from-env-0003" }), home);

        Assert.Equal("fake-from-env-0003", key.Key);
        Assert.Equal(OpenCodeCredentials.SourceConfig, key.Source);
    }

    [Fact]
    public void 配置引用的环境变量缺失时如实报错而不是退回别的密钥()
    {
        string home = Home(
            authJson: AuthJson,
            configJsonc: """{"provider":{"deepseek":{"options":{"apiKey":"{env:MISSING_KEY}"}}}}""");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), home);

        Assert.False(key.Ok);
        Assert.Null(key.Key);
        Assert.Contains("MISSING_KEY", key.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 自定义端点不发余额请求()
    {
        string home = Home(configJsonc: """{"provider":{"deepseek":{"options":{"baseURL":"https://relay.example.com/v1","apiKey":"fake-relay-0004"}}}}""");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), home);

        Assert.False(key.Ok);
        Assert.Null(key.Key);
        Assert.Contains("自定义端点", key.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 端点写在另一层配置里也要合并后判断_不能把中转密钥发给官方()
    {
        // opencode.json 把 DeepSeek 指到公司中转,opencode.jsonc 只写了密钥:OpenCode 会深度合并两层,
        // 只看写着密钥的那一层,就会把只属于中转的密钥发给 api.deepseek.com。
        string home = Home(configJsonc: """{"provider":{"deepseek":{"options":{"apiKey":"fake-relay-only-0007"}}}}""");
        File.WriteAllText(Path.Combine(home, ".config", "opencode", "opencode.json"),
            """{"provider":{"deepseek":{"options":{"baseURL":"https://relay.corp.example/v1"}}}}""");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), home);

        Assert.False(key.Ok);
        Assert.Null(key.Key);
        Assert.Contains("自定义端点", key.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCode2的凭据库优先于只导入过一次的auth_json()
    {
        // 2.2.5 录制形状:credential(id, integration_id, label, value, connector_id, method_id, active, time_created, time_updated),
        // value 为 {"type":"key","key":…}。auth.json 只在 2.x 首次启动时导入一次,之后换密钥只写库。
        string home = Home(authJson: AuthJson);
        string db = Path.Combine(home, ".local", "share", "opencode", "opencode.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                create table credential (id text primary key, integration_id text not null, label text, value text not null,
                  connector_id text, method_id text, active integer, time_created integer not null, time_updated integer not null);
                insert into credential values ('cred_old', 'deepseek', 'API key', '{"type":"key","key":"fake-imported-0008"}', null, null, null, 1, 1);
                insert into credential values ('cred_new', 'deepseek', 'API key', '{"type":"key","key":"fake-rotated-0009"}', null, null, null, 2, 2);
                insert into credential values ('cred_other', 'anthropic', 'API key', '{"type":"key","key":"other"}', null, null, null, 3, 3);
                """;
            command.ExecuteNonQuery();
        }

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), home);

        Assert.True(key.Ok);
        Assert.Equal("fake-rotated-0009", key.Key);
        Assert.Equal(OpenCodeCredentials.SourceDatabase, key.Source);

        // 库在但读不了:未核实，绝不悄悄改用 auth.json 里可能早已作废的旧密钥。
        File.Delete(db);
        File.WriteAllText(db, "not a sqlite database");
        OpenCodeProviderKey unreadable = OpenCodeCredentials.ReadDeepSeek(Env(), home);
        Assert.False(unreadable.Ok);
        Assert.Null(unreadable.Key);
        Assert.Contains("凭据库", unreadable.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void 凭据库里没有DeepSeek时不拿auth_json的旧密钥_只有库或表不存在才看auth_json()
    {
        // 用户在 OpenCode 里删了 DeepSeek 凭据(或改用环境变量):库里没有这一行，auth.json 仍留着 2.x 首次启动前的旧密钥。
        string home = Home(authJson: AuthJson);
        string db = Path.Combine(home, ".local", "share", "opencode", "opencode.db");
        CreateCredentialTable(db, "insert into credential values ('cred_other', 'anthropic', 'API key', '{\"type\":\"key\",\"key\":\"other\"}', null, null, null, 3, 3);");

        OpenCodeProviderKey none = OpenCodeCredentials.ReadDeepSeek(Env(), home);
        Assert.False(none.Ok);
        Assert.Null(none.Key);
        Assert.Equal(OpenCodeCredentials.SourceNone, none.Source);

        OpenCodeProviderKey fromEnv = OpenCodeCredentials.ReadDeepSeek(Env(new() { ["DEEPSEEK_API_KEY"] = "fake-env-0010" }), home);
        Assert.Equal("fake-env-0010", fromEnv.Key);
        Assert.Equal(OpenCodeCredentials.SourceEnvironment, fromEnv.Source);

        // 库里还没有 credential 表(尚未迁移):auth.json 仍是 OpenCode 的凭据来源。
        File.Delete(db);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "create table session (id text primary key)";
            command.ExecuteNonQuery();
        }

        OpenCodeProviderKey legacy = OpenCodeCredentials.ReadDeepSeek(Env(), home);
        Assert.Equal(FakeKey, legacy.Key);
        Assert.Equal(OpenCodeCredentials.SourceAuth, legacy.Source);
    }

    [Fact]
    public void 凭据库位置跟随OPENCODE_DB_相对路径按数据目录解析()
    {
        string home = Home(authJson: AuthJson);
        string data = Path.Combine(home, ".local", "share", "opencode");
        CreateCredentialTable(Path.Combine(data, "channel.db"),
            "insert into credential values ('cred_new', 'deepseek', 'API key', '{\"type\":\"key\",\"key\":\"fake-channel-0011\"}', null, null, 1, 2, 2);");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(new() { ["OPENCODE_DB"] = "channel.db" }), home);

        Assert.Equal("fake-channel-0011", key.Key);
        Assert.Equal(OpenCodeCredentials.SourceDatabase, key.Source);
    }

    [Fact]
    public void 配置目录跟随OPENCODE_CONFIG_DIR_且不再读1x的config_json()
    {
        string home = Home(authJson: AuthJson);
        string legacyDir = Path.Combine(home, ".config", "opencode");
        Directory.CreateDirectory(legacyDir);
        // 1.x 遗留的 config.json:2.x 不读它，里面的旧密钥不能压过 OpenCode 实际用的凭据。
        File.WriteAllText(Path.Combine(legacyDir, "config.json"), """{"provider":{"deepseek":{"options":{"apiKey":"fake-legacy-1x-0012"}}}}""");
        Assert.Equal(FakeKey, OpenCodeCredentials.ReadDeepSeek(Env(), home).Key);

        string customDir = TestTemp.NewDir("opencode-config-dir");
        File.WriteAllText(Path.Combine(customDir, "opencode.jsonc"), """{"provider":{"deepseek":{"options":{"baseURL":"https://relay.corp.example/v1"}}}}""");
        OpenCodeProviderKey relay = OpenCodeCredentials.ReadDeepSeek(Env(new() { ["OPENCODE_CONFIG_DIR"] = customDir }), home);
        Assert.False(relay.Ok);
        Assert.Contains("自定义端点", relay.Problem, StringComparison.Ordinal);
        Assert.Equal(customDir, OpenCodeCredentials.ConfigDirectory(Env(new() { ["OPENCODE_CONFIG_DIR"] = customDir }), home));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("managed")]
    public void 内联配置与ProgramData托管层把DeepSeek指到中转时同样不发余额请求(string layer)
    {
        string home = Home(authJson: AuthJson);
        const string relay = """{"provider":{"deepseek":{"options":{"baseURL":"https://relay.corp.example/v1"}}}}""";
        var env = new Dictionary<string, string>();
        if (layer == "content")
        {
            env["OPENCODE_CONFIG_CONTENT"] = relay;
        }
        else
        {
            string programData = TestTemp.NewDir("programdata");
            Directory.CreateDirectory(Path.Combine(programData, "opencode"));
            File.WriteAllText(Path.Combine(programData, "opencode", "opencode.json"), relay);
            env["ProgramData"] = programData;
        }

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(env), home);

        Assert.False(key.Ok);
        Assert.Null(key.Key);
        Assert.Contains("自定义端点", key.Problem, StringComparison.Ordinal);
    }

    private static void CreateCredentialTable(string db, string insert)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + db + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table credential (id text primary key, integration_id text not null, label text, value text not null,
              connector_id text, method_id text, active integer, time_created integer not null, time_updated integer not null);
            """ + insert;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void 官方端点的显式配置照常可用()
    {
        string home = Home(configJsonc: """{"provider":{"deepseek":{"options":{"baseURL":"https://api.deepseek.com/v1","apiKey":"fake-literal-0005"}}}}""");

        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), home);

        Assert.True(key.Ok);
        Assert.Equal("fake-literal-0005", key.Key);
    }

    [Fact]
    public void 都没有时回退旧环境变量并标明来源()
    {
        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(
            Env(new() { ["DEEPSEEK_API_KEY"] = "fake-legacy-0006" }), Home());

        Assert.True(key.Ok);
        Assert.Equal(OpenCodeCredentials.SourceEnvironment, key.Source);
    }

    [Fact]
    public void 什么都没配时不是故障而是未配置()
    {
        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), Home(authJson: """{"anthropic":{"type":"oauth"}}"""));

        Assert.False(key.Ok);
        Assert.Equal(OpenCodeCredentials.SourceNone, key.Source);
    }

    [Fact]
    public void 损坏的登录文件报未核实且不抛()
    {
        OpenCodeProviderKey key = OpenCodeCredentials.ReadDeepSeek(Env(), Home(authJson: "{ 坏"));

        Assert.False(key.Ok);
        Assert.Equal(OpenCodeCredentials.SourceAuth, key.Source);
        Assert.NotNull(key.Problem);
    }

    [Fact]
    public void 注册表把DeepSeek列为OpenCode余额行而不是续跑目标()
    {
        string home = Home(authJson: AuthJson);
        var registry = new ProviderRegistry(Env(), home,
            codex: () => throw new InvalidOperationException("测试不读 Codex"),
            codexOfficial: () => throw new InvalidOperationException("测试不读 Codex"));

        ProviderConnection row = Assert.Single(registry.Read(), p => p.Id == OpenCodeCredentials.ProviderId);

        Assert.Equal("DeepSeek · OpenCode", row.Name);
        Assert.Equal("opencode", row.Agent);
        Assert.DoesNotContain(FakeKey, row.ToString(), StringComparison.Ordinal);
    }
}
