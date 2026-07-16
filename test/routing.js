// Routing & hijack regression tests — the three user-reported failures, Murphy-style:
//   A) a COWORKER's bot-menu tap must reply into THE COWORKER's own chat, never the owner's;
//      a coworker's card click must NOT hijack feishuChatId (the owner's notification chat);
//   B) inside a conversation, answers like "选 A" / "1" must go to the conversation, not the menu;
//      in idle they still work as commands;
//   C) the model registry: Fable 5 button value, free-form 「模型 claude-*」, junk rejected.
// Offline (mock client, no claude run needed — the modify branch is stopped before claude finishes).
// Run: node test/routing.js
'use strict';
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_NO_CLAUDE = '1';   // dispatch-only test: NEVER spawn a real claude (see LESSONS 3b)
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const APP = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume');
const CFG = path.join(APP, 'config.json');
const SESS = path.join(APP, 'feishu-sessions.json');
const UCH = path.join(APP, 'feishu-userchats.json');
const cfgBackup = fs.readFileSync(CFG);
const sessBackup = fs.existsSync(SESS) ? fs.readFileSync(SESS) : null;
const uchBackup = fs.existsSync(UCH) ? fs.readFileSync(UCH) : null;
const cfg = JSON.parse(cfgBackup.toString('utf8').replace(/^﻿/, ''));
const OWNER_CHAT = cfg.feishuChatId; assert(OWNER_CHAT, 'need feishuChatId');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]); assert(OWNER, 'need owner');
const MATE = 'ou_coworker_routing_test';
const MATE_CHAT = 'oc_coworker_chat_routing_test';

