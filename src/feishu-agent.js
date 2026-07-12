/*
  feishu-agent.js  ---  Claude Resume 双向飞书助手 (long-connection, no public IP needed)

  Receives messages from Feishu via the official SDK's WSClient (event subscription over a
  persistent WebSocket), maps each message to a project, runs `claude --continue -p "<text>"`
  in that project's folder, and replies with the result -- all continuing the SAME conversation
  the VS Code extension shows (reopen the session there to see the full thread).

  Config is read from %LOCALAPPDATA%\ClaudeResume\config.json:
    feishuAppId, feishuAppSecret   (required -- from the Feishu 自建应用)
    selected / customProjects      (project routing table, written by the GUI)
    skipPermissions, resumeModel, perProjectTimeoutMinutes  (reused resume settings)
    feishuAllowOpenIds  (optional string[]: only these sender open_ids may command; empty = allow all in-chat)

  Commands (DM the bot, or @it in a group):
    帮助 / help                 -> usage
    状态 / status               -> armed state, engine phase, exact reset, recent log
    项目 / list                 -> known projects
    停止 <项目> / stop <项目>    -> cancel a running command for that project
    <项目名> <指令>             -> run <指令> in that project (prefix match on name)
    <指令>                      -> run in the default project (the single armed one, or feishuDefaultProject)
*/
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

let lark;
try { lark = require('@larksuiteoapi/node-sdk'); }
catch (e) { console.error('缺少依赖 @larksuiteoapi/node-sdk,请在本目录运行: npm install'); process.exit(1); }

const APP_DIR = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'ClaudeResume');
const CONFIG_PATH = path.join(APP_DIR, 'config.json');
const LOG_DIR = path.join(APP_DIR, 'logs');

