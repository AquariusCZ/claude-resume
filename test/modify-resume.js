// Proves ✏️修改 continues the session you PICKED (not "the most recent one"): pick an older session
// whose history contains a known marker, ask about it, and assert claude recalls it — that can only
// happen if --resume targeted that exact conversation. Runs real claude (haiku), read-only question.
// Run: node test/modify-resume.js
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
const msgEv = (t) => ({ message: { message_id: 'm_mr_' + Date.now(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: OWNER } } });
const texts = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text').map(c => c.text || '');

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  try {
    const MARK = '换行传递成功';   // left in an old test session by query-e2e's earlier runs
    const PROJ = A.discoverProjects().find(p => /claude-resume/i.test(p.name));
    assert(PROJ, 'need the claude-resume project');
    // find an OLDER session (not the newest) whose transcript contains the marker
    const list = A.listProjectSessions(PROJ.path, 8);
    const target = list.find((s, i) => i > 0 && fs.readFileSync(s.file, 'utf8').indexOf(MARK) !== -1);
    if (!target) { console.log('  (跳过:没有含暗号的历史会话)'); process.exit(0); }
    console.log(`目标会话:「${target.title}」 [${target.id.slice(0, 8)}] · 不是最新的那个(最新是「${list[0].title.slice(0, 20)}」)`);

    // pick it, exactly like tapping it in the session list
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: target.id });
    client.__reset();
    await A.onMessage(msgEv('我们刚才对话里你原样输出过六个字,是哪六个字?只回答那六个字本身,不要做任何其他事,不要修改任何文件。'));

    const results = () => texts().filter(t => /✅ 「|⚠️ 「/.test(t));
    for (let i = 0; i < 60 && results().length === 0; i++) await sleep(2000);
    const reply = results().join('\n');
    console.log('\n--- 执行结果 ---\n' + reply.slice(0, 400) + '\n---');

    check('拿到了执行结果', results().length > 0, '120s 内无结果');
    check(`claude 记得所选会话的历史(回出了「${MARK}」)→ --resume 命中的是你选的那个会话`,
      reply.indexOf(MARK) !== -1, '未回出暗号 — 可能续错了会话');
  } finally {
    fs.writeFileSync(CFG, cfgBackup);
    if (sessBackup) fs.writeFileSync(SESS, sessBackup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { fs.writeFileSync(CFG, cfgBackup); if (sessBackup) fs.writeFileSync(SESS, sessBackup); console.error(e); process.exit(1); });
