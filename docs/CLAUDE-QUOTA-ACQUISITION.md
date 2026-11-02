# Claude 额度获取协议与验证手册

> 现役详细真身。最后一次代码、真机响应、安装产物与界面交叉验证:2026-08-09;2026-10-12 v2.1 曾改版凭据来源(已被取代,见 §15);2026-10-30 v2.2 按 [ADR-0006](adr/0006-retire-cc-connect-per-project-resume.md) 改为只读 Claude Code 自己的登录、账号指纹改读 `oauthAccount`、scoped 门禁按模型族、逐项目取证(§4、§8.4、§8.5、§16),离线回归已通过,真机项见 §11.5(待真机验证)。
> 仓库协作与安全规则仍以 [`CLAUDE.md`](../CLAUDE.md) 为准;本文件定义 Claude 额度链路(含凭据与登录判定)的现役行为、故障边界与回归方法。逐项目续跑的义务、占用维度与状态机见 ADR-0006 与 [`ARCHITECTURE.md`](ARCHITECTURE.md),本文只管额度这一维。
> [`history/design/s7i-oauth-usage-spec.md`](history/design/s7i-oauth-usage-spec.md) 与 [`history/design/s7c-quota-probe-spec.md`](history/design/s7c-quota-probe-spec.md) 是初始设计记录,不得再当现役契约。

## 1. 第一性目标

额度功能必须回答五个不同的问题:

1. 服务端当前报告了哪些额度窗口、已用百分比和重置时间?
2. 某个模型此刻是否因 5 小时/7 天总额度、或该模型族的按模型周额度而不能继续运行?
3. 服务端本次响应稀疏、网络失败或多个 AI Resume 进程并发刷新时,上一次仍有效的服务端证据应如何连续展示?
4. 自动续跑将要为某个项目创建进程时,是否有与该项目模型相关的全部额度窗口一致的实时可运行证据,且账号仍是布防时那一个?
5. Claude Code 的登录此刻能不能用来读额度,什么时候需要用户重新 `/login`?

由此得到不可破坏的约束:

- **只展示外部可验证事实。** 百分比来自 Anthropic 用量响应或 Claude Code 的结构化 `rate_limit_event`;不得用本地 token 数估算账户额度。
- **未知不是 0,失败不是空闲。** 缺百分比、缺窗口或探测失败不得渲染成绿色正常。
- **缺失不是删除。** OAuth 与 CLI 都是稀疏观测;一次没返回 Fable 或百分比,不等于服务端明确撤销了它。
- **连续性必须有边界。** 只能在同账号、同稳定窗口身份、同一尚未到期的 reset 周期内承接旧值;主窗口身份是协议 kind,scoped 身份是规范化完整 scope 的 SHA-256。
- **账号身份只来自账号材料。** 账号指纹取 Claude Code 全局配置里的 `oauthAccount`,不随访问令牌轮换变化;核实不了就为空并等待,绝不拿令牌指纹顶替(§4.3)。
- **任一相关限额打满都算限流。** 对某个模型而言,5H/7D 主窗口、与它同模型族的 `weekly_scoped`、认不出模型族的 `weekly_scoped` 以及未归因限流,任一打满都挡住它;其它模型族的 scoped 满额只挡那个模型族(2026-10-30 起,ADR-0006 §2.D)。快照的聚合 `LimitReached` 仍是“任一窗口满额”,只用于展示与供应商行。
- **显示证据不等于执行许可。** CLI 降级和未到期的历史承接可用于诊断/连续显示,但都不能单独授权自动续跑;5H reset 也只表示可以重新取证。
- **额度许可按项目、按模型消费。** 同账号同模型的项目共用一次观测;首个项目可复用刚触发本轮的实时证据,一旦有项目真正启动,下一个项目启动前必须再次强刷并过闸。
- **凭据只读且不外泄。** AI Resume 只读 Claude Code 自己的登录文件与全局配置,不刷新、不写回、不替换 refresh token,也不调用任何 `claude` 子命令诱导刷新;不把 token、响应正文或凭据路径写进日志、UI、RPC 回包与聊天。
- **首帧不依赖 I/O。** 凭据读取、Claude 版本探测、SQLite 迁移和网络请求都发生在后台额度请求路径,不得进入 WPF 构造函数。

## 2. 上游盘点与自取依据

v2.2 起 AI Resume 不再依赖 cc-connect(已于 2026-10-30 随 [ADR-0006](adr/0006-retire-cc-connect-per-project-resume.md) 退役),自取额度的依据是对 Claude Code 本身的盘点(2026-10-30,本机 CLI 2.1.300、桌面端内嵌 2.1.309;二进制静态核对加官方文档,完整证据见 ADR-0006 §3):

- Claude Code(CLI)没有供外部进程零推理读取额度的命令或接口:`/usage` 是交互界面里的命令,`claude -p` 是一次推理、本身就消耗额度;Claude Code 自己读取用量同样要求登录作用域含 `user:profile`。
- 没有零推理成本的官方刷新命令。`claude auth status` 自身不刷新,它的 `loggedIn` 对已过期的访问令牌也报 true,不能当有效性信号。
- 凭据刷新只发生在 Claude Code 自己的 `.oauth_refresh.lock` 锁内:重读 → 已被别的进程刷新就采纳,否则刷新 → 按 refreshToken 做 CAS 写回。refresh token 一次性使用;服务端拒绝(invalid_grant)时 Claude Code 把 accessToken/refreshToken 置空、`expiresAt` 置 0。外部进程既不参与这把锁也不参与 CAS,所以 AI Resume 只读(§4.1)。
- v2.1 曾使用的 `claude setup-token` 令牌作用域只有 `user:inference`,usage 端点不接受;该来源已删除(§15、§16)。
- Windows 凭据管理器灰度(`CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1`,或全局配置 `cachedGrowthBookFeatures.tengu_windows_credman=true`)一旦生效,Claude Code 首次写入凭据管理器成功后会删除 `.credentials.json`;AI Resume 读不到时如实报 `StoreMoved`(§4.4)。2026-10-30 本机两份全局配置均未开启。

结论:额度取数、账号指纹与登录判定都直接读 Claude Code 的官方产物(登录文件、全局配置)并调用官方 OAuth usage 端点,不代刷、不经推理。

历史(v2.1 及之前):当时盘点的已依赖上游是 cc-connect v1.4.1。它的 `UsageReporter.GetUsage` 通过 PTY 驱动 Claude Code TUI、发送 `/usage`、抓取 ANSI 屏幕并解析,依赖 `creack/pty`;该库的 Windows 构建命中 `pty_unsupported.go`,`open()` 返回 `ErrUnsupported`,cc-connect 管理 API 也没有 usage 端点——“上游有接口”在 Windows 上不等于“本产品可用”,AI Resume 因此自取。`UsageSnapshot` 至今保持与 cc-connect `UsageReport` 兼容的形状,这只是历史形状;cc-connect 退役后它不再代表任何切换计划。

生态复核还确认了三条共同实践:

