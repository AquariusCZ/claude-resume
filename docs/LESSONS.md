# 踩坑记录 & 开发经验(为以后项目准备)

这份文档记录 AI Resume 开发过程中**真实踩过的坑、失败的尝试、以及最终的正确解法**,并尽量指到代码位置。目的是:以后做类似项目(Windows 后台服务 + Claude Code / OpenCode 等 AI CLI + 飞书)时,别再踩同样的坑。

> 阅读方式:第一~十三节大体是「现象 → 原因 → 解法 → 代码位置」,第十四节起是「现象 → 根因 → 规则 → 代码位置」。带 ★ 的是最隐蔽、最花时间才定位的坑。
>
> **时代背景**:第一~八节来自 v1(Windows PowerShell 5.1 GUI/引擎 + Node 飞书机器人,运行目录 `%LOCALAPPDATA%\ClaudeResume\`)。v1 运行时已于 2026-08-08 删除,文中的 `picker.ps1`、`lib.ps1`、`checker.ps1`、`feishu-agent.js`、`test/*.js` 只能在 git 历史里查到;教训本身仍然有效。第九节起是 C# v2(运行目录 `%LOCALAPPDATA%\AI Resume\`)。cc-connect 已于 2026-10-30 随 ADR-0006 整体退役,涉及它的条目只作历史背景,其中的通用纪律仍然有效。

---

## 一、Windows PowerShell 5.1 的坑

GUI(`picker.ps1`)和引擎(`checker.ps1`/`lib.ps1`)跑在 **Windows PowerShell 5.1**(不是 pwsh 7)。5.1 有一堆和 7 不一样的陷阱:

1. **★ `Start-Process -PassThru` 的 `.ExitCode` 退出后变 `$null`** —— 这是"自动续跑从不触发"的元凶,查了很久。
   - 现象:探测明明成功,却走进"探测未就绪 (exit-)"分支,fail-closed 循环永远不 fire。
   - 原因:进程退出后再读 `$p.ExitCode` 得到 `$null`(`WaitForExit(ms)` 和 `HasExited` 轮询都一样,句柄只开了 SYNCHRONIZE)。`$null -ne 0` → 判为失败。
   - 解法(双保险):① 启动后立刻 `$null = $p.Handle` 缓存一个可查询句柄;② **成功判定根本不看 exit code**,改看 stream-json 的 `"type":"result" … "is_error":false`。
   - 位置:`lib.ps1` `Test-ClaudeReady` / `Invoke-ClaudeResume`;同样的教训后来在 `feishu-agent.js` 的 `runClaude` 里贯彻(Node 也不信 exit code,只信 result 行)。

2. **★ `Set-Content -Encoding UTF8` 会写 BOM,Node `JSON.parse` 直接崩** —— 飞书 agent 反复"缺少 feishuAppId,退出"。
   - 原因:PS 5.1 的 UTF8 编码带 BOM(`﻿`),Node `JSON.parse` 遇 BOM 抛错 → agent 每次重启即死。
   - 解法:PS 侧一律 `[System.IO.File]::WriteAllText(path, json, (New-Object System.Text.UTF8Encoding($false)))`(无 BOM);Node 侧读 JSON 时统一剥 BOM:`.replace(/^﻿/, '')`。
   - 位置:`lib.ps1` `Set-CcuConfig`/`Set-CcuState`;`feishu-agent.js` `readJson`。**凡是 PS 写、Node 读的 JSON 文件,两头都要做。**

3. **`ConvertTo-Json` 对单元素数组会"拆包"成标量 —— 但只在裸数组管道时**。
   - `@('x') | ConvertTo-Json` → `"x"`(标量!);而 `[pscustomobject]@{a=@('x')} | ConvertTo-Json` → `{"a":["x"]}`(安全)。
   - 影响:授权名单 `feishuAuthOpenIds` 若被拆成标量,Node 侧 `Array.isArray` 为 false → 当作"未锁定"→ **解锁所有人**(安全事故)。实测确认:我们走的是"对象属性数组"路径,PS 5.1 下**不拆包**,空数组也序列化成 `[]`,安全。
   - 教训:任何"是否为数组"决定安全策略的地方,序列化后要实测一遍。

4. **`ConvertFrom-Json` 把 ISO-`Z` 时间偷偷重定向到本地时区(~8h 坑)**。
   - 解法:重置时间一律存 **Unix 整数**(`resetsAt`),整数往返 JSON 不变;读回用 `FromUnixTimeSeconds` 对 `UtcNow` 比较。
   - 位置:`state.json` 的 `realFiveHourResetUtc` 等;`Save-RealResetFromProbe`。

5. **`.ps1` 文件必须存成 UTF-8 with BOM**,否则 5.1 解析中文注释/字符串出错。(和上面第 2 条相反——脚本文件要 BOM,数据文件不要 BOM。)

6. **`Get-Content` 默认编码会丢中文** —— 项目发现里中文文件夹名消失。解法:读 `.jsonl` 用 `-Encoding UTF8`。位置:`lib.ps1` `Get-ClaudeProjects`。

7. **WPF 事件回调里 `.GetNewClosure()` + `$script:` 自引用会拿到 `$null`**。
   - 现象:授权窗口点"移除"后列表不刷新。
   - 原因:GetNewClosure 把 scriptblock 绑到新的动态模块,里面的 `$script:authRender` 解析到新模块作用域(空)。
   - 解法:在 render 顶部把它抓成局部 `$self = $script:authRender`,回调里 `& $self`。位置:`picker.ps1` `Show-AuthWindow`。

8. **★★ WPF 主窗截图通过,不代表动态弹窗真的能打开;catch 还会让 smoke 假绿**。
   - 事故:AI 设置窗使用 `x:Name`,却漏了 `xmlns:x`;点“当前 AI”必然 XML 解析失败。旧 `-SelfTest` 只是调用函数,异常被 catch 成状态提示后仍退出 0,所以静态截图和 smoke 都没有发现。
   - 同时,右栏 Chip 把内容固定成 272px,模板内部又硬加 28px padding;在真机 150% DPI 下“立即刷新”“15 分钟”被水平裁切。
   - 解法:动态 XAML 必须独立 XML parse + 真正 `XamlReader.Load`;`-SelfTest` 通过按钮事件打开弹窗、逐项切换 8 个模型、比较 ContentPresenter 与内容实际宽度,失败返回非零。再用外部 UI Automation 对**运行目录中的真实窗口**执行 Invoke/Selection,并在实际 DPI 截屏。Chip 模板只使用 `TemplateBinding Padding`,内容用 `* + Auto` 列自适应,不写与容器等宽的固定 Width。位置:`picker.ps1` `Show-AISettingsWindow` / `Assert-ChipContentFits`。

9. **启动 `.cmd` 不能用 `Start-Process claude.cmd -RedirectStandardOutput`**(UseShellExecute=false 无法 exec .cmd)。解法:`cmd.exe /c claude.cmd …` + tail 重定向文件 + 递归按 `ParentProcessId` **杀整个进程树**(返回的 PID 只是 cmd 外壳)。(注:经 `cmd.exe /c` 起的进程,多行参数会在换行处被截断,见第二节第 1 条与第十四节第 1 条。)

---

## 二、Claude Code CLI(headless `-p`)的坑

飞书 agent 和续跑引擎都是无头调用 `claude -p --output-format stream-json --verbose`。这里的坑最多、最隐蔽:

1. **★★★ Windows `cmd` 把 `-p "多行文本"` 在第一个换行处截断** —— 查询"总是失败"的真凶,绕了好几版才定位。
   - 现象:飞书里问项目问题,claude 回"我只看到作答策略,没有具体问题内容";agent 侧报"未拿到成功结果"。
   - 原因:prompt 作为命令行参数 `-p "<framing>\n\n<question>"` 传,`cmd /c` 在第一个 `\n` 处截断参数 → claude **只收到了 framing、没收到问题**。
   - 为什么难查:手动复刻时我用的是**单行** prompt,侥幸绕过,复刻一直"成功";真实 agent 用多行 framing 才暴露。**教训:复刻 bug 必须用和线上完全一致的输入(尤其是否含换行)。**
   - 解法:**prompt 改走 stdin**,不走命令行参数。`spawn(..., {stdio:['pipe','pipe','pipe']})` 后 `child.stdin.write(prompt); child.stdin.end()`。stdin 不受命令行换行影响,原样送达。
   - 位置:`feishu-agent.js` `runClaude`(`args.push('-p', ...)` 不带 prompt;prompt 写进 stdin)。该修复曾用多行暗号做真实端到端验证;后续改动必须继续保持 prompt 只经 stdin 传递。
   - 后续:2026-10-30 同一个坑在 C# 的飞书投递路径(经 `cmd.exe /c` 调 lark-cli 的 npm 垫片)里第三次出现,见第十四节第 1 条。

2. **★ spawn 开着 stdin pipe 不写,新版 claude 会干等 stdin**(`"no stdin data received in 3s, proceeding without it"`),然后**不执行 `-p` 就空退出**(exit 0、无 result)。
   - 这条和上一条是同一次排查里先后出现的:先发现 stdin 干等(一度改成 `stdio:['ignore',...]`),后来因为要用 stdin 传 prompt,改成**主动 write + end**——一举解决"干等"和"换行截断"两个问题。

3. **`--session-id <uuid>` 对已存在的 id 会报错 `already in use`**(不是续接)。
   - 所以"每个项目一个固定查询会话"要:首次 `--session-id <固定uuid>`(创建),之后 `--resume <固定uuid>`(续接);用一个 started 标记文件判断走哪条。
   - **清空**查询会话必须**删掉 `.jsonl` 文件**(光删标记不够——下次 `--session-id` 又会撞 "already in use")。
   - 位置:`feishu-agent.js` `querySession`/`runProjectQuery`/`clearQuerySession`。

4. **★★ `claude --continue` 续的是「当前 cwd 里最近修改的那个会话」** —— 只读查询会**污染**工作会话。
   - 现象/风险:只读查询若在 `project.path` 里跑,会在同一 `~/.claude/projects/<cwd>` 文件夹留下 session,并成为"最新";之后"修改项目"的 `--continue` 就会**续到查询会话**,而不是你 VS Code 的工作会话,还会把改动写进大家共享的只读会话。
   - 实测确认过这个 bug(查询后 `--continue` 拿到的是查询会话的暗号)。
   - 解法:只读查询跑在**隔离 cwd**(`feishu-query-cwd/<sha1>`)+ `--add-dir <项目路径>` 授予读权限。查询记录落在独立文件夹,`--continue` 永远只续工作会话。实测:查询 jsonl 只在隔离文件夹,项目文件夹里只有工作会话。
   - 附带坑:隔离 cwd 命名要避开 `Clear-OldCaches` 的清理 glob(它只删名字**正好以 `ClaudeResume` 结尾**的探测文件夹,`...-feishu-query-cwd` 不匹配,安全)。

5. **项目的"会话列表"就在 `~/.claude/projects/<编码cwd>/*.jsonl` 里,每个文件 = 一次对话**,可以直接做成"选会话继续"的 UI:
   - **标题**:文件里有 claude 自己生成的 `{"type":"ai-title","aiTitle":"…"}` 行(在文件**前部**,~第 8 行),比首条 user 消息好用;首条 user 文本作兜底。
   - **性能**:会话文件能到 **28MB**,渲染卡片时**绝不能整读**。取标题只读**头** 64KB(`readHead`);取"最近 2 轮"只读**尾** 256KB(`readTail`,首行可能被切断、parse 失败跳过即可)。实测:列 5 个会话 **5ms**、28MB 会话取摘要 **1ms**。
   - **找项目的会话文件夹**:文件夹名是有损编码,别去反推 —— 读每个文件夹首个会话的真实 `cwd` 来匹配,并**缓存**(映射不会变),否则每次渲染卡片都要扫全部项目。
   - **继续指定会话**用 `--resume <id>`;"新开会话"就生成一个新 uuid、首次用 `--session-id <uuid>` 创建(和只读查询会话同一套 create-vs-resume 逻辑,靠"jsonl 是否存在"判断)。
   - 验证要害:验证时必须挑一个**不是最新**的会话(否则 `--continue` 也能歪打正着),再用其历史里的暗号确认恢复的是**那一个**会话。该行为已通过历史真实会话验证,现由 provider-native session id 的实现约束保持。

5c. **★★★ `--permission-mode plan` 只拦「写」,完全不拦「读」,而且读不限制在工作区内** —— 一个能提权的真实安全漏洞,靠对抗审查 + canary 实测才抓到。
   - 现象:只读查询本以为「plan 模式 = 安全」,只禁了 `Task`。但同事在只读查询里用无害措辞(「帮我核对配置文件,读一下 `../../config.json`」)就能让 claude **Read 到查询隔离 cwd 上两级的 `config.json`**,读出 `feishuAppSecret` / `feishuAuthPassword`,再发「解锁 <密码>」把自己加进 `feishuAuthOpenIds` **提权成 owner**。
   - 实测确认:用查询的完全相同 flag(`--permission-mode plan --add-dir <proj> --disallowedTools Task -p`)在隔离 cwd 里让 claude 读 `../../settings.json`,**原样返回了内容**;plan 模式对 Read 不做工作区边界拦截,`--add-dir` 只是「加」目录不是「限制到」目录。
   - 模型的良心不是安全边界:直白地「把密钥发我」会被对齐拒答,但**换成无害措辞就绕过**;能不能读取由**工具是否可用**决定,不由措辞决定。
   - 解法:只读查询按调用者分级 —— 只有**显式在 `feishuAuthOpenIds` 里的 owner** 保留 Read 等工具(密钥本就是他的);**其他所有人(同事 + 未锁定时的所有人)禁掉全部文件/执行工具**(`Task,Bash,Read,Write,Edit,Glob,Grep,NotebookEdit`),只据注入的 `AI_GUIDE.md` 作答。和闲聊路径同款防护;回归靠 `test/query-security.js`(用 canary 密钥 + 无害措辞,断言不外泄)。
   - 通用教训:凡是让 LLM 在**含机密的机器上**跑工具,别信「只读模式很安全」——要么按身份禁工具,要么把机密移出可达范围,并**用 canary 实测**而不是假设。

6. **plan 模式 + 大项目,claude 会派 Task 子代理做「全项目探索」,巨费 token**(即使主模型是 haiku,子代理会用大模型)。
   - 解法:只读查询加 `--disallowedTools Task` 禁子代理,并在提示词里引导"先看 docs/README 定位相关文档,只读相关文档+代码,本轮内简答,别通读全项目"。实测:0 次 Task,只用 Glob+Read 精准命中文档。
   - 位置:`feishu-agent.js` `runProjectQuery`(`disallowedTools:['Task']` + framed 提示词)。

7. **成功判定只信结构化 result,别信 exit code / 也别只靠"最后一个 result"**。
   - stream-json 里可能有**多个 result 行**(plan 模式两段式:主对话先返回"已启动探索任务"、子任务完成再返回最终答案)。scanLine 逐行扫,取最后一个 result 的 `is_error`。
   - 兜底:没有 result 但有 assistant 文本时,把 assistant 文本返回(别给用户空的"未拿到结果")。位置:`runClaude` 的 close 分支。

8. **claude 的 stderr 一开始被完全忽略,失败时一片空白无从诊断**。加上"失败时记录 exit code + stderr 尾 + 是否有 assistant 文本"后,才定位到"no stdin data received"。**教训:外部进程的 stderr 一定要留一份,失败日志才有线索。**(2026-10-30 又一次:lark-cli 的结构化错误信封写在 stderr,封装只读 stdout,见第十四节第 3 条。)

9. **`rate_limit_event` 只在窗口被用到一定程度才由服务器下发**(5h 窗口很空时根本不发 5h 数字)。所以额度显示要能处理"某个窗口暂时没数据"。位置:`Test-ClaudeReady` / GUI 额度 chip 显示 `5h 低`。

10. **★ IDE Hook 能力必须按版本和真实事件复核，不能把一次缺失写成永久结论。**
    - 历史现象:2026-08-14 的 Claude Code 2.1.232 实测中，`claude-vscode` 会话没有触发 `Stop` / `Notification`，而桌面应用会触发；当时据此记录了扩展完成通知缺失。
    - 新证据:2026-08-19 的 `claude-vscode` 会话已明确执行全局 `Stop` command Hook。VS Code 日志记录 Hook 返回，同一 session transcript 保留正确中文 cwd，11 秒后 Worker 以同一来源投递成功；本机证据覆盖 Claude Code 2.1.233/2.1.235 代次。官方现行文档也把 IDE extensions 列入相同 Hook 事件范围。
    - 结论:VS Code 扩展没有“原生飞书完成通知”，但它会加载 `~/.claude/settings.json`，从而通过 AI Resume 注册的 `AiResume.Hook.exe claudecode` 间接通知。2.1.232 的缺失是历史版本行为，至少从本机后续版本实证已经恢复。
    - 诊断:仍需同时关联 VS Code Hook 日志、Claude transcript、`completion-events` 和 `worker.notify.item`；`notify list` 全绿只证明配置可执行，不能单独证明某个宿主版本会产生事件。
    - 2026-10-30 补充:钩子已改为官方 exec form(`command` 只放 `AiResume.Hook.exe`,`args` 放参数),并新增 `StopFailure` 与真正等人的 `Notification` 订阅,见第十四节第 8 条。

---

## 三、飞书(Feishu)机器人的坑

> 本节是 v1 Node 飞书机器人的教训。v2.2 起 AI Resume 已没有任何飞书入站(聊天、卡片、菜单),飞书只用于经官方 lark-cli 发完成通知;「mock 必须照抄真实结构」「长任务进程死了谁告诉用户」等通用纪律仍然有效。

0. **★★★ 长跑任务的结果最容易丢在「进程被重启」上,而用户只看到心跳刷屏后一片死寂**。
   - 现场:某次「修改项目」跑了 11 分钟以上,飞书里堆了 40+ 条「思考中…(已 690s)」然后**永远没有结果**。查日志:全部日志里最长的「完成」记录只有 428s,那次运行**没有任何完成记录**——因为我在那期间部署重启了 node,in-flight 的 `bg()` 任务连同 child 一起消失了。
   - 三个独立的坑,缺一不可修:
     1. **心跳每 15s 发一条新消息** → 12 分钟 = 48 条,聊天被埋掉。解法:**一条进度卡片,原地 patch**(每 20s 更新已用时),一次运行只占 1 条消息;结束时把它 patch 成「✅ 已完成 · 用时 Xs(结果见下方)」,结果另发一张卡(保证在最底部可见)。
     2. **运行状态只在内存里** → 重启即蒸发,用户永远等不到答复。解法:运行开始写 `feishu-inflight.json`,结束删;**启动时读残留并主动告知**「上次运行被打断,改动可能已部分完成,去 VS Code 那个会话看看或再发一次」。
     3. **结果卡片发送失败时被静默吞掉**(`sendCard` catch 住只 return null)→ 整个答案没了。解法:卡片失败**自动回退纯文本**(纯文本还有分片逻辑,最不容易失败)。
   - 通用教训:凡是「长任务 + 异步回报」,必须回答三个问题——进程死了谁告诉用户?回报通道失败了有没有兜底?进度提示会不会把结果淹掉?
   - 位置:`startProgress`/`trackRun`/`reportInterruptedRuns`/`sendResult`;回归 `test/progress-image.js`。

0a. **★★ 同一个 SDK 里,不同接口的返回结构不一样;mock 猜错结构 = 测试全绿但线上静默失效**。
   - 实测(`@larksuiteoapi/node-sdk` v1.70):
     - `im.image.create`(上传图片)→ **顶层** `{ image_key }`,**没有** `code/msg/data` 外壳;
     - `im.message.create`(发消息)→ `{ code, msg, data:{ message_id } }`;
     - `im.messageResource.get`(下载)→ `{ writeFile(path), getReadableStream(), headers }`。
   - 代码按"都有 `.data`"写成 `res.data.image_key` → 永远 `undefined` → `drainImageOut` 拿不到 key 就**什么都不发、还把文件删了**:claude 生成的图在线上**静默消失**,而 mock 返回的是 `{data:{image_key}}`,单测一路绿。
   - 解法:①取值**两种结构都兼容**;②**mock 必须照抄线上真实结构**(这次直接用真 API 打一次、把 `Object.keys()` 打印出来对齐);③拿不到 key 时**必须出声**(日志 + 告诉用户查 `im:resource`),绝不静默删文件。
   - 通用教训:**mock 是你对 API 的假设,不是 API**。凡是"线上不工作但测试全绿",第一个怀疑对象就是 mock 与真实返回结构不一致——用一次真实调用打印结构来校准,成本 30 秒。(同一形状在 2026-10-30 又出现了两次,见第十四节第 3、4 条。)

0b. **飞书图片有两种事件形态**:单独发送是没有正文的 `image`;图片和说明放在同一气泡会变成带语言包的富文本 `post`。只处理 `image`,或用 `message_type !== 'text'` 一刀切,都会让合法图文问题在进入 AI 前消失。
   - 解法:`image` 仍用 `im.messageResource.get` 下载后按 **chat+sender** 挂起到该用户下一条文字;`post` 必须解析 `{zh_cn:{title,content:[[...]]}}`(并兼容扁平 body),同时提取段落文字和 `img.image_key`,最多下载前 6 张(单张 10MB),并把这些文件只绑定到当前事件。进入后台任务前同步 claim,否则后到图片会串进先到问题;群聊若只按 chatId 排队,viewer 的图还可能注入 owner 修改。上限必须施加在**最终合并请求**而非各自队列,否则 6 张暂存+6 张 post 仍会变成 12 张;合并时当前 post 图优先。
   - 图片文件必须有生命周期:忙碌/不支持/无权限立即回滚,运行结束删除,未消费孤儿满 24h 后由每小时扫描同时清磁盘和内存 Map;部分下载/超限提示只是告警,发送失败不能阻断剩余有效图文进入 AI。身份边界还必须在资源下载前读取有效配置;`readConfig()` 失败回 `{}` 会同时把 allowlist 和 owner 名单变空,等价于把所有人提升成 full。
   - 图片 prompt 不要硬编码某一个 provider 的工具名;提示当前 AI 使用 `view_image / Read` 等可用本地图片能力。
   - 权限缺失/其它类型(文件、语音)都要**明确回一句**,不要静默——静默是这类 UI 最糟的失败模式。
   - 只读用户的 run 禁了 Read,拿不到图 → 明说「只读用户看不了图片」并删掉文件,别假装收到了。

1. **★ 卡片回调(`card.action.trigger`)必须几秒内返回**,否则飞书报"目标回调服务超时未响应"。
   - 仅靠约定 `onCardAction` 内部不 `await` 不够:后续维护只要加回一个挂起的网络调用,整个 WS ACK 又会被拖住;async IIFE 也会先同步执行到第一个 `await`,文件扫描仍可阻塞。现在在**注册边界**用 `dispatchEvent` + `setImmediate`,保证在 handler 的同步前缀之前就返回;同时按 chat 串行 handler,否则“忘记查询”与紧随其后的新问题会并发,新问题可能恢复正在删除的 thread。`test/session-pick.js` 同时覆盖永久挂起 patch、150ms 同步忙等、毫秒 ACK 与同聊天严格顺序。

2. **飞书开发者后台三处配置是分开的,少一个都不通**:事件配置(订阅 `im.message.receive_v1` / `card.action.trigger` / `application.bot.menu_v6`)、回调配置(订阅方式设 **长连接**)、机器人自定义菜单;改完还要**发布版本**。嵌入式浏览器里配容易出错,用真实浏览器配。(v2.2 只发通知,事件订阅与长连接都不再需要;机器人仍要有发消息的权限。)

3. **★ 控制卡「堆叠」与「被抢」是一对相互矛盾的坑**,来回改了好几版才平衡:
   - 只用一张控制卡原地 patch → 延迟/重复的底部菜单事件会把**项目卡 patch 回主菜单卡**(被抢);
   - 让项目卡独立、菜单发新卡 → 菜单事件**堆一堆主菜单卡**(堆叠)。
   - 中间方案:底部菜单事件 = 重绘「当前该显示的那张卡」且不改 session(项目里幂等重画,积压投递不堆不抢);想回主菜单用卡上的「⬅ 主菜单」。
   - **当时的方案**:底部主菜单是“逃生口”,任何状态都重置为 idle 并在底部补一张新卡;普通导航由 `enqueueControlCard` **按 chat 串行写入**,任务执行时跟随实时 `lastCard`,它已被文字顶走就必须在底部新建,绝不回退 patch 入队时的旧卡。`controlCardEpoch` 让慢 patch 不能跨越普通消息的可见性代次重新登记为 live。项目卡绑项目 hash,会话卡再绑随机 token + profile;过期卡、独立模型卡和进度卡都不得抢占 `lastCard`。
   - 位置:`feishu-agent.js` `enqueueControlCard`/`presentControlCard`/`requestSessionCard`/`onBotMenu`;回归:`test/card-flow.js` + `test/session-pick.js`。

4. **飞书会「补投」用户之前积压的点击**(用户狂点几十次,事件会陆续到达),3 秒时间窗去重挡不住。所以关键是让重复/延迟事件**幂等**(见上条),而不是单纯去重。

5. **网络抖动与永久 pending 是两类故障**:日志里出现 `socket disconnected`/TLS 等明确瞬时错时可重试一次;但 Promise 一直不 settle 必须有硬超时,否则会话加载任务永不释放。现在普通飞书 API 7s,资源 HTTP 与下载落盘各 60s;超时状态不明时**不重试**。消息 create 每次逻辑发送固定 `uuid`;上传禁止重试并在超时时销毁文件流。出站图片只在发送确认成功后删除,失败必须保留路径,不得提示一个已被删的文件。

6. **单实例用 pidfile + `process.kill(pid,0)` 存活检查**,别用端口锁(Windows 允许两个 socket 共享 loopback 端口,端口锁不可靠)。位置:`feishu-agent.js` `anotherInstanceAlive`。

7. **WSClient 长连接不需要公网 IP**(相比 webhook 回调要公网),自建应用首选长连接。

8. **★★★ 事件 handler 里 `await` 长任务 = 整个机器人"白天卡死"**。SDK 的 WS 层收到事件后 `await eventDispatcher.invoke(...)` **等 handler 完成才给飞书回 ACK**;`onMessage` 里 `await runClaude`(查询/修改/闲聊,1~4 分钟)→ ACK 几分钟不回 → 飞书停止推送/反复重投 → 期间点什么都没反应(还制造了此前的"事件重复投递")。卡片回调早就懂这个道理(秒回 + fire-and-forget),但消息路径漏了。
   - 解法:**注册回调必须秒回,同聊天业务必须有序**。`dispatchEvent` 先让出事件循环并把 handler 接到 chat 级队列;长跑的 AI 工作再包进 `bg(label, key, work)` 离开短队列。清空/删除这类状态转换必须在 handler 内 await 完成,不能二次 fire-and-forget。并发守卫(`running`/`inflight`)的检查和预留仍必须留在 handler 的同步段(任何 await 之前),否则两条快速连发的消息会双跑。顺带:`spawnSync` 会冻住整个 node 进程(连 WS ACK 一起冻),事件路径上一律用异步 `execFile`。
   - 验证:`test/concurrency.js` —— 查询 handler 4ms 返回;查询进行中点菜单 2ms 响应、再发查询 2ms 回"进行中";原查询后台照常完成。**改了事件处理必跑它。**
   - 教训之二:handler 改成秒回后,**旧的 e2e 测试会虚假通过**("await onMessage 后立刻断言"只能看到回显消息,断言撞上回显里的原话)。所有 e2e 都改成"轮询等待最终结果消息再断言"。

9. **卡片切换那 ~0.5s 延迟是飞书 API 往返的固有开销,不是本地代码卡**。实测 `im.message.patch` 单次往返 **~550ms**(首次含 tenant_access_token 获取 ~1.6s)。**SDK 1.70 的长连接不支持卡片回调返回内联卡片/toast**,所以省不掉这次 patch 往返。但可以保证顺序与可恢复:① `cardHash` 跳过真正无变化的 patch;② `enqueueControlCard` 串行所有导航写入;③ patch 超时则补发当前最终状态;④ 会话枚举超过 250ms 时在**同一串行队列、同一控制卡**上 patch 加载态,不创建独立加载卡;加载态若补发了替代卡,最终选择页必须跟随新的 message id;枚举完成时先取消定时器再排最终卡,防止加载态反而覆盖最终结果;⑤ 会话摘要这类慢读在发送前必须复核 project/session/profile 快照,用户已切页面就静默丢弃。

---

## 四、部署 & 工程习惯

1. **★ 改了 `src/` 线上不自动生效** —— 机器人从 `%LOCALAPPDATA%\ClaudeResume\` 跑。改完必须:复制到 AppDir → 精确定位命令行含 `ClaudeResume\feishu-agent.js` 的 node PID并用 `taskkill /PID <pid> /T /F` 终止整棵进程树(VBS 守护约 8s 重启)→ 确认恰好 1 个该进程并在日志看到 `ws client ready`。不要只杀 node 父进程,也不要误杀机器上的其他 Node 服务。见 `CLAUDE.md`。**每次都容易忘,吃过好几次"改了没反应"的亏。**(v2 同样如此:改了 `csharp/` 必须重新 `install`,运行的是 `%LOCALAPPDATA%\AI Resume\` 里的副本,见 `CLAUDE.md`「部署」。)

2. **火绒(Huorong)会删 `.lnk → powershell -WindowStyle Hidden` 这种组合**。所有隐藏启动走 `wscript` 隐藏的 `.vbs` + 计划任务,不用那个模式。

3. **从 bash 工具调 `wscript` 用正斜杠路径会静默失败**,改用 PowerShell 工具 + 反斜杠路径。

4. **日志用本地时间**,别用 `new Date().toISOString()`(那是 UTC,会写错日期文件名)。位置:`feishu-agent.js` `logLine`。

---

## 五、日志系统(踩过的坑 + 现在的约定)

日志被反复弄坏过好几次(空白、错日期、乱码、失败无信息)。这里一次讲清:**有哪些日志、写在哪、怎么读、踩过什么坑**,以后照这个来,别再弄错。(本节的文件与函数都是 v1 的;v2 的日志在 `%LOCALAPPDATA%\AI Resume\state\logs\`,「失败必留证据」「绝不用 UTC 拼文件名」等约定照旧。)

### 有哪些日志(都在 `%LOCALAPPDATA%\ClaudeResume\logs\`)
- `run-<yyyyMMdd>.log` —— 续跑引擎(`checker.ps1` / `lib.ps1` 的 `Write-CcuLog`),按**本地日期**每天一个。**GUI 主窗口 + 弹出大窗显示的就是它。**
- `feishu-<yyyyMMdd>.log` —— 飞书 agent(`feishu-agent.js` 的 `logLine`),按**本地日期**每天一个。
- `feishu-stdout.log` —— node 进程的 stdout/stderr(SDK 连接日志);`feishu-launch.vbs` 重定向,>1MB 时重启前删,`Clear-OldCaches` 另 cap 2MB。
- `gui-error.log` —— GUI 自身异常。
- 导出日志(导出按钮)= 合并所有 `run-*.log` + `gui-error.log` 成一个 **UTF-8 带 BOM** 文件(方便任意编辑器打开中文)。

### 踩过的坑(按中招顺序)
1. **★ 跨天日志空白(最坑,反复中招)**:GUI 启动时把 `$script:logFile` 固定成 `run-<开窗那天>.log`,但 checker 写的是 `run-<当天>.log`。**过了午夜**,GUI 还在读昨天那个(空)文件 → 日志区空白,"预演完成"却看不到内容。
   - 修:GUI **永远读最新的** `run-*.log`(`Get-CurLogFile` = 按 `LastWriteTime` 取最新),清空日志也清最新那个。**绝不**在启动时把日志文件名固定死。位置:`picker.ps1` 的 `Get-CurLogFile` / `Read-LogTail` / `BtnClearLog`。
2. **★ UTC 写错日期/差 8 小时**:agent 早期用 `new Date().toISOString()`(UTC)拼文件名/时间戳 → 写进**前一天**的文件、时间也差 8h。修:一律本地时间(从 `new Date()` 的 getFullYear/getMonth/getDate/... 拼)。位置:`feishu-agent.js` 的 `logLine`。
3. **清空日志 + 解除布防后一片空白**:清空后 checker 已解除、不再写,GUI 显示空,像坏了。修:空日志时显示占位提示,别让用户以为崩了。
4. **中文乱码**:读日志没指定编码 → PS 5.1 按本地代码页解码,中文乱。修:`Get-Content -Encoding UTF8`(及 `[IO.File]::ReadAllText(...,UTF8)`)。位置:`Read-LogTail` / 导出。
5. **★ 外部进程 stderr 被吞,失败像黑盒**:`runClaude` 早期 `child.stderr.on('data',()=>{})` 完全忽略 claude 的 stderr,查询失败时日志毫无线索。修:收集 stderr,失败时把 **exit code + stderr 尾 + 是否有 assistant 文本** 写进日志——正是靠它才定位到"no stdin data received"和 cmd 换行截断。位置:`runClaude` 的 close 分支。
6. **日志无限增长**:node 的 stdout 只涨不清。修:vbs 重启前 >1MB 删;`Clear-OldCaches` cap 2MB;`run/feishu-<date>.log` 保留 30 天。
7. **彩色日志的坑**:GUI 用 TextBlock 的 `Inlines` 按级别(info/ok/warn/error/launch/stream)着色(`Set-LogColored`),不是纯文本。改日志**行格式**时,注意别破坏着色用的正则/前缀,否则颜色乱或整块变默认色。

### 当时的约定(v1;v2 沿用其中的原则)
- **写**:引擎用 `Write-CcuLog`(本地时间、`run-<当天>`),agent 用 `logLine`(本地时间、`feishu-<当天>`)。**绝不用 UTC**。
- **读/显示**:一律经 `Read-LogTail`(读**最新** `run-*.log`、UTF-8)。**别再引用启动时固定的日期文件名。**
- **失败必留证据**:任何外部进程(claude 等)失败,必须把 exit code + stderr 尾记进日志。
- **新长期文件**要纳入 `Clear-OldCaches` 的清理与容量上限。

## 六、交互设计上的经验

1. **默认「什么都不做」(idle)**:机器人一进来不主动跑,等用户点卡片选模式,避免手滑乱花额度 / 误改项目。

2. **「先选模式,再对话」**:进项目先选 只读/修改,而不是默认改。用户原话:"能直接对我的项目做出修改太可怕了"。

3. **长任务必须持续可见,但不能刷屏**:旧实现每 15s 新发一条“思考中”,12 分钟就堆约 48 条。当时的实现是 `startProgress`:每次运行只发**一张进度卡**,约 20s 原地 patch 已用时,结束后把同一张卡改成完成态,结果另发到底部;同一次运行的 tick/stop 必须串行,否则慢 tick 会在完成 patch 之后落地,把卡片改回“进行中”。但本地超时不等于 SDK 请求被取消:tick 若已超时,最终态必须另发新卡,避免迟到的旧请求覆盖。`trackRun` 负责进程重启后的中断告知。回归:`test/progress-image.js`。

4. **用量透明**:每条结果末尾报 `⏱ 耗时 · 输出 N tokens · ≈ $成本`,用户能感知每次问答的开销。位置:`runClaude` 采集 usage/cost、`fmtMeta` 拼接。

5. **权限分级要给"看得见的名单"**:飞书后台看不到我们的授权名单(那是我们 app 的逻辑,存 `config.json`),所以在 GUI 里做了「授权用户」窗口来查看/移除。**别让用户去一个根本没有该信息的地方找。**

6. **权限模型别过度设计**:一开始做了 full/viewer/none 三级 + 逐个授权 viewer + owner 审批卡片。真实场景("把机器人开给同事,他们只能看不能改")其实只要**两档**:owner(在名单里)能改,**其他所有人自动只读**,不用逐个授权。简化后 `authLevel` 只判"是不是 owner",viewer 名单作废。教训:先问清真实使用场景,别先堆权限层级。（安全前提:owner 名单**非空**——空 = 未锁定 = 人人能改,GUI 会警告。）

7. **总时限必须按任务语义划分,而“无上限”必须一路落实到 OS 和崩溃边界**:修改/一次性执行/后台续跑都不设总时限,查询/闲聊各有一份共享 deadline;零值不能写成 `opts.timeoutMs || default`,计划任务必须 `PT0S`。后台每次布防生成 `armCycleId`,state 带 `cycleId`,连初始化分支也不能用 `-Force` 越过新周期。只在写文件时加锁仍不够:锁外读取的旧对象会在等待后覆盖新值;Node、GUI、checker 必须共用 create-exclusive 锁,在锁内重新读最新配置并只改职责字段。spawn 前先写 `launching` 意图,启动后升级为含父/子 PID、runKey、项目和强制启动时间的 active 登记;完整临时代也要参加恢复。进程探测必须是 found/gone/failed 三态,CIM failed 绝不能当 gone;父 PID、时间、签名或 `taskkill` 任一未确认就保留登记 fail-closed。测试还要同时隔离 AppDir 和 Claude projects 根目录,否则“离线回归”会清理真实探针会话。

8. **★★★ “已配置/命令存在”不是“服务可用”**:GUI 曾用 `openaiApiKey` / `deepseekApiKey` 是否非空显示绿色「已配置」,Claude 只要找到 `claude.cmd` 就显示绿色「本机可用」。结果 Claude 实际已退出登录(`Not logged in · Please run /login`),界面仍说可用,探测失败后额度 chip 甚至回退成「空闲」。修法:启动和手动刷新都跑真实最小请求;只有请求成功才绿色,失败按 auth/billing/rate-limit/network/timeout/model/missing 分类。Claude 失败原因与额度显示共用同一探测结果;截图/自测模式离线,只能显示「待检测」。位置:`provider-health.js`、`lib.ps1` 的 `Get-ClaudeProbeFailureReason` / `Test-ClaudeReady`、`picker.ps1` 的 provider presentation + quota state。

---

## 七、开发方法论(这次真正省时间的做法)

1. **★ 离线自测台,别每次让用户去飞书点**。
   - `FEISHU_TEST=1` 时:`feishu-agent.js` 用一个**记录型 mock client**(不联网、不启动长连接、不占单实例锁),导出 `onMessage/onCardAction/onBotMenu`。
   - `test/card-flow.js`:纯逻辑,断言"进项目→积压菜单→回主菜单"不堆卡、不跳回。
   - 真 API 回归只使用隔离的无工具 prompt 或合成 canary,不依赖个人 Claude 订阅和历史会话。
   - 教训:能自动化验证的交互,别靠人肉在飞书里反复试。这套台子让"卡片状态机 / 查询链路"的回归可以秒级自检。

2. **对抗式复审(adversarial review)真能抓到人肉漏掉的 bug**。一次多视角并行复审 + 逐条独立复核,确认了 9 个问题(含 1 个高危:只读查询污染 `--continue` 池)。写完复杂状态机后值得跑一轮。

3. **复刻 bug 要用线上一模一样的输入**。"复刻成功但线上失败"两次都栽在这:第一次是并发占用会话,第二次是单行 vs 多行 prompt。差一点点条件,结论就相反。(第三次:lark-cli 封装的测试同样经 `cmd.exe /c` 起脚本,却只喂单行文本,复现不了换行截断,见第十四节第 1 条。)

3b. **★★★ 测试绝不能对真实项目跑「修改」会话** —— 墨菲定律的现场处刑。routing 测试为了验证"会话里回复『选 A』不被菜单劫持",resume 了 claude-resume 的**真实最新会话**发了"选 A",然后 300ms 后发"停止"止损。结果:那个被唤醒的 claude(带着旧会话上下文、`--dangerously-skip-permissions`)把"选 A"理解成执行此前讨论的方案 A,**替我 git commit 并 push 了整个工作区**;"停止"因 running-map 注册的时序差没拦住。它推的恰好只是我自己未提交的重构(无损),纯属运气。
   - 修法:这类断言只需要"announce 出现 + 没弹菜单",把 work 会话 id 换成**不存在的假 uuid**——`--resume` 秒报错退出,零副作用;测试模型 pin 到 haiku。
   - 原则:任何会启动 claude 修改会话的测试,要么指向假会话/假项目,要么根本不该存在。"事后停止"不是安全措施,是祈祷。

4. **外部进程失败要留 stderr / exit code / 部分输出**,否则失败就是黑盒。

5. **同一个外部 CLI 的“新建/续接”不能各拼一套参数**。Codex 首轮在隔离的非 Git cwd 中带 `--skip-git-repo-check`,但 `exec resume` 分支漏了,于是首轮全绿、第二轮统一在模型调用前退出。修法不是再给 resume 手补一次,而是让两种模式共用 `buildCodexArgs` 的 provider/model/tool/Git 不变量,只保留 CLI 语法差异;离线断言两边参数,真 API 冒烟必须包含非 Git cwd 的第二轮 resume。

6. **完成 hook 的一次回调不等于一个用户可见顶层任务**。Codex Desktop 的 projectless 工作区会生成 `Documents\Codex\日期\slug`(例如 `wo-x`),subagent/ephemeral turn 也可能各自产生不同 eventId;纯去重无法把它们合并。现役准入要求 Codex thread 有本地持久化 rollout,并排除无 Git 根的 projectless 生成目录。先定义“什么值得通知”,再谈事件去重。(OpenCode 2.x 同理:只对顶层会话通知,子会话的结束不是整个任务的结束,依据见 `UPSTREAM-ARCHITECTURE-RESEARCH.md` 的 OpenCode 2.2.3 盘点。)

---

## 八、开发历程(这个项目是怎么长成现在这样的)

大致演进,记录"为什么会变成现在这样":

1. **v0 续跑工具**:PowerShell GUI 勾选项目 + 计划任务,5h 限流重置后自动 `claude --continue`。最早用"估算重置时间"触发。
2. **去掉估算,改实时探测**:本地估算(ccusage 分块 / jsonl 时间窗)和 claude.ai 的真实滚动窗口能差几小时,估算还一度**门控**了探测导致延迟触发。改成固定间隔探测 + 只在**实测**额度恢复时 fire。
3. **修好"从不触发"**:定位到 PS 5.1 `.ExitCode=$null`(见 一.1),改看 stream-json result。
4. **飞书通知**:限流/恢复/每个项目 ✅❌ 推送到飞书(先 webhook,后自建应用)。
5. **飞书双向**:能从飞书发指令在项目里跑(承认了做不到"在 VS Code 面板里实时刷新"——只能续同一会话、重开可见)。
6. **合并成一个 app 机器人**(通知 + 双向),GUI 单实例 + 任务栏图标。
7. **对话模型重构**:idle/chat/project 三态 + Telegram 式按钮卡片 + 底部常驻菜单 + 模型切换按钮。
8. **身份认证**:绑定飞书 open_id,只有 owner 能改项目(full/viewer/none 三级)。
9. **只读查询会话**:最初是“每项目共享一个固定 session-id”,这会把不同同事和不同提供商的上下文串在一起。现改为 **(project,openId,profileId)** 各自隔离 cwd/session;GUI/飞书清空统一走 `session-manager.js`,Codex 也真正删除原生 thread。
10. **项目子菜单**:进项目先选 只读/修改,再对话;理顺按钮层级(两级)。
11. **稳定性收尾**:卡片堆叠/抢卡、stdin 传 prompt(换行截断)、旧 15s 消息心跳改为单张进度卡、用量报告、离线自测台。
12. **多 AI + 会话治理**:OpenAI GPT-5.6 Sol、DeepSeek V4/V4 Pro、Claude 共存;GUI 重塑为 AI Resume 工作台。飞书 scratch 14 天归档/30 天删除,项目 work 永不自动删;Claude transcript+artifact 可恢复移动,Codex 用原生 archive/unarchive/delete。
13. **服务状态改为真实健康探测**:GUI 启动/手动刷新实测三家 provider,不再把配置或安装状态冒充可用;Claude 未登录等失败同步驱动服务行和额度区。
14. **C# v2 迁移(2026-08,Stage 0-11)**:WPF + WebView2 控制面 + Worker 服务 + SQLite/WAL,AI 运行改为 Start/Status/Cancel(ADR-0001/0002);v1 运行时 2026-08-07 停用、2026-08-08 从工作树删除。
15. **直接使用 cc-connect(2026-08-06,ADR-0003)**:飞书聊天与会话交给 cc-connect,AI Resume 收敛为续跑编排、项目发现、完成通知与控制面 GUI 四件事。
16. **按供应商固定目标续跑(2026-09-07,ADR-0004)**:布防固定供应商、账号、执行器、模型与原生会话 UUID。此后真机上一次自动续跑都没有发生,根因直到 v2.2 才查清(第十四节第 4、5 条)。
17. **v2.1(2026-10-12,ADR-0005)**:Codex 改走官方账号与 hooks 通知;Claude 额度改用 setup-token 长期令牌(v2.2 推翻,第十四节第 9 条)。
18. **v2.2(2026-10-30,ADR-0006)**:cc-connect 整体退役(手机远程交给 Claude Code 官方 Remote Control,飞书只发完成通知);逐项目续跑;完成通知送达修复;OpenCode 2.x 插件;GUI 重排(菜单栏、逐项目模型队列)。

---

## 九、C# Stage 2 骨架(.NET 10 shadow,2026-08)

1. **★★ 幂等命中 ≠ 无需处理:接口返回 `Accepted=true + Existing=true` 时,编排器仍可能把它当新接纳再驱动一次**。`RunStore.StartAsync` 对同 `requestId` 幂等返回既有 run,`Accepted` 仍为 true;`TaskOrchestrator.StartAsync` 只判 `!Accepted` 就短路,于是重复 Start(同一用户动作重放)会**二次 spawn**——违反 RUN-CONTRACT §13 #9「重复 Start 返回同 runId,spawn=1」。修复:`!Accepted || Existing` 一律原样返回(0f35123)。教训:幂等键命中后的返回结构也要在编排层专门断言(补测 `DuplicateStart_sameRequestId_returnsSameRunId_noSecondSpawn`),别只测存储层行数。
2. **接口冻结的代价要提前算**:Core 接口冻结后,`ProviderStatus` 没有 terminal 字段、`ProcessStatus` 没有退出码,骨架无法区分「干净退出」与「异常消失」。编排器只能把「gone + 无 cancel + 无 provider 失败」当 succeeded(骨架级简化,注释明示),这与 RUN-CONTRACT §13 #5「进程无 terminal 消失 → failed_local」存在字面差异。教训:冻结接口前先问「骨架的 succeeded 由谁产出」;若已冻结,如实记录偏差并归到真实适配阶段(Stage 4/5)收紧,别让文档冒充契约。
3. **独立复跑确认真实执行**(沿用 S2-E 教训):全量 1 秒跑完不代表用例执行了。每包收尾都单独 `--filter` 复跑并核对逐条耗时(如 323ms 的超时用例、1s 的真进程用例),再报完成。
4. **xUnit `Assert.Equal(expected, actual, msg)` 有 comparer 重载坑**:第三个字符串参数会被解析为 `IEqualityComparer`,不报错但断言可能变恒真/恒假。断言信息用变量拼接或注释写清,别依赖第三参数。
5. **Windows 专属依赖是传染的**:Worker 引用 windows-only 的 Secrets → Worker TFM 必须改 `net10.0-windows`;测试项目引用 Worker → 测试也变 windows。早定 TFM 矩阵,别等 NU1201。

## 十、探测的外部足迹(2026-08)

1. **★★ 一次性临时工作目录会在别人家里留下永久记录。** v1 的 `src/provider-health.js:64`
   每次健康探测都 `fs.mkdtempSync(os.tmpdir(), 'ai-resume-health-<provider>-')` 建一个随机目录,
   跑完自己删掉——**看起来很干净**。但 Claude Code 已经为那个 cwd 记了一条项目记录,
   而那条记录没有任何人清。2026-08-08 清点:用户 `~/.claude/projects` 下
   **1135 / 1169 = 97%** 是这些残留(36.4 MB,7 天累积)。
   代价不是磁盘,是**用户每次 `/resume` 都要从 1135 条垃圾里找自己的项目**。
   解法:探测复用**一个固定目录**(C# 侧的 `ClaudeCodeProbe` 传 `ShadowPaths.Root`,
   因此只产生一条记录)。教训:**判断"清理干净了"不能只看自己创建的那个目录——
   要问"我这次运行让别的程序在哪里写了什么"。** 起 agent 尤其如此:
   它有自己的持久化,不受你的 `finally` 管辖。
2. **同类风险清单**(新增探测/子进程时逐条过):Claude Code 的 `~/.claude/projects/<cwd编码>/`、
   Codex 的 thread rollout、cc-connect 的 `~/.cc-connect/sessions/`(cc-connect 已于 2026-10-30 退役,这一项只作历史)。
   凡是把 cwd 当成会话身份的工具,随机 cwd = 无限增长的记录。
3. **`codex exec` 也必须给固定或一次性目录,但理由相反**:它起的是真 agent,
   不指定工作目录就继承调用方的 cwd,无人值守的探测会落到用户仓库里(v1 的 `CodexProbe` 曾为此固定目录;v2.1 起不再启动 Codex 探测进程,供应商证据全部走 HTTP)。
   两个方向都要想:**别污染别人,也别在别人家里干活。**

## 十一、静默失败:界面说没事,实际早坏了(2026-08-08 第二轮审计)

外部审计一次找出六处缺陷,**没有一个是崩溃**。加上此前自查的一处,七个缺陷是同一种形状:

| 界面当时显示 | 实际 |
|---|---|
| 通知源「已启用」 | 钩子指向的程序被移走,命令永远执行不了 |
| 飞书「已配置 cli_xxx」 | 凭据被开放平台重置,换 token 返回 `code=10003` |
| 「cc-connect 配置已生成」 | 那份 TOML 解析失败(`expected ']'`) |
| 顶部「监视中」 | 续跑 Worker 已被 kill |
| Codex「可用 · 凭据已验证」 | `/v1/models` 200 而推理路由 403 |
| `install` 退出码 0 + "入口已全部指向安装目录" | 五个通知源一个都没启用 |
| 计划任务 `State=Running` | `PT72H` 会掐断、拔电源就停、崩了不拉起 |

> 2026-10-30 注:表中「飞书已配置」「cc-connect 配置已生成」两行对应的功能已随 cc-connect 退役删除(v2.2 的飞书机器人凭据只有经在线核验后才保存——起初用 `lark-cli doctor`,它在 env 来源下从不换令牌,对任何密钥都失败,2026-11-02 起改为真实换令牌 + 一次机器人接口调用(第十四节第 14 条),「可送达」只认投递健康文件里的真实送达记录)。同一形状在 v2.2 又出现了一次:OpenCode 插件被宿主拒绝加载,界面仍显示「已启用」,见第十四节第 6 条。

### 根因是同一个:判据只看配置,不看世界

每一条的判据都停在**我们自己那一步**:
- "配置里有我们的文件名" → 说成"已启用";
- "DPAPI 里有值" → 说成"已配置";
- "File.Exists 且写入没抛异常" → 说成"已生成";
- "config.Armed 是 true" → 说成"监视中";
- "带凭据的请求返回 200" → 说成"凭据已验证"。

这些判据全都为真,而结论全都为假。**崩溃至少会告诉你它崩了;静默失败要等到你发现"机器人怎么不理我"才暴露,那时距离根因已经隔了七层。**

### 三条现在必须遵守的纪律

1. **肯定句必须追到世界。** 界面上每一句"已 X",背后必须有一个**能被外部证伪**的判据:
   文件在不在、进程活没活、对方的解析器收不收、真发一次请求换不换得到 token。
2. **契约以对方的解析器为准。** 校验 cc-connect 的配置就调 `cc-connect config format`,
   不要手写 TOML 检查器去猜别人的解析器 —— 手写的两种错法(漏判、误判)里,
   误判更糟:它会告诉用户一份能用的配置坏了。
   注意标志位置:`cc-connect --config X config format` 会**获取实例锁**(走启动路径),
   必须写成 `config format --config X`;并且它会**重写文件**,所以只能校验副本。
   (cc-connect 已退役,这个例子只作历史;纪律本身不变。v2.2 的同类做法:OpenCode 插件是否生效以 OpenCode 自己的加载日志为准,机器人凭据是否有效以 `lark-cli doctor` 为准。)
3. **核对不了要说核对不了。** 找不到 cc-connect(已退役,例子只作历史)、命令不是 exe 形式、路径读不了 ——
   一律返回"未核实"而不是"没问题"。**把未知说成正常,和把故障说成正常一样是在骗人,只是方向相反。**

### 特别记一笔:B3 —— 现状被自己的上一步清空

`install → uninstall → install`,第二次安装退出码 0,而五个通知源全是关的。
原因是重指那一步的判据是"当前已启用的才重指",而 `uninstall` 上一步刚把它们全关了 ——
**循环体一次都没进,命令却报告成功。**

教训不是"少了个 if",而是:**现状永远只能回答"此刻是什么样",回答不了"本该是什么样"。**
后者是意图,必须自己持久化(`ProductConfig.NotifySources`),
再由安装做**意图 ∪ 现状**的对账(`NotifyIntent.Targets`)。

同类形状值得警惕:任何"读现状 → 按现状恢复"的流程,只要中间有一步会改现状,就已经坏了。

---

## 十二、控制台窗口就是一个杀死开关(2026-08-13)

**现象**:用户报"开机会弹一个黑框"(以为只是难看),同时报"完成通知失效"。
两条当成了两个 issue。

**真相是同一个 bug。** 证据链:

| 时间 | 事件 | 来源 |
|---|---|---|
| 20:28:06 | 用户从开始菜单重启 | 系统日志 1074 |
| 20:28:07 | Worker 最后一次心跳 | worker 日志 |
| 20:30:15 | 登录,Startup 快捷方式拉起 Worker,**弹出控制台窗口** | worker 日志 |
| 20:30:55 | 最后一条心跳,此后再无记录 | worker 日志 |
| 21:15 / 22:13 / 22:18 | 三条完成事件积压无人消费 | outbox |

定性靠两个**缺席的**证据:Application 日志里没有该进程的崩溃记录(排除崩溃),
worker 日志里没有 `Application is shutting down`(排除优雅退出)。
既不是崩的也不是正常停的 ⇒ 被外部终止。而当时唯一能终止它的,就是那个刚弹出来、
被顺手关掉的黑框 —— **关闭控制台窗口 = 杀进程**。

**教训**:

1. **长期驻留的后台进程绝不能带一个用户能关的窗口。** 用户关掉它是完全合理的行为,
   而代价是整条后台链路静默停摆。把"难看"归为体验问题,会漏掉它其实是可用性故障。
2. **两个看似无关的报障,如果时间线对得上,先假设它们是同一个根因。**
   这次差点分别去修 UI 和通知代码,而通知代码根本没有 bug。
3. **进程消失要能自证。** 靠"没有崩溃日志 + 没有退出日志"反推花了很久;
   后台进程应当在收到 CTRL_CLOSE_EVENT / SIGTERM 时留下一行,否则死因只能靠排除法。

**解法**:Startup 快捷方式改指 `AiResume.Launcher.exe`(WinExe 垫片,`CreateNoWindow`
拉起 Worker 后自己退出),Worker 保持控制台程序形态不变 —— `install` / `notify` /
`feishu-check` 都要 stdout(`feishu-check` 已于 v2.2 删除,通道体检改为 `notify check`)。想再要"挂了自动重启",用
`scripts/register-autostart.ps1` 提权升级成 S4U 计划任务
(非提权注册一律 `0x80070005`,所以不能塞进 install)。

**顺带一条平台事实**:Windows PowerShell 5.1 读 `.ps1` 默认按 ANSI,
UTF-8 无 BOM 的中文会被解码坏,连 here-string 的结束符都认不出来。
仓库里给用户手动运行的 `.ps1` 必须带 BOM,且要在 5.1(不是 pwsh 7)下实测。

---

---

## 十三、v2.1 重构挖出来的四条教训(2026-10-12)

项目一个月没动,五项功能静默失效(详见 ADR-0005 §1)。每一条背后都是同一类错误:**把别人的状态当成了自己的保证**。

1. **★★ 寄生在别的程序的登录态上,它一登出你就瞎了。** 额度与续跑全靠读 Claude Code CLI 的
   `~/.claude/.credentials.json`。CLI 一登出(或 refresh token 过期、`expiresAt=0`),产品就退化成「未核实」,
   而「未核实」不是通知,没人会看到。`quota_snapshots` 停在 2026-09-09,一个月后才被发现。
   解法:要一份**自己保管**的凭据(`claude setup-token` 一年期令牌,官方为无人值守签发),CLI 只作回退;
   仍然只读不刷新。教训:**依赖别人会话的功能,必须把那个依赖本身做成一等状态行**——
   不是等它失败后显示「未核实」,而是让「它靠谁活着」随时可见。
   > 2026-10-30 注(ADR-0006):这条的**解法已被推翻**——setup-token 签发的令牌作用域只有 `user:inference`,usage 端点一律 403,读不到额度(见第十四节第 9 条)。v2.2 回到只读 Claude Code 自己的登录,并把「它靠谁活着」做成主动提醒:界面单列「Claude 登录」一块,`ClaudeLoginReminder` 在登录到期前 3 天、2 天、1 天、12 小时与已过期各发一次飞书提醒。教训本身(依赖要做成一等状态)仍然成立。
2. **★★ 一次成功样本不能证明间歇性故障不存在。** 2026-08-14 验证过「S4U session 0 下 DPAPI 能解密」,
   `outcome=Sent`,于是写进了文档。2026-10 的日志里 `CryptUnprotectData` 连续失败几个小时后又自愈,
   `worker.notify.sweep_failed` 刷了一屏。解法:后台进程**只解密它必须解密的东西**——投递只需要
   收件人 `open_id`(不是秘密),明文存 `config.json`;Worker 真正必须读的 Claude 令牌用 LocalMachine 范围。
   教训:凡是「在另一个安全上下文里跑」的路径,验证要看**一段时间的失败率**,不是一次结果。
   > 2026-10-30 注:「投递只需要收件人」只对 AI Resume 自己的代码成立——Worker 启动的 lark-cli 每次发送仍要解它自己钥匙串里的 CurrentUser DPAPI 密文,故障点只是移了过去,见第十四节第 2 条。「Claude 令牌用 LocalMachine 范围」随 setup-token 一起删除;现役用 LocalMachine 范围保存的是飞书机器人凭据副本(`state\secrets\feishu-bot.bin`)。
3. **★ 单值共享键会被任何程序无痕覆盖。** `~/.codex/config.toml` 的 `notify` 只能放一条命令,
   AI Resume 把自己链在前面、用 `--previous-notify` 转发旧值。Codex Desktop 的 computer-use 功能上线后直接把它
   改写成自己的命令,AI Resume 的链整体消失,`notify list` 的所有权检查再精巧也救不回来。
   解法:改用 Codex hooks——`hooks.json` 里每个消费者自己一组,信任记录按组登记;只增删自己的那组。
   教训:**选落点时先问「这个键还有谁会写」**。能用「多槽位 + 所有权标记」的地方,不要用单值键。
4. **★ 重写上游的哈希/序列化之前,先在野外找一个 oracle。** Codex 的 hooks 信任哈希要对规范化 JSON
   做键排序、紧凑序列化再 SHA-256,`timeout`/`async` 的默认值、键名和事件标签任何一处理解偏差都会得到
   一个「看起来对」但 Codex 不认的哈希——而症状只是钩子静默不执行。本机 codex-chatgpt-web 写过一条
   `interrupt` 信任记录,拿它复算,一次就对上了。教训:**对接别人的二进制契约,测试里必须有一条来自真实世界的样本**,
   和「mock 必须照抄线上返回结构」是同一条纪律。

顺带两条平台事实(cc-connect 已于 2026-10-30 退役,这两条只作历史):cc-connect v1.4.1 的 `cmd` 用 Go `strings.Fields` 拆分,参数里不能有空格;
cc-connect 给 codex agent 配 provider 时会 `ensureCodexAuth` 改写 `~/.codex/auth.json`,与用户自己的 ChatGPT 登录互斥——
这是把 Codex 固定到官方账号、并从 cc-connect 删除 codex-only 第三方 provider 的直接原因。

---

## 十四、v2.2 体检挖出来的教训(2026-10-30)

这一轮体检(ADR-0006 §1)从几个现象查起:布防后「一直不能自动续跑」、飞书通知「时灵时不灵」,以及顺带查出的 OpenCode 通知自 10-29 起全部失效。逐条查下去,教训几乎都是同一个形状:**我们只核对了自己这一段,故障在链路的下一段**——Worker 不再解 DPAPI,它启动的 lark-cli 还在解;插件文件写好了,宿主却不加载;测试断言了错误信封,信封却写在另一条流里;账号指纹「有值」,却是会轮换的令牌。

1. **★★★ 经 `cmd.exe /c` 调 npm 垫片,多行参数在第一个换行处被截断——同一个坑第三次出现**
   - 现象:飞书通知只有第一行(「✅ 项目 已完成」),第二行「来源 · 时间」从来没到;`--idempotency-key` 从未送到飞书,文档里写的「本地去重 + 飞书 uuid」两道保险实际只有一道;五个通知源的冒烟消息内容完全相同,分不出来源。截断后的参数仍然合法,lark-cli 不报错,所以一直没人发现。
   - 根因:旧的投递代码在 PATH 上只找到 npm 装的 `lark-cli.cmd`,注释写着「lark-cli 在 Windows 上没有 .exe」,于是经 `cmd.exe /c` 启动。通知文本是「标题\n来源 · HH:mm」,cmd 在第一个换行(LF、CRLF 都一样)处截断,其后的第二行、`--idempotency-key`、`--format` 全部丢失。其实 npm 包里就有官方二进制 `node_modules/@larksuite/cli/bin/lark-cli.exe`,`.cmd` 只是包装它的垫片——「PATH 上找不到 .exe」不等于「没有 .exe」。
   - 此前出现过两次:第一节第 9 条(v1 用 `cmd.exe /c claude.cmd` 起 Claude)与第二节第 1 条(v1 的 `claude -p "多行文本"` 被 cmd 截断,改走 stdin)。C# 续跑运行器也早有防护(`ClaudeResumeRunner` 在没有执行计划的旧路径上对多行提示词显式失败),但新增 lark-cli 调用点时没人对照这条教训;封装的测试同样经 `cmd.exe /c` 起脚本,却只喂单行文本,复现不了(第七节第 3 条)。
   - 规则:① 外部 CLI 一律直接启动真正的 exe;PATH 上只有 `.cmd` / `.ps1` 时去找它包装的二进制,找不到就报 `launch_failed`,**绝不回退 `cmd.exe`**;② 仍需经 `cmd.exe` 的调用点(如 `ClaudeCodeProbe` 的诊断探测、续跑运行器的旧路径)只能传单行固定参数,参数含换行必须显式失败;③ 新增任何外部进程调用,评审时都要回答「经不经 shell、参数会不会含换行」;④ 参数保真要有测试:直接启动回显替身,断言含 LF 的参数和它后面的参数原样到达。
   - 位置:`LarkCliNotificationChannel.ResolveExecutable`(只返回 exe)、`LarkCliInvoker`(`ArgumentList`、UTF-8)、`LarkCliInvokerTests`(参数保真用例)。

2. **★★★ S4U 会话 0 里,第三方 CLI 解不开它自己的 CurrentUser DPAPI——10-12 修掉的是自己那一段,故障点移到了 lark-cli**
   - 现象:10-13 起完成通知成簇失败、又成簇恢复,日志只有笼统的 `send_rejected`;同一秒多条能一起成功,不像频控。10-26 至 10-30 投递成功 179 次、失败 158 次(含重试)。
   - 根因:Worker 跑在 S4U 计划任务的会话 0。lark-cli 1.0.81 在 Windows 上把 appSecret 存在 `HKCU\Software\LarkCli\keychain` 里,是 CurrentUser 范围的 DPAPI 密文,每次 `--as bot` 都要先解密再换 tenant_access_token。S4U 登录不保存口令,解 CurrentUser 密文会间歇报 `0x8009000B`(推断与 LSASS 的主密钥缓存是否过期有关,微软没有公开说明)。系统的 DPAPI 运行日志(Microsoft-Windows-Crypto-DPAPI/Operational)与投递日志逐条对齐:10-14 以来 312 次失败投递中 300 次(96%)对得上,每条失败恰好伴随 2 次 DPAPI 失败,全成功的轮次一次也没有。
   - 为什么说「故障点移动了」:10-12 之前 Worker 自己读 CurrentUser DPAPI 的 `feishu-platform.bin` 取收件人,08-14 起累计 5103 次 `sweep_failed`(同一个 `0x8009000B`);10-12 改读明文收件人后 `sweep_failed` 归零,`send_rejected` 从 10-13 开始成批出现。「Worker 投递路径不得依赖 DPAPI」只管住了我们自己的代码,没管住 Worker 启动的子进程(第十三节第 2 条)。
   - 规则:① 「后台安全上下文里不得依赖 CurrentUser 密文」必须覆盖**整棵进程树**:Worker 启动的每个第三方 CLI 把凭据存在哪、用什么范围加密,接入前就要查清;② 修掉一个故障后,看失败是「消失了」还是「换了名字」——旧错误归零的同时出现新的成簇失败,先怀疑同一根因;③ 优先用上游官方的凭据注入方式:lark-cli 的环境变量凭据来源(优先于钥匙串),由 AI Resume 在交互会话里保存一份 **LocalMachine** 范围、ACL 收紧到当前用户 + SYSTEM + Administrators 的副本,Worker 只读、只经子进程环境变量传递〔2026-11-02 更正:env 来源只收现成令牌,当初写的 `APP_ID/APP_SECRET` + 「dry-run 证实」+ doctor 核验这条路对任何密钥都失败;现役是副本自己换 tenant_access_token、只注入 App ID + 令牌,保存前真实换令牌并调一次机器人接口,见第 14 条与 ADR-0007〕;④ 间歇性故障要看一段时间的失败率和系统侧日志,一次成功不算数;⑤ 升级 lark-cli 前复核钥匙串用法(1.0.90 起有存进钥匙串的 tenant access token),别让 DPAPI 依赖从另一个口子回来。
   - 位置:`FeishuBotCredentials`(`state\secrets\feishu-bot.bin`,可一次性导入 v2.1 的 `feishu-platform.bin`)、`LarkCliNotificationChannel.ResolveCredentialAsync`(现役:换令牌后注入,第 14 条)、`DpapiSecretStore`(LocalMachine 分支与 ACL 收紧)。**2026-11-02 真机验证**:用户在「飞书通知设置」保存凭据后,由 S4U 计划任务拉起的会话 0 Worker 经副本换令牌真实送达(`credentialSource=env`),当天 DPAPI 运行日志没有 8198、Worker 日志没有 `0x8009000B`。这只是一次会话 0 的发送样本(规则 ④),失败率仍按 COMPLETION-NOTIFICATIONS §6 第 5 条观察;「导入旧版凭据」这条路没有真机走过。

3. **★★ 错误信封写在 stderr,封装只读 stdout;测试按猜的形状 mock,一直全绿**
   - 现象:直到 2026-10-30,AI Resume 没有记下过一个真实的飞书错误码,所有失败都是 `send_rejected`;lark-cli 自己的日志(`~/.lark-cli/logs/auth-*.log`)只记 OAuth 设备流,也没有。上一条的根因只能靠系统 DPAPI 日志反推。
   - 根因:lark-cli 的官方错误契约(`errs/ERROR_CONTRACT.md`、lark-shared Skill)规定**错误信封写 stderr**、stdout 留空,退出码按类型区分(validation=2、authentication / authorization / config=3、network=4、api=1、policy=6、internal=5、confirmation=10)。旧的 `LarkCliInvoker` 只解析 stdout,发送函数只返回 bool,exit 10 的提示也从 stdout 取。`LarkCliInvokerTests` 把错误信封 mock 在 stdout 上,`type` 还是编出来的 `scope`、`confirm`——测试断言的是我们对 lark-cli 的想象。
   - 同一形状此前出现过一次:第三节第 0a 条(`im.image.create` 没有 `data` 外壳);本轮还有另一处:Claude 凭据测试的夹具凭空给 `.credentials.json` 加了顶层 `organizationUuid`(下一条)。
   - 规则:① 对接外部 CLI 前,先用**本机实际版本**录一次成功、业务失败、未知命令三种输出,stdout、stderr、退出码都录;mock 只能照抄录制样本,并注明版本与日期;② 结构化错误两条流都要解析(先 stderr、后 stdout),把 code、log_id、retryable、retry_after 落进日志——「被拒绝」不是诊断;③ 失败要分级:条目级只退避这一条,通道级(凭据、权限、网络、频控)熔断整个通道、不再逐条撞墙,参数类永久错误进死信;一段时间(现役 30 分钟,跨轮记账)内两条不同事件命中同一永久类别时升级为通道级(多半是 CLI 契约变了),免得 CLI 一升级就把所有通知送进死信。最初写成「同一轮里两条」,常态下一轮只有一条事件,这条保险永远不会触发,见第 10 条。
   - 位置:`LarkEnvelope`(code / log_id / retryable / retry_after_seconds / message_id)、`LarkCliInvoker`(`TryParse(stderr) ?? stdout 信封`)、`NotifyClassifier`、`CompletionNotifier`(`completion-events\dead\`、`completion-notify-health.json`)、`LarkCliInvokerTests`(本机录制的 stderr 样本)。

4. **★★★ 账号指纹取自会轮换的令牌:同一个账号每 8 小时被判一次「供应商已改变」**
   - 现象:布防后最多约 8 小时,续跑就永久停在 `provider-changed`,界面让用户「解除后重新布防」,重新布防后照旧。生产库 `quota_snapshots` 里同一账号攒了 28 个不同指纹,每个都在令牌刷新时刻附近失效;10-20 的现场是布防不到 3 小时后就再没离开过 `provider-changed`。
   - 根因:代码想用 `.credentials.json` 顶层的 `organizationUuid` 做账号指纹,读不到时退回 `SHA256("token:" + accessToken)`。真实文件顶层只有 `claudeAiOauth` 与 `mcpOAuth`,**根本没有账号字段**,于是指纹永远是令牌哈希;访问令牌约 8 小时一换,`ProviderRegistry` 派生的供应商身份随之改变。测试夹具凭空写了顶层 `organizationUuid`,所以「同账号指纹稳定」的用例一直是绿的。
   - 规则:① 身份必须取自**身份**,不能取自会轮换的凭据——Claude 账号身份来自 Claude Code 全局配置的 `oauthAccount`(组织 + 账号 UUID,与 `claude auth status` 的 orgId 同源);② 核实不了身份就如实为空、让续跑等待,**绝不拿凭据哈希顶替**——「顶替」看似保住了可用性,实际是把「不知道」伪装成「已改变」;③ 第三方文件的测试夹具必须从真实文件录制结构(值脱敏),夹具里出现真实文件没有的字段,就是测试在说谎;④ 「供应商已改变」要用户重新布防,代价很高,它的判据必须能区分「账号真的换了」与「令牌刷新了」。
   - 位置:`ClaudeCredentialSource`(`AccountMaterial`:按长度与写入时间缓存,正被改写读不出时沿用上一次成功的值,多份全局配置给出不同账号时视为未核实)、`ProviderRegistry.Read`(身份为空时 `CredentialFailure=account_unverified`)。升级到 v2.2 后,旧布防周期里的身份是令牌派生的,必然判为 `provider-changed`,需要重新布防一次。

5. **★★ 周期级聚合粒度过粗:一个项目卡住,整个周期等待;没启动的失败也锁死周期**
   - 现象:2026-09-07 引入「固定目标续跑」后,真机上一次自动续跑都没有发生过(8 月的旧引擎在 08-13 至 08-29 间续跑过多次);10-20 那批 `notready` 日志有 66 条看不出原因。
   - 根因:除了上一条的指纹漂移,还有两个各自足以卡死的设计,本质都是把本该独立的维度压成了周期级的一个结论:
     - **额度**:scoped 门禁要求目标模型必须有自己的 `weekly_scoped` 正证据,且按「整桶 Allowed / LimitReached」聚合。本账号只有 5 小时、7 天与 Fable 三个窗口,所以 Opus 项目永远「未核实」;Fable 周额度一满,Opus 项目也被一起挡住。测试把「只有主窗口」「只有别的模型的 scoped」「别的模型的 scoped 满额」都固化成了 `Ready=false`,把过度保守当成了安全。
     - **占用与未启动**:占用检查遍历全部 Selected(包括已经成功的项目),任何一个会话开着就整轮不续;额度就绪后只要撞上一次瞬时占用,计划创建就抛异常,经 `execution-unverified` 置 `ReplayBlocked`,整个周期锁死、必须重新布防——可那时根本还没 spawn,不可能有副作用。
     - **可观测性**:`resume.probe.notready` 不带原因,`OnNotReady` 把白名单以外的原因一律压成 `unknown`,事后无法定位卡在哪。
   - 规则:① 先按第一性原理拆维度:义务(这个会话要不要续)、额度(这个模型现在能不能跑)、占用(这个会话有没有被别人拿着)按项目或按模型判定,只有「自己有没有未确认退出的进程」是全局的;② 保守要作用在最小粒度上:「未知」只让**这一个项目**等待,不让整个周期等待;③ 只有可能已产生副作用的结果(进程已启动)才锁住自动重放,「没启动」不是「失败」——记 `not-started:<原因>`,下一拍重新评估;④ 每一种等待都要落一个可读的原因码,日志与界面都能说清卡在哪;⑤ 把「拒绝放行」写进测试期望之前,先问它是安全需要还是实现限制。
   - 同一形状:通知投递的旧退避也是全局的——一轮里任一条失败,整个队列按 30 秒到 15 分钟加倍退避,永久失败的那条永不出队、也没有死信;10-12 起 462 个事件里 86 个被拖延,中位 30 分钟。现已改为单条退避 + 通道熔断 + 死信(第 3 条)。
   - 位置:`ProjectAdmission`(义务 / 额度 / 占用三维)、`CheckerCycle.OnProbe` / `NoteProject` / `PrepareActiveRun`(中断 uuid 持久化于 `ConsumedInterruptions`,同一次中断最多续一次)、`ResumeEngine`(逐项目最终门禁、`resume.probe.evaluated` 逐项目原因)、`QuotaResumeProbe.Evaluate`(只看目标模型族相关窗口)、`ProviderExecutionPlan`(`ResumeDeferredException`)。进程启动后的失败仍按 ADR-0002 锁住本周期的自动重放(`ReplayBlocked` 保留)。**待真机验证**:一次真实的用量上限中断后的逐项目自动续跑。

6. **★★ 上游大版本升级静默废掉插件,界面的「已启用」无法证伪**
   - 现象:10-29 起 OpenCode 的完成通知一条也没有,10 月 Worker 日志里 OpenCode 来源的投递是 0 条,而 GUI 和 `notify list` 照样显示「已启用 / 可送达」。
   - 根因:OpenCode Desktop 走 `latest` 自动升级通道,10-29 从 1.19.5 升到 2.2.x。2.x 不再运行 v1 插件(官方迁移指南原话 "V1 plugin implementations do not run in V2"),我们生成的单文件插件每次启动都被拒:`failed to load plugin … Plugin must export a default definition with an id and an effect or setup function`,到 10-30 已有 296 条——全在 OpenCode 自己的日志里。而我们的 Probe 只看「插件文件在不在、首行标记对不对」,`HookHealth` 只看 `AiResume.Hook.exe` 在不在,两样都为真,结论却为假(第十一节的同一个形状)。光改导出形状也不够:v2 的事件载荷从 `properties` 换成了 `data`,`session.idle` 已废弃且服务端不再发布,v1 的 `$` 也没了。
   - 旁证:v1 时期(8/14 至 10/28)OpenCode 也只投递过 1 条,原因已无法核实;opencode.ai 网站上的插件与配置文档仍是 v1 的,官方 v2 迁移指南的示例还在用 `session.idle`——文档与实现不一致时以源码为准。
   - 规则:① 宿主插件的「已启用」必须来自**宿主自己的加载证据**:插件写入之后,宿主日志里出现本插件的 `loading plugin` 才算已加载,`failed to load plugin` 标断链,没有记录就显示「待核实」;② 接在别人扩展机制上的功能,要把宿主版本当成依赖来跟踪——宿主会自动升级,「上个月能用」不是证据,大版本升级后必须复核;③ 所有权判据要认得我方**每一代**生成物,否则一升级,自己的旧文件就变成「别人的」,原位迁移与停用都会失败;④ 读上游 API 以本机实际版本的源码与生成类型为准,网站文档可能落后一个大版本。
   - 位置:`OpenCodeNotificationAdapter`(v2 目录形态插件 `plugins/airesume-notify/index.ts`;`ReadLoadEvidence` 读 `~/.local/share/opencode/log/opencode.log` 末尾 4 MB;`IsManagedV2Source` / `IsManagedLegacySource`)、`AiResume.Hook`(接受 `session.execution.succeeded` / `permission.asked` / `form.created` / `session.execution.failed`,OpenCode 事件 ID 只按上游 id 计算)。**待真机验证**:OpenCode 2.2.3 内真实加载新插件并在任务完成时发出通知。

7. **★ 官方能力已经覆盖的桥接层,要按使用证据及时退役(cc-connect)**
   - 现象:为了「在飞书里驱动 AI」,AI Resume 一直维护着 cc-connect 的配置生成、校验、激活事务、单消费者预检与计划任务核验(当时是 `CLAUDE.md` 里最长的一段门禁),还把上游钉死在 v1.4.1;可 10 月飞书入站只有 2 条,cron 在 34 次启动中一直是 0,微信平台整个周期只收到 1 条消息,却刷出 1 万多行连接失败;计划任务早已被用户自己停用。
   - 根因:采用上游时只论证了「它能做什么」,没设「它实际被用了多少」的复核点。用户的用法早已迁到 Claude Code 官方 Remote Control(手机聊天、附件、权限、停止、切模型),桥接层继续存在只剩维护成本,`~/.cc-connect` 里还留着多份明文密钥。
   - 规则:① 引入任何桥接 / 代理类上游时,同时写下「出现什么证据就退役它」;② 定期按使用证据(入站量、命令分布、定时任务数)复核组件价值,而不是按功能清单;③ 官方能力覆盖后,删掉自有实现比「冻结隐藏」更安全——留着的生成 / 重启入口随时可能把不再需要的平台重新拉起;④ 退役时只删自己的代码与数据,别人的程序与配置(计划任务、npm 包、`~/.cc-connect`)只写进文档交给用户清理,并提醒吊销其中的明文密钥。
   - 位置:ADR-0006 §2.A、`UPSTREAM-ARCHITECTURE-RESEARCH.md`「2026-10-30 退役评估」。已知缺口:手机驱动 OpenCode / Codex 的能力随之放弃。

8. **★ 只订阅了「正常结束」:失败路径完全无声,还按臆测丢掉了真正的结束**
   - 现象:用量上限中断、登录失效这类最需要把人叫回来的情况,飞书一条通知都没有;开着 `/goal` 等阻断型 Stop hook 时,真正结束的那一次反而收不到。
   - 根因:Claude Code 的回合因 API 错误结束时**不触发 `Stop`**,而是触发 `StopFailure`(2.1.78+),我们只订阅了 `Stop`;`Notification` 的 matcher 也漏了真正在等人的 `quota_auto_resume_stale` / `quota_auto_resume_disabled`。2026-08 的设计把 `stop_hook_active=true` 理解成「递归,丢弃」,而官方含义只是「Claude 正因某个 Stop hook 而继续」——我方 hook 从不阻断,丢弃它吞掉的正是最后那次结束。钩子还写成带引号的 shell 字符串,在 Claude Code 回退 PowerShell 时是 ParserError(本机复现),只是因为本机装了 Git Bash 才一直没暴露。
   - 规则:① 订阅上游事件前,先把官方列出的**全部终止与等待事件**列成表,逐个写明订阅与否和理由——成功、失败、中断、等人缺一不可;② 字段语义以官方文档为准,自己的推断要标成推断,上游文档更新后复核;③ 调用形态用官方推荐的(Claude Code hooks 对单个 exe 用 exec form:`command` 只放 exe、`args` 放参数),不依赖「本机恰好装了某个 shell」;④ 去重与合并放在投递端按会话做(稳定期 + 同会话后者替代前者),不在入口按某个标志直接丢。
   - 位置:`ClaudeCodeNotificationAdapter`(三组 exec form:`Stop` / `StopFailure --kind=failure` / `Notification --kind=decision`)、`AiResume.Hook`(`ShouldSuppress` 对 claudecode 不再按 `stop_hook_active` 丢弃;`ReadClaudeDetails`)、`CompletionNotifier`(对 Claude Code 完成类事件:20 秒稳定期、同会话 2 分钟内替代、后台任务未完时最多暂缓 30 分钟)。**待真机核实**:claude.ai 用量上限触发的 `StopFailure` 的 `error` 是否为 `rate_limit`。

9. **★ 「官方签发的长期令牌」不等于「能读你要的接口」:凭据方案上线前要真调一次**
   - 现象:v2.1 把 `claude setup-token` 长期令牌定为额度读取的主路径;用户真的保存了令牌之后,usage 端点以 403 拒绝了它(拒绝标记与界面上都是 `token_rejected_403`),额度照样读不到。
   - 根因:setup-token 签发的令牌作用域只有 `user:inference`(官方文档写明只能发模型请求),而 usage 端点需要 `user:profile`——Claude Code 自己取用量时也要求这个作用域。ADR-0005 落地时没有用这枚令牌对 usage 端点真调过一次(它的状态行写着「Claude 长期令牌……必须由用户操作后才能核实」),而方案在纸面上很合理:官方签发、为无人值守设计、一年有效。
   - 规则:① 凭据方案写进 ADR 之前,先用它对目标端点做一次只读、零推理的真实调用,核对作用域;② 「官方为无人值守签发」只说明它能做官方声明的事,不说明它能读你要的数据;③ 回到只读 Claude Code 自己的登录后,不要为了「保活」去刷新或代刷——refresh token 一次性轮换,外部刷新会把用户登出;能做的是提醒:令牌待续期时让续跑等待,登录将到期时按天提醒用户 `/login`。
   - 位置:`ClaudeCredentialSource`(只读,`ClaudeLoginVerdict`)、`ClaudeLoginReminder`、`InstallCommand.RetireLegacyClaudeToken`(`install` 删除旧的 `state\secrets\claude-oauth-token.bin` 与 `.rejected.json`)。

10. **★★ 新写的投递保险,一半按「测试里的形状」而不是「真实的到达节奏」设计(同日交叉审查)**
   - 现象:上面几条修完、测试全绿后,两路独立的对抗性审查又找出同一形状的一串问题:① 「同一轮两条事件命中同类永久失败就升级熔断」——Worker 每 15 秒一轮,真实事件几乎总是一轮一条,这条保险永远凑不齐;② 结果不明(超时)的重发没有上限,会一直持续到飞书 uuid 一小时去重窗口之外,真的重复送达;退出码 0 但多打了一行提示的输出被当失败重试,同样会重复;③ 「可送达」只看有没有成功过——之后的条目级失败(收件人跨应用)、或刚改过凭据/收件人,界面照样是绿的;④ 通知意图用「列表为空」同时表示「旧配置从没记录过」与「用户把通知源全关了」,全关的用户照样收到残留链路的通知;⑤ 积压超过一天的「已完成」在恢复后一次冲刷出去,文案只有 HH:mm,看起来像刚发生。
   - 根因:单元测试一轮塞进两条事件、一次失败一次成功,断言的是「这一轮怎么处理」,没有人按真实节奏(一轮一条、失败会跨很多轮、设置会在两次投递之间改)去推演;正面状态取自「历史上发生过」,而不是「最近一次、在当前设置下」。
   - 规则:① 去重、升级、封顶这类窗口一律用**时间**定义并跨轮持久化,不用「同一轮」「同一批」;② 可能已经生效的副作用(结果不明的发送)只能在对端去重窗口之内重试,窗口外宁可进死信也不盲发;外部 CLI 的退出码 0 就是成功,输出格式不认识不等于失败;③ 绿色要同时满足「最近一次尝试成功」与「那次成功时的设置指纹等于现在」,两者任一不成立就降级为琥珀;④ 「没有记录」与「记录为空」是两种状态,持久化意图要能区分(`NotifyIntentRecorded`);⑤ 通知有时效:超过时效的不补发,宁可进死信也不制造误导;⑥ 测试除了「这一轮的处理」,还要有「真实节奏」的用例:一轮一条地跨轮推进时钟。
   - 位置:`CompletionNotifier`(`StaleAfter`、`AmbiguousCap`、`PermanentWindow`、`completion-notify-sessions.json`)、`DeliveryHealth.LastAttemptOk` / `SuccessFingerprint`、`NotifyFingerprint`、`LarkCliNotificationChannel`(`sent_unverified`)、`CompletionNotifier.IsSourceEnabled`、`ProductConfig.NotifyIntentRecorded`、`index.html` 的 `AR.notifyModel`。

11. **★★ 「续的那一下失败了」被当成「已经续上了」;上游悄悄换了存储,读的还是旧文件(同日第二轮交叉审查)**
   - 现象:① 会话被用量上限打断后,原生自动继续在重置点撞上 529、或用户输入后令牌被拒,Claude Code 会再写一条合成错误记录(`isApiErrorMessage=true`,`error` 是 `server_error` / `authentication_failed`)。证据层只看「最近一条 assistant 记录是不是 rate_limit」,于是判「已在别处续上」——项目记为完成、永远不再续,界面还说「已在客户端继续」。② OpenCode 2.x 把登录凭据搬进了自己的 SQLite(`opencode.db` 的 `credential` 表),`auth.json` 只在 2.x 首次启动时导入一次;AI Resume 读的仍是 `auth.json`,下一次在 OpenCode 里换密钥,面板就又挂着旧账户的余额——正是用户最初抱怨的那个现象。③ 通知钩子把 monitor(发布 Artifact 后自动订阅评论)、teammate、开发服务器这类常驻任务算成「还在忙」,每次完成都推迟 30 分钟。
   - 根因:都是只认了一种形状:「不是 A 就是 B」(不是限流就是续上了;不是 shell 就是在忙),而上游的取值空间比这大;以及把「这个文件过去是真身」当成永久事实。
   - 规则:① 判定「状态已改变」之前,先把上游可能写出的记录类型列全(本机录一遍真实会话里出现过的 `error` 取值,只读键名与取值),对每一类写明归属;拿不准的类别跳过或判未核实,绝不归入会触发不可逆动作的那一类;② 依赖第三方的本地存储时,每次大版本升级都要复核「它现在把数据写在哪」;读不到新存储才退回旧文件,并在来源里标明;③ 「在忙」这类判断用白名单(会唤醒会话的子代理、workflow、云端会话),不用黑名单。
   - 位置:`ClaudeSessionEvidence`(跳过非限流合成记录、`LastReplyAtUtc`)、`ResumeSessionCatalog.ContinuedInAnotherSession` / `SameProcess`、`OpenCodeCredentials.ReadDatabaseKey`、`AiResume.Hook` 的 `background_tasks` 白名单。

12. **★★ 「读前 64 KiB 就够了」与「判断不了就当没有」:同一类放行,藏在三处(2026-11-02 修复轮复审)**
   - 现象:① 会话头部只读前 64 KiB 找 `cwd`:本机 138 个 Claude Code 会话里 43 个的首条带 `cwd` 的记录超过 64 KiB(首条提问贴了截图,图片 base64 内联,单行最长约 2 MB),这些会话在项目发现、会话绑定、转录定位与「换会话接着干」判定里全都「读不出目录」——绑定退回更旧的会话、或续跑永远停在未核实;② `ContinuedInAnotherSession` 读不了目录或读不出新会话的归属时回答「没人接着干」,于是放行续旧会话;③ OpenCode 凭据库可读但没有 DeepSeek 行时退回 `auth.json`,拿 OpenCode 早已不用的旧密钥查余额并点绿。
   - 根因:固定字节上限是按「平常的记录很短」设的,没有按真实分布量过;布尔返回值没有给「判断不了」留位置,只能被折成某个肯定答案;「库没有这一行」和「库读不了」被同一个 null 表达,下游只能二选一。
   - 规则:① 解析上游行式文件按行读,单行不设上限,只给总量设预算,上限要按本机真实分布量过再定;② 安全门禁类判定一律三态(是 / 否 / 判断不了),判断不了只导致等待;③ 「没有」与「读不了」必须用不同的返回值表达,读不了显示未核实,绝不退回另一个可能过时的来源。
   - 位置:`ProjectCatalog.ReadHeadString` / `ReadHeadLines`、`ResumeSessionCatalog.ContinuedInAnotherSession`(`bool?`,另以文件创建时间识别分叉续写)、`CheckerCycle`(superseded 记入 `ConsumedInterruptions`)、`OpenCodeCredentials.ReadDatabaseKey`、`ClaudeCredentialSource`(`unreadable` / `ClaudeLoginVerdict.Unverified`)。

13. **★★ 全局 hook 不只服务人:第三方工具拉起的一次性 claude 也会触发;通知文案不看状态,说了一句没发生的事(2026-11-02)**
   - 现象:周额度用完的两天里,飞书每 8 小时收到一条「⏸️ Default Project 因用量上限中断 / 额度恢复后 AI Resume 会按布防自动续跑」,额度恢复后又是「✅ Default Project 已完成」。当时根本没布防,本机也没有叫 Default Project 的项目。来源是常驻的第三方用量监视器(CodeZeno ClaudeCodeUsageMonitor,工作目录 `Documents\Default Project`):访问令牌过期时它以 `--no-session-persistence` 拉起 `claude` 刷新令牌,这个进程读同一份 `~/.claude/settings.json`,我们的 `StopFailure` / `Stop` hook 照常触发。另一个同源重复:同一次中断,Claude Code 原生自动继续放弃时还会发 `quota_auto_resume_disabled`,飞书收到两条。
   - 根因:① 把「hook 触发了」等同于「用户的一个会话结束了」——全局 settings 里的 hook 对本机任何 claude 进程都生效,包括没有转录、谁都打不开也续不了的程序化调用;② 去重只按事件:每次程序化调用都是新会话、新 eventId,而用量上限是账号级的,同一周期里每一次撞墙都被当成新消息;③ 文案是写死的套话,不读布防状态、不看这个会话在不在布防范围,也不给重置时间。
   - 规则:① 通知的准入要有「这是用户能接着干的会话吗」的外部证据(转录落盘);判断不了照发,确认没有才丢;② 去重的粒度要对准「新信息」:同一会话同一限额周期(服务端给的 `rateLimitType + resetsAt`,加配置根)只提醒一次,同时发生的合成一条;程序化调用靠第 ① 条拦,别指望靠会话去重——它每次都是新会话;判周期前先等转录写完,事件晚于证据里的重置时间时证据作废;③ 文案里每一句关于「接下来会发生什么」的话,都要在投递那一刻按真实状态(布防快照,连同引擎已知会一直等的原因:要重新布防、登录失效、监视卡住)生成,读不出就写「未核实」——与界面「肯定句可证伪」同一条规则;只说自己会做什么,不替上游承诺;④ 排查「谁发的通知」先看进程的工作目录与命令行,而不是只看事件内容。
   - 位置:`CompletionNotifier`(§5.3 第 6、7 步:`session_not_persisted`、`same_limit_cycle`、`completion-notify-limits.json`,`BuildLimitText` / `OutlookFor`)、`NotifyFacts`(转录、进程登记、`custom-title`、布防快照)。

14. **★★★ 「dry-run 证实」+「假 CLI 认 App Secret 就报就绪」:机器人凭据副本对任何密钥都存不进去(2026-11-02,ADR-0007)**
   - 现象:用户在「飞书通知设置」保存 App ID / App Secret,三次都报 `no tat available from credential source "env"`;故意编错的密钥报错一字不差。v2.2 上线以来机器范围副本一次也没存成功,后台投递一直落在 S4U 下不可靠的 lark-cli 钥匙串上——第 2 条要修的那个故障其实从未修好。
   - 根因:lark-cli 1.0.81 的 env 凭据来源只接受现成令牌(`LARKSUITE_CLI_TENANT_ACCESS_TOKEN`),给它 `LARKSUITE_CLI_APP_SECRET` 根本不去换令牌(网络接到只记录的代理:env 路径一次换令牌请求都没发;同一调用改给假令牌,立即收到飞书侧 99991663,说明令牌确实被送出去了)。当初的依据是 `im +messages-send --dry-run` 输出的 `context.app_id` 等于环境变量里的假 app id——dry-run 不换令牌,只证明「env 来源被选中了」,证明不了「env 来源能用」。测试里的假 lark-cli 又写成「APP_SECRET 是 fake-good-secret 就报 bot 就绪」,把这个猜想固化成了全绿的用例。
   - 规则:① 凭据链路的可用性只能用**会真的换令牌 / 真的调接口**的证据证明:用明显无效的假值跑一次,看错误是不是来自对端(飞书 log_id、业务码),而不是本地 `token_missing`;dry-run、`auth status`、「被选中」都不算;② 假 CLI 的分支条件必须来自录制的真实行为:真实 CLI 对某个输入从不成功,假 CLI 就不能让它成功;③ 新功能上线后至少要有一次真机走通(这里是用户在 GUI 保存一次);只有单测的「已实现」要在文档里写成待真机验证,并真的去验;④ 上游缺能力时的例外要窄:这里只自己做「换令牌」一步(官方端点、只用于 bot、令牌只在内存),发消息等其余操作仍走 lark-cli,失败时绝不退回把 App Secret 塞进子进程环境。
   - 位置:`FeishuTenantToken`、`LarkCliNotificationChannel.ResolveCredentialAsync` / `TokenEnvironment` / `VerifyAsync`、`NotifyClassifier.ClassifyTokenExchange`、`NotifyControlPlane.SetCredentialAsync`、`FeishuTenantTokenTests`、`NotifyControlPlaneTests.FakeLarkCli`(只给 App Secret 一律 token_missing)。

---

_维护提示:再遇到新坑,按上面的分类追加一条(现象 → 根因 → 规则 → 代码位置)。现役回归是 `dotnet test csharp\AiResume.sln`(`GuiPageContractTests` 与 `OpenCodeNotificationAdapterTests` 需要本机 node);v1 的 `node test/*.js` 回归已随 v1 运行时删除,只在 git 历史里。_
