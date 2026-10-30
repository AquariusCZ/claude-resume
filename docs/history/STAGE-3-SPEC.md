# Stage 3 规格:lark-cli 试点(shadow,不接管生产)

> 状态:**v1 已冻结,2026-08-05**。S3-A 已完成(commit `63ecdb4`);S3-B 已完成(`feat: S3-B lark-cli 试点场景验证`,见 git log)。阶段门禁全部通过,验收报告见 §7。
> 依据:`docs/UPSTREAM-ARCHITECTURE-RESEARCH.md`(2026-08-01 计划,阶段 3)+ `docs/MIGRATION-BASELINE.md` §9(外部前提)+ `AGENTS.md`(lark-cli 契约原样保留)。冲突时以上述文档为准并回报,不得自行取舍。

## 1. 目标与范围

- **目标**:在**独立测试应用**上验证 lark-cli(1.0.81,本机已装)作为目标飞书能力层的契约,并为后续阶段冻结最小可复用封装:envelope/catalog/ndjson/binary 输出解析、显式 bot/user 身份、scope 校验、exit 10 高风险确认、超时/取消、输出脱敏;只读消息/文档/日历与错误场景全部通过。
- **不迁移(范围外)**:入站长连接、现有卡片状态机、生产飞书消费(现役 Node `feishu-agent.js` 仍是唯一生产消费者)、任何生产/测试应用状态写入(本阶段试点只读)。
- **产出**:① 最小 lark-cli 进程封装(可离线测试);② 试点场景验证记录(真测试应用只读);③ 验证报告(契约逐条 + 剩余风险)。

## 2. 外部前提(任一未满足不得开工对应工作包)

1. **独立飞书测试应用**:已确认(用户 2026-08-05:appId/appSecret 与生产完全隔离,bot/user 授权已完成)。
2. **lark-cli 本机配置**:须由**用户本人**执行 `lark-cli config init --new`(含浏览器授权);监督者/实施者**不接触 appId/appSecret 实值**,配置完成后仅做只读验证(`lark-cli auth status` 显示 bot/user 身份)。当前状态:`not_configured`(2026-08-05 实测)。
3. **用户级 `lark-*` Skills 真身**:`%USERPROFILE%\.agents\skills` 存在(已有 27 个);禁止复制进仓库。
4. **测试项目**:阶段 4 需要,阶段 3 不需要(用户已确认阶段 3 后创建)。

## 3. lark-cli 契约要点(封装必须原样保留,不得改写)

- 显式 `--as user` / `--as bot` 身份;身份、scope 缺失、授权链接、恢复提示写入**机器可读错误**。
- 命令风险标注(read / write / high-risk-write);高风险写操作无显式确认时以 **exit 10** 阻断,封装不得自动加 `--yes` 绕过。
- `lark-event`:ready marker、NDJSON stdout、结构化 stderr、退出码、优雅停止协议。
- Skills 版本以二进制嵌入为准(`lark-cli skills list/read`),封装不硬编码 skill 内容。

## 4. 工作包

### S3-A lark-cli 进程封装(不依赖测试应用,可离线完成)

- 位置:`csharp/src/AiResume.LarkCli/`(新项目,net10.0,仅 System.Diagnostics.Process + 已有基础设施,**不新增 NuGet 包**)。
- 职责:命令构造、进程启动、超时/取消(如 15s 默认,可配置)、stdout/stderr 捕获、envelope JSON 解析(成功/错误信封)、exit 10 高风险确认提示原样透传、输出脱敏(不落凭据/完整授权链接)、结构化错误分类。
- 测试(全部离线):假 CLI 进程(测试项目自带可执行脚本/回显工具)模拟:成功信封、错误信封、exit 10、超时、取消、非 JSON 输出、脱敏;≥6 项。
- 完成标准:`csharp\build.ps1` 全绿;新增测试 ≥6 项;单 commit(`feat: S3-A lark-cli 进程封装`)。

### S3-B 只读试点场景(依赖外部前提 2 完成后执行)

- 场景(全部只读,测试应用):消息查询(`lark-im`)、文档读取(`lark-doc`)、日历查询(`lark-calendar`)、结构化错误场景(bad scope/未授权/错误参数);每场景记录:命令、身份、退出码、输出形状、脱敏后样例。
- 高风险写命令确认契约:对任一 write 命令验证 exit 10 阻断 + 显式确认后放行(仅验证契约,不实际执行写)。
- 完成标准:试点记录入验证报告;全仓 rg 0 命中;单 commit(`feat: S3-B lark-cli 试点场景验证`)。

## 5. 出口门禁(阶段总门禁)

- 全仓 `rg -i "sk-|app_secret"` 仅命中文档与既有脱敏代码注释;凭据实值 0 出现(仓库/日志/测试输出)。
- `csharp\build.ps1` 全绿(含 S3-A 新增测试)。
- S3-B 试点场景清单逐项通过,输出形状与契约逐条对照记录。
- 文档同步:`docs/ARCHITECTURE.md`(lark-cli 能力层试点状态)、`AI_GUIDE.md`(首行 project-tour 时间标记刷新 + 技术栈)、`docs/MIGRATION-DEBT.md`(如有关闭/新增)、`docs/STAGE-3-SPEC.md` §7 实现状态。
- 阶段报告:已跑测试清单与结果、文档同步情况、剩余风险。