function readJson(p) {
  // strip a UTF-8 BOM: PowerShell may write config/state with one, which JSON.parse rejects
  return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, ''));
}
function readConfig() {
  try { return readJson(CONFIG_PATH); }
  catch (e) { return {}; }
}
function logLine(msg) {
  const d = new Date(), p = n => String(n).padStart(2, '0');   // LOCAL time (was UTC via toISOString)
  const day = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`;
  const ts = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  const line = `[${ts}] ${msg}\r\n`;
  try { fs.mkdirSync(LOG_DIR, { recursive: true }); fs.appendFileSync(path.join(LOG_DIR, 'feishu-' + day + '.log'), line, 'utf8'); } catch (e) {}
  process.stdout.write(line);
}

// single instance via pidfile (Windows lets two sockets share a loopback port, so a port lock
// is unreliable here). Two live agents would each receive every event and double-run commands.
const PID_PATH = path.join(APP_DIR, 'feishu-agent.pid');
function anotherInstanceAlive() {
  try {
    if (!fs.existsSync(PID_PATH)) return false;
    const pid = parseInt(String(fs.readFileSync(PID_PATH, 'utf8')).trim(), 10);
    if (!pid || pid === process.pid) return false;
    try { process.kill(pid, 0); return true; }        // signal 0 = liveness probe
    catch (e) { return e && e.code === 'EPERM'; }      // EPERM = alive but not ours; ESRCH = dead
  } catch (e) { return false; }
}
if (anotherInstanceAlive()) { console.error('另一个 feishu-agent 已在运行,退出。'); process.exit(0); }
try { fs.mkdirSync(APP_DIR, { recursive: true }); fs.writeFileSync(PID_PATH, String(process.pid)); } catch (e) {}
process.on('exit', () => { try { if (parseInt(fs.readFileSync(PID_PATH, 'utf8'), 10) === process.pid) fs.unlinkSync(PID_PATH); } catch (e) {} });

const cfg0 = readConfig();
const APP_ID = cfg0.feishuAppId || '';
const APP_SECRET = cfg0.feishuAppSecret || '';
if (!APP_ID || !APP_SECRET) { logLine('config.json 缺少 feishuAppId / feishuAppSecret,退出。'); process.exit(1); }

const client = new lark.Client({ appId: APP_ID, appSecret: APP_SECRET });

// ---- claude launcher resolution (same locations lib.ps1 checks) ----
function findClaudeCmd() {
  const cands = [
    path.join(process.env.APPDATA || '', 'npm', 'claude.cmd'),
    path.join(process.env.ProgramFiles || '', 'nodejs', 'claude.cmd'),
  ];
  for (const c of cands) { try { if (c && fs.existsSync(c)) return c; } catch (e) {} }
  return 'claude.cmd'; // rely on PATH
}
const CLAUDE_CMD = findClaudeCmd();

// ---- project discovery — mirrors the desktop GUI: ~/.claude/projects cwd scan (recency-sorted)
// + customProjects, minus hiddenProjects and the tool's own dirs. Keep this in sync with
// Get-ClaudeProjects/the picker so the Feishu 项目 list matches the desktop app exactly.
function discoverProjects() {
  const cfg = readConfig();
  const hidden = new Set((Array.isArray(cfg.hiddenProjects) ? cfg.hiddenProjects : []).map(h => String(h).toLowerCase()));
  const appDir = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume').toLowerCase();
  const excluded = cwd => {
    const l = String(cwd).toLowerCase();
    return hidden.has(l) || l.startsWith(appDir) || /^[a-z]:\\windows/i.test(cwd);
  };
  const disc = []; // {name, path, mtime}
  try {
    const root = path.join(os.homedir(), '.claude', 'projects');
    for (const dir of fs.readdirSync(root)) {
      const full = path.join(root, dir);
      let jsonls;
      try { jsonls = fs.readdirSync(full).filter(f => f.endsWith('.jsonl')); } catch (e) { continue; }
      if (!jsonls.length) continue;
      jsonls.sort((a, b) => fs.statSync(path.join(full, b)).mtimeMs - fs.statSync(path.join(full, a)).mtimeMs);
      const file = path.join(full, jsonls[0]);
      const mtime = fs.statSync(file).mtimeMs;
      const head = fs.readFileSync(file, 'utf8').split(/\r?\n/).slice(0, 60);
      for (const ln of head) {
        if (ln.indexOf('"cwd"') === -1) continue;
        try {
          const j = JSON.parse(ln);
          if (j.cwd && fs.existsSync(j.cwd) && !excluded(j.cwd)) disc.push({ name: path.basename(j.cwd), path: j.cwd, mtime });
          break;
        } catch (e) {}
      }
    }
  } catch (e) {}
  // dedup by path (keep newest), sort by recency
  const byPath = new Map();
  for (const d of disc) { const k = d.path.toLowerCase(); const c = byPath.get(k); if (!c || d.mtime > c.mtime) byPath.set(k, d); }
  const list = Array.from(byPath.values()).sort((a, b) => b.mtime - a.mtime).map(d => ({ name: d.name, path: d.path }));
  const seen = new Set(list.map(p => p.path.toLowerCase()));
  for (const p of (Array.isArray(cfg.customProjects) ? cfg.customProjects : [])) {
    if (p && p.path && fs.existsSync(p.path) && !excluded(p.path) && !seen.has(p.path.toLowerCase())) {
      list.push({ name: p.name || path.basename(p.path), path: p.path });
      seen.add(p.path.toLowerCase());
    }
  }
  return list;
}

// ---- per-chat active project (persisted) + a chat scratch session ----
const SESSIONS_PATH = path.join(APP_DIR, 'feishu-sessions.json');
const CHAT_DIR = path.join(APP_DIR, 'feishu-chat');
function readSessions() { try { return JSON.parse(fs.readFileSync(SESSIONS_PATH, 'utf8').replace(/^﻿/, '')); } catch (e) { return {}; } }
function writeSessions(o) { try { fs.writeFileSync(SESSIONS_PATH, JSON.stringify(o, null, 2), 'utf8'); } catch (e) {} }
// three modes: 'idle' (default — do nothing until the user picks via the card), 'chat', 'project'
function getSession(chatId) {
  const s = readSessions(); const v = s[chatId];
  if (!v) return { mode: 'idle' };
  if (typeof v === 'string') return v ? { mode: 'project', project: v } : { mode: 'idle' };   // legacy
  return { mode: v.mode || (v.project ? 'project' : 'idle'), project: v.project };
}
function setSession(chatId, sess) { const s = readSessions(); s[chatId] = sess; writeSessions(s); }
function activeProject(chatId) {
  const sess = getSession(chatId);
  if (sess.mode !== 'project' || !sess.project) return null;
  const found = discoverProjects().find(x => x.path.toLowerCase() === sess.project.toLowerCase());
  return found || { name: path.basename(sess.project), path: sess.project };
}
function chatStarted() { try { fs.mkdirSync(CHAT_DIR, { recursive: true }); return fs.existsSync(path.join(CHAT_DIR, '.started')); } catch (e) { return false; } }
function markChatStarted() { try { fs.writeFileSync(path.join(CHAT_DIR, '.started'), '1'); } catch (e) {} }

// find a project by 1-based number, exact name, or fuzzy (startsWith/includes)
function findProject(query) {
  const ps = discoverProjects(); const q = String(query || '').trim(); if (!q) return null;
  if (/^\d+$/.test(q)) { const i = parseInt(q, 10) - 1; return (i >= 0 && i < ps.length) ? ps[i] : null; }
  const low = q.toLowerCase();
  return ps.find(p => p.name.toLowerCase() === low)
      || ps.find(p => p.name.toLowerCase().startsWith(low))
      || ps.find(p => p.name.toLowerCase().includes(low)) || null;
}
// a bare message that is exactly a project number or full name -> that project (else null)
function projectIfBareName(text) {
  const ps = discoverProjects(); const q = text.trim();
  if (/^\d+$/.test(q)) { const i = parseInt(q, 10) - 1; return (i >= 0 && i < ps.length) ? ps[i] : null; }
  return ps.find(p => p.name.toLowerCase() === q.toLowerCase()) || null;
}
// "<project name> <command>" via longest-name-prefix (no default fallback)
function oneOffTarget(text) {
  const t = text.trim();
  const byLen = discoverProjects().slice().sort((a, b) => b.name.length - a.name.length);
  for (const p of byLen) {
    const n = p.name.toLowerCase();
    if (t.toLowerCase().startsWith(n)) {
      const rest = t.slice(p.name.length).replace(/^\s*[:：,，]?\s*/, '');
      if (rest) return { project: p, prompt: rest };
    }
  }
  return { project: null };
}

// ---- run claude in a cwd (project or the chat scratch dir); return {ok, limited, text} ----
const running = new Map(); // cwd(lower) -> child
function runClaude(cwd, label, prompt, opts) {
  opts = opts || {};
  return new Promise((resolve) => {
    const cfg = readConfig();
    try { fs.mkdirSync(cwd, { recursive: true }); } catch (e) {}
    const args = ['/c', CLAUDE_CMD];
    if (opts.useContinue !== false) args.push('--continue');
    args.push('-p', prompt, '--output-format', 'stream-json', '--verbose');
    const model = opts.model || cfg.resumeModel;   // opts.model lets chat use its own model
    if (model) { args.push('--model', model); }
    const skip = (opts.skipPermissions !== undefined) ? opts.skipPermissions : cfg.skipPermissions;
    if (skip) { args.push('--dangerously-skip-permissions'); }
    const timeoutMs = Math.max(1, (parseInt(cfg.perProjectTimeoutMinutes, 10) || 30)) * 60000;
    const key = cwd.toLowerCase();

    let child;
    try { child = spawn(process.env.ComSpec || 'cmd.exe', args, { cwd, windowsHide: true }); }
    catch (e) { resolve({ ok: false, limited: false, text: '启动 claude 失败: ' + e.message }); return; }
    running.set(key, child);

    let buf = '', resultText = null, isError = null, limited = false, killedForTimeout = false;
    const to = setTimeout(() => { killedForTimeout = true; try { child.kill(); } catch (e) {} }, timeoutMs);
    function scanLine(ln) {
      if (!ln) return;
      if (/"status"\s*:\s*"(blocked|rejected|limited|exceeded)"/.test(ln) ||
          /usage limit|rate limit|limit reached|weekly limit/i.test(ln)) limited = true;
      if (ln.indexOf('"type":"result"') !== -1 || /"type"\s*:\s*"result"/.test(ln)) {
        try { const j = JSON.parse(ln); if (j.type === 'result') { if (typeof j.result === 'string') resultText = j.result; if (typeof j.is_error === 'boolean') isError = j.is_error; } } catch (e) {}
      }
    }
    child.stdout.on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\n')) >= 0) { scanLine(buf.slice(0, i)); buf = buf.slice(i + 1); } });
    child.stderr.on('data', () => {});
    child.on('close', () => {
      clearTimeout(to); running.delete(key);
      if (buf) scanLine(buf);
      if (killedForTimeout) { resolve({ ok: false, limited, text: `执行超时(> ${timeoutMs / 60000} 分钟),已终止。` }); return; }
      if (resultText !== null && isError !== true) { resolve({ ok: true, limited, text: resultText }); return; }
      if (limited) { resolve({ ok: false, limited: true, text: '又被限流了。可在「Claude续跑」里布防,额度恢复后自动续跑。' }); return; }
      resolve({ ok: false, limited, text: resultText || '执行结束但未拿到成功结果(可能出错)。' });
    });
  });
}

// ---- Feishu send helpers ----
async function sendText(chatId, text) {
  // Feishu text messages get unwieldy past a few KB; chunk to <=3500 chars, cap 6 parts.
  const MAX = 3500, PARTS = 6;
  let parts = [];
  let s = String(text);
  while (s.length && parts.length < PARTS) { parts.push(s.slice(0, MAX)); s = s.slice(MAX); }
  if (s.length) parts[parts.length - 1] += '\n…(内容过长已截断,完整结果见 VS Code 该项目会话)';
  for (const p of parts) {
    try {
      await client.im.message.create({ params: { receive_id_type: 'chat_id' }, data: { receive_id: chatId, msg_type: 'text', content: JSON.stringify({ text: p }) } });
    } catch (e) { logLine('发送失败: ' + (e && e.message)); }
  }
}
async function sendCard(chatId, card) {
  try {
    await client.im.message.create({ params: { receive_id_type: 'chat_id' }, data: { receive_id: chatId, msg_type: 'interactive', content: JSON.stringify(card) } });
  } catch (e) { logLine('发送卡片失败: ' + (e && e.message)); }
}
// update the clicked card in place so the ✅ (current project / model / mode) moves
async function refreshCard(chatId, messageId) {
  if (!messageId) return;
  try {
    await client.im.message.patch({ path: { message_id: messageId }, data: { content: JSON.stringify(buildMenuCard(chatId)) } });
  } catch (e) { logLine('更新卡片失败: ' + (e && e.message)); }
}
// long runs go silent while claude works; send a heartbeat so the user knows it's alive.
// returns a stop() to clear it. First beat at 60s, so short runs produce no heartbeat.
function startHeartbeat(chatId, label) {
  let secs = 0;
  const t = setInterval(() => { secs += 60; sendText(chatId, `⏳ 「${label}」仍在执行…(已 ${secs}s;复杂任务/Opus 常需 1-4 分钟,跑完自动回结果)`); }, 60000);
  return () => { try { clearInterval(t); } catch (e) {} };
}
// Telegram-style menu: buttons to enter a project / chat / status / switch model.
// Default 'idle' mode does nothing until the user taps a button here.
function buildMenuCard(chatId) {
  const sess = getSession(chatId);
  const ap = activeProject(chatId);
  const projects = discoverProjects();
  const curModel = String(readConfig().feishuChatModel || '').toLowerCase();
  const modelLabel = { sonnet: 'Sonnet', opus: 'Opus', haiku: 'Haiku' }[curModel] || '默认';
  let modeLine;
  if (ap) modeLine = `**当前:📂 项目「${ap.name}」** — 直接发消息就在这里续跑。`;
  else if (sess.mode === 'chat') modeLine = '**当前:💬 闲聊模式** — 直接说话就是和我聊天。';
  else modeLine = '**请选择 👇** 点「闲聊模式」开始聊天,或点一个项目进入。选之前我不处理任何消息。';
  const eq = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: modeLine } }];
  elements.push({ tag: 'action', actions: [
    { tag: 'button', text: { tag: 'plain_text', content: (sess.mode === 'chat' ? '✅ ' : '💬 ') + '闲聊模式' }, type: sess.mode === 'chat' ? 'primary' : 'default', value: { do: 'chat' } },
    { tag: 'button', text: { tag: 'plain_text', content: 'ℹ️ 状态' }, type: 'default', value: { do: 'status' } },
    { tag: 'button', text: { tag: 'plain_text', content: '🔑 权限' }, type: 'default', value: { do: 'perm' } },
  ] });
  // model switch buttons (applies to BOTH chat and project execution)
  elements.push({ tag: 'div', text: { tag: 'lark_md', content: `**模型:${modelLabel}** — 聊天和项目执行都用它,点下面切换(被限流时可换个模型重试):` } });
  const models = [['默认', ''], ['Sonnet', 'sonnet'], ['Opus', 'opus'], ['Haiku', 'haiku']];
  elements.push({ tag: 'action', actions: models.map(([lbl, v]) => ({
    tag: 'button', text: { tag: 'plain_text', content: (eq(curModel, v) ? '✅ ' : '') + lbl },
    type: eq(curModel, v) ? 'primary' : 'default', value: { do: 'model', m: v },
  })) });
  if (projects.length) {
    elements.push({ tag: 'hr' });
    const btns = projects.slice(0, 15).map(p => ({
      tag: 'button',
      text: { tag: 'plain_text', content: ((ap && eq(ap.path, p.path)) ? '✅ ' : '📂 ') + p.name },
      type: (ap && eq(ap.path, p.path)) ? 'primary' : 'default',
      value: { do: 'enter', p: p.path },
    }));
    for (let i = 0; i < btns.length; i += 3) elements.push({ tag: 'action', actions: btns.slice(i, i + 3) });
  } else {
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: '_未发现项目。先在「Claude续跑」软件里添加/勾选。_' } });
  }
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'orange', title: { tag: 'plain_text', content: 'Claude 服务器助手 · 选择操作' } },
    elements,
  };
}

// ---- status / list / help (mode-aware) ----
function statusText(chatId) {
  const cfg = readConfig();
  let st = {};
  try { st = JSON.parse(fs.readFileSync(path.join(APP_DIR, 'state.json'), 'utf8')); } catch (e) {}
  let reset = '额度未接近上限';
  if (st.realFiveHourResetUtc && st.realResetProbedUtc) {
    const rr = st.realFiveHourResetUtc * 1000, now = Date.now();
    if (rr > now && (now - st.realResetProbedUtc * 1000) < 5 * 3600e3) {
      const s = Math.floor((rr - now) / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
      reset = `5h 距重置 ${h}h ${String(m).padStart(2, '0')}m`;
    }
  }
  const ap = activeProject(chatId);
  const mode = ap ? `📂 当前项目:「${ap.name}」` : (getSession(chatId).mode === 'chat' ? '💬 当前:闲聊模式' : '🅾️ 当前:待选(发「菜单」选择)');
  return `${mode}\n布防:${cfg.enabled ? '● 已布防' : '○ 未布防'} · 引擎 ${st.phase || 'idle'}\n${reset} · 实探间隔 ${cfg.probeIntervalMinutes || 15}m`;
}
function listText() {
  const ps = discoverProjects();
  if (!ps.length) return '未发现任何项目。先在「Claude续跑」里添加/勾选项目。';
  return '项目列表(回复「进入 编号」进入,例:进入 2):\n' + ps.map((p, i) => `${i + 1}. ${p.name}`).join('\n');
}
function helpText(chatId) {
  const ap = activeProject(chatId);
  const cur = ap
    ? `📂 现在在项目「${ap.name}」——直接发消息就在这里续跑。`
    : '💬 现在是闲聊模式——直接说话就是和我聊天,不碰任何项目。';
  return [
    'Claude 服务器助手', cur, '',
    '· 项目 → 列出所有项目',
    '· 进入 <编号或名字> → 进入某项目开始操作(例:进入 2)',
    '· 退出 → 回到闲聊模式',
    '· <项目名> <指令> → 不切换,一次性在该项目执行',
    '· 状态 → 布防 / 额度 / 当前模式',
    '· 停止 <项目> → 取消正在跑的指令',
    '· 模型 / 模型 opus → 查看或切换模型(聊天+项目都用,与软件同步)',
    '· 授权 ou_xxx / 取消授权 / 授权列表 → 管理谁能操作项目',
    '· 忘记闲聊 → 清空闲聊记忆,从头开始',
    '',
    '注:项目执行会继续 VS Code 里同一个会话,面板不实时刷新,重开可见。',
  ].join('\n');
}

// ---- authorization: project/config operations are gated to bound Feishu users ----
// feishuAuthOpenIds empty = not locked (anyone). Once set, only listed open_ids may operate
// on projects / change config; everyone else can still chat. A password (feishuAuthPassword)
// lets a new account self-authorize via 「解锁 <密码>」.
function isAuthorized(openId) {
  const cfg = readConfig();
  const list = Array.isArray(cfg.feishuAuthOpenIds) ? cfg.feishuAuthOpenIds.filter(Boolean) : [];
  if (!list.length) return true;                 // not locked yet
  return !!openId && list.indexOf(openId) !== -1;
}
const notifiedUnauth = new Set();   // notify the owner at most once per unauthorized open_id
async function denyIfUnauthorized(openId, chatId) {
  if (isAuthorized(openId)) return false;
  await sendText(chatId, `🔒 无权限:操作项目 / 改配置仅限已授权账号(你可以闲聊)。\n你的 open_id:${openId || '未知'}`);
  logLine('拦截未授权操作: ' + openId);
  try {
    const owner = readConfig().feishuChatId;
    if (owner && owner !== chatId && openId && !notifiedUnauth.has(openId)) {
      notifiedUnauth.add(openId);
      await sendCard(owner, {
        config: { wide_screen_mode: true },
        header: { template: 'red', title: { tag: 'plain_text', content: '🔔 有人请求操作项目' } },
        elements: [
          { tag: 'div', text: { tag: 'lark_md', content: `open_id:\`${openId}\`\n授权他操作你的项目?` } },
          { tag: 'action', actions: [
            { tag: 'button', text: { tag: 'plain_text', content: '✅ 授权此人' }, type: 'primary', value: { do: 'authorize', id: openId } },
            { tag: 'button', text: { tag: 'plain_text', content: '忽略' }, type: 'default', value: { do: 'noop' } },
          ] },
        ],
      });
    }
  } catch (e) {}
  return true;
}

