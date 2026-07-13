// Offline card-flow test: no network. Mocks the Feishu client and drives real handlers, asserting
// that entering a project + a backlog of bottom-menu events does NOT pile up cards or snap back.
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

const menuEv = () => ({ event_key: 'menu', operator: { operator_id: { open_id: OWNER } } });
const cardEv = (val, mid) => ({ action: { value: val }, context: { open_chat_id: CHAT, open_message_id: mid }, operator: { open_id: OWNER } });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const creates = () => client.__calls.filter(c => c.op === 'create' && c.type === 'interactive');
const isProjectCard = t => /项目操作/.test(t || '');
const isMenuCard = t => /选择操作/.test(t || '');

let failed = 0;
function check(name, cond, extra) { if (cond) { console.log('  ✓ ' + name); } else { console.log('  ✗ ' + name + (extra ? ' — ' + extra : '')); failed++; } }

async function main() {
  const backup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;
  try {
    A.lastCard.clear();
    A.setSession(CHAT, { mode: 'idle' });

    // 1) bottom menu from idle -> one fresh main-menu card
    client.__reset();
    await A.onBotMenu(menuEv());
    const first = client.__calls[0];
    check('idle 点菜单 → 发一张主菜单卡', creates().length === 1 && isMenuCard(first && first.title), 'calls=' + JSON.stringify(client.__calls));
    const cardId = first && first.id;

    // 2) tap the project on that card -> it becomes the project card (patch, no new card)
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, cardId));
    const afterEnter = client.__calls[client.__calls.length - 1];
    check('点项目 → 原地变项目卡(patch,不新发)', afterEnter && afterEnter.op === 'patch' && isProjectCard(afterEnter.title) && creates().length === 1,
      'last=' + JSON.stringify(afterEnter) + ' creates=' + creates().length);
    check('进项目后 session=project', A.getSession(CHAT).mode === 'project');

    // 3) a backlog of stale menu events (>3s apart to clear the dedup window) must NOT pile up a card
    for (let i = 0; i < 2; i++) {
      await sleep(3100);
      await A.onBotMenu(menuEv());
    }
    check('积压菜单事件不堆卡(仍只有 1 张 create)', creates().length === 1, 'creates=' + creates().length + ' calls=' + JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    const lastCard = client.__calls[client.__calls.length - 1];
    check('积压菜单后仍停在项目卡(未跳回主菜单)', isProjectCard(lastCard && lastCard.title), 'last=' + JSON.stringify(lastCard));
    check('积压菜单后 session 仍是 project(未被踢出)', A.getSession(CHAT).mode === 'project');

    // 4) the card's ⬅主菜单 button DOES return to the menu (in place)
    await A.onCardAction(cardEv({ do: 'home' }, cardId));
    const afterHome = client.__calls[client.__calls.length - 1];
    check('点卡片「⬅主菜单」→ 原地回主菜单卡', afterHome && afterHome.op === 'patch' && isMenuCard(afterHome.title) && creates().length === 1,
      'last=' + JSON.stringify(afterHome));
    check('回主菜单后 session=idle', A.getSession(CHAT).mode === 'idle');
  } finally {
    if (backup) fs.writeFileSync(SESS, backup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
