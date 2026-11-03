# ADR-0007:飞书机器人凭据副本自己换 tenant_access_token(方案 A)

状态：已实现(2026-11-02);离线回归、两路对抗审查与真机验证结论见 §5。本 ADR 修订 ADR-0006 §2 中「机器范围副本经
`LARKSUITE_CLI_APP_ID/APP_SECRET` 环境变量交给 lark-cli」与「保存前用 `lark-cli doctor` 在线核验」两处;
ADR-0006 的其余决策(机器范围 DPAPI 副本、ACL、直接启动 `lark-cli.exe`、错误信封在 stderr、分级熔断)继续有效。

## 1. 背景:保存凭据对任何密钥都失败

2026-11-02 用户在「飞书通知设置」保存 App ID / App Secret,连续三次报
`no tat available from credential source "env"`。查证(全程只用假 app_id、假令牌、假 secret,没碰真实凭据):

| # | 实验(lark-cli 1.0.81) | 结果 |
|---|---|---|
| 1 | 故意编错的 secret 跑核验 | 报错与用户的一字不差:不论密钥对错都失败 |
| 2 | 网络接到只记录、一律拒绝的本地代理 | 钥匙串路径会去换令牌(随即报网络错误);env 路径**根本不发换令牌请求**,照样报「no tat」 |
| 3 | env 只给 `APP_ID` + `APP_SECRET`,调 `api GET /open-apis/bot/v3/info --as bot` | 立即 `authentication/token_missing`:"no access token available for bot" |
| 4 | env 给 `APP_ID` + **假** `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`,同一调用 | 退出 3,飞书侧 `authentication/token_invalid`,code 99991663,带 log_id——令牌确实送到了飞书 |
| 5 | 同 4,用用户真实的 lark-cli 配置目录 | 同样 99991663:env 令牌优先于钥匙串，不会悄悄改用钥匙串 |
| 6 | 同 4,`im +messages-send` | 同样 99991663:发消息路径也吃注入令牌 |
| 7 | 同 4,配置目录为空(`LARKSUITE_CLI_CONFIG_DIR` 指向空目录) | `api` 与发消息照样到达飞书;`doctor` 则在 `config_file` 处停下 |
| 8 | 以上实验后在 `~/.lark-cli` 与临时配置目录里搜假令牌 | 找不到:lark-cli 没把注入的令牌写进任何文件 |

二进制里有 `LARKSUITE_CLI_TENANT_ACCESS_TOKEN`、报错串「TENANT_ACCESS_TOKEN is set but LARKSUITE_CLI_APP_ID is missing」与哨兵
`__LARKSUITE_CLI_TOKEN_ONLY__`;1.0.82–1.0.98 的 CHANGELOG 没有相关改动，升级解决不了。

**结论**:lark-cli 的 env 凭据来源只接受现成的令牌，不会拿 App Secret 去换。v2.2 当初「本机 dry-run 证实 env 来源可用」不成立——
dry-run 不换令牌;测试里的假 lark-cli 又「认 APP_SECRET 就报 bot 就绪」,真 lark-cli 从不这样(教训见 LESSONS 第十四节第 14 条)。

## 2. 决策

AI Resume 用自己按 DPAPI 机器范围保存的 App ID / App Secret,**只做「换 tenant_access_token」这一步**,再把令牌经
`LARKSUITE_CLI_TENANT_ACCESS_TOKEN` 注入 lark-cli 子进程;发消息、诊断等其余所有操作仍走 lark-cli。

这是「新增飞书能力先查官方 lark-cli,有官方命令就调用它，不手写同类请求」规则的**例外**。依据：上游 env 来源不换令牌(§1 实验 2–4);
钥匙串来源在 S4U 会话 0 下间歇解不开(ADR-0006 §1);lark-cli 没有「用给定 App Secret 换令牌」的命令，`api` 子命令本身要先有令牌;
上游其余能让它自己换令牌的办法都越过本 ADR 的边界(§4 E)。上游若将来支持「不落明文、不进子进程环境」的方式交出 App Secret,删除本例外。

端点(官方文档，自建应用):`POST https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal`,
`Content-Type: application/json; charset=utf-8`,体 `{"app_id","app_secret"}`。成功应答
`{"code":0,"msg":"ok","tenant_access_token":"t-…","expire":7200}`——字段在顶层、没有 data 外壳,expire 单位秒;
有效期最长 2 小时，剩余不足 30 分钟时再换会拿到新令牌(新旧并存),否则返回原令牌。错误应答(本机用假值录制):
app_id 不存在 HTTP 200 `{"code":10014,"msg":"app id not exists"}`;缺参数 HTTP 200 `{"code":10003,"data":{},"msg":"invalid param"}`;
请求体不是 JSON HTTP 400 `{"code":9499,…,"error":{"log_id":…}}`;log_id 在响应头 `X-Tt-Logid`。

