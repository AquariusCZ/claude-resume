// End-to-end query test: mocks ONLY the Feishu API; runClaude actually spawns claude. Simulates a
// user sending a multi-line read-only query and asserts claude received the WHOLE prompt (the marker
// after a newline proves stdin carried it) instead of "没看到具体问题" / "未拿到成功结果".
// Runs real claude (haiku) once (~$0.1). Run: node test/query-e2e.js
'use strict';
process.env.FEISHU_TEST = '1';
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const APP = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume');
const CFG = path.join(APP, 'config.json');
const SESS = path.join(APP, 'feishu-sessions.json');
const cfgBackup = fs.readFileSync(CFG);
const sessBackup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;

const cfg = JSON.parse(cfgBackup.toString('utf8').replace(/^﻿/, ''));
const CHAT = cfg.feishuChatId; assert(CHAT, 'need feishuChatId');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]); assert(OWNER, 'need owner');

// force haiku for this test to keep it cheap (restored in finally)
cfg.feishuChatModel = 'haiku';
fs.writeFileSync(CFG, JSON.stringify(cfg, null, 4));

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;

async function main() {
  let failed = 0;
  const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };
  try {
    const PROJ = A.discoverProjects().find(p => /Probe-Station/i.test(p.name)) || A.discoverProjects()[0];
    assert(PROJ, 'need a project');
    console.log('项目:', PROJ.name, '· 模型: haiku · 直接跑真实 claude…');
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    client.__reset();

    const MARK = '换行传递成功';
    const q = '用一句话说明这个项目大致做什么。\n然后另起一行,原样输出这六个字:' + MARK;
    const ev = { message: { message_id: 'm_test_' + Date.now(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: q }) }, sender: { sender_id: { open_id: OWNER } } };

    const t0 = Date.now();
    await A.onMessage(ev);   // resolves fast; the claude run continues in the background
    // poll until the RESULT message (not the announce echo) arrives
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    // results are rendered CARDS now (title + lark_md body); scan title+body
    const results = () => client.__calls.filter(c => c.op === 'create')
      .map(c => (c.title || '') + '\n' + (c.text || '')).filter(t => /✅ 查询结果|⚠️ 查询/.test(t));
    for (let i = 0; i < 60 && results().length === 0; i++) await sleep(2000);
    const secs = Math.round((Date.now() - t0) / 1000);
    const joined = results().join('\n---\n');
    console.log('\n--- 查询结果(' + secs + 's)---\n' + joined.slice(0, 800) + '\n---');

    check('拿到了查询结果消息', results().length > 0, '120s 内无结果');
    check('claude 收到了完整问题(结果里输出了换行后的暗号)', joined.indexOf(MARK) !== -1, '结果中未见暗号,可能仍被换行截断');
    check('不是"未拿到成功结果"', joined.length > 0 && !/未拿到成功结果/.test(joined), '仍走到失败兜底');
    check('不是"没看到具体问题"', joined.length > 0 && !/没.{0,4}具体问题|只.{0,4}作答策略|补充.{0,4}问题/.test(joined), 'claude 只收到了框架、没收到问题');
  } finally {
    fs.writeFileSync(CFG, cfgBackup);
    if (sessBackup) fs.writeFileSync(SESS, sessBackup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { fs.writeFileSync(CFG, cfgBackup); if (sessBackup) fs.writeFileSync(SESS, sessBackup); console.error(e); process.exit(1); });
