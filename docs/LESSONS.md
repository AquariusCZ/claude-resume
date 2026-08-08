# 踩坑记录 & 开发经验(为以后项目准备)

这份文档记录 AI Resume(内部运行目录仍名为 ClaudeResume)开发过程中**真实踩过的坑、失败的尝试、以及最终的正确解法**,并尽量指到代码位置。目的是:以后做类似项目(Windows + PowerShell + Node + Claude Code CLI + 飞书机器人)时,别再踩同样的坑。

> 阅读方式:每条都是「现象 → 原因 → 解法 → 代码位置」。带 ★ 的是最隐蔽、最花时间才定位的坑。

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

9. **启动 `.cmd` 不能用 `Start-Process claude.cmd -RedirectStandardOutput`**(UseShellExecute=false 无法 exec .cmd)。解法:`cmd.exe /c claude.cmd …` + tail 重定向文件 + 递归按 `ParentProcessId` **杀整个进程树**(返回的 PID 只是 cmd 外壳)。

---

## 二、Claude Code CLI(headless `-p`)的坑

飞书 agent 和续跑引擎都是无头调用 `claude -p --output-format stream-json --verbose`。这里的坑最多、最隐蔽:

1. **★★★ Windows `cmd` 把 `-p "多行文本"` 在第一个换行处截断** —— 查询"总是失败"的真凶,绕了好几版才定位。
   - 现象:飞书里问项目问题,claude 回"我只看到作答策略,没有具体问题内容";agent 侧报"未拿到成功结果"。
   - 原因:prompt 作为命令行参数 `-p "<framing>\n\n<question>"` 传,`cmd /c` 在第一个 `\n` 处截断参数 → claude **只收到了 framing、没收到问题**。
   - 为什么难查:手动复刻时我用的是**单行** prompt,侥幸绕过,复刻一直"成功";真实 agent 用多行 framing 才暴露。**教训:复刻 bug 必须用和线上完全一致的输入(尤其是否含换行)。**
   - 解法:**prompt 改走 stdin**,不走命令行参数。`spawn(..., {stdio:['pipe','pipe','pipe']})` 后 `child.stdin.write(prompt); child.stdin.end()`。stdin 不受命令行换行影响,原样送达。
   - 位置:`feishu-agent.js` `runClaude`(`args.push('-p', ...)` 不带 prompt;prompt 写进 stdin)。该修复曾用多行暗号做真实端到端验证;后续改动必须继续保持 prompt 只经 stdin 传递。

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

8. **claude 的 stderr 一开始被完全忽略,失败时一片空白无从诊断**。加上"失败时记录 exit code + stderr 尾 + 是否有 assistant 文本"后,才定位到"no stdin data received"。**教训:外部进程的 stderr 一定要留一份,失败日志才有线索。**

9. **`rate_limit_event` 只在窗口被用到一定程度才由服务器下发**(5h 窗口很空时根本不发 5h 数字)。所以额度显示要能处理"某个窗口暂时没数据"。位置:`Test-ClaudeReady` / GUI 额度 chip 显示 `5h 低`。

---

## 三、飞书(Feishu)机器人的坑

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
   - 通用教训:**mock 是你对 API 的假设,不是 API**。凡是"线上不工作但测试全绿",第一个怀疑对象就是 mock 与真实返回结构不一致——用一次真实调用打印结构来校准,成本 30 秒。

0b. **飞书图片有两种事件形态**:单独发送是没有正文的 `image`;图片和说明放在同一气泡会变成带语言包的富文本 `post`。只处理 `image`,或用 `message_type !== 'text'` 一刀切,都会让合法图文问题在进入 AI 前消失。
   - 解法:`image` 仍用 `im.messageResource.get` 下载后按 **chat+sender** 挂起到该用户下一条文字;`post` 必须解析 `{zh_cn:{title,content:[[...]]}}`(并兼容扁平 body),同时提取段落文字和 `img.image_key`,最多下载前 6 张(单张 10MB),并把这些文件只绑定到当前事件。进入后台任务前同步 claim,否则后到图片会串进先到问题;群聊若只按 chatId 排队,viewer 的图还可能注入 owner 修改。上限必须施加在**最终合并请求**而非各自队列,否则 6 张暂存+6 张 post 仍会变成 12 张;合并时当前 post 图优先。
   - 图片文件必须有生命周期:忙碌/不支持/无权限立即回滚,运行结束删除,未消费孤儿满 24h 后由每小时扫描同时清磁盘和内存 Map;部分下载/超限提示只是告警,发送失败不能阻断剩余有效图文进入 AI。身份边界还必须在资源下载前读取有效配置;`readConfig()` 失败回 `{}` 会同时把 allowlist 和 owner 名单变空,等价于把所有人提升成 full。
   - 图片 prompt 不要硬编码某一个 provider 的工具名;提示当前 AI 使用 `view_image / Read` 等可用本地图片能力。
   - 权限缺失/其它类型(文件、语音)都要**明确回一句**,不要静默——静默是这类 UI 最糟的失败模式。
   - 只读用户的 run 禁了 Read,拿不到图 → 明说「只读用户看不了图片」并删掉文件,别假装收到了。

