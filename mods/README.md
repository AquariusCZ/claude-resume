# mods/

AI Resume 随安装 payload 分发的 Claude Code mod。决策与数据契约见 [docs/adr/0008-subagent-budget-preflight.md](../docs/adr/0008-subagent-budget-preflight.md)。

| 目录 | 内容 |
|---|---|
| `airesume-budget-gate/` | 子代理额度预检 mod 本体(会被安装):`hooks/register.ts` 适配层只做 IO,判断在 `core/`(纯函数 + `BudgetEngine`) |
| `tests/` | Node 测试与 fixture;fixture 全部来自 Claude Code 2.1.309 的真实录制(临时目录的 `-p` 会话与 Desktop 会话),不手写 mock |
| `tools/replay.mts` | 离线回放:把数据目录里的日志按会话喂给 `BudgetEngine`,输出各规则判断次数与 p80 覆盖率(只读) |

```powershell
# 测试(Node 22.6+ 直接跑 TypeScript;dotnet test 的 BudgetGateTests 也会跑这一组)
cd mods; node --test tests/basics.test.ts tests/calibrate.test.ts tests/decide.test.ts tests/engine.test.ts tests/messages.test.ts tests/replay.test.ts

# 离线回放真实数据(默认 %LOCALAPPDATA%\AI Resume\state\budget-gate)
node mods/tools/replay.mts

# 校验 mod 能被加载(用与 Desktop 同版本的 claude)
claude plugin validate mods/airesume-budget-gate
```

类型检查:mod API 的声明由 Claude Code 写出(加载 plugin-authoring skill 时的 `types/claude-code.d.ts`,或热重载时 mod 旁的
`.claude-plugin/types/`),版本跟着本机 Claude Code 走,所以不进仓库;拿到声明后用 `tsc --noEmit` 加 `allowImportingTsExtensions` 检查即可。

改了 mod 之后:`dotnet build` 会把它带进各项目输出,`install` 装到 `%LOCALAPPDATA%\AI Resume\mods\airesume-budget-gate\`,**只有新启动的
Claude Code 会话**才加载新版本。