// ---- message handling ----
const seen = new Set(); // dedupe message_id (Feishu may redeliver)
function stripMentions(text, mentions) {
  let t = text || '';
  t = t.replace(/@_user_\d+/g, ' ');           // mention placeholders
  t = t.replace(/@[^\s]+/g, m => m);            // keep literal @ that aren't placeholders
  return t.replace(/\s+/g, ' ').trim();
}

async function onMessage(data) {
  try {
    const msg = data.message || {};
    const mid = msg.message_id;
    if (!mid || seen.has(mid)) return;
    seen.add(mid); if (seen.size > 500) seen.clear();
    if (msg.message_type !== 'text') return;
    const chatId = msg.chat_id;
    const senderOpen = data.sender && data.sender.sender_id && data.sender.sender_id.open_id;

    // single-bot mode: remember this chat so the checker's notifications go here too
    try {
      const c = readConfig();
      if (chatId && c.feishuChatId !== chatId) {
        c.feishuChatId = chatId;
        fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8');
        logLine('已记录通知 chatId: ' + chatId);
      }
    } catch (e) {}

    const cfg = readConfig();
    const allow = Array.isArray(cfg.feishuAllowOpenIds) ? cfg.feishuAllowOpenIds.filter(Boolean) : [];
    if (allow.length && senderOpen && allow.indexOf(senderOpen) === -1) {
      logLine('拒绝未授权发送者: ' + senderOpen); return;
    }

    let text = '';
    try { text = JSON.parse(msg.content || '{}').text || ''; } catch (e) {}
    text = stripMentions(text, msg.mentions);
    if (!text) return;
    logLine(`收到消息 chat=${chatId} sender=${senderOpen}: ${text}`);
    const low = text.toLowerCase();

    // password unlock: authorize this account for project/config ops
    const um = text.match(/^(解锁|认证|密码|auth|unlock)\s+(.+)$/i);
    if (um) {
      const c2 = readConfig();
      if (c2.feishuAuthPassword && um[2].trim() === String(c2.feishuAuthPassword)) {
        const list = Array.isArray(c2.feishuAuthOpenIds) ? c2.feishuAuthOpenIds.slice() : [];
        if (senderOpen && list.indexOf(senderOpen) === -1) list.push(senderOpen);
        c2.feishuAuthOpenIds = list;
        try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(c2, null, 4), 'utf8'); } catch (e) {}
        await sendText(chatId, '✅ 已授权本账号,现在可以操作项目了。');
      } else await sendText(chatId, c2.feishuAuthPassword ? '❌ 密码错误。' : '未设置解锁密码(在服务器 config.json 的 feishuAuthPassword 里设)。');
      return;
    }

    // authorize other people (authorized users only): 授权 ou_xxx / 取消授权 ou_xxx / 授权列表
    if (/^(授权列表|权限列表|谁有权限)$/.test(text)) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      const list = (readConfig().feishuAuthOpenIds || []).filter(Boolean);
      await sendText(chatId, '已授权账号:\n' + (list.length ? list.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 当前对所有人开放)'));
      return;
    }
    if (/^(授权|取消授权|解除授权)\b/.test(text)) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      const idm = text.match(/(ou_[A-Za-z0-9]+)/);
      if (!idm) { await sendText(chatId, '用法:「授权 ou_xxxx」添加,「取消授权 ou_xxxx」移除,「授权列表」查看。\n让对方给机器人发条消息,他会收到自己的 open_id,发给你即可。'); return; }
      const c2 = readConfig(); let list = Array.isArray(c2.feishuAuthOpenIds) ? c2.feishuAuthOpenIds.filter(Boolean) : [];
      const id = idm[1];
      if (/^(取消授权|解除授权)/.test(text)) { list = list.filter(x => x !== id); await sendText(chatId, '已移除授权:' + id); }
      else { if (list.indexOf(id) === -1) list.push(id); await sendText(chatId, '✅ 已授权:' + id); }
      c2.feishuAuthOpenIds = list; try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(c2, null, 4), 'utf8'); } catch (e) {}
      return;
    }

    // ---- global commands (work in any mode) ----
    if (['帮助', 'help', '?', '？'].indexOf(low) !== -1) { await sendText(chatId, helpText(chatId)); return; }
    if (['状态', 'status', 'zt'].indexOf(low) !== -1) { if (await denyIfUnauthorized(senderOpen, chatId)) return; await sendText(chatId, statusText(chatId)); return; }
    if (['项目', 'list', '项目列表', '列出项目', '所有项目', '菜单', 'menu', '选择', '操作'].indexOf(low) !== -1) { await sendCard(chatId, buildMenuCard(chatId)); return; }
    if (['退出', '返回', 'exit', 'quit', '退出项目', '主菜单'].indexOf(low) !== -1) {
      setSession(chatId, { mode: 'idle' });
      await sendCard(chatId, buildMenuCard(chatId));   // back to the main menu (idle)
      return;
    }
    if (['闲聊', '闲聊模式', 'chat'].indexOf(low) !== -1) {
      setSession(chatId, { mode: 'chat' });
      await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。发「退出」回主菜单。');
      return;
    }
    // chat model: show (模型) or set (模型 opus) — shared with the GUI chip
    if (['模型', 'model', '闲聊模型'].indexOf(low) !== -1) {
      const cur = String(readConfig().feishuChatModel || '').toLowerCase();
      const label = { sonnet: 'Sonnet', opus: 'Opus', haiku: 'Haiku' }[cur] || '默认';
      await sendText(chatId, `当前模型:${label}\n改用:发「模型 opus / sonnet / haiku / 默认」;软件里也能切换,两边同步。`);
      return;
    }
    const setm = text.match(/^(模型|闲聊模型|model)\s+(opus|sonnet|haiku|默认|default|清除|空|none)$/i);
    if (setm) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      const a = setm[2].toLowerCase();
      const val = (['opus', 'sonnet', 'haiku'].indexOf(a) !== -1) ? a : '';
      try { const c = readConfig(); c.feishuChatModel = val; fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8'); } catch (e) {}
      await sendText(chatId, `模型已设为:${val || '默认'}(下一句闲聊生效;软件已同步)。`);
      return;
    }
    // forget chat memory (drop the started flag + the claude session for the chat cwd)
    if (['忘记闲聊', '清空闲聊', '重置闲聊', '忘记记忆', 'forget', 'reset chat'].indexOf(low) !== -1) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      try { fs.rmSync(path.join(CHAT_DIR, '.started'), { force: true }); } catch (e) {}
      try {
        const proot = path.join(os.homedir(), '.claude', 'projects');
        for (const d of fs.readdirSync(proot)) { if (/ClaudeResume-feishu-chat$/i.test(d)) fs.rmSync(path.join(proot, d), { recursive: true, force: true }); }
      } catch (e) {}
      await sendText(chatId, '已清空闲聊记忆,下次闲聊从头开始。');
      return;
    }
    if (/^(停止|stop)\b/i.test(text)) {
      const rest = text.replace(/^(停止|stop)\s*/i, '').trim();
      const p = rest ? findProject(rest) : activeProject(chatId);
      if (p && running.has(p.path.toLowerCase())) { try { running.get(p.path.toLowerCase()).kill(); } catch (e) {} await sendText(chatId, `已请求停止:${p.name}`); }
      else await sendText(chatId, '没有正在运行的项目。');
      return;
    }

    // ---- explicit enter/switch: 进入 / 选择 / 切换 <编号或名字> ----
    const m = text.match(/^(进入|选择|选|切换|打开|进|use|open)\s+(.+)$/i);
    if (m) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      const p = findProject(m[2]);
      if (p) { setSession(chatId, { mode: 'project', project: p.path }); await sendText(chatId, `已进入 📂「${p.name}」。之后消息都会在这里续跑;发「退出」回主菜单,「进入 X」换项目。`); }
      else await sendText(chatId, `没找到项目「${m[2]}」。\n\n` + listText());
      return;
    }

    // greetings: show the button menu (Telegram-style) so it's easy to pick chat vs a project
    if (/^(你好|您好|hi|hello|hey|哈喽|在吗|在么|在不在|在|你好呀|嗨|yo|start|开始)$/i.test(text)) {
      await sendCard(chatId, buildMenuCard(chatId));
      return;
    }

    // bare project name/number -> enter it (works from any mode)
    const bare = projectIfBareName(text);
    if (bare) { if (await denyIfUnauthorized(senderOpen, chatId)) return; setSession(chatId, { mode: 'project', project: bare.path }); await sendText(chatId, `已进入 📂「${bare.name}」。直接发指令即可;发「退出」回主菜单。`); return; }
    // "<project> <command>" -> one-off run, doesn't change the current mode
    const oneoff = oneOffTarget(text);
    if (oneoff.project) {
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      if (running.has(oneoff.project.path.toLowerCase())) { await sendText(chatId, `「${oneoff.project.name}」正在执行中,请稍候。`); return; }
      await sendText(chatId, `📂 一次性在「${oneoff.project.name}」执行:${oneoff.prompt}\n(可能要 1-4 分钟,跑完自动回结果)`);
      const stopHb = startHeartbeat(chatId, oneoff.project.name);
      const r = await runClaude(oneoff.project.path, oneoff.project.name, oneoff.prompt, { useContinue: true, model: cfg.feishuChatModel });
      stopHb();
      await sendText(chatId, (r.ok ? `✅ 「${oneoff.project.name}」完成:\n\n` : `⚠️ 「${oneoff.project.name}」:\n\n`) + (r.text || '(无输出)'));
      logLine(`一次性完成 ${oneoff.project.name} ok=${r.ok}`);
      return;
    }

    // ---- mode dispatch ----
    const active = activeProject(chatId);
    if (active) {   // project mode: run in the active project
      if (await denyIfUnauthorized(senderOpen, chatId)) return;
      if (running.has(active.path.toLowerCase())) { await sendText(chatId, `「${active.name}」正在执行中,请稍候,或发「停止」取消。`); return; }
      await sendText(chatId, `📂 在「${active.name}」执行:${text}\n(可能要 1-4 分钟,跑完自动回结果)`);
      const stopHb = startHeartbeat(chatId, active.name);
      const r = await runClaude(active.path, active.name, text, { useContinue: true, model: cfg.feishuChatModel });
      stopHb();
      await sendText(chatId, (r.ok ? `✅ 「${active.name}」完成:\n\n` : `⚠️ 「${active.name}」:\n\n`) + (r.text || '(无输出)'));
      logLine(`完成 ${active.name} ok=${r.ok}`);
      return;
    }
    if (getSession(chatId).mode === 'chat') {   // chat mode: talk to Claude
      if (running.has(CHAT_DIR.toLowerCase())) { await sendText(chatId, '上一句还在想,请稍候…'); return; }
      await sendText(chatId, '🤔 正在思考…');
      logLine(`闲聊 思考中: ${text}`);
      const stopHb = startHeartbeat(chatId, '闲聊');
      const r = await runClaude(CHAT_DIR, '闲聊', text, { useContinue: chatStarted(), skipPermissions: false, model: cfg.feishuChatModel });
      stopHb();
      if (r.ok) markChatStarted();
      await sendText(chatId, (r.text || '(无输出)') + '\n\n———\n💬 闲聊模式 · 发「菜单」切换');
      logLine(`闲聊 完成 ok=${r.ok}`);
      return;
    }
    // idle mode: don't run anything — show the menu so the user picks a mode first
    await sendCard(chatId, buildMenuCard(chatId));
  } catch (e) { logLine('处理消息异常: ' + (e && e.stack || e)); }
}

