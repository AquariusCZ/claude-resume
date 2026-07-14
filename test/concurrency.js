// Concurrency smoke test — the "daytime freeze" regression guard.
// The WS layer ACKs an event only after its handler resolves, so a handler that awaits a 1-4 min
// claude run makes Feishu stop delivering / re-deliver: every tap looks dead. This test proves the
// handlers now return fast while the claude work continues in the background:
//   1) a query message's onMessage resolves in seconds (not the ~10-60s the query takes);
//   2) while the query is still running, bottom-menu taps and card clicks respond instantly;
//   3) a second query during the first gets an immediate "查询进行中";
//   4) the original query still completes and delivers its result.
// Mocks only the Feishu API; runs real claude (haiku). Run: node test/concurrency.js
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
cfg.feishuChatModel = 'haiku'; fs.writeFileSync(CFG, JSON.stringify(cfg, null, 4));

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const sleep = ms => new Promise(r => setTimeout(r, ms));

let eid = 0;
const msgEv = (t) => ({ message: { message_id: 'm_cc_' + (++eid) + '_' + Date.now(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: OWNER } } });
const menuEv = () => ({ event_key: 'menu', operator: { operator_id: { open_id: OWNER } }, header: { event_id: 'ecc' + (++eid), create_time: String(Date.now()) } });
const texts = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text').map(c => c.text || '');
const cards = () => client.__calls.filter(c => c.op === 'create' && c.type === 'interactive');

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  try {
    const PROJ = A.discoverProjects().find(p => /Wafer Map/i.test(p.name)) || A.discoverProjects()[0];
    assert(PROJ, 'need a project');
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    client.__reset();

    // 1) the query handler must resolve FAST (work continues in background)
    let t0 = Date.now();
    await A.onMessage(msgEv('这个项目的入口脚本是哪个文件?一句话回答。'));
    const handlerMs = Date.now() - t0;
    check(`查询消息的 handler 秒回(${handlerMs}ms,阈值 5s)`, handlerMs < 5000, handlerMs + 'ms — handler 阻塞到跑完了');

    // 2) while the query runs: bottom menu responds instantly
    await sleep(400);
    t0 = Date.now();
    await A.onBotMenu(menuEv());
    const menuMs = Date.now() - t0;
    check(`查询进行中点底部主菜单 → 立即响应(${menuMs}ms)`, menuMs < 3000 && cards().length >= 1, menuMs + 'ms cards=' + cards().length);

    // 3) a second query during the first -> immediate busy reply (session got reset by menu; re-enter)
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    t0 = Date.now();
    await A.onMessage(msgEv('再问一个问题'));
    const busyMs = Date.now() - t0;
    const gotBusy = texts().some(t => /查询进行中|请稍候/.test(t));
    check(`查询进行中再发查询 → 立即回「进行中」(${busyMs}ms)`, busyMs < 3000 && gotBusy, busyMs + 'ms texts=' + JSON.stringify(texts().slice(-2)));

    // 4) the original query still completes and posts a result
    let done = false;
    for (let i = 0; i < 60 && !done; i++) { await sleep(2000); done = texts().some(t => /✅ 查询结果|⚠️/.test(t)); }
    check('原查询后台完成并回了结果', done, '120s 内未见查询结果');
    const resultText = texts().filter(t => /✅ 查询结果/.test(t)).join(' ').slice(0, 160);
    if (resultText) console.log('  (结果预览: ' + resultText.replace(/\n/g, ' ') + '…)');
  } finally {
    fs.writeFileSync(CFG, cfgBackup);
    if (sessBackup) fs.writeFileSync(SESS, sessBackup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { fs.writeFileSync(CFG, cfgBackup); if (sessBackup) fs.writeFileSync(SESS, sessBackup); console.error(e); process.exit(1); });
