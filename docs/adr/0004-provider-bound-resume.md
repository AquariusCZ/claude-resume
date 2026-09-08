# ADR-0004：供应商实际模型目录与固定目标续跑

状态：实现与两份独立只读审查完成；真实限额恢复待正常使用验证（2026-09-07）。

## 目标与已确认行为

模型与额度面板覆盖 Claude、Codex、DeepSeek 及已配置的第三方供应商，并扩展自动续跑。
模型目录来自供应商或执行器提供的目录。布防时确定供应商、账号、执行器和实际模型 ID；
本周期不因目录升级自动换模型，下次布防重新取证。显示名不作为执行标识。

额度证据必须与目标供应商和账号一致。订阅窗口、按量余额与模型目录分别保留各自语义；
模型被列出不代表其额度已恢复。供应商不提供可验证额度时明确显示未核实，不猜测恢复。
供应商或账号变更使当前证据失效，凭据不写入产品配置、日志或目录缓存。

续跑沿用现有 CheckerCycle、ResumeEngine、ProcessSupervisor 和 Windows Job Object；
cc-connect 仍直接运行，继续拥有飞书会话及上游交互状态。AI Resume 只监督自己启动的续跑。
每个项目启动前重新获取额度，已有副作用的失败运行仍禁止自动重放。

## 上游盘点

- 本机 cc-connect v1.4.1，commit `5d4c96dd`。固定源码的 `agent/codex/codex.go`
  已有 `AvailableModels` 和 HTTP 取目录，但其旧模型白名单过滤新 ID，且请求路径直接拼接
  `/v1/models`；`core/management.go` 的模型接口不提供实时性或来源，可能返回内置候选。
  这条路径使用普通 Go HTTP/文件 I/O，在 Windows 可用，限制在语义而非平台。
- 现有 `CodexAuthProbe` 已按活动供应商配置访问 `/models`，但丢弃响应正文。
  在既有请求内保留模型目录，复用既有认证、URL 和身份指纹处理，无需改造 cc-connect。
- OpenAI 官方 app-server 提供 `model/list`、`account/rateLimits/read` 和原生会话恢复；
  DeepSeek 官方提供 `GET /models` 和 `GET /user/balance`。不维护新的静态版本推荐表。
- Claude Code 本机二进制提供控制协议初始化的模型目录；官方 Models API 的单模型查询
  提供别名解析。具体可用协议以本平台验证结果为准，未验证的数据源不得授权续跑。

来源：

- https://github.com/chenhg5/cc-connect/blob/5d4c96dd12774574369e75b60084140101c9a59a/agent/codex/codex.go
- https://github.com/chenhg5/cc-connect/blob/5d4c96dd12774574369e75b60084140101c9a59a/core/management.go
- https://developers.openai.com/codex/app-server
- https://api-docs.deepseek.com/api/list-models

## 验证边界

测试只用隔离状态、假会话、注入网络与进程；不对真实项目启动 AI，不发付费请求。
完成主实现与基础验证后安排两份独立只读对抗性审查。部署沿用现有安装事务。

## 本平台实际采用的上游能力

Claude Code 2.1.251 原生二进制（GIT_SHA `37534ac596d80cefb02d272f036adba4ba055d2c`）已有
`CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST=1`：过滤 global/user/project/local/flag/policy 的 provider、auth 和 env 覆盖，保留普通权限、hooks、MCP 设置。`YH()` / `Mre()` 在该标记下会跳过组织登录限制，因此本产品遇到组织 managed 文件、`managed-settings.d`、HKLM/HKCU 的 `SOFTWARE\Policies\ClaudeCode`、远端设置或动态策略时拒绝自动执行；也不通过禁用 settings 来源绕过项目约束。Windows managed 文件位于 `C:\Program Files\ClaudeCode`。全局配置来源包括 `.config.json` / `.claude.json` 的 env。该能力从本机二进制读取确认，未运行 Claude initialize 实验。

