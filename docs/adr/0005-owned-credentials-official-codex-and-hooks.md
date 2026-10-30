# ADR-0005:自持 Claude 凭据、Codex 官方账号、Codex hooks 通知与 v1 遗留清理(v2.1)

状态:已实现并通过离线回归(1264/1264)与两份独立只读对抗审查;真机验证分两段——安装/配置激活由本轮完成,
Claude 长期令牌与 Codex 真实完成事件必须由用户操作后才能核实(2026-10-12)。

## 1. 背景:2026-10-12 盘点出的五处失效

项目自 2026-09-07 起没有更新,控制面与引擎的多项功能在真机上已经静默失效。先按第一性原理核实每一条,
再决定改什么(全部证据来自本机只读检查,未打印任何凭据正文):

| # | 现象 | 根因证据 |
|---|---|---|
| 1 | 额度区长期「未核实」,`quota_snapshots` 最后更新停在 2026-09-09 | `~/.claude/.credentials.json` 的 `claudeAiOauth.expiresAt=0`,refresh token 已于 2026-09-30 过期;`claude auth status` 报 `loggedIn=false`。AI Resume 的额度与续跑完全寄生在 Claude Code CLI 的登录态上,用户登出或刷新失败,产品就失明 |
| 2 | 完成通知间歇几小时发不出 | Worker 日志 `worker.notify.sweep_failed`:S4U 计划任务跑在 session 0,`CryptUnprotectData` 解 CurrentUser 范围的 DPAPI 密文间歇失败(`0x8009000B`)。投递本身只需要收件人 `open_id`,却被绑在飞书 app_secret 的加密存储上 |
| 3 | Codex 完成通知彻底消失 | `~/.codex/config.toml` 的 `notify` 被 Codex Desktop 的 computer-use 功能改写为 `[codex-computer-use.exe, turn-ended]`,AI Resume 原先链在 `notify` 上的命令被整体覆盖。`notify` 是单值共享键,任何程序都可以无痕覆盖 |
| 4 | Codex 供应商行与自动续跑无法使用 | `~/.codex/config.toml` 的 `model_provider="codex_web_gpt"` 指向本机 codex-chatgpt-web 桥(回环地址,`requires_openai_auth=true`);cc-connect 中的 sub2api provider(`agent_types=["codex"]`)配合上游 `ensureCodexAuth` 会在 codex agent 启动时**覆盖** `auth.json` 为 api_key 模式,与用户本机的 ChatGPT 登录相互冲突 |
| 5 | Claude 「在等你」通知缺一类 | `~/.claude/settings.json` 的 `Notification` matcher 没有 Claude Code 新增的 `permission_prompt` |

附带发现:S4U 下单消费者预检对命令行不可读的脚本宿主一律报 Unverifiable,会误拒「生成并重启 cc-connect」(已改为豁免 cc-connect 自身祖先链);
cc-connect 激活的任务归属判定只认「LastRunTime ±5 秒内创建的 powershell」,而 PT5M 重复触发让 LastRunTime 每 5 分钟推进,稳态下必然 Unknown 并拒绝切换(2026-10-12 真机复现,已改为以任务计划程序 EnginePID 对照父进程链);
仓库里还留着 v1 迁移命令和一批没有生产调用方的研究性类型,以及 30 多份历史迁移文档混在现役文档中。

## 2. 决策(用户 2026-10-12 确认「都按你的建议来」)

### A1 Claude 凭据:AI Resume 自持 `claude setup-token` 长期令牌,CLI 登录文件只作回退

- 用户在终端运行 `claude setup-token`(需先 `claude` → `/login`),把打印出的令牌粘进控制面「Claude · 凭据」;
  令牌只经 GUI 进入 `ClaudeCredentialSource.SaveToken`,以 **DPAPI LocalMachine** 范围加密存
  `%LOCALAPPDATA%\AI Resume\state\secrets\claude-oauth-token.bin`——S4U session 0 的 Worker 必须能读它。
