# ADR-0008:子代理发起前的额度预检(budget-gate)——AI Resume 的第五件事

状态:V0–V2 已实现(2026-11-02);V3(启用拦截)代码已就绪,默认关闭,只能由用户在界面上看过证据后亲手打开。
本 ADR 扩展 ADR-0006 §「现役架构」的职责清单(四件事 → 五件事),不改变续跑、项目发现、完成通知与 GUI 的既有语义。

## 1. 背景

2026-10-31 会话 00bd46fd 在 Desktop 里派发工作流与子代理,两次撞 5 小时限额、一次撞周限额:
被杀的子代理约 2,790 万等价 token,工作流"失败的 agent 及其之后启动的全部 agent 都重跑"又连带重跑约 1,000 万,合计约占周额度 7%。
调研报告(`side-session-notes/…/budget-preflight-report.md`)的结论:

- Desktop 代码页属于 SDK 托管会话,官方"撞限暂停、重置后继续"只适用于交互式 CLI;Desktop 里工作流 agent 撞限只会失败。
- 单个子代理成本预测不准(同一提示重跑可差 2.3 倍,整批偏差 −36% 到 +70%),一次性整批判断要么全放要么全拒;
  **逐个放行 + 计入在途 + 只算到重置为止 + 软着陆**在所有模拟场景下都占优。
- 美西工作日高峰里 5 小时额度消耗约为平时 2.1–2.6 倍(证据有限,见 §4),价格必须在线测量。

用户约束(原话要点):不弹窗、不要求用户批准、不限制子代理数量、上下文窗口暂不改、限额重置后的续跑保持全自动;
做成 AI Resume 的一个模块,便于验证与实施,并在 AI Resume 界面里方便地可视化。

## 2. 决策

### A. 职责:从四件事扩到五件事

AI Resume 现役职责新增第 ⑤ 件:**子代理发起前的额度预检**——AI Resume 做控制面与证据可视化,判断与拦截在 Claude Code 进程内的 mod 里。
只有 mod 能逐个看到工作流里的每个 `agent()`(`agent.spawn`)、逐次计量每个请求(`turn.step`)、拦下在途子代理的工具调用(`tool.call`);
AI Resume 的 Worker 在进程外,看不到这些。代码放在 AI Resume 仓库(`mods/airesume-budget-gate/`),随 AI Resume 安装与卸载,不另开仓库。

### B. 分工

