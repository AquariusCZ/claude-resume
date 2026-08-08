# P5 凭据暴露面审计(S10-O,2026-08-06 夜,只读复核)

规则:**全文无完整凭据值**,只给「路径 + 键名 + 长度 + 前 4 位」。只报告,不清理。
本轮为上一会话(同日早)审计的复核 + 残留扫描;上轮结论(工作树/git 历史干净、
pilot 目录是已知泄漏点)本轮全部复验成立。

## 一、确认泄漏(明文凭据在位,均为已知)

| # | 路径 | 内容 | 备注 |
|---|---|---|---|
| L1 | `%TEMP%\cc-connect-pilot-config\config.toml` | `app_secret` len=32 pre=gtl8(生产飞书应用);`api_key` len=35 pre=sk-a(试点 deepseek);`token`×2 len=32 pre=bd68/befa([management]) | **试点已结束近 2 天,目录仍整目录在位**,含 11 张截图与日志 |
| L2 | `%TEMP%\cc-connect-pilot-config\cc-connect.log` | `ANTHROPIC_API_KEY=` / `ANTHROPIC_AUTH_TOKEN=` 各 12 处 | 上游 cc-connect 的 spawn 日志打印 providerEnv(S4-B 实测记录同源);值本身未独立扫描出 sk- 实值形态,但环境变量行完整保留 |
| L3 | `%TEMP%\cc-connect-pilot-config\s6d\stdout.log` | 同上 ×2 | 同 L2 |
| L4 | `~/.cc-connect\config.toml.bak-before-repair` | `api_key` len=67 pre=sk-b(生产 OpenAI 同值);`app_secret` len=32 pre=gtl8;`token` len=31 pre=eEIu | S10-M 修复时留下的备份,**与现役 config.toml 同目录**,含全量明文凭据 |
| L5 | `%LOCALAPPDATA%\ClaudeResume\logs\run-20260804.log` 第 335 行 | 1 处凭据形命中(sk- 或 gtl8 形态,实值未打印) | **本轮新发现**;疑似 run 日志把 provider 命令/环境带入。需人看该行定性 |

## 二、疑似(形态存在,但属设计内或待定性)

| # | 路径 | 内容 | 定性 |
|---|---|---|---|
| S1 | `%LOCALAPPDATA%\ClaudeResume\config.json` | deepseekApiKey len=35 pre=sk-d;openaiApiKey len=67 pre=sk-b;feishuAppSecret len=32 pre=gtl8;feishuSecret len=22 pre=RgrS;feishuAppId len=20 pre=cli_;feishuWebhook len=81 | **架构设计如此**(CLAUDE.md:机密只放 AppDir gitignore 的 config.json);风险是该文件无 ACL 加固,任何本机进程可读 |
| S2 | `%LOCALAPPDATA%\ClaudeResume\feishu-token.json` | token len=42 pre=t-g1(租户访问令牌,短时效) | 缓存令牌,过期自然失效 |
| S3 | `~/.cc-connect\config.toml` 本体 | 含凭据(本审计按红线不展开) | cc-connect 自管文件,只读不动 |

## 三、已确认安全

- **git 全历史(154 commits)**:`sk-{28+}` / `ghp_{36+}` / `AKIA{16}` / appSecret 赋值形 / `cli_{20+}` 全模式扫描 **0 命中**;
- **csharp/ 工作树**:`scan-secrets.ps1` 门禁通过(0 命中);
- **生产 feishu 日志**(feishu-*.log、feishu-stdout.log、completion-notify-*.log、gui-error.log):凭据形 0 命中(唯一命中在 run-20260804.log,见 L5);
- **s5d-powerloss-* / shadow 测试目录**:抽查最新 1 个 0 命中;Node 测试机制(keepSecrets)只把 openai/deepseek key 注入测试进程环境变量、不落盘临时 JSON,与机制描述一致;
- **pilot 目录未被复制**:TEMP/Desktop/LOCALAPPDATA 顶层无其它 pilot 副本(cc-connect-pilot 与 -config 是原身)。

## 四、其它残留(非凭据,顺带记录)

- `%TEMP%\s5d-powerloss-*` 约 **150+ 个**残留目录(PowerLossRecoveryTests 历史运行
  宿主被杀后 CleanupShadowDir 未能删净),2026-08-05 起累积;无凭据,占空间。
- `%TEMP%\claude-resume-feishu-test-*` 6 个、`ai-resume-health-*` / `ai-resume-baseline-*` 等若干。

## 五、建议清理顺序(只建议,删除由人决定)

1. **L4**(`config.toml.bak-before-repair`):与现役配置同目录的全量明文副本,
   修复已确认成功后备份失去价值,优先删;
2. **L1-L3**(整个 `%TEMP%\cc-connect-pilot-config\` 与 `cc-connect-pilot\`):
   试点已收尾,截图价值已在 STAGE-4-SPEC 记录,整目录可删;
3. **L5**:人工查看 `run-20260804.log` 第 335 行定性;若是 provider 命令行/环境泄漏,
   需追写入路径并在 Node 侧补脱敏(这属于行为变更,先裁决);
4. TEMP 测试残留(s5d-* 等)可随时批量清,无凭据顾虑。
