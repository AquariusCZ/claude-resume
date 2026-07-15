// Offline card-flow smoke test: no network. Mocks the Feishu client and drives the real handlers.
// The bottom menu is a reliable ESCAPE HATCH — from ANY state (in a project, after clearing the chat
// so the card was deleted, or when the control card was pushed off-screen by a notification) tapping
// it emits a FRESH visible main-menu card and resets to idle. Card buttons still update in place.
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

// tap bottom 主菜单 and assert it produced a NEW visible main-menu card + reset to idle
async function tapMenu(name, presetup) {
  const before = creates().length;
  if (presetup) presetup();
  await sleep(3100);   // clear the menu-dedup window
  await A.onBotMenu(menuEv('menu'));
  const grew = creates().length - before;
  check(name, grew >= 1 && isMenuCard(last().title) && A.getSession(CHAT).mode === 'idle',
    'newMenuCards=' + grew + ' sessionMode=' + A.getSession(CHAT).mode + ' last=' + JSON.stringify(last()));
}

async function main() {
  const backup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;
  try {
    A.lastCard.clear(); A.setSession(CHAT, { mode: 'idle' }); client.__reset();

    // 1. idle -> one main-menu card
    await A.onBotMenu(menuEv('menu'));
    check('idle 点底部主菜单 → 发主菜单卡', creates().length === 1 && isMenuCard(creates()[0].title), JSON.stringify(client.__calls));
    const c1 = creates()[0].id;

    // 2. enter a project (card button) -> project card in place (patch, no new card)
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, c1));
    check('点项目 → 原地变项目卡(patch)', last().op === 'patch' && isProjectCard(last().title), JSON.stringify(last()));
    check('进项目后 session=project', A.getSession(CHAT).mode === 'project');

    // 3. from a project -> escape hatch: fresh main menu + idle
    await tapMenu('项目里点底部主菜单 → 补发主菜单卡 + 回 idle', null);

    // 4. cleared chat: lastCard points at a DELETED message + stuck in project -> still a fresh card
    await tapMenu('清空聊天(卡已删)+卡在项目 → 仍补发主菜单卡', () => {
      A.lastCard.set(CHAT, 'msg_gone_x'); A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    });

    // 5. control card still ALIVE but scrolled off-screen (a checker/quota notification pushed it up)
    await tapMenu('控制卡被通知顶到上方(仍存活)→ 仍在底部补发主菜单卡', () => {
      A.lastCard.set(CHAT, 'msg_alive_scrolled'); A.setSession(CHAT, { mode: 'idle' });
    });

    // 6. bottom 闲聊 / 状态 always respond
    await sleep(3100); client.__reset();
    await A.onBotMenu(menuEv('chat'));
    check('底部「闲聊」→ 文字反馈 + session=chat', client.__calls.some(c => c.op === 'create' && c.type === 'text') && A.getSession(CHAT).mode === 'chat', JSON.stringify(client.__calls));
    await sleep(3100); client.__reset();
    await A.onBotMenu(menuEv('status'));
    check('底部「状态」→ 文字反馈', client.__calls.some(c => c.op === 'create' && c.type === 'text'), JSON.stringify(client.__calls));

    // 7. duplicate delivery (same event_id) dropped
    await sleep(3100); client.__reset();
    const dup = menuEv('menu'); await A.onBotMenu(dup); const n = client.__calls.length;
    await A.onBotMenu(dup);
    check('同一 event_id 的重复投递被丢弃', client.__calls.length === n, 'grew: ' + JSON.stringify(client.__calls));

    // 8. card buttons still work in place: enter -> 修改 -> ⬅主菜单
    await sleep(3100); A.lastCard.clear(); A.setSession(CHAT, { mode: 'idle' }); client.__reset();
    await A.onBotMenu(menuEv('menu')); const c8 = creates()[0].id;
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, c8));
    await A.onCardAction(cardEv({ do: 'submode', sm: 'modify' }, c8));
    check('进项目→选修改 → 弹出会话列表卡', /选择会话/.test(last().title || ''), JSON.stringify(last()));
    const sl = A.listProjectSessions(PROJ.path, 5);
    if (sl.length) {
      client.__reset();
      await A.onCardAction(cardEv({ do: 'pick', s: sl[0].id }, c8));
      await sleep(250);   // the card patch and the history digest are both async
      check('选一个会话 → 回项目卡(修改模式)',
        client.__calls.some(c => c.op === 'patch' && isProjectCard(c.title)) && A.getSession(CHAT).work === sl[0].id,
        JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    }
    await A.onCardAction(cardEv({ do: 'home' }, c8));
    check('卡片「⬅主菜单」→ 回主菜单卡(原地)+ session idle', isMenuCard(last().title) && A.getSession(CHAT).mode === 'idle', JSON.stringify(last()));
  } finally {
    if (backup) fs.writeFileSync(SESS, backup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