// pin the cheapest model for the one real (harmless, isolated-cwd) chat run B2 makes; restored in finally
cfg.feishuChatModel = 'haiku'; fs.writeFileSync(CFG, JSON.stringify(cfg, null, 4));

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const sleep = ms => new Promise(r => setTimeout(r, ms));
let eid = 0;
const msgEv = (t, open, chat, chatType) => ({ message: { message_id: 'm_rt_' + (++eid) + '_' + Date.now(), chat_id: chat, chat_type: chatType || 'p2p', message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: open } } });
const menuEv = (key, open) => ({ event_key: key, operator: { operator_id: { open_id: open } }, header: { event_id: 'ert' + (++eid), create_time: String(Date.now()) } });
const cardEv = (val, open, chat, mid) => ({ action: { value: val }, context: { open_chat_id: chat, open_message_id: mid || 'msg_rt' }, operator: { open_id: open } });
const readCfg = () => JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^﻿/, ''));
const callsTo = (id) => client.__calls.filter(c => c.op === 'create' && (c.to === id));

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  try {
    // ---------- A. per-user routing ----------
    // coworker sends a message from THEIR chat -> their chat becomes known, and feishuChatId must NOT change
    A.setSession(MATE_CHAT, { mode: 'idle' }); client.__reset();
    await A.onMessage(msgEv('帮助', MATE, MATE_CHAT));
    check('A1 同事发消息后 feishuChatId 未被劫持(仍是 owner 的)', readCfg().feishuChatId === OWNER_CHAT, 'now=' + readCfg().feishuChatId);

    // coworker taps the bottom menu -> the menu card goes to THE COWORKER's chat, not the owner's
    await sleep(1600); client.__reset();
    await A.onBotMenu(menuEv('menu', MATE));
    const mateGot = client.__calls.filter(c => c.op === 'create' && c.type === 'interactive' && c.to === MATE_CHAT).length;
    const ownerGot = client.__calls.filter(c => c.to === OWNER_CHAT).length;
    check('A2 同事点底部菜单 → 卡片发到同事自己的聊天', mateGot >= 1, JSON.stringify(client.__calls));
    check('A3 同事点底部菜单 → owner 的聊天里没有任何东西', ownerGot === 0, JSON.stringify(client.__calls));

    // coworker card click must not hijack feishuChatId either
    client.__reset();
    await A.onCardAction(cardEv({ do: 'status' }, MATE, MATE_CHAT));
    check('A4 同事点卡片后 feishuChatId 仍未被劫持', readCfg().feishuChatId === OWNER_CHAT, 'now=' + readCfg().feishuChatId);

    // owner's own message still (re)binds the notification chat
    await A.onMessage(msgEv('帮助', OWNER, OWNER_CHAT));
    check('A5 owner 消息仍能绑定通知 chat', readCfg().feishuChatId === OWNER_CHAT);

    // coworker enters a project via card -> goes STRAIGHT to read-only query (no dead choice card)
    client.__reset();
    const PROJ = A.discoverProjects()[0];
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path }, MATE, MATE_CHAT));
    const mateSess = A.getSession(MATE_CHAT);
    check('A6 同事进项目 → 直接只读查询模式(无需选 只读/修改)', mateSess.mode === 'project' && mateSess.sub === 'query', JSON.stringify(mateSess));

    // GROUP poisoning: an @-mention in a group must NOT change the user's p2p mapping — the next
    // bottom-menu tap must still reply to their own chat, never into the group
    await sleep(1600); client.__reset();
    await A.onMessage(msgEv('帮助', MATE, 'oc_some_group_chat', 'group'));
    await A.onBotMenu(menuEv('menu', MATE));
    const toGroup = client.__calls.filter(c => c.op === 'create' && c.to === 'oc_some_group_chat' && c.type === 'interactive').length;
    const toMate = client.__calls.filter(c => c.op === 'create' && c.to === MATE_CHAT && c.type === 'interactive').length;
    check('A7 群里@机器人后点底部菜单 → 卡片仍发到本人私聊,绝不进群', toGroup === 0 && toMate >= 1, JSON.stringify(client.__calls.map(c => c.op + '->' + c.to)));

    // owner in a group must not rebind the notification chat
    await A.onMessage(msgEv('你好', OWNER, 'oc_some_group_chat', 'group'));
    check('A8 owner 群发言不改通知 chat', readCfg().feishuChatId === OWNER_CHAT, 'now=' + readCfg().feishuChatId);

    // menu-first NEW user: mode picked from the bottom menu must survive their first typed message
    const FRESH = 'ou_fresh_user_routing_test';
    await sleep(1600); client.__reset();
    await A.onBotMenu(menuEv('chat', FRESH));            // no mapping yet -> od: pseudo target
    await A.onMessage(msgEv('随便聊聊', FRESH, 'oc_fresh_user_chat'));   // first real message
    await sleep(300);
    const freshSess = A.getSession('oc_fresh_user_chat');
    check('A9 新用户先点菜单再发消息 → 菜单选的闲聊模式不丢(od:状态已迁移)', freshSess.mode === 'chat', JSON.stringify(freshSess));

    // viewer must not reach the modify-session flow buttons
    client.__reset();
    await A.onCardAction(cardEv({ do: 'sesslist' }, MATE, MATE_CHAT));
    const gotList = client.__calls.some(c => c.op === 'patch' && /选择会话/.test(c.title || ''));
    check('A10 viewer 点 sesslist(伪造)→ 被拒,看不到 owner 会话列表', !gotList, JSON.stringify(client.__calls));

    // ---------- B. conversation text is NOT hijacked ----------
    // owner in a project modify session: "选 A" must reach the conversation (announce appears), not
    // the menu. SAFETY (learned the hard way): use a NON-EXISTENT work session id — claude's
    // --resume then errors out instantly, so nothing actually executes against the real repo.
    // (An earlier version resumed a REAL session here; the claude it woke up interpreted "选 A"
    // against that session's old context and pushed a git commit. Murphy wins; never again.)
    A.setSession(OWNER_CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: 'deaddead-dead-4dea-8dea-deaddeaddead' });
    client.__reset();
    await A.onMessage(msgEv('选 A', OWNER, OWNER_CHAT));
    await sleep(400);
    const t1 = client.__calls.filter(c => c.type === 'text').map(c => c.text || '');
    check('B1 修改会话中回复「选 A」→ 进入会话执行(不再弹“没找到项目A”/菜单)',
      t1.some(t => /在「.*」执行:选 A/.test(t)) && !t1.some(t => /没找到项目/.test(t)), JSON.stringify(t1));
    await A.onMessage(msgEv('停止', OWNER, OWNER_CHAT));   // belt-and-braces

    // chat mode: a greeting goes to the chat, not the menu (it used to pop the menu card)
    A.setSession(OWNER_CHAT, { mode: 'chat' }); await sleep(200); client.__reset();
    await A.onMessage(msgEv('在吗', OWNER, OWNER_CHAT));
    await sleep(300);
    const popped = client.__calls.some(c => c.type === 'interactive');
    check('B2 闲聊中发「在吗」→ 不弹菜单卡(进对话)', !popped, JSON.stringify(client.__calls.map(c => c.op + ':' + c.type)));
    await A.onMessage(msgEv('停止', OWNER, OWNER_CHAT)); await sleep(100);
    // stop chat claude if any: chat has no 停止 target; just clear session state
    A.setSession(OWNER_CHAT, { mode: 'idle' });

    // idle mode: "1" still enters project #1 (commands work where they should)
    await sleep(200); client.__reset();
    await A.onMessage(msgEv('1', OWNER, OWNER_CHAT));
    const s1 = A.getSession(OWNER_CHAT);
    check('B3 idle 里发「1」→ 仍作为命令进入 1 号项目', s1.mode === 'project', JSON.stringify(s1));

    // ---------- C. models ----------
    A.setSession(OWNER_CHAT, { mode: 'idle' }); client.__reset();
    await A.onCardAction(cardEv({ do: 'model', m: 'claude-fable-5' }, OWNER, OWNER_CHAT));
    check('C1 卡片按钮切到 Fable 5(完整 id 被接受)', readCfg().feishuChatModel === 'claude-fable-5', 'model=' + readCfg().feishuChatModel);
    await A.onMessage(msgEv('模型 claude-someday-7', OWNER, OWNER_CHAT));
    check('C2 「模型 claude-someday-7」→ 任意未来 id 直接可用', readCfg().feishuChatModel === 'claude-someday-7', 'model=' + readCfg().feishuChatModel);
    await A.onMessage(msgEv('模型 gpt5', OWNER, OWNER_CHAT));
    check('C3 非 claude id 被拒绝且模型不变', readCfg().feishuChatModel === 'claude-someday-7', 'model=' + readCfg().feishuChatModel);
    await A.onMessage(msgEv('模型 默认', OWNER, OWNER_CHAT));
    check('C4 「模型 默认」→ 清空回默认', readCfg().feishuChatModel === '', 'model=' + readCfg().feishuChatModel);

    // ---------- D. bottom-menu model switch mid-conversation (must NOT disturb the session) ----------
    // owner is deep in a modify conversation; tapping the bottom 🤖 model button must post a model
    // card WITHOUT resetting mode/project/work
    const before = { mode: 'project', project: PROJ.path, sub: 'modify', work: 'deaddead-dead-4dea-8dea-deaddeaddead' };
    A.setSession(OWNER_CHAT, { ...before }); client.__reset();
    await A.onBotMenu(menuEv('model', OWNER));
    const modelCardSent = client.__calls.some(c => c.op === 'create' && c.type === 'interactive' && /切换模型/.test(c.title || ''));
    const sAfter = A.getSession(OWNER_CHAT);
    check('D1 对话中点底部「🤖模型」→ 弹独立模型卡', modelCardSent, JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    check('D2 弹模型卡不打断会话(mode/project/work 原样保留)',
      sAfter.mode === before.mode && sAfter.project === before.project && sAfter.sub === before.sub && sAfter.work === before.work, JSON.stringify(sAfter));

    // picking a model on that card updates config + re-renders the MODEL card (not the main menu), session still intact
    client.__reset();
    await A.onCardAction(cardEv({ do: 'model', m: 'opus', from: 'm' }, OWNER, OWNER_CHAT, 'msg_modelcard'));
    const reRenderedModelCard = client.__calls.some(c => c.op === 'patch' && /切换模型/.test(c.title || ''));
    const sAfter2 = A.getSession(OWNER_CHAT);
    check('D3 在模型卡上选 Opus → 配置生效', readCfg().feishuChatModel === 'opus', 'model=' + readCfg().feishuChatModel);
    check('D4 选完仍是模型卡(不回落主菜单)+ 会话仍未被打断',
      reRenderedModelCard && sAfter2.mode === before.mode && sAfter2.work === before.work, JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))) + ' sess=' + JSON.stringify(sAfter2));

    // a VIEWER tapping the bottom model button is denied (feishuChatModel is shared config)
    client.__reset();
    await A.onBotMenu(menuEv('model', MATE));
    const viewerGotModelCard = client.__calls.some(c => c.op === 'create' && c.type === 'interactive' && /切换模型/.test(c.title || ''));
    check('D5 viewer 点底部「🤖模型」→ 被拒,拿不到模型卡', !viewerGotModelCard, JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
  } finally {
    fs.writeFileSync(CFG, cfgBackup);
    if (sessBackup) fs.writeFileSync(SESS, sessBackup); else { try { fs.unlinkSync(SESS); } catch (e) {} }
    if (uchBackup) fs.writeFileSync(UCH, uchBackup); else { try { fs.unlinkSync(UCH); } catch (e) {} }
    // belt-and-braces: if any stray test session jsonl ever appears, remove it
    try {
      const base = path.join(process.env.USERPROFILE || '', '.claude', 'projects');
      for (const d of fs.readdirSync(base)) { const f = path.join(base, d, 'deaddead-dead-4dea-8dea-deaddeaddead.jsonl'); if (fs.existsSync(f)) fs.unlinkSync(f); }
    } catch (e) {}
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
