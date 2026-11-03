# airesume-budget-gate

AI Resume 的子代理额度预检 mod(决策见 AI Resume 仓库 `docs/adr/0008-subagent-budget-preflight.md`)。
它运行在 Claude Code 进程里,逐个看到 Agent 工具、队友与工作流里的每个 `agent()` 派发,逐次计量每个模型请求,
在线校准"每百万等价 token 值多少额度百分点",并按模式记录、影子判断或推迟派发。mod 自身出错一律放行。

- 由 AI Resume 的 `install` 安装:文件复制到 `%LOCALAPPDATA%\AI Resume\mods\airesume-budget-gate\`,
  用户级 `~/.claude/settings.json` 的 `env.CLAUDE_CODE_PLUGIN_DIRS` 里加一条指向它的路径;`uninstall` 移除这条路径。
- 模式由 AI Resume 界面写的 `%LOCALAPPDATA%\AI Resume\state\budget-gate\config.json` 决定:
  `off`、`observe`(只记录)、`shadow`(判断但一律放行,install 后默认)、`enforce`(推迟与软着陆,只能由用户在界面上打开)。
- 证据写在同一目录:`logs\*.jsonl`(读数、计量、判断、复盘;不含提示正文与凭据)、`sessions\<会话>.json`(心跳)、`calibration.json`。
- mod 不读、不写、不刷新任何令牌,只用 Claude Code 给的额度读数;按模型族的周额度来自 AI Resume 导出的只读快照 `quota-snapshot.json`。

需要 Claude Code 2.1.287 以上(mod API);在 Desktop 的 WSL 会话里不运行。