1. **★ 卡片回调(`card.action.trigger`)必须几秒内返回**,否则飞书报"目标回调服务超时未响应"。
   - 仅靠约定 `onCardAction` 内部不 `await` 不够:后续维护只要加回一个挂起的网络调用,整个 WS ACK 又会被拖住;async IIFE 也会先同步执行到第一个 `await`,文件扫描仍可阻塞。现在在**注册边界**用 `dispatchEvent` + `setImmediate`,保证在 handler 的同步前缀之前就返回;同时按 chat 串行 handler,否则“忘记查询”与紧随其后的新问题会并发,新问题可能恢复正在删除的 thread。`test/session-pick.js` 同时覆盖永久挂起 patch、150ms 同步忙等、毫秒 ACK 与同聊天严格顺序。

2. **飞书开发者后台三处配置是分开的,少一个都不通**:事件配置(订阅 `im.message.receive_v1` / `card.action.trigger` / `application.bot.menu_v6`)、回调配置(订阅方式设 **长连接**)、机器人自定义菜单;改完还要**发布版本**。嵌入式浏览器里配容易出错,用真实浏览器配。

3. **★ 控制卡「堆叠」与「被抢」是一对相互矛盾的坑**,来回改了好几版才平衡:
   - 只用一张控制卡原地 patch → 延迟/重复的底部菜单事件会把**项目卡 patch 回主菜单卡**(被抢);
   - 让项目卡独立、菜单发新卡 → 菜单事件**堆一堆主菜单卡**(堆叠)。
   - 中间方案:底部菜单事件 = 重绘「当前该显示的那张卡」且不改 session(项目里幂等重画,积压投递不堆不抢);想回主菜单用卡上的「⬅ 主菜单」。
   - **现役方案**:底部主菜单是“逃生口”,任何状态都重置为 idle 并在底部补一张新卡;普通导航由 `enqueueControlCard` **按 chat 串行写入**,任务执行时跟随实时 `lastCard`,它已被文字顶走就必须在底部新建,绝不回退 patch 入队时的旧卡。`controlCardEpoch` 让慢 patch 不能跨越普通消息的可见性代次重新登记为 live。项目卡绑项目 hash,会话卡再绑随机 token + profile;过期卡、独立模型卡和进度卡都不得抢占 `lastCard`。
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