- [llm-cost-bar](https://github.com/kpnemo/llm-cost-bar/blob/378786c55ae4830de4c864592d679a3b44eaedad/Core/Sources/LLMCostBarCore/SubscriptionProvider.swift) 使用 OAuth beta 头和 `claude-code/...` User-Agent,并采用“陈旧证据优于空白”。
- [CodexBar](https://github.com/steipete/CodexBar/blob/171c2dce44d1e48cb1e9fab57c24df2a773fba2b/Sources/CodexBarCore/Providers/Claude/ClaudeOAuth/ClaudeOAuthUsageFetcher.swift) 动态构造 Claude Code User-Agent,按账号保存最近成功快照并展示 Fable 等 scoped 窗口。
- [ClaudeTimer](https://github.com/TimeWinder-dk/ClaudeTimer/blob/9b09a72d86b7f51241de7d61a2df71c59ef2294e/src/ClaudeTimer/Services/ClaudeUsageClient.cs) 优先解析现代 `limits` 数组,旧顶层字段只作兼容回退。

固定快照、平台证据和本机实测过程见 [`UPSTREAM-ARCHITECTURE-RESEARCH.md`](UPSTREAM-ARCHITECTURE-RESEARCH.md#2026-08-09-claude-额度读取生态复核)。

## 3. 端到端数据流

```text
GUI quota.get                         GUI providers.catalog / Worker ResumeEngine
     |                                                 |
     |                       ProviderCatalogService(ObserveAsync / CheckModelsAsync)
     |                         +-- ProviderRegistry:Claude 订阅行身份 = 账号指纹派生
     |                         +-- Models API(/v1/models)
     |                         +-- QuotaService.GetAsync(forceRefresh:true) --+
     v                                                                        |
QuotaService.GetAsync(forceRefresh) <-----------------------------------------+
       |
       +-- 5 分钟成功缓存 / 30 秒失败缓存
       +-- 同一把锁串行(single-flight)
       |
       v
ClaudeCredentialSource(只读 Claude Code 登录文件 + 全局配置 oauthAccount)
       |
       v
ClaudeOAuthUsageProbe
       |
       +-- 2xx 且有窗口
       |      +-- 账号指纹非空:SQLite IMMEDIATE 事务内 读取 -> 稀疏合并 -> 写回
       |      +-- 账号指纹为空:只作本次显示,不落库、不承接历史
       |
       +-- 失败 / 无凭据
              |
              v
        Unavailable(仅承接同账号未过期的历史读数)
       |
       v
ControlPlaneBridge 转成 JSON RPC;续跑侧由 QuotaResumeProbe.Evaluate 逐模型判定
       |
       v
GUI 额度磁贴、供应商与额度详情、队列等待原因;续跑逐项目准入
```

`QuotaService` 是 GUI 与续跑引擎共用的唯一额度入口:GUI 进程与 Worker 进程各持有一个实例,跨进程经 SQLite 合并(§8)。不得在 GUI、Worker 或通知侧再复制一套解析、缓存或合并逻辑。`ProviderCatalogService` 只在其上叠加 Models API 目录与账号配对,不另行解析额度。

## 4. OAuth 请求协议

### 4.1 端点与凭据

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <Claude Code access token>
Accept: application/json
anthropic-beta: oauth-2025-04-20
anthropic-version: 2023-06-01
User-Agent: claude-code/<本机版本>
```

- 凭据唯一来源是 Claude Code 自己的登录文件 `<配置根>\.credentials.json`,配置根为 `%CLAUDE_CONFIG_DIR%`,未设置时为 `%USERPROFILE%\.claude`(`ClaudeCredentialSource.CredentialsPathFor`)。以共享读写/删除方式只读打开,不妨碍 Claude Code 原子替换该文件。文件形状(2026-10-30 本机 Claude Code 2.1.300 录制):顶层只有 `claudeAiOauth` 与 `mcpOAuth`,**没有任何账号字段**。AI Resume 只解析下列字段,不解析 `refreshToken`:

| 字段 | 用途 |
| --- | --- |
| `claudeAiOauth.accessToken` | usage 请求的 Bearer;只在调用栈内流动 |
| `claudeAiOauth.expiresAt` | 访问令牌到期(epoch 毫秒);剩余不足 60 秒视同过期,不发请求 |
| `claudeAiOauth.refreshTokenExpiresAt` | 登录到期(epoch 毫秒);已到期判登录失效,3 天内提醒(§4.4) |
| `claudeAiOauth.subscriptionType` | 只用于显示订阅类型 |

- `ClaudeCredentialSource.Read()` 的判定顺序:登录文件在却读不了(被占用)或解析不了(多半正被 Claude Code 改写)→ `unreadable`(判断不了,不是未登录)。没有 accessToken 时,先看是否被 Claude Code 清空(accessToken 为空且 `expiresAt=0`)→ `login_expired`;再看是否命中 Windows 凭据管理器灰度 → `credential_store_moved`;其余(文件不存在、没有令牌)→ `no_credentials`。有 accessToken 时:缺 `expiresAt` → `no_credentials`(不是 Claude Code 写的或已损坏);`refreshTokenExpiresAt` 已到 → `login_expired`;访问令牌剩余不足 60 秒 → `token_expired`;否则可用。所有失败结果(包括没有 accessToken 的 `login_expired` / `credential_store_moved` / `no_credentials`)都带上账号指纹,供同账号历史读数承接(§7);账号核实不了时指纹为空串。
- **只读、绝不刷新、绝不写回。** refresh token 一次性使用,外部进程刷新一次,Claude Code 手里那份就作废,它下次刷新得到 invalid_grant 后会清空登录(§2)。访问令牌约 8 小时一换,由正在运行的 Claude Code(交互会话,或 Remote Control 桥提前 5 分钟)自行续期;登录本身(`refreshTokenExpiresAt`)是固定的约 30 天,刷新不会延长,只能由用户在终端运行 `claude` 并 `/login` 续期。
- 历史:v2.1(2026-10-12,ADR-0005 A1)曾把 AI Resume 自持的 `claude setup-token` 长期令牌作为第一来源,被 usage 端点拒绝时写 rejected 标记再回退 Claude Code 登录。实测 usage 端点不接受这类令牌,该来源连同 GUI「Claude · 凭据」与 `claude.token.*` RPC 已于 2026-10-30 随 ADR-0006 删除;`install` 激活成功后删除残留的 `state\secrets\claude-oauth-token.bin` 与 `claude-oauth-token.bin.rejected.json`(`InstallCommand.RetireLegacyClaudeToken`,删不掉只提示,不影响安装结果)。
- GUI RPC:`claude.login.status` 只回 `verdict` / `serverRejected` / `accessTokenExpiresAtUnix` / `loginExpiresAtUnix` / `subscriptionType` / `accountVerified` / `problem`,不回令牌、邮箱、组织或账号 UUID;页面永远读不回令牌。`serverRejected` 见 §6(usage 端点最近以 401 / 结构化 403 拒绝了本机令牌、且之后登录文件没再被改写)。`claude.token.*` 请求只会得到 `.error` 应答。
- 产品不读取 v2rayN 订阅、不修改系统代理或 Windows 网络设置;请求使用 .NET/Windows 当前网络栈。开发阶段查公开上游时按全局规则先直连,网络链路失败后才试 `127.0.0.1:10808` / `10809`。

### 4.2 User-Agent 解析

`ClaudeOAuthUsageProbe.ResolveClaudeCodeUserAgent` 按以下顺序取得本机 Claude Code 版本:

1. `%USERPROFILE%\.local\bin\claude.exe`。
2. `PATH` 各目录下的 `claude.exe`(只认 `.exe`,不解析 `claude.cmd` 一类脚本垫片)。
3. 取该文件的 Windows `FileVersionInfo.ProductVersion`,规范化为 `claude-code/<主>.<次>.<构建>`。
4. 无法识别时退回兼容基线 `claude-code/2.0.0`。

版本解析发生在后台请求路径。`ProviderCatalogService` 读取 Claude 订阅的 Models API 目录时复用同一个 User-Agent,并带 `anthropic-version` 与 OAuth beta 头。2026-08-09 目标机实际版本为 `2.1.185 (Claude Code)`,请求使用 `claude-code/2.1.185`;2026-10-30 本机 CLI 为 2.1.300。

### 4.3 账号指纹

持久化键与供应商身份不能直接使用 token,也不能让 token 轮换把同一账号误判为新账号:

```text
material    = "account:" + Trim(oauthAccount.organizationUuid) + "/" + Trim(oauthAccount.accountUuid ?? "")
fingerprint = HEX(SHA-256(UTF-8(material)))        // 64 位十六进制
```

- `oauthAccount` 来自 Claude Code 的全局配置,与 `claude auth status` 的 orgId 同源;登录文件本身没有账号字段。`organizationUuid` 必须能解析为 GUID,否则视为没有账号材料。指纹同时包含组织与账号 UUID,同组织不同席位的额度与历史不会串在一起;`accountUuid` 缺失时按空串计入。
- 全局配置定位与 Claude Code 一致(`ClaudeCredentialSource.GlobalConfigPathFor`):`<配置根>\.config.json` 是非空文件就用它,否则用 `%CLAUDE_CONFIG_DIR%`(未设置时 `%USERPROFILE%`)下的 `.claude.json`。`GlobalConfigPathsFor` 再加上 `<配置根>\.claude.json`——以 `CLAUDE_CONFIG_DIR=%USERPROFILE%\.claude` 运行的 Claude Code(如桌面端内嵌版本)写这一份,两份共用同一个登录文件;两份都给出账号而组织/账号不一致时判为未核实。
- 读法:`JsonDocument` 只按精确大小写取顶层 `oauthAccount`(该文件 `projects` 映射里有仅大小写不同的重复键,PowerShell `ConvertFrom-Json` 会因此失败);共享读写/删除;按(长度, 写入时间)缓存解析结果;文件正被改写而读不出时,沿用同一个 `ClaudeCredentialSource` 实例上一次成功的值。
- 数据库和返回值只保存指纹,不保存 identity material。账号指纹不同,历史窗口绝不互相承接。
- **核实不了账号就如实为空,绝不退回令牌指纹。** 指纹为空时:`QuotaService` 本次读数只作显示,不写入 `quota_snapshots`、不承接任何历史(§8);`ProviderRegistry` 的 Claude 订阅行 `Identity` 为空、`CredentialFailure=account_unverified`,布防被拒(「账号身份未核实」);已布防周期的取证返回 `identity-unverified`,只等待、不要求重新布防(§8.4);右栏「Claude 登录」显示「有效 · 账号未核实」。打开一次 Claude Code 通常即可让它写入账号信息。
- 交叉核对的范围:`QuotaService` 默认 OAuth 探针、`ClaudeLoginReminder` 与 `ProviderRegistry`(供应商身份、`claude.login.status`、续跑执行计划;构造时传入 `GlobalConfigPathsFor`)都读取并核对两份。两份配置分叉(都给出账号而组织/账号不一致)时供应商身份与额度快照指纹都为空:布防被拒(「账号身份未核实」),已布防周期按 `identity-unverified` 等待(§8.4)。
- 历史与升级影响:v2.1 及之前读登录文件顶层的 `organizationUuid`——该字段实际不存在(当时的测试夹具虚构了它)——于是一律回退 `SHA-256("token:" + accessToken)`,访问令牌每 8 小时轮换一次指纹就变一次:生产库同一账号积累了 28 个指纹,已布防周期永久停在「供应商或账号已改变」(ADR-0006 §1 根因 1a)。升级到 v2.2 后,旧布防里由旧指纹派生的 `ResumeTarget.ProviderIdentity` 必然对不上,队列显示「账号已变化 · 重新布防」,解除后重新布防一次即可;`quota_snapshots` 里旧指纹的行不迁移、不清理,也不再被读取。

### 4.4 登录判定与到期提醒

`ClaudeCredentialSource.Describe()` 给出不含实值的 `ClaudeCredentialStatus`(`Verdict`、`AccessTokenExpiresAtUtc`、`LoginExpiresAtUtc`、`SubscriptionType`、`AccountVerified`、`Problem`),控制面经 `claude.login.status` 显示在右栏「Claude 登录」块,`ClaudeLoginReminder` 据此提醒。判定 `ClaudeLoginVerdict` 按表中顺序取第一条成立的:

| 判定 | 条件 | 「Claude 登录」块 | 额度读取 |
| --- | --- | --- | --- |
| `Unverified` | 登录文件在,但这次读不了(被占用)或解析不了(多半正被 Claude Code 改写) | 「未核实」(不染红状态灯) | `unreadable`,只等待 |
| `LoginExpired` | 登录被 Claude Code 清空(accessToken 为空且 `expiresAt=0`) | 红「已过期」 | `login_expired` |
| `StoreMoved` | 登录文件不存在或没有 `claudeAiOauth`,且 `CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1` 或任一全局配置 `cachedGrowthBookFeatures.tengu_windows_credman=true` | 红「凭据位置变化」 | `credential_store_moved` |
| `LoggedOut` | 其余没有 accessToken 的情形(文件不存在、没有令牌),或有令牌但缺 `expiresAt` | 红「未登录」 | `no_credentials` |
| `LoginExpired` | `refreshTokenExpiresAt` 已到 | 红「已过期」 | `login_expired` |
| `TokenExpired` | 登录有效,访问令牌剩余不足 60 秒 | 琥珀「令牌待续期」 | `token_expired`,等 Claude Code 自行续期 |
| `LoginExpiring` | 访问令牌有效,登录剩余不足 3 天(`LoginExpiryWarning`,与 Claude Code 自己提示 `/login` 的阈值一致) | 琥珀「即将到期」 | 照常读取 |
| `Ok` | 其余 | 绿「有效」;账号未核实时琥珀「有效 · 账号未核实」 | 照常读取 |

- 访问令牌已过期而登录也将在 3 天内到期时,判定为 `TokenExpired`,`Problem` 两件事都说。账号未核实不单设判定,由 `AccountVerified=false` 与 `Problem` 表达。
- `StoreMoved` 不能说成「未登录」:灰度一旦生效,S4U 会话 0 的 Worker 也读不了用户的凭据管理器,额度与续跑只能等待人工处理。
- **到期提醒。** `ClaudeLoginReminder`(Worker 后台服务)每 30 分钟检查一次:登录剩余 ≤3 天、≤2 天、≤1 天、≤12 小时各写一条 `login-expiring`,到期后写一条 `login-expired`;每次只写当前所处的最低一档,Worker 不在时错过的更早档位不补发;同一到期时间、同一阈值只写一次(进程内去重;事件 ID 由 `ai-resume|<类型>|login|<到期秒>|<阈值>` 哈希得出,投递端再按事件 ID 去重)。没设置通知收件人(`ProductConfig.NotifyRecipientOpenId` 为空)时不写,免得设置后补发一串过期提醒。事件进完成通知队列(`source=ai-resume`、`kind=resume`),与钩子事件走同一投递链路,见 [`COMPLETION-NOTIFICATIONS.md`](COMPLETION-NOTIFICATIONS.md)。
- 边界:登录被 Claude Code 清空后 `Describe()` 不再给出登录到期时间(判定 `LoginExpired`)。这正是最需要 `/login` 的终态,提醒服务每个本地日写一条 `login-expired`(去重键 `login|cleared|<本地日期>`),直到登录恢复;`LoggedOut` 也可能只是一次读文件失败,不据此提醒。写进队列失败时撤回去重键,下一次检查重写。此时「Claude 登录」块显示「已过期」。提醒与 `Problem` 里的时间按本机时区格式化、不带时区标注。AI Resume 只提醒、不代刷。

## 5. 响应解析契约

### 5.1 现代与旧字段的优先级

现代响应的 `limits` 是主协议,旧顶层 `five_hour` / `seven_day` 只作逐字段兼容:

| 现代 `kind` | 内部窗口名 | 窗口长度 | 旧字段回退 |
| --- | --- | ---: | --- |
| `session` | `five_hour` | 5 小时 | `five_hour` |
| `weekly_all` | `seven_day` | 7 天 | `seven_day` |
| `weekly_scoped` | `weekly_scoped:<display_name>` | 7 天 | 无 |

同一个主窗口按字段分别取值:

```text
UsedPercent = modern.percent ?? legacy.utilization
ResetAtUnix = modern.resets_at ?? legacy.resets_at
```

不能因为 legacy 对象存在就整体盖掉 modern 对象。现代 percent 和 reset 可以分别出现;两边都缺的字段才是 `null`。

### 5.2 字段语义

- `UsedPercent:int?`:四舍五入后钳制到 `0..100`;`null` 表示服务端未报告,不是 0。
- `ResetAtUnix:long?`:支持 ISO 8601 字符串和 epoch 秒。
- `ResetAfterSeconds`:由 `ResetAtUnix - now` 计算,最小为 0。
- `DerivedWindowStart`:由 `ResetAtUnix - WindowSeconds` 推导,不是服务端原始字段,UI 必须标“推导”。
- `Status`:窗口自身百分比达到 100 时为 `blocked`;百分比低于 100 或本次探测整体成功时可为 `allowed`;只有 reset 的失败/全局限流降级观测保持未知,不自行判满或判可用。
- `LimitReached`:任一主窗口或 `limits` 条目的 percent 达到 100 即为 true。
- `UnattributedLimitReached`:provider 已明确限流,但结论无法归因到已知主/scoped 窗口时为 true。它让展示层能区分“仅 scoped 已满”和“scoped 之外还有未知限流”;续跑门禁对任何模型都把它当作阻断(§8.4)。该事实没有可绑定的 reset 窗口,跨实例合并时按 `CapturedAt` 排序:较旧观测不能清除较新的未归因限流,只有不早于现有快照的新观测才有权解除。
- `severity=critical` 不是限流判据。实测未到 100% 也可能为 critical,它只能表示接近上限。

### 5.3 Fable 与其它 scoped 限额

`weekly_scoped.scope.model.display_name` 是用户需要看到的具体模型名。示例:

```json
{
  "kind": "weekly_scoped",
  "percent": 100,
  "resets_at": "2026-08-10T14:00:00Z",
  "scope": { "model": { "display_name": "Fable" } }
}
```

映射为 `weekly_scoped:Fable`。同一次响应里的每条逻辑 `weekly_scoped` 都要保留并分别显示,不能只取第一条;否则后一条模型已满时会被隐藏。内部身份不是显示名或数组序号,而是完整 scope JSON 经过属性名排序后的 SHA-256;同名 scope 即使响应顺序互换也不会串窗。完全相同的重复 scope 合并为一条:同 reset 取更高百分比,不同 reset 取更新代次。即使 percent 缺失,只要 scoped 条目有 reset,也保留窗口名并显示“未报告”;不得把它删除或显示成 `0%`。模型名中的冒号会被移除,避免破坏内部窗口名分隔;取不到显示名时窗口名为 `weekly_scoped:未命名模型`;同一次响应里显示名相同而身份不同的 scope 以 `#<身份哈希末 6 位>` 后缀区分。

**完备性假设(2026-10-30 起)。** 一次成功的实时 OAuth 响应,其 `limits` 视为该账号此刻全部按模型周窗口的完整列表:目标模型没有专属 scoped 窗口时,由 5H/7D 主窗口决定(§8.4)。依据有二:Claude Code 2.1.300 内嵌的 usage schema 对按模型窗口的说明是“端点实际作答时,空数组表示该账号没有任何按模型周窗口,缓存答复不适用”(调研对二进制的静态核对);本账号实测只出现过 Fable 的 scoped 窗口,Opus 从未出现。v2.1 及之前要求本次响应必须含与目标模型族明确匹配的 scoped 条目,缺失即 Unknown——这让没有专属窗口的 Opus 永远放行不了(ADR-0006 §1 根因 1b),已废止。

已知边界:同一份上游 schema 另有顶层 `seven_day_opus` / `seven_day_sonnet` 等字段(同一静态核对,尚未录制真实响应);AI Resume 目前只映射 `five_hour` / `seven_day` / `limits`,其余顶层字段被忽略。若日后发现服务端只在这些字段里报告满额,需要先录制真实响应再扩展解析。

## 6. 失败分类与显式 CLI 诊断

OAuth 探测不把 token、响应正文或异常正文回传。自建的 `HttpClient` 超时 15 秒、不跟随重定向。失败分类如下(界面说明来自 `QuotaService.DescribeOAuthFailure`):

| 场景 | `FailureReason` | 界面说明与后续 |
| --- | --- | --- |
| 未登录:登录文件不存在、无法读取或解析、没有 accessToken,或缺 `expiresAt` | `no_credentials` | 「Claude Code 未登录或登录已到期:请在终端运行 claude 并 /login」;不发请求 |
| 登录已到期:`refreshTokenExpiresAt` 已到,或被 Claude Code 清空 | `login_expired` | 同上;不发请求 |
| 登录改存 Windows 凭据管理器 | `credential_store_moved` | 「Claude Code 已改用 Windows 凭据管理器保存登录,AI Resume 读不到额度凭据」;不发请求 |
| 访问令牌剩余小于 60 秒 | `token_expired` | 「Claude Code 访问令牌已过期:打开一次 Claude Code 即自动续期…」;不发请求 |
| HTTP 401 | `token_rejected_401` | 「Claude Code 登录被服务端拒绝:请在终端运行 claude 并 /login」 |
| HTTP 403 且正文是 Anthropic 结构化错误(`type=error` 或含 `error` 对象) | `token_rejected_403` | 同上 |
| HTTP 403 其它正文(CDN/网关/代理拦截) | `http_403_gateway` | 「Claude 额度接口被网络或网关拦截(HTTP 403),稍后自动重试」;不提示 `/login` |
| HTTP 408 / 504 | `gateway_timeout` | 显示未核实,稍后自动重试 |
| HTTP 429 | `http_429` | 显示未核实,稍后自动重试 |
| 其它非 2xx | `http_<status>` | 显示未核实 |
| DNS/TCP/TLS/reset/HttpClient 自身超时 | `failed_local` | 显示未核实,稍后自动重试 |
| 2xx 但 JSON 不可解析 | `malformed_response` | 显示未核实 |
| 调用方明确取消 | 抛出取消 | 终止本轮,不得伪装成失败后继续 |

- 判断 403 形状时最多读 8 KB 正文,只用于分类,不保存、不记日志。
- 能读到账号时,失败结果仍带账号指纹(例如 `token_expired`、`http_429`),GUI 因而可以按 §7 承接同账号同周期的最近读数(琥珀)。续跑侧在发请求前就把凭据问题判成 `credentials-unavailable` / `identity-unverified`(§8.4)。
- 承接最近读数时失败原因不能被吞掉:额度磁贴的悬停说明写「本次读取失败:<原因>(显示的是最近一次服务端读数)」,页头写「部分更新」。
- `token_rejected_401` / `token_rejected_403` 另记在 `QuotaService.LastRejection`(之后任何一次成功清掉;网络类失败不改写它)。`claude.login.status` 据此给出 `serverRejected`:服务端刚拒绝过本机令牌、且之后登录文件没再被改写时,登录卡片显示红色「被服务端拒绝 · 需 /login」,而不是只凭本地时间戳的绿色「有效」或「打开一次 Claude Code 即可续期」(改密码、被移出组织、在别处登出时,本地文件都还是新的)。

生产刷新不启动推理,OAuth 失败时返回未核实,并仅按原规则承接同账号未过期读数。`QuotaService` 只有在调用方显式注入 CLI 探针时(诊断/隔离测试)才允许 CLI 降级,生产构造不降级。以下 CLI 能力仅供显式诊断/兼容注入,不是 GUI 或 Worker 的默认降级。

CLI 诊断由 [`ClaudeCodeProbe`](../csharp/src/AiResume.Worker/Probes/ClaudeCodeProbe.cs) 执行:

```text
claude -p ready --model haiku --max-turns 1 --output-format stream-json --verbose
```

- 工作目录固定为 AI Resume 状态根,不污染真实项目发现。
- 子进程带 `AI_RESUME_INTERNAL_RUN=1`,不得产生完成通知。
- 只解析结构化 `rate_limit_event`;输出写临时文件,解析后删除,不落正式日志。
- CLI 常只给 reset、状态或部分窗口,尤其可能没有百分比与 `weekly_scoped`;因此它是降级观测,不是删除 OAuth 历史证据的理由。
- CLI 探测若整体失败,即使解析到部分窗口也只能作为失败详情与连续性证据;`Allowed=false`,GUI 不得显示绿色正常。
- OAuth 与 CLI 都没有可用窗口时返回 `Unavailable`,GUI 显示真实原因,不得回退成“空闲”。

## 7. 稀疏观测状态机

### 7.1 合并身份

一条历史值可被承接,必须同时满足:

```text
同一个账号指纹(§4.3;指纹为空时不承接任何历史)
+ 主窗口同一个 window name / scoped 同一个 canonical scope Identity
+ 同一个 resetAtUnix 代次
+ 历史 resetAtUnix > 当前时间
```

这里的 `resetAtUnix` 是窗口代次。主窗口名相同但 reset 改变,就是新周期;旧百分比不能跨周期泄漏。scoped 的显示名不构成身份,规范化 scope 哈希才构成身份。schema v5 早期无 Identity 的 scoped 行只在本次完全没有稳定 scoped 观测时作为历史证据承接;一旦出现同名稳定 scoped,旧行因无法证明归属而丢弃,不能按名字猜给新 scope。

### 7.2 合并规则

1. 当前明确字段通常优先;较旧(`CapturedAt` 更早、晚提交)的观测与非 OAuth 观测在同一 reset 代次里取已用百分比的较大值,较晚提交的 99% 不得覆盖已落库的 100%;reset 代次冲突时按原始 `CapturedAt` 判新旧,旧观测不得倒写新代次。
   **较新的实时 OAuth 观测带着明确百分比时以它为准,不再取大**(2026-10-30 交叉审查后修正):同一 reset 代次里用量也会合法下降——中途升级套餐、服务端提前重置。v2.2 起快照按稳定的账号指纹保存,旧的 100% 若一直取大会卡到周窗口重置(最长 7 天,而 v2.1 的令牌指纹约 8 小时就换一次,这个问题被掩盖了)。这样的观测只缺 resetAt 时,才向同代旧值借重置时间;但它比旧值低又没给 resetAt(`{utilization:0, resets_at:null}` 往往是新窗口还没开始),证明不了同代,不借、也不算承接。
2. 当前窗口存在但 percent 缺失,且 reset 与旧值相同,可承接旧 percent。
3. 当前窗口完全缺失,旧窗口仍未 reset 时可整体承接。
4. 当前 reset 缺失时可承接旧 reset;但较旧观测的 percent-only 窗口无法证明属于较新的 reset 代次,必须完整保留 prior,不得吸收其 percent/status。当前给了不同 reset 时不得承接旧 percent。
5. 承接窗口重新计算 `ResetAfterSeconds`,并设置 `CarriedForward=true`。
6. 承接的 blocked/100% 仍参与 `LimitReached`,避免错误启动任务。
7. 只有历史值、没有本次实时窗口时,`Allowed` 必须为 false;历史证据不能冒充实时成功。
8. `CapturedAt` 单调不回退,避免旧进程覆盖新进程时间线。
9. 历史快照结构损坏时放弃历史,保留本次观测。

### 7.3 失效条件

以下任一条件立即停止承接:

- 账号指纹变化。
- 同名窗口返回新的 `resetAtUnix`。
- 历史 `resetAtUnix` 已到或已过。
- 历史快照结构无效。

服务端“没返回某条”不是失效条件;只有可证明的账号、代次或时间边界才是。v2.1 及之前账号指纹随访问令牌每 8 小时漂移一次,相当于每 8 小时触发一次“账号指纹变化”,承接被频繁切断;v2.2 起令牌轮换不再改变指纹。

## 8. 存储、并发与缓存

### 8.1 SQLite `quota_snapshots`

权威快照位于 `%LOCALAPPDATA%\AI Resume\state\runs.db` 的 `quota_snapshots`(表形状自 schema v5 起未变;当前 schema 版本为 6,v6 只为 `product_state` 预置默认行):

| 列 | 含义 |
| --- | --- |
| `provider` | 当前为 `claudecode` |
| `credential_fingerprint` | 64 位不可逆账号指纹(§4.3) |
| `captured_at` | 快照观察时间 |
| `snapshot_json` | 只含窗口、百分比、状态与 reset 的 `UsageSnapshot` |
| `updated_at` | 数据库更新时间 |

主键是 `(provider, credential_fingerprint)`。v4 旧表只有 provider、JSON、更新时间,无法证明账号归属;迁移到 v5 时必须丢弃旧额度行,不能把它猜给当前账号。v2.2 之前以令牌派生指纹写入的行留在表里,不迁移、不清理,也不会再被读取(§4.3)。

### 8.2 原子更新

多开 GUI、GUI 与 Worker 同时刷新时,锁外 `Load -> Merge -> Save` 会发生丢失更新。现役边界是:

```text
BEGIN IMMEDIATE
  SELECT 当前账号快照
  MergeSparseObservation(本次观测, 当前快照)
  INSERT ... ON CONFLICT DO UPDATE
COMMIT
```

读取、合并、写回必须在同一个 SQLite `IMMEDIATE` 事务内。这样后到事务必然看到先提交事务的窗口,不会用基于旧基线生成的 carried 值覆盖新实值。

SQLite 在后台额度请求时才迁移/打开,不阻塞 WPF 首帧。存储失败不应阻断实时额度显示;UI 继续显示本次快照,脱敏的 `StorageWarning` 随 `quota.get` 返回,并作为「供应商与额度详情」里 Claude 订阅行的问题说明(主页磁贴不显示它,§9)。账号指纹为空时不进入该事务(`QuotaService` 直接跳过,`QuotaSnapshotStore.TryUpdate` 也拒绝空指纹),本次读数只作显示,`StorageWarning` 随之置位,任何以它为前提的续跑放行都不会成立。

### 8.3 进程内缓存

- 有窗口的成功快照缓存 5 分钟。
- 无窗口、失败或只有部分失败证据的快照只缓存 30 秒;只有成功且无 `UnavailableReason` 的有数据快照缓存 5 分钟。
- 手动或定时刷新传 `force=true`,绕过额度缓存;续跑取证与布防总是强刷。控制面的 `providers.catalog` 在 `quota.get` 返回之后才读,其 Claude 订阅行用 `GetRecentAsync(1 分钟)`:先等在途的那次刷新完成,再复用 1 分钟内的读数(成功或失败都算),过期才强刷——一轮界面刷新只请求一次 usage 端点,不额外吃 429。
- 同进程请求经同一把锁串行(single-flight):非强制请求等锁后直接复用刚写入的快照,强制请求各自重新探测。
- 降级前重新读取 SQLite,让另一个 GUI/Worker 进程刚提交的快照立即可见。

### 8.4 自动续跑放行契约

生产注册入口 `AddQuotaResumeAdmission()`(`QuotaResumeServiceCollectionExtensions`)注册 `QuotaService`、`QuotaResumeProbe`、`ProviderRegistry`、`ResumeSessionCatalog`(同时作为 `IProjectSessionInspector`)与 `ProviderCatalogService`,并把 `IClaudeUsageProbe` 解析为 `ProviderResumeProbe`。Claude 订阅继续复用本文的持久化、稀疏合并与 scoped 门禁;其它供应商见 [ADR-0004](adr/0004-provider-bound-resume.md) 与 ADR-0005 B1。

**一次取证,逐项目结论。** `ProviderResumeProbe.ProbeAsync(config)` 要求 `ResumeTarget` 有效且 `ModelId` 等于 `ResumeModel`(否则 `target-unbound`);先读本地会话事实,再按每个项目的有效模型(`ResumeTarget.EffectiveModel`:项目自己的 `ModelId`,旧配置为空时沿用 `ResumeTarget.ModelId`)去重后调用一次 `ProviderCatalogService.CheckModelsAsync`。同账号同模型的项目共用这一份结论,不为每个项目重复请求。

**身份与凭据先于取数。** `CheckModelsAsync` 在发任何请求前按序核对:

| 情形 | 结论 | 后果 |
| --- | --- | --- |
| 目标无效、供应商行不存在或 agent 不同 | `provider-changed` | 需重新布防 |
| Claude 订阅行身份为空(全局配置里没有一致的 `oauthAccount`:账号未核实;与访问令牌在不在无关) | `identity-unverified`,`Detail` 为凭据原因码(多为 `account_unverified`) | 只等待 |
| 身份与布防时的 `ProviderIdentity` 不同 | `provider-changed` | 需重新布防,不会用新账号自动续跑 |
| 身份一致但凭据暂不可用:没有访问令牌(`no_credentials`、`login_expired`(含被清空的登录)、`credential_store_moved`),或 `token_expired`、登录到期、缺到期时间、登录文件这次读不出(`unreadable`) | `credentials-unavailable`,`Detail` 为原因码 | 只等待 |

随后做一次 `ObserveAsync`:Models API 目录 + `QuotaService.GetAsync(forceRefresh:true)`。额度快照必须是 OAuth 证据,且 `SHA-256("claude-subscription:" + 快照账号指纹)` 等于连接身份,否则视为「Claude 订阅额度未核实」(结论 `unknown`)。逐个模型:不在已核实目录里 → `model-unverified`;否则交给 `QuotaResumeProbe.Evaluate(snapshot, model, StorageWarning)`。

**按模型族评估(`QuotaResumeProbe.Evaluate`)。**

```text
目标模型族 = ClaudeModelFamilies.TryNormalizeConfiguredModel(model)
             只接受 fable / opus / sonnet / haiku 短别名,或只含一个模型族 token 的 claude-... 官方 ID

相关窗口 = 非 scoped 窗口(five_hour、seven_day)
         + 与目标同模型族的 weekly_scoped(任何版本都算,宁可多等)
         + 认不出模型族的 weekly_scoped(不知道它管不管目标,就当它管)

Limited = EvidenceSource == OAuth
        AND 目标模型族可识别
        AND (任一相关窗口 blocked:百分比 >= 100 或 status=blocked,承接值也算
             OR 未归因限流:UnattributedLimitReached,或 LimitReached 却没有任何窗口 blocked)

Ready   = NOT Limited,且以下各项依次成立(第一项不成立即为 Detail):
            EvidenceSource == OAuth                           否则 evidence-not-oauth
            HasData 且有 bucket                               否则 no-data
            UnavailableReason == null                         否则 unavailable
            StorageWarning == null                            否则 storage
            目标模型族可识别                                    否则 model-invalid
            five_hour 是本次实时读数且 0 <= % < 100             否则 five_hour:missing / :carried / :no-percent
            seven_day 同上                                    否则 seven_day:...
            每个相关 scoped:承接值不挡;本次实时值必须有百分比    否则 <窗口名>:no-percent

其它 = Unknown -> waiting
```

- `Detail` 只含窗口名与状态码:Limited 时为 `<窗口名>:blocked` 逗号串(只有未归因限流时为 `unattributed`),否则为上面的未就绪码。`BlockingResetUtc` 取阻塞相关窗口里最晚的 reset(要等全部挡住它的窗口都重置),只用于展示「预计 HH:mm 起续跑」与提醒,不是放行条件。
- 示例(测试按 2026-10-30 真机形状构造):5H 0%、7D 54%、`weekly_scoped:Fable` 100% 时,Opus 项目 `ok`,Fable 项目 `limited`,`Detail=weekly_scoped:Fable:blocked`。同族不同版本的 scoped(如 `Opus 5`)满额挡住 `claude-opus-5-5`;认不出族的 scoped(如 `Mythic`)满额挡住所有模型。
- **承接未满不阻塞只适用于相关 scoped 窗口**:本次成功响应省略了某个相关 scoped,而同账号同周期的历史读数未满时,它不构成约束(§5.3 完备性假设);承接值满额仍判 Limited。5H/7D 主窗口没有这个豁免——主窗口是承接值或缺百分比时一律 Unknown。
- `EvidenceSource` 随快照持久化。升级前 JSON 没有该字段时默认 `Unknown`;CLI 观测即使与历史 OAuth 窗口合并也仍是 `Cli`,不得被历史数据升格。CLI/Haiku 限流和其它模型族的 scoped 满额都不能替目标模型建立限流事实。未到 reset 的目标 carried 100% 继续阻塞;reset 到点只允许下一次强刷,绝不自动视为恢复。
- 历史:v2.1 及之前按“整桶”放行——每个 bucket `Allowed` 且未 `LimitReached`、所有窗口(含其它模型族的 scoped)都是本次实时读数且未满,并且必须存在与目标匹配的本次 scoped 窗口;带版本的 scope 只能证明同版本(`ClaudeModelFamilies.ScopeMatchesModel`,现已无生产调用方)。结果是 Fable 满额连带挡住 Opus,而没有专属窗口的 Opus 永远放行不了(ADR-0006 §1 根因 1b)。

**逐项目准入与节奏。** `ProviderResumeProbe` 把每个项目的额度结论写进 `ProjectAdmission`:`Quota` 取 `ok` / `limited` / `unknown` / `provider-changed` / `identity-unverified` / `credentials-unavailable` / `model-unverified`,`QuotaDetail` 与 `QuotaResetUtc` 分别取 `Detail` 与 `BlockingResetUtc`;只有 `Quota=ok`、占用为 idle(或不适用)且会话证据可读的项目才 `Admissible`(义务与占用两维见 ADR-0006 §2.C)。每次状态机探测(`CheckerCycle`:周期看到过限流后 4 分钟一拍,否则按 `ProbeIntervalMinutes`,默认 15 分钟)都强刷,绕过 GUI 的 5 分钟成功缓存。续跑一轮内按队列顺序串行:首个项目复用刚触发本轮的证据;一旦有项目真正启动(或结果未知),下一个项目启动前重新取证——同模型的项目共用额度,前一个可能刚好把它用完;没能启动(会话刚被打开、令牌不足 45 分钟、执行条件未核实)的项目只记 `not-started:<原因>` 并跳过,不消耗证据、不锁周期。

**提醒。** `ResumeEngine` 对待续跑项目:额度为 `credentials-unavailable` 或 `identity-unverified` 时写一条 `credentials-unavailable` 续跑提醒(原因码在事件 `detail` 里,去重键含原因:同一周期同一原因每 4 小时最多一条,原因变了重新提醒;文案按原因给处理办法——`token_expired` 打开一次 Claude Code 或保持手机远程连接即可续期,`login_expired` / `no_credentials` 在终端 `/login`,`credential_store_moved` 说明 AI Resume 读不到 Windows 凭据管理器,`account_unverified` 打开一次 Claude Code 写入账号信息,其余引导到控制面「Claude 登录」);`provider-changed` 每周期提醒一次。提醒与登录到期提醒走同一完成通知队列。

**布防与执行身份。** 布防时 `ResumeBindingService` 从供应商实时目录逐项目选模型(手选 > 会话最近实际用的模型 > 目标默认),写入 `ProjectRef.ModelId` / `ModelName` / `ModelSource` 与项目原生会话 UUID;账号未核实时不能布防,目录更新不改变当前周期,旧配置没有项目模型时沿用 `ResumeTarget.ModelId`。`ResumeTarget.Identity` 把供应商/账号/默认模型与每个项目的(路径, 会话 UUID, 有效模型)一起带到每个异步与 spawn 门禁,任一项变化都丢弃在途证据并重新取证。实际执行用精确 `--resume <UUID> --model <项目有效模型>`,凭据只走子进程环境(§8.5),不通过命令行或持久配置。运行中已出现副作用的失败周期继续锁住,额度恢复不解除重放阻断。

runner 在最终门禁后先把精确 `RunId`(连同本次中断 uuid)预登记到 `product_state`,再交给 `IProcessSupervisor`。只有 supervisor 以取消异常明确保证尚未登记、尚未 spawn 时,引擎才按同一 cycle、项目、`running` 状态与精确 `RunId` 撤销这条预登记并回到 waiting(本次中断 uuid 同时恢复为未消费);任一字段不匹配、普通启动失败或启动结果不确定都继续 fail-closed。

### 8.5 续跑子进程的令牌门槛

- 续跑子进程(经受监督的 `internal-agent-run`)以环境变量 `CLAUDE_CODE_OAUTH_TOKEN` 拿到启动那一刻 Claude Code 的访问令牌,同时 `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` 置空、`ANTHROPIC_BASE_URL=https://api.anthropic.com`。环境里没有 refresh token,子进程无法自行续期;令牌只走子进程环境,不进命令行、日志或持久配置。
- 因此 `ProviderExecutionPlan.Create`(启动用途)要求访问令牌剩余寿命不少于 `ProviderExecutionPlan.MinimumTokenLifetime`(45 分钟);取不到到期时间或不足时抛 `ResumeDeferredException("token-expiring")`:本次不启动、不产生 RunId、不锁周期,项目记 `not-started:token-expiring`(队列「等登录令牌续期」),下一轮重新评估。布防(`ExecutionPurpose.Arm`)不检查这道门槛。
- 已知缺口:45 分钟只保证启动时充足,运行时间超过令牌剩余寿命的续跑可能中途 401(ADR-0006 §5)。夜间无人值守时,保持一个 Claude Code 会话或 Remote Control 在线,访问令牌才会被及时续期。

## 9. GUI 呈现契约

v2.2 起额度相关界面由四个只读 RPC 驱动,视图模型是 `index.html` 里 `<script id="vm">` 的纯函数(`AR.quotaTiles`、`AR.loginModel` 等):

| 区域 | 数据 | 说明 |
| --- | --- | --- |
| 顶部 CRT 额度磁贴 | `quota.get`(`QuotaService` 快照)、`providers.catalog`(DeepSeek / Codex)、`arm.get`(角标) | 含义固定:Claude · 5 小时 / Claude · 7 天 / Claude · 按模型 / DeepSeek · 余额;「视图 › 显示 Codex」打开时加第五块 |
| 队列行「续跑模型 / 状态」 | `arm.get.projects[]` | 逐项目等待原因,见下 |
| 右栏「Claude 登录」 | `claude.login.status` | 判定与配色见 §4.4 |
| 铭牌状态灯 | `arm.get` + `claude.login.status` | 已布防时登录为已过期/未登录/凭据位置变化即标红「额度无法核实」;登录即将到期且没有别的等待时琥珀「监视中 · 登录即将到期」;引擎与进程门禁的状态优先 |
| 「视图 › 供应商与额度详情…」 | `providers.catalog` | 各供应商行与服务端返回的窗口 |

历史(v2.1 及之前):左侧 5 小时屏先用本地 `~/.claude/projects/**/*.jsonl` 计算当前会话块(`ClaudeUsageBlocks` / `quota.local` RPC),服务端返回 `five_hour` 后再覆盖;顶部卡片含义随供应商下拉改变;侧栏有「Claude · 凭据」两行。本地会话块因无调用方已于 2026-10-30 删除(ADR-0006 §2.H),其余随 v2.2 重排一并取消。

**额度磁贴(`AR.quotaTiles`)**:

| 数据状态 | 磁贴 | 读数与光柱 | 底行与角标 |
| --- | --- | --- | --- |
| 窗口 `status=blocked` 或百分比 ≥ 100 | 红 | 「N% 已用尽」(缺百分比时「未报告 已用尽」) | 有 reset 时「<时间> 重置 · 剩 <时长>」 |
| 承接值且未满 | 琥珀 | 数值 + 琥珀光柱,不播放扫描 | 角标「最近读数」 |
| 承接值且已满 | 红,仍按限流处理 | 同第一行 | 角标「最近读数」 |
| 百分比未报告 | 暗 | 「未报告」;光柱占满轨道承载未知扫描,不是 100%;系统减少动态效果时静止 | — |
| 实时读数 ≥ 90% | 琥珀字 | 「N% 已用」 | — |
| 其它实时读数 | 绿 | 「N% 已用」,光柱宽度 = 已用百分比 | — |
| reset 缺失 | — | — | 「重置时间未报告」 |
| 本次快照里没有该窗口 | 暗「未核实」 | 光柱为空 | 「服务端本次未返回此窗口」 |
| 请求失败,或 `hasData=false` 且没有任何窗口 | 三块 Claude 磁贴暗「未核实」 | 光柱为空 | 失败原因(`unavailableReason`) |
| 按模型:没有任何 `weekly_scoped` | 暗「—」 | 光柱为空 | 「服务端未返回按模型额度」 |

- 按模型磁贴取已满优先、其次已用最高的一条 scoped,标题为「Claude · <模型名>」(首字母大写);其余 scoped 的读数放进悬停说明。已布防且有项目因这个窗口在等(`reason=quota:limited` 且 `detail` 含该窗口)时,角标为「N 项在等」,把磁贴和队列行连起来。
- 每块磁贴只按自己窗口的 status/百分比上色:按模型满额不得染红 5 小时/7 天磁贴;只有 bucket 级(未归因)限流时也不得给 5H/7D 磁贴贴「已用尽」。
- 已知百分比的光柱使用具名 `role="meter"`(带 `aria-valuenow`);未知读数使用 `role="status"`,不冒充进度条、不设 `aria-valuenow`,也不对应 0% 或 100%。
- 四(五)块 CRT 等高,固定四行网格;窗口高度不超过 640px 时只保留标题与大读数。光柱表示额度已用百分比,不表示时间流逝;v2.2 不再画时间刻度。
- `quota.get` 仍回 `allowed`、`limitReached`、`unattributedLimitReached`、`globalHasCurrentData`、`globalHasCarried`、`globalLimitReached`(`ControlPlaneBridge.ClassifyGlobalQuota`)与 `storageWarning`,但 v2.2 页面不渲染它们;未归因限流在「供应商与额度详情」里表现为「已限额」,在队列行表现为「等额度」(悬停「额度已用尽」)。

**供应商与额度详情。** Claude 订阅行:`limited`(任一非承接窗口满额或 blocked,含 scoped;或有未归因限流)→「已限额」;`quotaKnown`(全部窗口都是本次实时读数且有百分比)且没有 `problem` →「已核实」;其余「未核实」。`problem` 取 `QuotaService.StorageWarning`,其次 `UnavailableReason`;凭据或账号未核实时这一行直接显示凭据问题,不发额度请求。窗口逐条列出,承接值标「(最近读数)」;悬停给出凭据来源标签(`claude-oauth:claude-cli`)。这一行是供应商级聚合,scoped 满额会让它显示「已限额」,与磁贴的分层不矛盾。「已核实」只来自本次真实请求的证据,模型可列不等于额度可用。

**队列行的额度原因。** `quota:limited` →「等额度」,悬停「<窗口>已用尽」(`five_hour` → 5 小时额度、`seven_day` → 7 天额度、`weekly_scoped:X` → X 周额度);`resetAtUnix`(取 `BlockingResetUtc`,缺时取会话中断记录里的 reset)仍在未来时,短句后补「 · <时间>」,悬停补「预计 <时间> 起续跑」。`quota:credentials-unavailable` / `quota:identity-unverified` 按 `detail` 原因码区分:`token_expired` →「等登录令牌续期」,`login_expired` / `no_credentials` →「Claude 登录失效 · 需 /login」,`credential_store_moved` →「凭据位置变化」,`unreadable` →「登录状态未核实」,其余(含 `account_unverified`)→「账号身份未核实」;Codex 周期统一为「Codex 登录未核实」(提示 `codex login` 或检查 `~/.codex/auth.json`);`quota:unknown` 且 `detail=model-invalid` →「模型额度无法核实 · 重新布防」(红);`quota:provider-changed` →「账号已变化 · 重新布防」(红);`quota:model-unverified` →「模型不在当前目录」;`not-started:token-expiring` →「等登录令牌续期」(悬停「访问令牌剩余不足 45 分钟…」);其它 `quota:*` →「额度未核实」。

**刷新节奏。** 打开窗口时 `quota.get` 不强刷(可能命中 5 分钟成功缓存);F5 与「工具 › 刷新额度与状态」强刷;之后按 `QuotaRefreshMinutes`(默认 15 分钟)定时强刷。`providers.catalog` 在 `quota.get` 返回之后才读,Claude 订阅行复用这一轮 1 分钟内的读数,不再额外强刷;磁贴在 `quota.get` 返回时就绘制,不等供应商目录;`claude.login.status` 在额度请求结束后再读一次,`serverRejected` 反映这一轮的结果。v2.2 页面不再在 reset 到点时额外强刷。已打开的窗口不会热更新,部署后要关掉旧窗口再打开。

## 10. 回归测试矩阵

| 层 | 必须覆盖的行为 | 现役测试 |
| --- | --- | --- |
| 凭据来源与登录判定 | 只读 Claude Code 登录、请求只用它且不回写;未登录/缺 `expiresAt`/损坏按未登录;被清空判登录失效;凭据管理器灰度判 `StoreMoved`;令牌过期、登录将到期、已到期,两件事同时成立都说 | `ClaudeCredentialSourceTests` |
| 账号指纹 | `oauthAccount` 指纹在令牌轮换下不变、换组织或账号即变;读不到账号为空而不是令牌指纹;两份全局配置分叉判未核实;全局配置半写时沿用上次值 | `ClaudeCredentialSourceTests`,`ClaudeOAuthUsageProbeTests` |
| OAuth 请求 | 五个协议头、token 只读、到期短路、401/结构化 403/裸 403 分类、429 保留账号指纹、token 不泄露、取消传播 | `ClaudeOAuthUsageProbeTests`,`ClaudeCredentialSourceTests` |
| 响应解析 | modern-only、modern 逐字段优先 legacy、空对象、多条/同名重排 scoped、缺 percent、ISO/epoch reset、100% 限流、scope 模型名提取 | `ClaudeOAuthUsageProbeTests`,`ScopeModelTests` |
| CLI 映射 | `rate_limit_event`、无窗口、部分失败不 Allowed、状态/错误分类、未报告不当 0 | `ClaudeProbeTests`,`UsageSnapshotMapperTests` |
| 稀疏合并 | 同 reset 承接与百分比单调、旧 reset 晚提交拒绝、未归因限流按快照时间单调、换代/到期清除、纯历史不冒充 Allowed、scoped 稳定身份 | `QuotaServiceTests` |
| 账号隔离 | 同账号跨实例可见、不同指纹不承接 | `QuotaServiceTests` |
| SQLite | 单调迁移到当前 schema、真实 v4 升级 v5、无账号身份旧行丢弃、旧代次晚提交拒绝、损坏容错、`EvidenceSource` 往返、原子并发更新 | `QuotaSnapshotStoreTests` |
| scoped 安全 | Fable 等多个模型额度全部显示并进入聚合限流判定 | `ScopedLimitTests`,`ClaudeOAuthUsageProbeTests` |
| 模型族 | 配置模型与 scoped 名规范化,歧义与非官方形状拒绝 | `ClaudeModelFamiliesTests` |
| 续跑放行 | 仅实时 OAuth 可 Ready;同族任何版本与认不出族的 scoped 满额挡住目标;其它族满额不挡也不伪造限流;目标无专属窗口由主窗口决定;相关 scoped 承接未满不挡、承接满额仍 limited;`BlockingResetUtc` 取最晚;缺主窗口/缺百分比/存储故障/CLI 证据保持等待;每次探测绕过 GUI 缓存 | `QuotaResumeProbeTests`,`ProjectResumeFactsTests` |
| 逐项目取证 | 多项目多模型只做一次供应商观测、逐项目给结论;Fable 满只挡 Fable 项目;同模型第二个项目启动前重新取证;令牌将过期只记 not-started、不锁周期、继续下一个项目;生产 DI 使用完整额度服务 | `ProjectResumeFactsTests`,`ResumeSchedulerTests`,`ResumeEngineTests`,`WorkerStartupContractTests` |
| 登录提醒 | 3 天/2 天/1 天/12 小时/已过期各一次;没设收件人不写 | `NotifyControlPlaneTests` |
| GUI 契约 | 磁贴无证据不绿、承接琥珀、按模型满额不染红主窗口、「N 项在等」;登录块各判定配色;`claude.login.status` 不回令牌/邮箱/UUID;长期令牌入口已删除;截图只用合成数据 | `GuiPageContractTests`(需 PATH 上有 Node),`ControlPlaneBridgeQuotaTests`,`ControlPlaneBridgeProviderTests`,`ControlPlaneBridgeDemoTests` + 真机截图 |

只改额度链路时的最小自动化验证:

```powershell
dotnet test csharp\test\AiResume.Tests\AiResume.Tests.csproj `
  --filter "FullyQualifiedName~ClaudeCredentialSourceTests|FullyQualifiedName~ClaudeOAuthUsageProbeTests|FullyQualifiedName~ClaudeProbeTests|FullyQualifiedName~UsageSnapshotMapperTests|FullyQualifiedName~QuotaServiceTests|FullyQualifiedName~QuotaSnapshotStoreTests|FullyQualifiedName~ScopedLimitTests|FullyQualifiedName~ScopeModelTests|FullyQualifiedName~ClaudeModelFamiliesTests|FullyQualifiedName~QuotaResumeProbeTests|FullyQualifiedName~ProjectResumeFactsTests|FullyQualifiedName~NotifyControlPlaneTests|FullyQualifiedName~ControlPlaneBridgeQuotaTests|FullyQualifiedName~ControlPlaneBridgeProviderTests|FullyQualifiedName~GuiPageContractTests"
```

交付前全量门禁:

```powershell
dotnet build csharp\AiResume.sln -c Release -warnaserror
dotnet test csharp\AiResume.sln -c Release --no-build
```

测试必须使用假 `HttpMessageHandler`、`TestTemp` 申请的临时凭据目录与临时 SQLite;夹具照抄录制的真实形状(登录文件顶层只有 `claudeAiOauth` 与 `mcpOAuth`,账号在全局配置的 `oauthAccount`);不得把真实 token 写进 fixture,不得读取真机 `~/.claude` 或 `~/.claude.json`,不得对真实项目启动 AI 修改运行。

## 11. 真机验证手册

### 11.1 前置检查

1. `claude --version` 能返回本机版本。
2. Claude Code 已登录(终端运行 `claude` 并 `/login`);不要读取或打印登录文件与全局配置的内容。
3. 最近打开过一次 Claude Code(或保持 Remote Control 在线),让访问令牌处于有效期内、全局配置里有 `oauthAccount`。
4. 先完成 Release build 与全量测试。
5. 安装 Release 产物,关闭旧 GUI 后再打开;已打开窗口不会热更新。

```powershell
csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.exe install
```

### 11.2 脱敏界面冒烟

用安装目录的 GUI 生成不读取生产状态的合成数据截图(用于布局与公开文档)。截图模式仍会写 GUI 日志,先把 `AIRESUME_SHADOW_DIR` 指向临时目录,GUI 启动后立即在当前会话里撤掉它——否则之后在同一窗口运行的 `install` 与 Worker 会继承这个临时状态根。GUI 截完图自行退出,`&` 启动 GUI 程序不会等待,约 20 秒后再查看 `$shot`:

```powershell
$env:AIRESUME_SHADOW_DIR = Join-Path $env:TEMP ("ai-resume-shadow-" + [guid]::NewGuid().ToString("N"))
$shot = Join-Path $env:TEMP ("ai-resume-quota-" + [guid]::NewGuid().ToString("N") + ".png")
& "$env:LOCALAPPDATA\AI Resume\AiResume.Gui.exe" --screenshot $shot
Remove-Item Env:\AIRESUME_SHADOW_DIR
$shot
```

看未布防形态时追加 `--screenshot-unarmed`。人工核对:

1. 四块 CRT 等高、含义固定(Claude · 5 小时 / Claude · 7 天 / Claude · 按模型 / DeepSeek · 余额)。
2. 有百分比时光柱存在,宽度与数字一致。
3. 合成数据里 Fable 为 100%:按模型磁贴标题为「Claude · Fable」、红色「已用尽」,5 小时/7 天磁贴不变红;布防形态下角标为「1 项在等」。
4. 右栏「Claude 登录」显示「即将到期」(合成数据的判定为 `LoginExpiring`)。
5. 队列行逐项目显示模型与等待原因(如「等额度」「会话开着 · 关闭后续跑」「监视中 · 未被打断」)。
6. 承接琥珀、未报告扫描、失败「未核实」等状态不在合成截图里,由 `GuiPageContractTests` 覆盖;真机窗口出现时按 §9 核对,绝不能是绿色。

### 11.3 脱敏存储核对

若机器已安装 `sqlite3`,只查询列长度与时间,不要输出 `snapshot_json` 正文:

```powershell
sqlite3 "$env:LOCALAPPDATA\AI Resume\state\runs.db" `
  "SELECT provider,length(credential_fingerprint),length(snapshot_json),updated_at FROM quota_snapshots ORDER BY updated_at DESC;"
```

预期:升级到 v2.2 后同一 Claude 账号只有一行在持续更新,指纹长度为 64,跨过一次访问令牌轮换(约 8 小时)后不新增行;v2.2 之前遗留的令牌指纹行仍在,`updated_at` 停在升级前。切换账号后允许出现另一行,但两个账号不得互相承接窗口。

### 11.4 安装产物一致性

```powershell
Get-FileHash csharp\src\AiResume.Worker\bin\Release\net10.0-windows\AiResume.Worker.dll
Get-FileHash "$env:LOCALAPPDATA\AI Resume\AiResume.Worker.dll"
Get-FileHash csharp\src\AiResume.Gui\bin\Release\net10.0-windows\AiResume.Gui.dll
Get-FileHash "$env:LOCALAPPDATA\AI Resume\AiResume.Gui.dll"
```

同名源码产物与安装产物哈希应一致。若不一致,用户看到的不是本次实现,不得继续解释界面行为。

### 11.5 v2.2 凭据与续跑门禁(待真机验证)

1. 登录状态:右栏「Claude 登录」应为「有效」并显示登录到期时间;若为「有效 · 账号未核实」,打开一次 Claude Code 后按 F5 刷新。
2. 令牌轮换不换账号:跨过一次访问令牌自然刷新(约 8 小时)后,§11.3 不应新增行,已布防周期不应出现「账号已变化 · 重新布防」。
3. 升级前已布防:升级后队列会显示「账号已变化 · 重新布防」,解除后重新布防一次即可。
4. 登录到期提醒:本机登录 2026-11-11 05:55 UTC 到期。已设置通知收件人、飞书通知通道可送达(见 [`COMPLETION-NOTIFICATIONS.md`](COMPLETION-NOTIFICATIONS.md))且后台 Worker 持续运行时,到期前 3 天、2 天、1 天、12 小时内应各收到一条飞书提醒,到期后收到「已过期」;执行 `/login` 后右栏恢复「有效」。
5. 一次真实的用量上限中断后逐项目续跑:Fable 周额度满时只有 Fable 项目「等额度」并显示预计时间,Opus 项目照常续跑;Fable 恢复后按队列顺序续,每个项目启动前重新取证。
6. 令牌门槛:访问令牌剩余不足 45 分钟时,待续跑项目显示「等登录令牌续期」,下一轮自动重试,周期不被锁住。
7. `StoreMoved`:本机目前未开启凭据管理器灰度;一旦上游开启,右栏应显示「凭据位置变化」而不是「未登录」,只能等灰度发生时验证。

## 12. 2026-08-09 验证记录

以下为历史验证证据,界面描述对应当时的两块 CRT 与侧栏布局。

本轮在同一个真实 OAuth token 上得到以下脱敏证据:

- 普通 .NET 请求形状返回 HTTP 429。
- 补齐 `Accept`、OAuth beta、Anthropic version 与 `claude-code/2.1.185` User-Agent 后返回 HTTP 200。
- 现代响应包含 `session=0%`、`weekly_all=100%`、`weekly_scoped:Fable=100%`。
- 产品 `ClaudeOAuthUsageProbe` 映射为:`five_hour` 0%、`seven_day` 100% blocked、`weekly_scoped:Fable` 100% blocked。
- 真机 GUI 显示 7 天真实满额进度条与 `Claude · Fable 已用尽`;5 小时 0% 在 reset 缺失时仍显示百分比并明确“重置时间未报告”。
- 合成 reset-only GUI 冒烟显示完整低亮分段轨道与移动未知扫描,没有把扫描位置写成百分比或 `aria-valuenow`;全局 limited 但单窗无 100% 证据时不再给该窗贴“已限流”。
- 两块 CRT 内屏等高。
- 生产 `quota_snapshots` 有 1 行,账号指纹长度 64,快照 JSON 长度 780;未输出 token 或 JSON 正文。
- Release Worker/GUI DLL 与安装目录 DLL 哈希一致。
- 全量 xUnit:972 通过、0 失败、0 跳过;Release build:0 warning、0 error。
- 本轮额度/GUI 聚焦测试:94 通过、0 失败、0 跳过。
- 两轮独立只读对抗性审查覆盖 modern/legacy 优先级、跨进程丢失更新、v4 到 v5 迁移、Fable 稀疏承接、carried UI、percent 无 reset、scoped 缺 percent 与左右布局;发现项均复核并修正。

这些百分比只证明 2026-08-09 当时的链路与语义,不是长期固定值。以后验证必须以当次服务端响应和截图为准。

### 12.1 2026-08-20 Fable 误放行事故与修复

生产日志与 SQLite/WAL 的脱敏证据给出完整反例:

- 08:29:59 PDT:5H 与 `weekly_scoped:Fable` 都达到 100%。
- 12:00:00:5H 已进入新代次并降到 0%,Fable 仍为 100%,其周限额要到 2026-08-24 07:00 PDT 才重置。
- 12:02:20:旧 Worker 的 Haiku CLI 探针返回 ready,状态机进入 `StartResuming`。
- 12:03:04:`claude --continue` 继承原 Fable 会话,立即再次限流并落为 `limited-side-effects`;安全阻断随后正确禁止重放。

根因是生产 DI 直接把 `ResumeEngine` 接到 `ClaudeCodeProbe`,而 GUI 才在使用能看见全部 `weekly_scoped` 的 `QuotaService`。探针证明的是 Haiku 调用可用,真正执行的是 Fable 会话,两者不是同一份可运行性证据。

本机上游盘点同时确认 Claude Code 2.1.235 的 `--model <model>` 支持短别名或完整模型名,可以把额度门禁与实际执行绑定到同一个显式模型;`--continue` 的语义仍是继续当前目录最近会话。因而本轮复用官方 `--model` 能解决额度错配,但不会把“最近会话”冒充为精确会话身份;后续若要消除同目录多会话选择竞态,应另行绑定已持久化的 session id 与同一显式模型(其后已由 ADR-0004 的精确 `--resume <UUID>` 落实)。

修复复用了现有 `ClaudeOAuthUsageProbe + QuotaService + QuotaSnapshotStore`,没有重写上游协议:新增 `QuotaResumeProbe` 和可解析验证的 DI 注册,保留 OAuth/CLI 证据来源,仅在本次 OAuth 响应明确包含目标模型匹配的实时窗口证据时放行,并在多项目队列的每个后续 spawn 前重新取证。当时 cc-connect v1.4.1 的 usage reporter 在 Windows 仍受 `creack/pty` 的 `ErrUnsupported` 限制,管理 API 也无 usage 端点,因此自有薄适配继续成立(cc-connect 已于 2026-10-30 退役;“必须有目标模型匹配的 scoped 窗口”这一条已被 §5.3 的完备性假设取代)。

## 13. 修改额度功能时的检查清单

改动前:

- 先读本文件、[ADR-0006](adr/0006-retire-cc-connect-per-project-resume.md) 与 [`UPSTREAM-ARCHITECTURE-RESEARCH.md`](UPSTREAM-ARCHITECTURE-RESEARCH.md)。
- 若引入新端点、新状态机、新解析或新持久化形状,先重新盘点 Claude Code 与成熟监视器在 Windows 上的现状;升级 Claude Code 后复核登录文件形状、全局配置定位规则与 `oauthAccount` 字段、凭据管理器灰度开关和 usage schema。
- 明确本次改变的是凭据、请求、解析、合并、存储、续跑判定还是 UI;不要在错误层补丁。

改动中:

- 不打印真实凭据或完整响应;不解析 `refreshToken`,不刷新、不写回 Claude Code 的登录与全局配置,不调用 `claude` 子命令诱导刷新。
- 账号身份只来自 `oauthAccount`;核实不了就为空并等待,不得退回令牌指纹。
- 保持 modern 逐字段优先 legacy。
- 保持 null、0、100、blocked、carriedForward 的不同语义。
- 保持账号 + 稳定窗口身份(主窗口 kind / scoped 完整 scope 哈希)+ reset 代次边界。
- 保持 scoped 按模型族的相关性:同族任何版本相关,认不出族的一律相关,其它族不相关。
- 保持 SQLite 事务内 read-merge-write。
- 保持存储和网络 I/O 在后台路径。
- 新服务端字段先保留未知,不要猜成已有窗口。

交付前:

- 跑额度聚焦测试与全量测试。
- Release build 使用 `-warnaserror`。
- 重新 install,确认 DLL 哈希。
- 用安装目录 GUI 的合成数据截图核对布局,并在真实窗口核对磁贴与状态行。
- 同时检查磁贴的实时、承接、未报告、已满、失败态,按模型磁贴与「Claude 登录」块。
- 更新本文件的“最后验证日期”和验证记录;若协议发生决策级变化,同步 `CLAUDE.md`、`ARCHITECTURE.md`、双语 README 与 `AI_GUIDE.md`,必要时新增 ADR。

## 14. 代码索引

- 登录文件只读解析、登录判定 `ClaudeLoginVerdict`、账号指纹与凭据管理器检测:[`ClaudeCredentialSource.cs`](../csharp/src/AiResume.Worker/Quota/ClaudeCredentialSource.cs)
- OAuth 请求、UA、modern/legacy/Fable 解析与 401/403 分类:[`ClaudeOAuthUsageProbe.cs`](../csharp/src/AiResume.Worker/Quota/ClaudeOAuthUsageProbe.cs)
- 数据源优先级、缓存、single-flight、稀疏合并与失败说明:[`QuotaService.cs`](../csharp/src/AiResume.Worker/Quota/QuotaService.cs)
- SQLite 账号快照与原子更新:[`QuotaSnapshotStore.cs`](../csharp/src/AiResume.Worker/Quota/QuotaSnapshotStore.cs)
- SQLite schema 迁移(v4/v5 额度表):[`StorageDatabase.cs`](../csharp/src/AiResume.Storage/StorageDatabase.cs)
- CLI 降级探测:[`ClaudeCodeProbe.cs`](../csharp/src/AiResume.Worker/Probes/ClaudeCodeProbe.cs)
- 模型族规范化:[`ClaudeModelFamilies.cs`](../csharp/src/AiResume.Core/ClaudeModelFamilies.cs)
- 按模型族的续跑额度判定与生产 DI:[`QuotaResumeProbe.cs`](../csharp/src/AiResume.Worker/Quota/QuotaResumeProbe.cs)、[`QuotaResumeServiceCollectionExtensions.cs`](../csharp/src/AiResume.Worker/Quota/QuotaResumeServiceCollectionExtensions.cs)
- 供应商身份、目录与逐模型取证:[`ProviderRegistry.cs`](../csharp/src/AiResume.Worker/Probes/ProviderRegistry.cs)、[`ProviderCatalogService.cs`](../csharp/src/AiResume.Worker/Probes/ProviderCatalogService.cs)、[`ProviderResumeProbe.cs`](../csharp/src/AiResume.Worker/Probes/ProviderResumeProbe.cs)
- 逐项目准入事实与探测结果:[`ProjectAdmission.cs`](../csharp/src/AiResume.Core/ProjectAdmission.cs)、[`ClaudeProbeResult.cs`](../csharp/src/AiResume.Core/ClaudeProbeResult.cs)(`Detail` / `BlockingResetUtc` / `Projects`)
- 续跑执行计划与 45 分钟令牌门槛:[`ProviderExecutionPlan.cs`](../csharp/src/AiResume.Worker/Resume/ProviderExecutionPlan.cs)
- 多项目 spawn 前复核与续跑提醒:[`ResumeEngine.cs`](../csharp/src/AiResume.Worker/Resume/ResumeEngine.cs)
- 登录到期提醒:[`ClaudeLoginReminder.cs`](../csharp/src/AiResume.Worker/Resume/ClaudeLoginReminder.cs);提醒写入队列:[`ResumeNotices.cs`](../csharp/src/AiResume.Worker/Resume/ResumeNotices.cs);提醒文案:[`CompletionNotifier.cs`](../csharp/src/AiResume.Worker/Notifications/CompletionNotifier.cs)
- 残留长期令牌清理:[`InstallCommand.cs`](../csharp/src/AiResume.Worker/Migration/InstallCommand.cs)(`RetireLegacyClaudeToken`)
- provider-neutral 契约:[`UsageSnapshot.cs`](../csharp/src/AiResume.Worker/Quota/UsageSnapshot.cs)
- GUI RPC(`quota.get` / `providers.catalog` / `claude.login.status` / `arm.get`):[`ControlPlaneBridge.cs`](../csharp/src/AiResume.Gui/ControlPlaneBridge.cs)
- 额度磁贴、「Claude 登录」块与队列文案的视图模型:[`index.html`](../csharp/src/AiResume.Gui/wwwroot/index.html)(`<script id="vm">` 的 `AR.quotaTiles` / `AR.loginModel`)
- 自动化测试:[`AiResume.Tests`](../csharp/test/AiResume.Tests)

## 2026-09-07 动态供应商界面

历史(v2.1 及之前):界面改用 `providers.catalog`,不再从名称猜最新模型,也不在刷新时发最小推理;选择供应商后顶部卡片随其余额/订阅窗口变化,Claude 的原有本地诊断与 OAuth 卡片在选择 Claude 订阅时保留,侧栏显示各供应商和服务端返回窗口。

现役(v2.2):`providers.catalog` 仍是零 token 的刷新入口,订阅、余额和模型目录互不冒充;但顶部 CRT 含义固定、不随任何选择改变(§9),供应商明细收进「视图 › 供应商与额度详情…」;DeepSeek 行只展示 OpenCode 账户的余额、不参与续跑(ADR-0006 §2.F)。完整上游盘点、兼容限制和实际目录录制见 ADR-0004;Codex 官方账号的放行规则见 ADR-0005 B1。

## 15. 2026-10-12 v2.1 凭据来源改版(历史,已被 ADR-0006 取代)

故障证据(只读检查,未打印凭据):`~/.claude/.credentials.json` 的 `claudeAiOauth.expiresAt=0`,refresh token 已于 2026-09-30 过期,`claude auth status` 报 `loggedIn=false`;生产 `quota_snapshots` 最后更新停在 2026-09-09。这正是 v2.2 判为「登录已失效」的形状(§4.4)。额度链路寄生在 Claude Code CLI 的登录态上,CLI 一登出产品就失明,而界面只显示「未核实」,没有任何主动提醒。

当时的修复见 [ADR-0005](adr/0005-owned-credentials-official-codex-and-hooks.md) A1:自持 `claude setup-token` 令牌优先、CLI 回退、401/403 标记回退,`QuotaService.DescribeOAuthFailure` 提示「运行 claude 并 /login,或在控制面保存 setup-token 长期令牌」。当轮离线回归 1264/1264,Release 构建 0 警告。当时留下的真机待验证项“usage 端点是否接受 setup-token 签发的令牌”已于 2026-10-30 得出结论:不接受(403;该令牌作用域只有 `user:inference`)。这一来源随 ADR-0006 删除,登出失明改由登录判定与到期提醒应对(§4.4、§16)。

## 16. 2026-10-30 v2.2 凭据与续跑门禁改版(ADR-0006)

故障证据(只读检查,未打印凭据,详见 ADR-0006 §1):

- 账号指纹漂移:登录文件顶层没有 `organizationUuid`,指纹一直退回令牌派生,访问令牌约 8 小时轮换一次指纹就变一次;生产 `quota_snapshots` 同一账号已有 28 个指纹,布防周期自 2026-10-20 起一直停在 `provider-changed`。
- scoped 门禁要求目标模型有专属窗口的正证据并整桶聚合:账号只有 5H、7D、Fable 三个窗口,Opus 项目永远 Unknown,Fable 一满又连带挡住 Opus。
- setup-token 路线:usage 端点以 403 拒绝,残留的机器范围密文与 rejected 标记仍在磁盘;v2.1 也没有任何登录到期提醒。

修复(ADR-0006 §2.B、§2.C、§2.D):

- 凭据只读 Claude Code 自己的登录(§4.1);账号指纹改读 `oauthAccount`(§4.3);`ClaudeLoginVerdict`、`claude.login.status` 与 `ClaudeLoginReminder`(§4.4);401、Anthropic 结构化 403 与网关裸 403 三分(§6);识别被清空的登录与凭据管理器灰度。
- scoped 门禁按模型族,目标无专属窗口由主窗口决定,相关 scoped 承接未满不阻塞,窗口级 `Detail` 与 `BlockingResetUtc`(§8.4);逐项目取证与 45 分钟令牌门槛(§8.4、§8.5)。
- 删除 setup-token、`claude.token.*`、GUI「Claude · 凭据」、本地 5 小时块 `ClaudeUsageBlocks` 与 `quota.local`;`install` 清理残留令牌文件。

验证:

- 离线:Release 构建 `-warnaserror` 0 警告;`dotnet test` 全绿(数量以当次输出为准;2026-10-30 首轮 1156 项,10-31 交叉审查后 1195 项,11-02 修复轮复审后 1215 项)。凭据、指纹、登录判定、scoped 门禁与逐项目取证都对录制的真实形状断言(§10)。
- 待真机验证:§11.5 各项;其中登录到期提醒以本机 2026-11-11 05:55 UTC 的登录到期为准。