2026-09-07 只读调用 Anthropic 官方 `GET /v1/models?limit=1000`，使用既有 OAuth 与 Claude Code 协议头，返回 HTTP 200、`has_more=false`。实际目录包含 `claude-fable-5-1`（Claude Fable 5.1）、`claude-fable-5`、`claude-opus-5`、`claude-sonnet-5` 等。测试保留无凭据的白名单字段。目录请求有界读取、禁重定向；Anthropic `has_more` 使用 `last_id` / `after_id` 继续分页，有页数与总模型数上限。

Codex 使用固定上游 0.148.0 commit `3ba0f711642a888aec92a611a3f3b2211157ff89` 核查：

- ChatGPT HTTP 目录是 `/backend-api/codex/models?client_version=<所选CLI版本>`，响应 `models[].slug/display_name/visibility`，仅 `visibility=list` 进入选择器；HTTP 接口无分页。
- 账号额度是 `/backend-api/wham/usage`，字段 `rate_limit.primary_window/secondary_window`、`additional_rate_limits`、`spend_control` 等。此版本没有模型到额度桶的 join key，不能授权某个目标模型恢复；目录和账号窗口可展示，布防后保持明确的等待原因。
- `exec resume UUID PROMPT --model ID --json` 支持 Windows，使用所选 CLI 的同一入口；npm 包读取自身版本，不混用其它 PATH 中的 CLI。
- JSONL 的 error 只有 message，没有结构化限流码。任何 error、未知项、损坏行或缺少完整 `turn.completed` 都锁住周期，不按错误文案授权自动重放。命令、文件和 MCP 项从 started 即视为可能有副作用。
- 静态第三方 provider 保留原 ID，通过官方 `-c` 固定 endpoint/wire/env_key，密钥与自定义头值只存在子进程环境。内置 openai 的同名 provider 表覆盖被上游忽略，所以不能套用该方法。复杂认证、查询参数、冲突项目/managed 配置不冒充可固定。
- 已有连接配置在运行期持只读共享句柄，防止并发改写；不写入或复制上游凭据。新配置来源以及未来上游配置层变化仍需真机兼容性验证。

对应源码模块：`model-provider-info/src/lib.rs`、`config/src/merge.rs`、`config/src/loader/layer_io.rs`、`exec/src/lib.rs`、`codex-api/src/endpoint/models.rs`、`backend-client/src/client/rate_limit_resets.rs`。这些是固定上游协议证据，不冒充本账号的线上录制。

## 当前产品行为与边界

`ProviderRegistry` 读取 Claude 订阅、Claude API 配置、当前 Codex provider 以及 `DEEPSEEK_API_KEY`。同一份内存连接用于该次模型与计费请求；`ProviderCatalogService` 产生不含凭据的显示 DTO。开窗和手动刷新均只请求元数据，不发最小推理。旧的 `providers.probe` shallow/deep 兼容接口仍保留，现役界面改用 `providers.catalog`。

布防强刷目录，写入 `ResumeTarget`（供应商 ID、账号指纹、agent、实际模型 ID、显示名、绑定时间）与每个项目的原生会话 UUID。目录更新不会改写已布防 ID。原配置缺少绑定时等待重新布防。模型 ID 可以是供应商可变别名：固定 ID 不等于冻结服务端物理模型版本。

Claude 订阅仍经过原 `QuotaService` 的持久化/承接/账号隔离及 `QuotaResumeProbe` scoped 门禁；有版本的 scope 必须匹配同版本，无版本 scope 才表示整个模型族。DeepSeek 与兼容余额接口的第三方依据实时非陈旧正余额；未知或不提供额度接口的供应商保持等待。模型目录存在本身不能证明余额可用。