- 读取顺序:自持令牌 → `~/.claude/.credentials.json`。两者**只读、绝不刷新、绝不写回**(ADR-0003 的约束不变)。
- usage 端点明确拒绝自持令牌(401,或带 Anthropic 结构化错误体的 403)时写 `claude-oauth-token.bin.rejected.json` 标记并立刻用 CLI 凭据重试;
  标记绑定被拒那份令牌的文件签名、1 小时后自动失效(允许再试一次,仍被拒会重新标记),用户重新保存令牌也会清掉它。
  CDN/网关的裸 403 只影响本轮。界面同时显示两行来源状态,任何一行都不以「已保存」冒充「可用」;令牌文件在但解不开时显红。
- 自持令牌没有 `organizationUuid`,账号指纹按令牌派生;CLI 指纹按 organization 派生。凭据来源切换(保存/清除令牌、令牌被拒回退 CLI)
  等于账号指纹变化:已布防周期会报「供应商或账号已改变」并等待重新布防,历史读数也不跨来源承接。推荐流程是先保存令牌再布防。
- 机器范围密文保存后把文件 ACL 收紧到当前用户 + SYSTEM + Administrators 并读回核验,收不紧就放弃保存。
- 否决的替代方案:只提醒用户重新 `/login`(依赖别人的会话,问题会反复出现);AI Resume 自己用 refresh token 刷新
  (与 Claude Code 争用 refresh token,ADR-0003 已明确禁止);依赖 Claude Code ≥2.1.234 的
  `autoContinueAtUsageLimit`(上游只对**交互式打开**的会话生效,`-p`/后台无效,不能替代续跑引擎)。

### B1 Codex 供应商:面板与续跑引擎固定使用官方 ChatGPT/OpenAI 账号

- `CodexAuthProbe.ReadOfficialCredentials` 只读 `~/.codex/auth.json`:`auth_mode=chatgpt` 走 `https://chatgpt.com/backend-api/codex`,
  API key 走 `https://api.openai.com/v1`;**不读** `config.toml` 的 `model_provider`,也不修改 `~/.codex/config.toml`。
- 面板读 `/backend-api/codex/models?client_version=<本机 CLI 版本>` 与 `/backend-api/wham/usage`;
  自动续跑放行由 `ProviderCatalogService.EvaluateChatGpt` 判定:账号主/次窗口全部 `allowed` 且 0 ≤ percent < 100、
  无附加限额或 `spend_control` 命中、目标模型未在 `model_usage` 中标为不可用 → ready;任一限额命中 → limited;
  无数据/缺百分比 → unknown(等待,不猜)。`model_usage` 只枚举有过记录的模型,缺目标模型不算不可用。
- 主配置若用 `profile = "…"` 且该 profile 自带 `model_provider`,上游会以 profile 值压过 `-c` 根表覆盖,执行计划因此失败关闭;续跑子进程还把 `OPENAI_BASE_URL` 环境钉成同一官方端点。
- 续跑执行计划对官方账号用上游 `-c` 覆盖:`codex -c model_provider="openai" -c openai_base_url="<官方端点>" exec resume <uuid> … --model <id> --json --skip-git-repo-check`,
  凭据由 Codex 自己的 `auth.json` 提供,不再经环境变量传递。第三方 provider 路径保留原逻辑,只在 `config.toml`
  显式配置了非官方、非回环的 provider 时作为附加行展示并可续跑。
- cc-connect 生成配置:仅当所选 agent 为 codex 时,codex agent 不引用任何 provider,删除 `agent_types` 恰为 `["codex"]` 的第三方 provider
  (先备份为 `config.toml.bak-ai-resume-<yyyyMMddHHmmss>`,同一内容只备份一次,备份失败则中止;该备份含飞书密钥与管理令牌,留在 `~/.cc-connect` 由用户保管),并写 `cmd = "codex -c model_provider=\"openai\" -c openai_base_url=\"…\""`。
  上游 v1.4.1 用 Go `strings.Fields` 拆分 `cmd`,所以参数不得含空白;生成器据此拒绝含空格或引号的端点。
