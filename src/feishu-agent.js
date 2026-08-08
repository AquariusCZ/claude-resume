/*
  feishu-agent.js  ---  AI Resume 双向飞书助手 —— Stage 1 稳定入口(只装配)

  Stage 1 D-006 收口:本文件只 require/导出兼容 runtime,不再实现飞书 SDK、权限、会话、
  provider、卡片、图片、进程登记或完成通知逻辑。全部现役实现已机械迁移到
  feishu-runtime.js(无行为改写):TEST_MODE 导出形状、测试 hook、生产启动副作用与
  node feishu-agent.js 直接执行行为均保持不变;既有测试 require('../src/feishu-agent')
  无需修改继续工作。

  注意:feishu-runtime.js 只是 Stage 1 legacy compatibility application shell,不是第七个
  目标边界;Stage 6/10/11 会用目标链路(cc-connect / C# Worker)替换/删除整个 shell。
*/
'use strict';
module.exports = require('./feishu-runtime');