// ---- interactive card button clicks (card.action.trigger) ----
const cardSeen = new Map(); // dedup rapid Feishu re-deliveries of the same click
async function onCardAction(ev) {
  try {
    const chatId = (ev.context && ev.context.open_chat_id) || ev.open_chat_id;
    const val = (ev.action && ev.action.value) || {};
    const senderOpen = ev.operator && ev.operator.open_id;
    const messageId = (ev.context && ev.context.open_message_id) || ev.open_message_id;
    if (!chatId || !val || !val.do) return;
    const key = chatId + ':' + JSON.stringify(val) + ':' + (senderOpen || '');
    const now = Date.now();
    if (cardSeen.get(key) && now - cardSeen.get(key) < 4000) return;
    cardSeen.set(key, now); if (cardSeen.size > 300) cardSeen.clear();

    const cfg = readConfig();
    const allow = Array.isArray(cfg.feishuAllowOpenIds) ? cfg.feishuAllowOpenIds.filter(Boolean) : [];
    if (allow.length && senderOpen && allow.indexOf(senderOpen) === -1) { logLine('拒绝未授权点击: ' + senderOpen); return; }
    try { if (chatId && cfg.feishuChatId !== chatId) { cfg.feishuChatId = chatId; fs.writeFileSync(CONFIG_PATH, JSON.stringify(cfg, null, 4), 'utf8'); } } catch (e) {}
    logLine(`卡片点击 chat=${chatId}: ${JSON.stringify(val)}`);

    // IMPORTANT: a card callback must respond within a few seconds or Feishu shows
    // "目标回调服务超时未响应". So do ONLY fast local work here (sync file writes) and fire every
    // API call (send/patch) WITHOUT await, then return immediately.
    const gated = ['status', 'perm', 'model', 'enter', 'authorize', 'revoke'].indexOf(val.do) !== -1;
    if (gated && !isAuthorized(senderOpen)) { denyIfUnauthorized(senderOpen, chatId); return; }

    if (val.do === 'chat') { setSession(chatId, { mode: 'chat' }); refreshCard(chatId, messageId); return; }
    if (val.do === 'status') { sendText(chatId, statusText(chatId)); return; }
    if (val.do === 'perm') {
      const list = (readConfig().feishuAuthOpenIds || []).filter(Boolean);
      sendText(chatId, '🔑 已授权账号:\n' + (list.length ? list.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 当前对所有人开放)') + '\n\n加人:让对方给我发条消息 → 你会收到「授权此人」卡片,一键点即可(也可发「授权 ou_xxx」/「取消授权 ou_xxx」)。');
      return;
    }
    if (val.do === 'noop') { return; }
    if (val.do === 'model') {
      const mm = String(val.m || '').toLowerCase();
      const v = (['opus', 'sonnet', 'haiku'].indexOf(mm) !== -1) ? mm : '';
      try { const c = readConfig(); c.feishuChatModel = v; fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8'); } catch (e) {}
      refreshCard(chatId, messageId);      // card update is the feedback (no extra text)
      return;
    }
    if (val.do === 'enter') {
      const p = discoverProjects().find(x => x.path.toLowerCase() === String(val.p).toLowerCase()) || (val.p ? { name: path.basename(val.p), path: val.p } : null);
      if (p) { setSession(chatId, { mode: 'project', project: p.path }); refreshCard(chatId, messageId); }
      else sendText(chatId, '项目未找到(可能已变化)。发「菜单」重新选。');
      return;
    }
    if (val.do === 'authorize' || val.do === 'revoke') {   // one-tap from the owner-notification card
      const id = String(val.id || '');
      if (/^ou_[A-Za-z0-9]+$/.test(id)) {
        const c = readConfig(); let list = Array.isArray(c.feishuAuthOpenIds) ? c.feishuAuthOpenIds.filter(Boolean) : [];
        if (val.do === 'revoke') list = list.filter(x => x !== id); else if (list.indexOf(id) === -1) list.push(id);
        c.feishuAuthOpenIds = list; try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8'); } catch (e) {}
        sendText(chatId, (val.do === 'revoke' ? '已移除授权:' : '✅ 已授权:') + id);
      }
      return;
    }
  } catch (e) { logLine('卡片动作异常: ' + (e && (e.stack || e))); }
}

// ---- persistent bottom menu (机器人自定义菜单) clicks: application.bot.menu_v6 ----
async function onBotMenu(ev) {
  try {
    const key = ev.event_key || (ev.event && ev.event.event_key) || '';
    const senderOpen = (ev.operator && ev.operator.operator_id && ev.operator.operator_id.open_id)
      || (ev.operator && ev.operator.open_id);
    const cfg = readConfig();
    const chatId = cfg.feishuChatId;   // bot menu lives in the p2p chat; reply to the known chat
    if (!chatId) { logLine('菜单点击但无 chatId(先随便发一句让我记录会话)'); return; }
    const allow = Array.isArray(cfg.feishuAllowOpenIds) ? cfg.feishuAllowOpenIds.filter(Boolean) : [];
    if (allow.length && senderOpen && allow.indexOf(senderOpen) === -1) return;
    logLine(`底部菜单点击: ${key}`);
    if (key === 'chat') { setSession(chatId, { mode: 'chat' }); await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。'); return; }
    if (key === 'status') { if (await denyIfUnauthorized(senderOpen, chatId)) return; await sendText(chatId, statusText(chatId)); return; }
    if (key === 'idle' || key === 'exit') { setSession(chatId, { mode: 'idle' }); }
    // default (menu / unknown) -> show the main menu card
    await sendCard(chatId, buildMenuCard(chatId));
  } catch (e) { logLine('底部菜单事件异常: ' + (e && (e.stack || e))); }
}

// ---- boot ----
const wsClient = new lark.WSClient({ appId: APP_ID, appSecret: APP_SECRET });
// register both v1 and v2 of the receive-message event so whichever the console offers works
const handlers = { 'im.message.receive_v1': async (data) => { await onMessage(data); } };
try { handlers['im.message.receive_v2'] = async (data) => { await onMessage(data); }; } catch (e) {}
handlers['card.action.trigger'] = async (ev) => { await onCardAction(ev); };   // card button clicks
handlers['application.bot.menu_v6'] = async (ev) => { await onBotMenu(ev); };   // bottom-menu clicks
// no-op handlers for other events the console may have subscribed (read/reaction/recall/mute),
// so the SDK doesn't log "no handle" warnings for events we don't act on
const _noop = async () => {};
for (const k of ['im.message.message_read_v1', 'im.message.reaction.created_v1', 'im.message.reaction.deleted_v1', 'im.message.recalled_v1', 'im.message.bot_muted_v1']) { handlers[k] = _noop; }
let eventDispatcher;
try {
  eventDispatcher = new lark.EventDispatcher({}).register(handlers);
} catch (e) {
  logLine('注册 v2 事件失败,回退仅 v1: ' + (e && e.message));
  eventDispatcher = new lark.EventDispatcher({}).register({ 'im.message.receive_v1': async (data) => { await onMessage(data); } });
}
logLine('feishu-agent 启动,连接飞书长连接…  claude=' + CLAUDE_CMD);
wsClient.start({ eventDispatcher });
// keep the process alive
process.on('uncaughtException', e => logLine('uncaughtException: ' + (e && e.stack || e)));
process.on('unhandledRejection', e => logLine('unhandledRejection: ' + (e && (e.stack || e))));