| | mod(Claude Code 进程内,TypeScript) | AI Resume(进程外,C#) |
|---|---|---|
| 读数 | `session.measure` 推送 + 每次请求后 `$.session.usage()`(零成本,不碰凭据) | 已有的 OAuth usage(含按模型族的 weekly_scoped)导出只读快照 |
| 计量 | 逐次请求的四项 token → 等价 token,按会话 / 模型 / agent 累计 | — |
| 校准 | 按 窗口 × 模型 × 时段 在线学"百分点 / 百万等价 token",跨会话合并 | 展示价格表与置信度 |
| 决策 | 观测 / 影子 / 启用三种模式(另有 off) | 写 config.json(模式与参数),界面切换 |
| 动作 | 启用模式:推迟派发、软着陆、重置后补派、续跑简报 | — |
| 证据 | 本地 JSONL 日志 + 每会话心跳 | 读日志与心跳,给出"是否真的生效"、对表、复盘 |
| 安装 | — | install/uninstall 安装与移除 mod,意图持久化 + 对账 + 外部可证伪健康证据 |
| 出错 | 一律放行(fail-open),记日志 | 读不到证据显示「未核实」,绝不显示正常 |

### C. 数据契约(`%LOCALAPPDATA%\AI Resume\state\budget-gate\`)

`state\` 由 AI Resume 建立并在卸载时保留。mod **只在 `state\` 已存在时**写入,绝不自己创建 `AI Resume` 顶层目录(§4 MSIX 虚拟化)。

| 文件 | 写者 | 读者 | 内容 |
|---|---|---|---|
| `config.json` | AI Resume | mod | `{ schema, version, mode, params?, updatedAt }`;`version` 每次变更递增,心跳回报读到的版本,用来证明配置真的生效 |
| `quota-snapshot.json` | AI Resume Worker | mod | `{ schema, exportedAt, windows:[{kind,percentUsed,resetsAt,observedAt,carried}], scoped:[{family,name,percentUsed,resetsAt,observedAt,carried}] }`;**不含任何凭据或账号标识** |
| `sessions\<会话 id>.json` | mod(每会话一份) | AI Resume、其它会话的 mod | 心跳:mod/引擎版本、模式与配置版本、读数、在途 agent、累计计量、主循环速率、待补派、价格表、计数器、最后错误 |
| `logs\*.jsonl` | mod(每进程分段,512 KiB 换段) | AI Resume、离线回放 | 读数、逐次请求、派发前后、判断、结束复盘、软着陆、补派、简报、错误 |
| `calibration.json` | mod(各会话合并写) | mod、AI Resume | 价格样本(按 窗口\|重置时刻\|起点\|终点 去重)与子代理历史 |

日志红线:不记令牌、提示正文与项目私密内容。提示与描述只留长度和 SHA-256 前 16 位(认出"同一提示重跑"),工作目录只留哈希(项目键)。
mod 不调用 `$.session.authorize`,不读、不写、不刷新任何 OAuth 令牌。

### D. 决策策略(报告 §4.3,实证后的修订)

对每个 `agent.spawn`(含工作流里每个 `agent()`),对每个有读数的窗口:

```
需求 = k(窗口, 模型, 时段) × min(成本 p80, 速率 p80 × 距重置)     只算到重置为止
在途 = Σ 本机在途与已预留 agent 的 k × min(剩余 p80(已跑轮数), 速率 × 距重置)
预留 = 5 小时:主循环近 15 分钟速率 × 距重置,封顶 10 点;周:2 点,重置前 12 小时线性降到 0
放行 ⇔ 每个窗口 需求 ≤ 100 − 读数 − 在途 − 预留
```

- **闸门与预留在同一步**:实测同一 `parallel()` 批次的 `agent.spawn` 在 1 毫秒内全部先触发、之后才逐个启动,
  所以放行后立即同步预留(判断与预留之间没有任何 await),后面的候选才看得见它。
- **前缀闭合**:同一工作流 run 推迟了第 i 个,其后全部推迟,失败的永远是启动顺序上的后缀;`resumeFromRunId` 续跑时解除。
- **保底推进**:本会话没有在途时,中位需求在扣除最小预留后放得下就放行一个。
- **按模型族周额度**:来自 AI Resume 快照,≥ 99% 时推迟到该族重置;子代理首个请求就被限额拒绝时被动标记"受限至重置"。
- **新模型**:已知族按 Opus 参照价 × 族比例(sonnet 0.6、haiku 0.33);没见过的族按已知最贵 × 2,单路放行,攒够 3 段样本解除。
- **读数来源链**:本会话最新读数 → 其它会话 3 分钟内心跳 → AI Resume 快照(30 分钟内);都没有就放行(退化为现状)。
- **成本先验**:同一 run 已完成的兄弟(对数尺度贝叶斯更新,σ_b 0.40、σ_e 0.25)→ 同类型历史 → 种子(中位 458 万等价)。
- **软着陆**:5 小时 ≥ 97%、周 ≥ 99% 时,在途子代理的工具调用不执行(交卷类 `StructuredOutput` 豁免);主会话不拦。
- **补派**:启用模式下推迟项到重置时刻、会话空闲时,以插件消息提醒一次(工作流给出 `resumeFromRunId`,单独派发的列出简短描述);
  续跑或工作流回报的那一轮在 `context` 里附预算简报(不改系统提示,不冲掉主会话缓存)。
- 模型可读的文字只陈述事实(为什么没执行、何时恢复),不写祈使句(§4 实证)。

### E. 模式与上线节奏

| 模式 | 行为 | 何时 |
|---|---|---|
| off | 撤下 settings.json 里的登记,新会话不再加载 mod(不卸载 AI Resume 也能持久退出);已加载的旧会话只写心跳 | 用户关闭 |
| observe | 只记录读数与计量,校准照学 | V0(没有 config.json 时的回落模式) |
| shadow | 照常判断但一律放行,只记"如果启用会怎么做" | V2,**install 后默认** |
| enforce | 推迟、软着陆、补派、简报(各有子开关) | V3,**只能由用户在界面上打开**,本轮不默认启用 |

V1 是离线回放:录下来的真实 JSONL 按时间喂给同一个 `BudgetEngine`,得到影子模式的结论;测试 fixture 全部来自真实录制。

### F. 安装、卸载与健康证据

- **意图持久化**:`ProductConfig.BudgetGateMode`(空串 = 旧配置从未记录,install 补默认 `shadow`)与
  `BudgetGateConfigVersion`(每次模式变更递增)。install 按意图对账,永不把用户选的模式改回;uninstall 只移除登记,意图与 state 保留,重装原样恢复。
- **随 payload 走**:`mods/airesume-budget-gate/**` 经 Worker csproj 的 `None`(`LinkBase="mods\airesume-budget-gate"`)进每个项目的输出,
  install 的安装清单、SHA-256 冻结、备份、过时文件删除与卸载退役全部自动覆盖;装在 `%LOCALAPPDATA%\AI Resume\mods\airesume-budget-gate\`。
- **登记**:用户级 `~/.claude/settings.json`(跟随 `CLAUDE_CONFIG_DIR`)的 `env.CLAUDE_CODE_PLUGIN_DIRS` 是多值键(`;` 分隔),
  只增删我方条目(末级目录名为 `airesume-budget-gate` 的都算我方,开发期指向开发目录的条目在首次 install 时被替换);
  整树解析、只动这一个键、写前重读(被别的进程改过就放弃这次)、临时文件原子替换、改前留 `.bak`。只影响新启动的会话。
  续跑进程运行期间 `ExecutionConfigLease` 以只读共享持有 settings.json,原子替换会失败——按未对齐处理,不吞掉。
- **install**:预检对账与通知源对账一起放在"新 Worker 就绪、快捷方式提交"之后;两者各自记结果,任一没对齐都返回 2,提示语分开。
  对账还建好 `sessions\`、`logs\` 与哈希盐 `salt`(mod 只往已存在的目录里写;盐让日志里的项目键与提示哈希无法按常见路径或短提示反推)。
  **uninstall**:最先撤下登记——早于拆快捷方式、停进程与删 payload;撤不下(settings.json 正被改写,或读不懂却含我方条目)就抛出,
  卸载在什么都没动时中止;后续步骤失败中止时按意图恢复登记。
- **组织策略**(审查发现,本机二进制实证):托管设置里 `disableSideloadFlags === true` 时,Claude Code 启动只要看到
  `CLAUDE_CODE_PLUGIN_DIRS` 非空就报错退出——我方登记会让终端、Desktop、续跑拉起的**所有**新会话起不来。`SideloadPolicy` 读与
  Claude Code 相同的来源(`%ProgramFiles%\ClaudeCode\managed-settings.json`、`managed-settings.d\*.json`、配置根 `remote-settings.json`、
  `CLAUDE_CODE_REMOTE_SETTINGS_PATH`、注册表 `HKLM/HKCU\SOFTWARE\Policies\ClaudeCode\Settings`);禁止或任一来源读不懂都不登记、已有的撤下,
  登记前、界面切换模式时与 Worker 每小时各查一次。宁可 mod 不生效,也不能让 Claude Code 起不来。这时 install 如实返回 2(意图没能落实);
  不想再看到,在界面选「已关闭」即与策略对齐。本机目前没有任何托管策略(三处文件都不存在,两处注册表项也不存在)。
- **"生效中"必须同时满足**:settings.json 里恰好一条我方条目且指向本程序目录下的 mod、mod 必需文件齐全、config.json 可读、组织策略允许、
  3 分钟内的**全部**新鲜心跳(未结束)回报的模式、配置版本与 mod 版本都等于当前意图、config.json 与已装 mod;任一前提不满足显示「未生效」并给出原因,
  settings.json 读不出显示「未核实」,只有一部分心跳跟上显示「部分生效」,都没跟上显示「配置未跟上」(配置约 1 分钟内跟上,旧版 mod 要重开会话)。
  没有活跃会话时显示「已安装 · 等新会话加载」——安装本身不证明生效。「已关闭」单独显示,仍登记着时说明原因。
- **界面**:右栏「子代理预检」一块(模式、是否生效、最近 7 天判断与"本来会推迟"次数),菜单「视图 › 子代理预检…」打开详情:
  模式切换、mod 与 OAuth 读数对表、最近判断、p80 覆盖率、避免的中断、待补派、校准价格。RPC 为 `budget.get` / `budget.setMode`;
  `enforce` 必须带 `confirm=true`,界面上要用户先勾选「我已看过下面的证据」再点一次。
- **看护**:Worker 的 `BudgetGateJanitor` 每小时清理 30 天前的日志、7 天前的心跳与 1 小时前的半截临时文件;config.json 丢了或落后于意图时
  按意图重写(只补不降,免得和界面刚做的切换交错时把旧模式写回);组织策略开始禁止旁加载时撤下登记。
- **快照**:`QuotaService` 每次拿到真实 OAuth 读数(GUI 开着时按刷新周期,Worker 布防时按探测周期)都导出一次;
  不为它新增任何 usage 请求,所以 GUI 关着且未布防时快照会变旧——mod 只把 30 分钟内的 5h/7d 当兜底,族周额度过了重置时刻自动作废。
  5h/7d 的承接值(同一周期更早的服务端读数)不导出:拿它兜底会偏低(少拦),对表也会把它当成刚观测;文件里的捕获时间若在未来(时钟回拨留下的坏值)照常覆盖。
- **证据汇总**:按行的 `ts` 取最近 7 天,同一行出现在多个分段只算一次,按文件长度与修改时间增量解析,超过 64 MiB 只汇总最近的部分并在界面标出;
  影子模式"本可避免的中断"只认 `stop_failure` 的 `rate_limit`(其它报错不算被限额打断),启用后同一工作流 agent 被反复推迟只算一次。

### G. 与续跑、通知的关系

| 通道 | 管什么 | 不管什么 |
|---|---|---|
| Desktop"重置后自动继续" | 打开着的会话撞 5 小时限额后重试那一轮 | 周限额;工作流 agent 不暂停 |
| AI Resume 续跑 | 已关闭、因限额中断的会话,额度恢复后逐项目续跑 | 打开着的会话 |
| budget-gate | 自己推迟的子代理:重置后补派提醒 | 撞限中断(交给上两条) |

AI Resume 续跑拉起的 `-p` 会话同样加载 mod(用户级 env);续跑门禁的"最小有用余量"建议(报告 §5)不在本 ADR 范围。

## 3. 上游盘点(先查已有,再决定自研)

- **Claude Code mod API(本机 2.1.309)**:`session.measure`/`$.session.usage()` 给 5h/7d 读数(没有按模型族的窗口);
  `agent.spawn` 覆盖 Agent 工具、队友、续派与工作流每个 `agent()`,可拒绝;`turn.step` 给逐次 usage;`tool.call` 带 `agentId`。
  数据与拦截点上游都有,缺的是决策语义。`$.store` 跨会话共享但并发写语义未文档化,改用每会话一份心跳文件。
- **agent-usage-guard 1.1.3(MIT)**:语义是询问用户、限计数(并发 4、10 分钟 12 个)、固定阈值,还拦 ≥50 万上下文的提示与工具——
  与"不弹窗、不限数量、上下文不改"直接冲突,不安装、不 fork;借鉴其"闸门与预留同一步"、peer 记录与 fail-open 包装。
- **官方工作流撞限暂停**:只在交互式 CLI;Desktop、`-p`、SDK、Remote Control 都不暂停。周限额重置超过 24 小时也不暂停。
- **statusline `rate_limits`**:与 mod 读数同源,只适合命令式钩子方案(报告方案 E,mod 不可用时的降级备选)。
- **`CLAUDE_CODE_SUBAGENT_EFFORT`**(2.1.316+):本机 2.1.309 没有;会改变质量,是否使用由用户决定。
- 结论:自研决策层;数据与拦截全部走官方 mod API。

## 4. 实证(2026-11-02,本机 2.1.309)

临时目录、haiku、`--setting-sources project`(不加载用户级 hooks,免得发出完成通知)的 `-p` 会话,另加真实 Desktop 会话的 V0 记录:

- **Desktop 下有读数**:安装 V0 后第一个 Desktop 会话(opus-5-5)记录到 5h 96→97→98%、7d 11%;Desktop 会话的提示来源也是 `sdk`,
  `surface` 为 null、`isInteractive` 为 false(不弹任何对话框)。
- **读数分辨率与频率**:目前见到的全是整数;同一轮里每涨满一个整点推送一次 `session.measure`(高峰期间隔 34–60 秒),每次请求后
  `$.session.usage()` 也是最新值。
- mod 在 `-p` 下加载正常;工作流完成以 `task-notification` 来源的 `prompt.submit` 回主循环。
- 工作流 agent:`agent.spawn` 按 agentIndex 顺序触发,`subagentType` 为 `workflow-subagent`,模型写的脚本提示可见(非占位);
  并行批次第 2 个起等第 1 个写好提示缓存(约 2.6 秒)才真正启动,`next()` 在启动时才返回。
- 被拒语义:`parallel()` 里被拒位置为 `null`,run 继续;顺序 `await agent()` 被拒抛
  `Workflow agent spawn denied by a plugin: <理由>`,脚本可捕获,后续 agent 照常编号运行。
- 软着陆:子代理把工具结果里"不要再调用工具、写部分完成"当成提示注入拒绝执行(仍很快收尾);改成只陈述事实后,只调用 1 次工具就如实收尾。
- 全新会话第一次派发时 `$.session.usage()` 仍为空(读数要等第一个响应处理完),所以读数来源链加了 AI Resume 快照兜底。
- **MSIX 文件系统虚拟化**:Desktop 是 MSIX 包,它拉起的进程(Claude Code 及其工具、从会话里运行的程序)在 `%LOCALAPPDATA%`
  下**新建顶层目录**会被重定向到 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`,包外进程看不到;
  写进已存在的真实目录落在真实位置。8 月从会话里运行 install 留下的 `.airesume-install-*` / `.airesume-backup-*` 就在那里。
- 报告 §7 的 10-31 01:05 调研 ×8 发生在美西周五 10:05(高峰),实际全部完成未撞限;按高峰先验 ×2.3 计算会只放 4 个——
  高峰先验可能偏高,由在线校准覆盖;影子模式要盯的正是这类"估大"。
- **价格实测**:V0 期间本机 Desktop 会话的真实记录离线回放,opus-5-5 非高峰 5 小时窗口中位 1.581 点/百万等价 token(39 段)、
  7 天窗口 0.178(11 段),种子分别是 1.34 与 0.19——5 小时窗口比种子贵约 18%,在线校准会覆盖种子;高峰时段还没有足够的干净样本。
- **运行中的会话不锁 mod 文件**:已加载 mod 的会话在跑时把 `hooks/register.ts` 改名再改回成功,覆盖安装与卸载退役不会被占用挡住;
  经 `CLAUDE_CODE_PLUGIN_DIRS` 加载的目录里也没有引擎生成的 `types/`(不像热重载目录那样写类型文件),安装目录不会冒出未知文件。

## 5. 已知缺口与风险

- 单个子代理的成本仍然不准(任务本身的随机性),策略只能止损不能扩容。
- 读数分辨率:目前只见过整数;校准按 1 点分辨率设计(单段 ≥ 3 点),需要 25–30 分钟活跃使用才收敛。
- 网页、手机聊天与别的电脑的消耗看不到,只会让价格偏大(保守)。**安装后、旧会话重开之前**同理:
  只有新启动的会话加载 mod,旧会话的消耗进不了全机账本,这段时间学到的价格偏贵、判断偏保守。
- 全新会话第一次派发、且没有别的会话心跳和新鲜快照时,没有读数可用,按现状放行(no-readings)。
- 检查点协议(spawn 时改写提示、agent 把进度写盘)本轮未实现:需要处理子代理写盘的权限提示(否则弹窗),留作后续;
  软着陆下工作流 agent 的"部分完成"答复会被 journal 当作完成缓存,续跑时不会重跑。
- Desktop 自动续跑的提示是否经过 `prompt.submit` 还没观测到(要等一次真实撞限后的自动继续);mod 已按 `limitLike` 与来源记录,届时核对。
- 经 `CLAUDE_CODE_PLUGIN_DIRS` 加载的 mod 是否热重载未核实:覆盖安装后,已开着的会话可能仍跑旧版 mod,界面按心跳里的 mod 版本显示「部分生效」或「配置未跟上」,重开会话即可。
- mod API 仍是早期访问,每周发版;用录下来的真实事件做测试,遇到不认识的形状一律放行。
- 心跳与日志文件不会自删(mod API 没有删除),由 AI Resume 定期清理。

## 6. 否决

- 直接安装或 fork agent-usage-guard(语义冲突,见 §3)。
- 命令式钩子 + statusline 缓存(报告方案 E):管不到工作流里的每个 agent,只作为 mod 不可用时的备选。
- 另开仓库:用户决定放进 AI Resume 仓库,随 AI Resume 安装与可视化。
- 默认启用拦截:违背"V3 由用户看过证据后亲手打开"。

## 7. 验证边界

- 离线:mod 核心纯函数 + 引擎 + 回放 + 适配层(假 `$` 驱动钩子)的 Node 测试(fixture 为真实录制,会话 UUID、项目键与提示哈希已换成合成值);
  `dotnet test` 覆盖快照导出(断言不含凭据、承接值不导出)、env 条目增删、组织策略拦登记、已关闭撤登记、证据汇总(部分生效、旧版 mod、去重、限额打断判据)、
  模式持久化与只补不降、看护清理、GUI 视图模型(「无心跳不显示生效」等)。
- 对抗性审查:mod 与 C# 控制面各一个独立只读审查子代理,所提问题逐条复核并修正(fail-open 路径、判断与预留的原子性、/clear 后的日志与心跳、
  组织策略、卸载顺序、生效判定、证据去重与限额判据等),修正都有对应用例。
- 真机(临时目录、haiku):加载、影子判断、启用推迟、软着陆四个场景。
- 真机(待真实使用):Desktop 下的读数与派发、自动续跑提示、影子模式 1–2 周的 p80 覆盖率(验收 75–85%)与"本来会推迟"比例。