- 否决的替代方案:继续用 sub2api 第三方(与 `ensureCodexAuth` 覆盖 `auth.json` 的冲突无法在不改上游的前提下消除);
  使用 codex-chatgpt-web 本机桥(回环地址、第三方进程生命周期不受本产品监督,额度无法核实)。

### C1 Codex 完成通知:改用 Codex 0.148.0 hooks 的 `Stop` 事件

- `CodexHooksNotificationAdapter` 在 `~/.codex/hooks.json` 的 `hooks.Stop` **末尾**追加 AI Resume 自己的分组
  (handler `type=command`、`command="\"<安装目录>\AiResume.Hook.exe\" codex"`、`timeout=30`),并在 `config.toml` 写
  `[hooks.state."<hooks.json 全路径>:stop:<group>:<handler>"] trusted_hash = "sha256:…"`。
- 信任哈希与上游 `hook_hash`/`version_for_toml` 一致:对规范化身份
  `{"event_name":"stop","hooks":[{"async":false,"command":…,"timeout":30,"type":"command"}]}` 做键排序、紧凑序列化后取 SHA-256;
  算法用本机已有的 codex-chatgpt-web `interrupt` 条目作 oracle 复算通过。state key 由上游 `hook_key(key_source, event, group, handler)`
  生成,`key_source` 是 hooks.json 的绝对路径 `display()` 字符串。
- 只增删自己的分组与「哈希等于我方命令哈希且仅含 trusted_hash/enabled 键」的信任记录;停用时把排在我方之后的分组的信任键序号前移,启用时先写信任再写 hooks.json(失败回滚)。
  `CODEX_HOME` 环境变量指定的目录按上游规则 canonicalize 后再构造信任键;`[features] hooks = false` 时报断链。旧 `notify` 残留只提示不改写,Hook 对 Codex 按 `(session_id, turn_id)` 去重,升级期旧链与新钩子同回合不会双发。
  信任不匹配时 `notify list` 标 `hookBroken` 并提示在 codex 里 `/hooks` 信任一次。已打开的 Codex 会话不会重载 hooks.json,新会话才生效。
- `AiResume.Hook.exe codex` 同时接受旧 `notify` argv 负载(`agent-turn-complete`)与 hooks `Stop` 的 stdin JSON
  (`hook_event_name=="Stop"`);`transcript_path` 只在以 `-<session_id>.jsonl` 结尾、是绝对路径且存在时直接使用,否则回到既有的 rollout 扫描与准入规则。
- 否决的替代方案:继续链在 `notify` 上并增加所有权修复(单值键会被任何程序再次覆盖,治标不治本)。

### D1 通知收件人明文化,Worker 不再依赖 DPAPI

- 收件人 `open_id` 存 `ProductConfig.NotifyRecipientOpenId`(明文 `config.json`),GUI 在读取/保存飞书凭据时从 `allow_from` 第一个 `ou_…` 同步,清除凭据时一并清空。
- Worker 投递先读该字段,只有 v2.1 之前的旧配置才回退 DPAPI 读取。`open_id` 不是秘密(飞书自己也以明文下发),app_secret 仍只在 GUI 侧 CurrentUser DPAPI。
- 否决的替代方案:把 Worker 改成交互式登录任务(失去 S4U 的会话无关性与崩溃重启);所有秘密改 LocalMachine 范围(扩大 app_secret 的可读范围,没必要)。

### E1 版本固定

本轮固定 cc-connect **1.4.1**、Codex npm **0.148.0**、Claude Code CLI **2.1.296**。cc-connect 1.5.0 的升级与其 TOML/管理 API 差异另开一轮,不与本轮混做。

### F1 清理与文档

- 删除 v1 迁移命令 `migrate` / `import-feishu` / `sync-dirs` / `cutover-config`(CLI)及 `ProductStateMigrator`、`ShadowPaths` 旧目录迁移;
  删除无生产调用方的 `CcConnectSessionBridge`、`CcConnectAuthMapper`、`CcConnectSessionAgent`、`CcConnectSupervisor`、
  `CodexNotificationAdapter`、`CodexProbe`、`ClaudeCodeProviderAdapter` 及其测试;GUI 的 `providers.probe` 旧 RPC 一并删除。