`AgentExecCommand` 是由原 ProcessSupervisor/Windows Job 监督的内部子进程，不创建第二个 Worker Host；通过 ArgumentList 传参数、流式落盘 stdout/stderr，并在输出收敛后记录退出码。命令、提示词中的引号和换行不经 shell。原生会话只读选择，Claude subagents 与 Codex 非顶层来源被排除；不使用 `--continue` / `--last` 猜测会话。运行中的真实客户端并发与真实限额恢复端到端仍列为待真机验证。

cc-connect 继续使用用户显式模型列表；缺列表时只补已配置默认值，删除按版本猜测 Sol/Terra/Luna 的代码。实际目录显示属于 AI Resume 控制面，不改造上游管理 API。

## 独立审查后的修订

两份相互独立的只读审查分别检查供应商取证/认证，以及运行器/配置/会话/异常边界。已修正重复 User-Agent、仅请求头认证、空 Bearer 回退、额度门禁日志丢失、stdout/stderr 授权混用和退出码缺失。Claude 必须从 stdout 取得匹配模型且唯一的初始化与结果；诊断通道不能授权成功或重试。身份比较使用 SHA-256，避免将提示词或完整项目队列记为日志中的模型身份。

Codex 0.148.0 固定上游 `thread-store/src/local/writer_lock.rs` 和 `live_writer.rs` 证明 `exec resume` 会原子取得 `<CODEX_HOME>/thread-writer-locks/<UUID>.lock`，Windows 原生二进制也包含对应能力。锁随 live recorder 保留，原客户端仍加载会话时会拒绝第二个 writer；不按锁文件存在或错误文本猜测恢复。当前仅核实 0.148.0 的该执行能力，自定义 `experimental_thread_store` 拒绝自动执行。

Codex 管理配置晚于 runtime 配置；app-server 的 `thread/resume.config` 也不能覆盖这个优先级。因此不改为另一套 app-server 状态机。执行必须已经取得实际 `<CODEX_HOME>/managed_config.toml` 的只读共享句柄，缺失时拒绝布防，不创建空文件或绕过管理策略。其它已知配置来源记录存在性，在计划完成、最终启动前和运行监控中复核；已有文件保持锁定。本轮因此没有承诺任意 Codex 配置都能自动续跑。

Claude 原生 `sessions/<pid>.json` 的 `pid/sessionId` 字段已在本机只读确认。恢复前查询登记 PID，存活或无法判定时等待，并在启动前再次检查；不会把残留登记文件当成存活进程。这是对现有客户端占用的保护，尚未证明任意旧客户端在同一瞬间重新打开会话的原子互斥，仍需后续真机验证。

前端回归实际执行页面 JavaScript，覆盖供应商切换后的迟到 Claude 响应、切回 Claude 的缓存重绘、独立窗口 blocked 状态和刷新失败清空旧窗口。生产 OAuth 失败不再触发 CLI 推理，保留同账号、未过期历史读数的承接语义。隔离 helper 测试实际运行假 PowerShell 子进程，验证 stdout/stderr 收敛后记录退出 0/7；没有启动真实 AI 或付费请求。

## 最终验证与安装记录

- 全量 `dotnet test csharp/AiResume.sln --no-restore`：1422 通过，0 失败，0 跳过（2026-09-07）。
- Release 构建使用 `--no-restore -warnaserror`：0 警告、0 错误；`git diff --check` 通过。
- 安装前只读确认旧周期处于 waiting，无 activeRunId、pendingCancellationRunId 或登记进程。现有安装事务退出 0，安装后的 Worker/GUI DLL SHA-256 与 Release 产物一致，Named Pipe pong PID 与新 Worker PID 一致；五类 `notify list` 均为可送达。
- 重新打开安装后的真实窗口，用本机 UI Automation 核验供应商选择器、刷新按钮、实际供应商额度及“旧布防未固定目标”提示。公开截图仅使用合成数据。
- 未对真实会话执行自动续跑，未发送付费模型请求；真实限额恢复、Claude 极窄并发窗口、ChatGPT 账号响应及未来 CLI 版本仍属待验证范围。
