# 与本仓库协作的约定

- **语言:始终用中文和我沟通**(所有回复、总结、说明都用中文)。代码注释沿用文件已有风格。

## 部署(飞书 Node 机器人)

改完 `src/feishu-agent.js` 后,线上机器人**不会**自动生效——它从 `%LOCALAPPDATA%\ClaudeResume\feishu-agent.js` 运行:

1. 把 `src/feishu-agent.js` 复制到 `%LOCALAPPDATA%\ClaudeResume\`;
2. 杀掉 node 进程(`Get-Process node | Stop-Process -Force`),VBS 守护循环约 8 秒内自动重启;
3. 验证:node 进程应为 1 个,`logs\feishu-stdout.log` 里出现 `ws client ready`。

GUI(`src/picker.ps1`)同理复制到该目录,改动在**下次打开窗口**时生效。

## 自测(飞书卡片交互)

改动飞书卡片/菜单状态机后,先跑离线自测,别每次都让用户在飞书里试:

```
node test/card-flow.js
```

它设 `FEISHU_TEST=1`,让 `feishu-agent.js` 用一个记录型 mock client(不联网、不启动长连接、不占单实例锁),直接调用 `onBotMenu/onCardAction`,断言"进项目→积压菜单事件"不堆卡、不跳回主菜单。测试会备份/恢复 `feishu-sessions.json`。

## 安全约束

- 只有 `feishuAuthOpenIds`(full)里的用户能**修改**项目;viewer 只能只读查询;闲聊对所有人开放。
- 机密只放在 gitignore 的 `config.json`,**绝不**进仓库。
- `feishuAuthOpenIds` 为空 = 未锁定(所有人可改),移除最后一个 full 用户会解锁,需警告。