- GUI「生成并重启 cc-connect」与新 CLI `AiResume.Worker.exe cc-connect-apply` 共用 `CcConnectApplyCommand.Apply`。
- 历史迁移文档(AUDIT-*、MIGRATION-*、STAGE-*、STATE-OWNERSHIP、design/、evidence/ 等)移入 `docs/history/`;现役文档只剩
  ARCHITECTURE、CLAUDE-QUOTA-ACQUISITION、COMPLETION-NOTIFICATIONS、RUN-CONTRACT、EVENT-CONTRACTS、FEISHU-BOT-GUIDE、LESSONS、UPSTREAM-ARCHITECTURE-RESEARCH 与 ADR。
- 版本号 `2.1.0`(`csharp/Directory.Build.props`)。

## 3. 上游盘点(先查已有,再决定自研)

- **Claude Code 2.1.296 / 2.1.295**:`claude setup-token` 为无人值守场景签发一年期 OAuth 令牌,`CLAUDE_CODE_OAUTH_TOKEN` 可供 `-p` 使用;
  `claude auth status` 给出登录态。原生 `autoContinueAtUsageLimit`(≥2.1.234)仅作用于交互式会话。`Notification` 类型新增
  `permission_prompt`、`idle_prompt`、`quota_auto_resume_*` 等。结论:令牌由官方签发,AI Resume 只保管与使用,不实现任何 OAuth 刷新。
- **Codex 0.148.0(tag `rust-v0.148.0`)**:hooks 事件含 `Stop`;非 managed hook 需 `trusted_hash == current_hash` 才执行;
  `hook_key` = `"{key_source}:{event_label}:{group}:{handler}"`,hooks.json 的 `key_source` 为其绝对路径;`Stop` 的 stdin JSON 含
  `session_id`、`transcript_path`、`cwd`、`hook_event_name`、`turn_id`、`stop_hook_active`。`-c key=value` 在 `exec` 前作全局配置覆盖;
  内置 `openai` provider 的 base_url 取 `openai_base_url`,缺省按认证模式选 chatgpt backend 或 `api.openai.com/v1`。
  ChatGPT `/backend-api/wham/usage` 真实返回形状(本机只读录制):`rate_limit.{allowed,limit_reached,primary_window,secondary_window}`、
  `additional_rate_limits[]`、`model_usage{slug:{available,available_at}}`、`spend_control.reached`、`rate_limit_reached_type`。
- **cc-connect v1.4.1**:`cmd` 字符串经 `strings.Fields` 拆分;codex agent 配了 provider 时 `ensureCodexAuth` 会重写 `auth.json`;
  管理 API 仅 `/api/v1/restart` 等,无 usage 端点(ADR-0003 §2.3 结论不变)。
- **Windows DPAPI**:`CRYPTPROTECT_LOCAL_MACHINE (0x4)` 让同机任何用户可解密——因此只用于「S4U Worker 必须读、且泄露影响有限(可在 Anthropic 侧撤销)」的 Claude 令牌,app_secret 不用。

## 4. 验证边界与遗留

- 离线:`dotnet build -c Release -warnaserror` 0 警告;`dotnet test` 1264/1264。新增 `ClaudeCredentialSourceTests`、`CodexOfficialProviderTests`、
  `CodexHooksNotificationAdapterTests`,ChatGPT 解析对真实录制响应断言,hooks 哈希对本机 oracle 断言。
- 真机(本轮):`install` 后 `notify list` 五源可送达、`feishu-check` 成功、`cc-connect-apply` 验证新代次。
- 真机(需用户):粘贴 `setup-token` 后额度区变为实时读数且 `quota_snapshots` 新增一行;一次真实 Codex 任务结束后飞书收到通知
  (若 `codex /hooks` 显示未信任,在那里信任一次)。
- 未决:usage 端点是否接受 setup-token 签发的令牌只能真机证实,代码已准备好 401/403 回退;`CODEX_HOME` 大小写与上游 `display()` 不一致时信任键会错位,
  界面会如实报「未信任」;cc-connect 1.5.0 升级另开 ADR。
