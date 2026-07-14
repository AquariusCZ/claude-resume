// End-to-end: a project WITH an AI_GUIDE.md answers a filename-decoding question fast & accurately.
// Clears the project's query session first (so the guide is injected on session creation), then asks
// what the filename fields mean; asserts the answer reflects the guide's decoding (宽度/偏置), NOT the
// naive "wavelength" reading. Runs real claude (haiku). Run: node test/guide-e2e.js
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

async function main() {
  let failed = 0;
  const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };
  try {
    const PROJ = A.discoverProjects().find(p => /Wafer Map/i.test(p.name));
    assert(PROJ, 'need the Wafer Map_beta project discovered');
    assert(fs.existsSync(path.join(PROJ.path, 'AI_GUIDE.md')), 'need AI_GUIDE.md in the project root');
    A.clearQuerySession(PROJ.path);   // ensure a fresh session so the guide is injected
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    client.__reset();

    const q = '这个项目的数据文件名里,w6000 和 bp120 分别代表什么?一句话说明。';
    const ev = { message: { message_id: 'm_guide_' + Date.now(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: q }) }, sender: { sender_id: { open_id: OWNER } } };
    console.log('查询(首次,注入 AI_GUIDE.md)…');
    const t0 = Date.now();
    await A.onMessage(ev);   // resolves fast; poll for the RESULT message (not the announce echo)
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    const results = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text')
      .map(c => c.text || '').filter(t => /✅ 查询结果|⚠️/.test(t));
    for (let i = 0; i < 60 && results().length === 0; i++) await sleep(2000);
    const secs = Math.round((Date.now() - t0) / 1000);
    const reply = results().join('\n');
    console.log('\n--- 查询结果(' + secs + 's)---\n' + reply.slice(0, 500) + '\n---');
    check('拿到了查询结果', results().length > 0, '120s 内无结果');
    check('w6000 解读为「宽度」(用了导览的纠正,而非误判为波长)', /宽度|width|µm|um|微米/i.test(reply) && !/w6000.{0,6}波长|波长.{0,6}6000/.test(reply), reply.slice(0, 200));
    check('bp120 解读为「偏置/bias」(mV,正)', /偏置|bias/i.test(reply), reply.slice(0, 200));
    check('不是失败兜底', reply.length > 0 && !/未拿到成功结果/.test(reply), reply.slice(0, 120));
  } finally {
    fs.writeFileSync(CFG, cfgBackup);
    if (sessBackup) fs.writeFileSync(SESS, sessBackup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { fs.writeFileSync(CFG, cfgBackup); if (sessBackup) fs.writeFileSync(SESS, sessBackup); console.error(e); process.exit(1); });
