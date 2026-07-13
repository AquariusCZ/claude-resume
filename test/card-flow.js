// Offline card-flow smoke test: no network. Mocks the Feishu client and drives the real handlers.
// Focus: the bottom menu is a reliable ESCAPE HATCH — from ANY state (incl. after clearing the chat,
// i.e. the live card was deleted) tapping it returns to a visible main menu; project/chat/home work.
// Run: node test/card-flow.js
'use strict';
process.env.FEISHU_TEST = '1';
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const APP = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume');
const CFG = path.join(APP, 'config.json');
const SESS = path.join(APP, 'feishu-sessions.json');
const cfg = JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^﻿/, ''));
const CHAT = cfg.feishuChatId; assert(CHAT, 'config.feishuChatId required');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]) || null;
assert(OWNER, 'need an owner in feishuAuthOpenIds');

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const projects = A.discoverProjects();
assert(projects.length, 'need >=1 discovered project');
const PROJ = projects.find(p => /Probe-Station/i.test(p.name)) || projects[0];

let eid = 0;
const menuEv = (key) => ({ event_key: key || 'menu', operator: { operator_id: { open_id: OWNER } }, header: { event_id: 'e' + (++eid), create_time: String(Date.now()) } });
const cardEv = (val, mid) => ({ action: { value: val }, context: { open_chat_id: CHAT, open_message_id: mid }, operator: { open_id: OWNER } });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const creates = () => client.__calls.filter(c => c.op === 'create' && c.type === 'interactive');
const isProjectCard = t => /项目操作/.test(t || '');
const isMenuCard = t => /选择操作/.test(t || '');
const last = () => client.__calls[client.__calls.length - 1];

let failed = 0;
function check(n, c, x) { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; }

async function main() {
  const backup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;
  try {
    // ---- 1. bottom menu from idle -> one fresh main-menu card ----
    A.lastCard.clear(); A.setSession(CHAT, { mode: 'idle' }); client.__reset();
    await A.onBotMenu(menuEv('menu'));
    check('idle 点底部主菜单 → 发主菜单卡', creates().length === 1 && isMenuCard(creates()[0].title), JSON.stringify(client.__calls));
    const cardId = creates()[0].id;

    // ---- 2. enter a project -> project card in place ----
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, cardId));
    check('点项目 → 原地变项目卡', last().op === 'patch' && isProjectCard(last().title) && creates().length === 1, JSON.stringify(last()));
    check('进项目后 session=project', A.getSession(CHAT).mode === 'project');

    // ---- 3. bottom menu from a project -> back to MAIN MENU (escape hatch), visible, session idle ----
    await sleep(3100);
    await A.onBotMenu(menuEv('menu'));
    check('项目里点底部主菜单 → 回到主菜单卡(可见)', isMenuCard(last().title), JSON.stringify(last()));
    check('回主菜单后 session=idle(不再卡在项目)', A.getSession(CHAT).mode === 'idle');
    check('没有堆卡(仍只有 1 张 create)', creates().length === 1, 'creates=' + creates().length);

    // ---- 4. cleared chat: lastCard points at a DELETED message -> patch fails -> fresh card ----
    A.lastCard.set(CHAT, 'msg_gone_1'); A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    const before = creates().length;
    await sleep(3100);
    await A.onBotMenu(menuEv('menu'));
    check('清空聊天后点底部主菜单 → 补发一张新主菜单卡(不再无反应)', creates().length === before + 1 && isMenuCard(last().title), JSON.stringify(last()));
    check('清空聊天后回主菜单 session=idle', A.getSession(CHAT).mode === 'idle');

    // ---- 5. bottom 闲聊 / 状态 always respond ----
    await sleep(3100); client.__reset();
    await A.onBotMenu(menuEv('chat'));
    check('底部「闲聊」→ 有文字反馈 + session=chat', client.__calls.some(c => c.op === 'create' && c.type === 'text') && A.getSession(CHAT).mode === 'chat', JSON.stringify(client.__calls));
    await sleep(3100); client.__reset();
    await A.onBotMenu(menuEv('status'));
    check('底部「状态」→ 有文字反馈', client.__calls.some(c => c.op === 'create' && c.type === 'text'), JSON.stringify(client.__calls));

    // ---- 6. duplicate delivery (same event_id) is dropped ----
    client.__reset();
    const dup = menuEv('menu');
    await A.onBotMenu(dup); const afterFirst = client.__calls.length;
    await A.onBotMenu(dup);  // same event_id -> must be ignored
    check('同一 event_id 的重复投递被丢弃', client.__calls.length === afterFirst, 'calls grew: ' + JSON.stringify(client.__calls));

    // ---- 7. project card buttons still work: enter -> pick 修改 -> ⬅主菜单 ----
    await sleep(3100);   // let the 3s menu-dedup window from case 6 clear
    A.lastCard.clear(); A.setSession(CHAT, { mode: 'idle' }); client.__reset();
    await A.onBotMenu(menuEv('menu')); const mid2 = creates()[0].id;
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, mid2));
    await A.onCardAction(cardEv({ do: 'submode', sm: 'modify' }, mid2));
    check('进项目→选修改 → 项目卡显示修改模式', isProjectCard(last().title), JSON.stringify(last()));
    await A.onCardAction(cardEv({ do: 'home' }, mid2));
    check('点卡片「⬅主菜单」→ 回主菜单卡 + session idle', isMenuCard(last().title) && A.getSession(CHAT).mode === 'idle', JSON.stringify(last()));
  } finally {
    if (backup) fs.writeFileSync(SESS, backup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