## 3. 边界

- **只换令牌、只用于 bot、只 feishu 品牌**。不调用其它任何飞书接口;核验用的机器人信息接口也经 lark-cli 调用。
- **令牌只在内存里**:`FeishuTenantToken` 进程内按「同一组凭据(SHA-256 键)且剩余 ≥ 5 分钟」复用，否则重换;不落盘，
  不进日志、RPC 回包、argv、截图。`TenantTokenOutcome.ToString()` 不含令牌;lark-cli 输出按「环境变量名含 SECRET 或 TOKEN」的值脱敏;
  令牌只收 16–512 位可见 ASCII(它进子进程环境块，也进全文脱敏集合，过短的值会把正常输出替换坏)。
- **App Secret 只出现在发往官方端点的请求体里**:飞书的 msg 万一回显它也替换掉。子进程环境里先删掉继承来的全部凭据类变量
  (`APP_ID`、`APP_SECRET`、`TENANT_ACCESS_TOKEN`、`USER_ACCESS_TOKEN`、`DEFAULT_AS`、`AUTH_PROXY`),再写入自己的几项;钥匙串路径与
  `auth list` 同样先删，界面与健康文件写的凭据来源才是真的。**换令牌失败时绝不退回到把 App Secret 塞进环境变量**,也不启动 lark-cli;
  副本存在却解不开时报 `credential_unreadable`,不静默改用钥匙串。
