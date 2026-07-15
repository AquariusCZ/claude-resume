// Session picker smoke test: ✏️修改项目 must let you choose WHICH conversation to continue
// (with a short history digest), or start a fresh one — and must never guess.
// Mocks the Feishu API; does NOT run claude (asserts it refuses to run until a session is picked).
// Run: node test/session-pick.js
'use strict';
process.env.FEISHU_TEST = '1';
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const APP = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume');
const CFG = path.join(APP, 'config.json');
const SESS = path.join(APP, 'feishu-sessions.json');
const cfg = JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^﻿/, ''));
const CHAT = cfg.feishuChatId; assert(CHAT, 'need feishuChatId');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]); assert(OWNER, 'need owner');

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const cardEv = (val, mid) => ({ action: { value: val }, context: { open_chat_id: CHAT, open_message_id: mid }, operator: { open_id: OWNER } });
const msgEv = (t) => ({ message: { message_id: 'm_sp_' + Date.now() + Math.random(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: OWNER } } });
const last = () => client.__calls[client.__calls.length - 1];
const texts = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text').map(c => c.text || '');
const isSessionCard = t => /选择会话/.test(t || '');
const isProjectCard = t => /项目操作/.test(t || '');

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  const backup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;
  try {
    // a project that actually has past conversations
    const PROJ = A.discoverProjects().find(p => A.listProjectSessions(p.path, 3).length >= 2);
    assert(PROJ, 'need a project with >=2 past sessions');
    const list = A.listProjectSessions(PROJ.path, 5);
    console.log(`项目: ${PROJ.name} · 历史会话 ${list.length} 个`);

    const MID = 'msg_sp_card';
    A.lastCard.clear(); A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: undefined }); client.__reset();

    // 1. picking ✏️修改 must show the session list, not silently continue something
    await A.onCardAction(cardEv({ do: 'submode', sm: 'modify' }, MID));
    check('点「✏️修改项目」→ 弹会话列表(不擅自续会话)', isSessionCard(last().title), JSON.stringify(last()));
    check('此时还没选会话(work 未定)', !A.getSession(CHAT).work);

    // 2. sending an instruction before picking must NOT run claude — it re-shows the picker
    client.__reset();
    await A.onMessage(msgEv('把 README 改一下'));
    await sleep(300);
    check('未选会话就发指令 → 不跑 claude,重新弹会话列表', isSessionCard(last().title) && !texts().some(t => /在「.*」执行/.test(t)), JSON.stringify(client.__calls));

    // 3. pick an existing session -> project card + work set + history digest posted
    // (the card patch and the digest are both async — assert on the call log, not on last())
    client.__reset();
    await A.onCardAction(cardEv({ do: 'pick', s: list[0].id }, MID));
    await sleep(250);
    check('选中会话 → 卡片回到项目卡', client.__calls.some(c => c.op === 'patch' && isProjectCard(c.title)), JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    check('work 记录为所选会话', A.getSession(CHAT).work === list[0].id, 'work=' + A.getSession(CHAT).work);
    for (let i = 0; i < 20 && !texts().some(t => /已进入会话/.test(t)); i++) await sleep(150);
    const digest = texts().find(t => /已进入会话/.test(t)) || '';
    check('推送了该会话的历史摘要', /已进入会话/.test(digest) && /最近的对话|直接发指令/.test(digest), JSON.stringify(texts()));
    console.log('  (摘要预览: ' + digest.replace(/\n/g, ' | ').slice(0, 150) + '…)');

    // 4. 🔀 切换会话 reopens the list, with the current one ticked
    client.__reset();
    await A.onCardAction(cardEv({ do: 'sesslist' }, MID));
    check('点「🔀 切换会话」→ 再次弹列表', isSessionCard(last().title), JSON.stringify(last()));

    // 5. 🆕 新开会话 -> a fresh uuid, no history
    client.__reset();
    await A.onCardAction(cardEv({ do: 'newsess' }, MID));
    const w = A.getSession(CHAT).work;
    check('点「🆕 新开会话」→ work 变成一个全新 uuid', !!w && w !== list[0].id && /^[0-9a-f-]{36}$/i.test(w), 'work=' + w);
    for (let i = 0; i < 20 && !texts().some(t => /全新会话/.test(t)); i++) await sleep(150);
    check('提示这是全新会话(不带历史)', texts().some(t => /全新会话/.test(t)), JSON.stringify(texts()));

    // 6. switching back to 👁只读 still works and doesn't need a session
    client.__reset();
    await A.onCardAction(cardEv({ do: 'submode', sm: 'query' }, MID));
    check('切回「👁只读查询」→ 项目卡(只读不需选会话)', isProjectCard(last().title) && A.getSession(CHAT).sub === 'query', JSON.stringify(last()));
  } finally {
    if (backup) fs.writeFileSync(SESS, backup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