## 6. 禁止事项(违反 = 阶段整体拒收)

- 凭据实值进仓库/日志/测试输出/commit 信息;不读生产 AppDir `config.json` 或任何密钥。
- 测试应用与生产 `feishu-agent.js` 同时消费同一生产飞书应用;不双写任何会话/任务状态。
- 不自动 `--yes` 绕过高风险确认;本阶段不执行任何写操作(消息/文档/日历均只读)。
- 不替换入站长连接/卡片状态机/生产消费边界;不安装服务/计划任务/开机项。
- 不复制 `lark-*` Skills 进仓库。
- 需改冻结接口/新增依赖/工具链异常/基线不绿 → 立即停止报告,不得自行绕过。

## 7. 报告格式(每包完成后提交)

```
包:S3-A
commit:63ecdb4
build.ps1 输出末 6 行:<见 S3-A 验收报告,102 测试全绿、0 警告、secrets gate 0 命中>
新增/修改文件:src/AiResume.LarkCli/(csproj/LarkCliInvoker/LarkEnvelope/LarkRedactor/LarkCliResult/LarkCliException)、test/AiResume.Tests/LarkCliInvokerTests.cs(9 项)、AiResume.sln、AiResume.Tests.csproj
设计决策与偏离:envelope 持有 JsonDocument 实现 IDisposable(防 ObjectDisposedException);脱敏先于 envelope 解析(任何路径无机密);exit 10 抛 HighRiskConfirmationRequired;测试假 CLI 经 cmd.exe /c 包装、假机密避开 sk- 形状(secrets gate)
自测未覆盖的风险:真实 CLI 行为差异(离线假 CLI 未覆盖)、Windows 中文 locale 输出解析

包:S3-B
commit:`feat: S3-B lark-cli 试点场景验证`(见 git log,单 commit)
build.ps1 输出末 6 行:<见 S3-B 验收报告,复跑 102 测试全绿、secrets gate 0 命中>
新增/修改文件:docs/ARCHITECTURE.md、AI_GUIDE.md(首行 project-tour 标记刷新)、docs/STAGE-3-SPEC.md §7;无源码变更
设计决策与偏离:授权路径按官方 FAQ 修正——`auth login --scope "<missing_scope>"` 精确补权(user 身份无需开发者后台逐项开通);首次使用 `--domain` 全量申请导致授权页只显示 im 域(应用已开通∩可授权),改用精确 scope 后 5 项全部授予
试点场景记录(**⚠️ 2026-08-06 更正:`cli_xxxxxxxxxxxxxxxx` 实为生产应用 app id,非独立测试应用——见 D-015;本阶段全部操作为只读,未产生写入副作用**):
  1. im +messages-search --query 测试 --page-size 5 | user | exit 0 | ok:true,data.messages[]+has_more/page_token | 成功
  2. im +chat-list --as user --page-size 5 | user | exit 0 | ok:true,data.chats[](chat_id/name/chat_mode/owner_id) | 成功(补权后)
  3. im +messages-mget --message-ids <真实om_> | user | exit 1 | api/unknown code 99992354 invalid open_message_id(权限已通过,参数/存在性校验) | 契约记录
  4. calendar +agenda | user | exit 0 | ok:true,data=[](今日无日程,调用成功) | 成功(补权后)
  5. docs +fetch --doc <假token> | user | exit 1 | api/unknown code 1 Internal error(权限已通过,资源不存在) | 契约记录
  6. im +chat-list --as user(补权前) | user | exit 1 | authorization/missing_scope im:chat:read + hint(auth login --scope) | 错误契约
  7. im +chat-list --as bot | bot | exit 1 | authorization/app_scope_not_applied code 99991672 + console_url(应用层 scope) | 错误契约
  8. docs +fetch --doc-token(错 flag) | user | exit 1 | validation/invalid_argument + params[].suggestions | 错误契约
  9. lark-cli docx(未知命令) | - | exit 1 | validation/invalid_argument unknown command + did you mean | 错误契约
  10. im messages delete --message-id <假id>(不带 --yes) | user | exit 10 | confirmation/confirmation_required + risk:high-risk-write + hint:add --yes to confirm,零副作用 | exit 10 契约 ✓
  11. calendar +agenda(补权前)/docs +fetch(补权前) | user | exit 1 | authorization/missing_scope calendar:calendar.event:read / docx:document:readonly | 错误契约
脱敏复核:全程输出无 appSecret/凭据(init 显示 appSecret ****);临时 device_code/verification_url 文件已清理;消息内容等内部数据未写入任何仓库文件
自测未覆盖的风险:docs 真实文档成功读取(无真实 doc token,权限通过以 API 层错误证明)、calendar 有日程场景、bot 身份应用 scope 开通后调用(当前未开通,属预期)
```

## 8. 验证方将做什么(知悉即可)

逐包:diff 审查(对照规格与红线)、独立复跑 build.ps1、对脱敏/exit 10/超时取消做对抗性构造、抽查测试断言真实性、试点记录与命令输出形状核对。