- **副本路径让 lark-cli 用 AI Resume 自有的空配置目录** `state\lark-cli\`(`LARKSUITE_CLI_CONFIG_DIR`):用户配置里引用的 CurrentUser
  钥匙串从结构上就碰不到(lark-cli 在注入令牌时会不会仍去解钥匙串，本机没能用合成配置实验定性,这样做就不必依赖它的内部实现;实验 7 证明空配置目录下照样能发)。
  诊断时附带的 doctor 仍看用户自己的 lark-cli 配置(版本、端点),注入的令牌照样优先。
- 不跟随重定向(307/308 会带着请求体重发到别处)。代理规则与 Go net/http 的 `ProxyFromEnvironment` 一致：只看 `HTTPS_PROXY`(没写 scheme 按 http://)
  与 `NO_PROXY`;不读 `ALL_PROXY`,不用 WinINet 系统代理，解析不出或 scheme 不受支持就直连并在细类里标明——绝不落回 .NET 的默认代理
  (它在 Windows 上会改用系统代理，本机代理客户端停了而系统代理残留时,lark-cli 照样直连得上飞书)。lark-cli 自己配置文件里的代理设置不跟随。
  本机核对:`open.feishu.cn` 没有 AAAA 记录、本机没有全局 IPv6,不存在 .NET 不做 Happy Eyeballs 导致的 IPv6 黑洞超时。
- 失败分类进 `NotifyClassifier.ClassifyTokenExchange`:一律通道级、结果明确(消息没发出去，不 ambiguous、不进死信):
  已知凭据类错误码(10003、10012、10013、10014)→ `auth_config`(设置一变即解除);网络与 5xx → `network`;超时 → `network_timeout`;
  429 / 99991400 → `rate_limit`;其余(未识别的错误码、9499、跳转或拦截页、读应答时的意外异常)→ `token_unexpected`(与网络同样短退避,
  不冒充「凭据不对」或「要升级」)。副本解不开 → `credential_unreadable`(短退避:可能只是正被占用;重新保存即解除)。
  `ErrorType=tenant_token`,`ErrorSubtype` 形如 `server_error:http_502@direct`(性质、HTTP 状态、`HttpRequestError`、网络路径),
  并写进健康文件的 `lastErrorType` / `lastErrorSubtype`,界面悬停标「换取令牌时失败」。
- 缓存的令牌被飞书判无效(lark-cli 报 authentication 或 99991663/4/5,不含 `token_missing`)时作废、重换一次再发(同一幂等键，日志 `tokenRetried=True`);
  刚换来的令牌被拒不再重试;网络等非令牌失效的失败不重试。注入了令牌 lark-cli 却报 `token_missing`(它不再读这个变量)→ `cli_contract`,不提示用户重新保存。
  熔断指纹含 lark-cli 的写入时间，原地升级也会解除熔断。
- 设置指纹在**发送前**记下：一次发送(含换令牌与重试)可能持续几十秒，期间用户改了设置，这次的熔断与成功都记在旧设置名下。
- 保存与导入：真实换令牌 + 以令牌经 lark-cli 调一次 `GET /open-apis/bot/v3/info`,退出码 0 且 `bot.activate_status` 为 2(读得到时)
  才通过——停用或待启用的机器人发不了消息;应答其余字段不解析、不记录。两步都过才写入。
  「诊断通道」/`notify check`:有副本时同样两步(只有这两步决定 BotReady 与 Problem),再附注入令牌后的 doctor(它的问题只作附注检查项);
  没换到令牌就不跑 doctor。没有副本时照旧只跑 doctor(钥匙串)。
- 凭据来源的线上值保持 `env`(副本：换令牌后经环境变量注入)/ `keychain`(lark-cli 钥匙串),界面与 CLI 显示「AI Resume 机器范围副本」/「lark-cli 钥匙串」;
  副本在却读不出时 `notify.channel` 带 `credentialUnreadable=true`,机器人行显红、压过历史成功。

## 4. 否决的方案

- **B 等上游 / 升级 lark-cli**:1.0.82–1.0.98 没有相关改动;通知会一直落在不可靠的钥匙串上。
- **C 用 `lark-cli config init` 写进它自己的钥匙串**:那是 CurrentUser DPAPI,正是 S4U 下解不开的那一份。
- **D 自己实现发消息**:超出「只换令牌」的最小例外，重复 lark-cli 已有且已验证的发送、幂等与错误契约。
- **E 让 lark-cli 自己换令牌，只换一种方式交出 App Secret**(上游盘点，二进制字符串，未在 S4U 实测):lark-cli 配置里的 `appSecret` 可以是明文字符串或
  `{source, id}` 引用(钥匙串报错时会提示「use file: reference in config to bypass keychain」);OpenClaw 绑定模式另有 env / file / exec 提供者。
  明文配置与文件引用要把 App Secret 明文落盘，比 DPAPI 机器范围 + ACL 更弱;env 引用要把 App Secret 交给子进程环境，越过本 ADR 的边界;
  exec 提供者只在 OpenClaw 绑定模式下存在，要另建 openclaw.json 与私有配置;而 1.0.90 起 lark-cli 自己换来的令牌会缓存进钥匙串，升级后可能重新依赖 DPAPI。
  所以「上游有」在这里不等于「能用」,仍按 §2 自己换令牌。

## 5. 验证

- 离线：`FeishuTenantTokenTests`(成功顶层形状;录制的 10014 / 10003 / 9499;未识别错误码、跳转、拦截页、429 / 5xx / 非 JSON、坏令牌;
  请求形状;缓存复用与临期重换、换凭据与作废;网络、超时与意外异常;Retry-After 一路带到熔断时长;代理规则与路径说明;
  不跟随重定向;分类表与细类;通道只注入 App ID + 令牌并用私有配置目录、换不到令牌不启动 lark-cli、缓存令牌被拒重换一次、
  网络失败与 token_missing 不重试、副本读不出不改用钥匙串);`NotifyControlPlaneTests`(真实换令牌 + 机器人接口才落盘、机器人未启用不落盘、
  doctor 的问题不推翻核验结论、副本读不出时诊断与识别如实报错、导入旧版凭据同一核验);`NotificationDeliveryTests`(发送期间改设置，
  熔断与成功记在旧设置名下);`NotificationWorkerTests`(新类别的熔断时长);`LarkCliInvokerTests`(TOKEN 值脱敏、继承变量删除);
  `GuiPageContractTests`(新原因有文案与说明、换令牌失败的悬停、副本读不出显红)。假 lark-cli 按 1.0.81 实测行为:只给 App Secret 一律 token_missing,
  有令牌没有 App ID 报错。全量 `dotnet test` 1303/1303。
- 两路独立只读对抗审查(正确性/回归，安全/可观测性)共 19 条确认成立的问题与 9 条存疑，全部逐条复核:成立的已修(副本读不出时界面与 CLI 的说法、
  异常兜底、诊断超时、短暂读失败的熔断时长、ALL_PROXY 与默认代理回落、token_missing、继承变量、指纹时机、错误细类与日志、文案过度断言、测试缺口、
  文档与演示残留);存疑项按上文处理(S1 上游盘点见 §4 E;Q1 改为私有配置目录;S2 加 activate_status;S3 msg 替换;S4 实验 8;Q3 本机核对;Q4 只认已知凭据错误码)。
- 真机(2026-11-02,记录见 `docs/COMPLETION-NOTIFICATIONS.md` §6):用户在 GUI 重新保存凭据，保存时的真实换令牌与机器人接口核验通过;
  `notify check` 退出码 0(`tenant_token`、`bot_api` 为 pass);交互会话与 S4U 计划任务拉起的会话 0 Worker 各经副本真实送达
  (`outcome=Sent … credentialSource=env`),投递健康的凭据来源为副本，私有配置目录里没有令牌落盘。