1. **★ 改了 `src/` 线上不自动生效** —— 机器人从 `%LOCALAPPDATA%\ClaudeResume\` 跑。改完必须:复制到 AppDir → 精确定位命令行含 `ClaudeResume\feishu-agent.js` 的 node PID并用 `taskkill /PID <pid> /T /F` 终止整棵进程树(VBS 守护约 8s 重启)→ 确认恰好 1 个该进程并在日志看到 `ws client ready`。不要只杀 node 父进程,也不要误杀机器上的其他 Node 服务。见 `CLAUDE.md`。**每次都容易忘,吃过好几次"改了没反应"的亏。**

2. **火绒(Huorong)会删 `.lnk → powershell -WindowStyle Hidden` 这种组合**。所有隐藏启动走 `wscript` 隐藏的 `.vbs` + 计划任务,不用那个模式。

3. **从 bash 工具调 `wscript` 用正斜杠路径会静默失败**,改用 PowerShell 工具 + 反斜杠路径。

4. **日志用本地时间**,别用 `new Date().toISOString()`(那是 UTC,会写错日期文件名)。位置:`feishu-agent.js` `logLine`。

---

## 五、日志系统(踩过的坑 + 现在的约定)

日志被反复弄坏过好几次(空白、错日期、乱码、失败无信息)。这里一次讲清:**有哪些日志、写在哪、怎么读、踩过什么坑**,以后照这个来,别再弄错。

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

### 现在的约定(改日志前先看这条)
- **写**:引擎用 `Write-CcuLog`(本地时间、`run-<当天>`),agent 用 `logLine`(本地时间、`feishu-<当天>`)。**绝不用 UTC**。
- **读/显示**:一律经 `Read-LogTail`(读**最新** `run-*.log`、UTF-8)。**别再引用启动时固定的日期文件名。**
- **失败必留证据**:任何外部进程(claude 等)失败,必须把 exit code + stderr 尾记进日志。
- **新长期文件**要纳入 `Clear-OldCaches` 的清理与容量上限。

## 六、交互设计上的经验

1. **默认「什么都不做」(idle)**:机器人一进来不主动跑,等用户点卡片选模式,避免手滑乱花额度 / 误改项目。

2. **「先选模式,再对话」**:进项目先选 只读/修改,而不是默认改。用户原话:"能直接对我的项目做出修改太可怕了"。

3. **长任务必须持续可见,但不能刷屏**:旧实现每 15s 新发一条“思考中”,12 分钟就堆约 48 条。现役实现是 `startProgress`:每次运行只发**一张进度卡**,约 20s 原地 patch 已用时,结束后把同一张卡改成完成态,结果另发到底部;同一次运行的 tick/stop 必须串行,否则慢 tick 会在完成 patch 之后落地,把卡片改回“进行中”。但本地超时不等于 SDK 请求被取消:tick 若已超时,最终态必须另发新卡,避免迟到的旧请求覆盖。`trackRun` 负责进程重启后的中断告知。回归:`test/progress-image.js`。

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

3. **复刻 bug 要用线上一模一样的输入**。"复刻成功但线上失败"两次都栽在这:第一次是并发占用会话,第二次是单行 vs 多行 prompt。差一点点条件,结论就相反。

3b. **★★★ 测试绝不能对真实项目跑「修改」会话** —— 墨菲定律的现场处刑。routing 测试为了验证"会话里回复『选 A』不被菜单劫持",resume 了 claude-resume 的**真实最新会话**发了"选 A",然后 300ms 后发"停止"止损。结果:那个被唤醒的 claude(带着旧会话上下文、`--dangerously-skip-permissions`)把"选 A"理解成执行此前讨论的方案 A,**替我 git commit 并 push 了整个工作区**;"停止"因 running-map 注册的时序差没拦住。它推的恰好只是我自己未提交的重构(无损),纯属运气。
   - 修法:这类断言只需要"announce 出现 + 没弹菜单",把 work 会话 id 换成**不存在的假 uuid**——`--resume` 秒报错退出,零副作用;测试模型 pin 到 haiku。
   - 原则:任何会启动 claude 修改会话的测试,要么指向假会话/假项目,要么根本不该存在。"事后停止"不是安全措施,是祈祷。

4. **外部进程失败要留 stderr / exit code / 部分输出**,否则失败就是黑盒。

5. **同一个外部 CLI 的“新建/续接”不能各拼一套参数**。Codex 首轮在隔离的非 Git cwd 中带 `--skip-git-repo-check`,但 `exec resume` 分支漏了,于是首轮全绿、第二轮统一在模型调用前退出。修法不是再给 resume 手补一次,而是让两种模式共用 `buildCodexArgs` 的 provider/model/tool/Git 不变量,只保留 CLI 语法差异;离线断言两边参数,真 API 冒烟必须包含非 Git cwd 的第二轮 resume。

6. **完成 hook 的一次回调不等于一个用户可见顶层任务**。Codex Desktop 的 projectless 工作区会生成 `Documents\Codex\日期\slug`(例如 `wo-x`),subagent/ephemeral turn 也可能各自产生不同 eventId;纯去重无法把它们合并。现役准入要求 Codex thread 有本地持久化 rollout,并排除无 Git 根的 projectless 生成目录。先定义“什么值得通知”,再谈事件去重。

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
   Codex 的 thread rollout、cc-connect 的 `~/.cc-connect/sessions/`。
   凡是把 cwd 当成会话身份的工具,随机 cwd = 无限增长的记录。
3. **`codex exec` 也必须给固定或一次性目录,但理由相反**:它起的是真 agent,
   不指定工作目录就继承调用方的 cwd,无人值守的探测会落到用户仓库里(见 `CodexProbe`)。
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
3. **核对不了要说核对不了。** 找不到 cc-connect、命令不是 exe 形式、路径读不了 ——
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

---

_维护提示:再遇到新坑,按上面的分类追加一条(现象→原因→解法→代码位置);改了状态机先跑 `node test/card-flow.js`,改了查询或安全边界再跑对应离线回归与 `query-security.js` / `chat-security.js`。_
