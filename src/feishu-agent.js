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
const crypto = require('crypto');
const { spawn } = require('child_process');

let lark;
try { lark = require('@larksuiteoapi/node-sdk'); }
catch (e) {
  if (process.env.FEISHU_TEST) { lark = {}; }   // offline tests use a mock client, not the SDK
  else { console.error('缺少依赖 @larksuiteoapi/node-sdk,请在本目录运行: npm install'); process.exit(1); }
}

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
if (!process.env.FEISHU_TEST) {   // tests don't take the single-instance lock or touch the live pidfile
  if (anotherInstanceAlive()) { console.error('另一个 feishu-agent 已在运行,退出。'); process.exit(0); }
  try { fs.mkdirSync(APP_DIR, { recursive: true }); fs.writeFileSync(PID_PATH, String(process.pid)); } catch (e) {}
  process.on('exit', () => { try { if (parseInt(fs.readFileSync(PID_PATH, 'utf8'), 10) === process.pid) fs.unlinkSync(PID_PATH); } catch (e) {} });
}

const cfg0 = readConfig();
const APP_ID = cfg0.feishuAppId || '';
const APP_SECRET = cfg0.feishuAppSecret || '';
const TEST_MODE = !!process.env.FEISHU_TEST;   // offline unit tests: mock client, no WS, export handlers
if (!TEST_MODE && (!APP_ID || !APP_SECRET)) { logLine('config.json 缺少 feishuAppId / feishuAppSecret,退出。'); process.exit(1); }

// a recording mock client so the card-flow logic can be tested without touching the network.
// tests read client.__calls (each {op:'create'|'patch', type, title, id}) and client.__reset().
function makeMockClient() {
  let seq = 0; const calls = [];
  const titleOf = c => { try { const j = JSON.parse(c); return (j.header && j.header.title && j.header.title.content) || null; } catch (e) { return null; } };
  // text of a message: plain-text content, OR (for interactive cards) all lark_md/plain_text bodies
  // concatenated — so tests can scan a result card's body just like a text message.
  const textOf = c => {
    try {
      const j = JSON.parse(c);
      if (j.text) return j.text;
      if (j.elements) {
        const out = [];
        const walk = (el) => {
          if (!el) return;
          if (Array.isArray(el)) return el.forEach(walk);
          if (el.text && typeof el.text.content === 'string') out.push(el.text.content);
          if (typeof el.content === 'string') out.push(el.content);
          if (el.elements) walk(el.elements);
          if (el.actions) walk(el.actions);
        };
        walk(j.elements);
        return out.join('\n') || null;
      }
      return null;
    } catch (e) { return null; }
  };
  return {
    __calls: calls,
    __reset() { calls.length = 0; },
    im: { message: {
      create: async o => { const id = 'msg_' + (++seq); calls.push({ op: 'create', type: o.data.msg_type, to: o.data.receive_id, toType: o.params && o.params.receive_id_type, title: titleOf(o.data.content), text: textOf(o.data.content), id }); return { data: { message_id: id } }; },
      patch: async o => { if (String(o.path.message_id).indexOf('gone') !== -1) throw new Error('mock: message not found (deleted)'); calls.push({ op: 'patch', id: o.path.message_id, title: titleOf(o.data.content) }); return {}; },
    } },
  };
}
const client = TEST_MODE ? makeMockClient() : new lark.Client({ appId: APP_ID, appSecret: APP_SECRET });

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
// read only the first N bytes (session jsonl can be many MB; the cwd line sits at the very top)
function readHead(file, bytes) {
  let fd;
  try { fd = fs.openSync(file, 'r'); const buf = Buffer.alloc(bytes); const n = fs.readSync(fd, buf, 0, bytes, 0); return buf.toString('utf8', 0, n); }
  catch (e) { return ''; }
  finally { try { if (fd !== undefined) fs.closeSync(fd); } catch (e) {} }
}
let _discCache = null, _discAt = 0;   // memo: one card click may call this several times
function discoverProjects() {
  if (_discCache && (Date.now() - _discAt) < 3000) return _discCache;
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
      const head = readHead(file, 65536).split(/\r?\n/).slice(0, 60);
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
  _discCache = list; _discAt = Date.now();
  return list;
}

// ---- per-chat active project (persisted) + a chat scratch session ----
const SESSIONS_PATH = path.join(APP_DIR, 'feishu-sessions.json');
const CHAT_DIR = path.join(APP_DIR, 'feishu-chat');
// open_id -> that user's own p2p chat with the bot. Bot-menu events carry only the operator's
// open_id, so replies MUST be routed via this map (previously they all went to the owner's chat).
const USERCHATS_PATH = path.join(APP_DIR, 'feishu-userchats.json');
let userChats = {};
try { userChats = JSON.parse(fs.readFileSync(USERCHATS_PATH, 'utf8').replace(/^﻿/, '')) || {}; } catch (e) {}
function rememberUserChat(openId, chatId) {
  if (!openId || !chatId || userChats[openId] === chatId) return;
  userChats[openId] = chatId;
  try { fs.writeFileSync(USERCHATS_PATH, JSON.stringify(userChats, null, 2), 'utf8'); } catch (e) {}
  // a brand-new user may have tapped the bottom menu BEFORE ever messaging: their session/card
  // then lives under the 'od:<open_id>' pseudo-target. Migrate it to the real chat so the mode
  // they picked from the menu survives their first typed message (instead of splitting state).
  try {
    const od = 'od:' + openId;
    const s = readSessions();
    if (s[od]) { if (!s[chatId]) s[chatId] = s[od]; delete s[od]; writeSessions(s); }
    if (lastCard.has(od)) { lastCard.set(chatId, lastCard.get(od)); lastCard.delete(od); }
  } catch (e) {}
}
// reply target for a user when no chat_id is on the event: their known chat, else send by open_id
// (an 'od:ou_xxx' target — sendText/sendCard translate it to receive_id_type='open_id', which
// delivers into that user's p2p chat with the bot).
function userTarget(openId) { return userChats[openId] || (openId ? 'od:' + openId : null); }
function readSessions() { try { return JSON.parse(fs.readFileSync(SESSIONS_PATH, 'utf8').replace(/^﻿/, '')); } catch (e) { return {}; } }
function writeSessions(o) { try { fs.writeFileSync(SESSIONS_PATH, JSON.stringify(o, null, 2), 'utf8'); } catch (e) {} }
// three modes: 'idle' (default — do nothing until the user picks via the card), 'chat', 'project'
function getSession(chatId) {
  const s = readSessions(); const v = s[chatId];
  if (!v) return { mode: 'idle' };
  if (typeof v === 'string') return v ? { mode: 'project', project: v } : { mode: 'idle' };   // legacy
  // sub (project sub-mode): 'query' | 'modify' | undefined (not chosen yet -> ask first)
  // work: the claude session id that ✏️修改 continues (picked from the session list, or a fresh uuid
  //       for 新开会话); undefined -> the session list is shown before anything runs.
  return { mode: v.mode || (v.project ? 'project' : 'idle'), project: v.project, sub: v.sub, work: v.work };
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

// each project has ONE dedicated read-only "query session" (fixed session id from the path),
// shared by everyone: --session-id creates it, --resume continues it. Separate from work sessions.
const QUERY_DIR = path.join(APP_DIR, 'feishu-query');        // per-project .started flags
const QUERY_CWD_BASE = path.join(APP_DIR, 'feishu-query-cwd'); // queries run HERE, never in project.path
function querySession(projectPath, openId) {
  // key by (project, USER) — each person's read-only queries are PRIVATE: a coworker resuming this
  // project's query never sees your conversation (separate session + isolated cwd), and two people
  // querying the same project run CONCURRENTLY instead of blocking each other. openId falls back to
  // 'anon' only for degenerate callers (every real query path passes the sender).
  const h = crypto.createHash('sha1').update(String(projectPath).toLowerCase() + '|' + String(openId || 'anon')).digest('hex');
  const id = `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`;
  const flag = path.join(QUERY_DIR, h + '.started');
  // isolated cwd so the query transcript does NOT land in project.path's session folder — otherwise
  // a later modify `--continue` would resume the query session instead of the VS Code work session.
  const cwd = path.join(QUERY_CWD_BASE, h);
  return { id, flag, cwd, started: (() => { try { return fs.existsSync(flag); } catch (e) { return false; } })() };
}
// did claude actually persist this session's jsonl? (so we only flip to --resume once it truly exists)
function querySessionExists(id) {
  try {
    const base = path.join(os.homedir(), '.claude', 'projects');
    return fs.readdirSync(base).some(d => { try { return fs.existsSync(path.join(base, d, id + '.jsonl')); } catch (e) { return false; } });
  } catch (e) { return false; }
}
// flag content = {id, path, name} so the GUI's "清空查询记忆" can locate & delete the session jsonl
function markQueryStarted(flag, meta) { try { fs.mkdirSync(QUERY_DIR, { recursive: true }); fs.writeFileSync(flag, JSON.stringify(meta || {}), 'utf8'); } catch (e) {} }
// wipe one project's shared query session: remove the flag AND the claude session jsonl(s) with
// that id (must delete the jsonl — --session-id on an existing id errors "already in use").
function clearQuerySession(projectPath, openId) {
  const qs = querySession(projectPath, openId);
  let deleted = 0;
  try { fs.unlinkSync(qs.flag); } catch (e) {}
  try {
    const base = path.join(os.homedir(), '.claude', 'projects');
    for (const d of fs.readdirSync(base)) {
      const f = path.join(base, d, qs.id + '.jsonl');
      try { if (fs.existsSync(f)) { fs.unlinkSync(f); deleted++; } } catch (e) {}
    }
  } catch (e) {}
  return deleted;
}

// prefix that makes a full user's message a READ-ONLY query (viewers are always read-only).
// requires a separator (space or colon) after the keyword so "只读xxx"-style prose in a modify
// conversation is not silently rerouted into the shared query session.
const QUERY_RE = /^\s*(查询|只读查询|只读|query)(?:\s+|[:：])\s*([\s\S]+)$/i;

// ---- a project's claude WORK sessions (what ✏️修改 continues) ----
// Each *.jsonl in the project's ~/.claude/projects/<encoded-cwd>/ folder is one conversation (the
// ones your VS Code sessions create). Read-only queries live in an isolated cwd, so they never show
// up here. Folder names are lossy, so find the folder by reading each session's real cwd.
const _sessDirCache = new Map();   // projectPath(lower) -> folder; the mapping never changes
function projectSessionDir(projectPath) {
  const ck = String(projectPath).toLowerCase();
  if (_sessDirCache.has(ck)) return _sessDirCache.get(ck);
  const found = findProjectSessionDir(projectPath);
  if (found) _sessDirCache.set(ck, found);
  return found;
}
function findProjectSessionDir(projectPath) {
  try {
    const base = path.join(os.homedir(), '.claude', 'projects');
    const want = String(projectPath).toLowerCase();
    for (const d of fs.readdirSync(base)) {
      const full = path.join(base, d);
      let files; try { files = fs.readdirSync(full).filter(f => f.endsWith('.jsonl')); } catch (e) { continue; }
      if (!files.length) continue;
      const head = readHead(path.join(full, files[0]), 65536).split(/\r?\n/).slice(0, 60);
      for (const ln of head) {
        if (ln.indexOf('"cwd"') === -1) continue;
        try { const j = JSON.parse(ln); if (j.cwd && String(j.cwd).toLowerCase() === want) return full; } catch (e) {}
        break;
      }
    }
  } catch (e) {}
  return null;
}
// [{id, title, mtime}] newest first. Title prefers claude's own `ai-title`, else the first user line.
function listProjectSessions(projectPath, limit) {
  const dir = projectSessionDir(projectPath);
  if (!dir) return [];
  let out = [];
  try {
    for (const f of fs.readdirSync(dir)) {
      if (!f.endsWith('.jsonl')) continue;
      const full = path.join(dir, f);
      let mtime = 0; try { mtime = fs.statSync(full).mtimeMs; } catch (e) { continue; }
      out.push({ id: f.replace(/\.jsonl$/i, ''), title: '', mtime, file: full });
    }
  } catch (e) { return []; }
  out.sort((a, b) => b.mtime - a.mtime);
  out = out.slice(0, limit || 5);
  for (const s of out) {
    let aiTitle = '', firstUser = '';
    // the ai-title line sits near the top (~line 8), so a bounded head read is enough even for a 27MB
    // transcript — never read whole session files here, this runs while rendering a card.
    for (const ln of readHead(s.file, 65536).split(/\r?\n/)) {
      if (!ln) continue;
      if (!aiTitle && ln.indexOf('"ai-title"') !== -1) { try { const j = JSON.parse(ln); if (j.aiTitle) aiTitle = String(j.aiTitle); } catch (e) {} }
      if (!firstUser && ln.indexOf('"type":"user"') !== -1) { try { firstUser = msgText(JSON.parse(ln)); } catch (e) {} }
      if (aiTitle) break;
    }
    s.title = (aiTitle || firstUser || '(无标题)').replace(/\s+/g, ' ').trim();
  }
  return out;
}
// plain text of a transcript line's message content ('' if none)
function msgText(j) {
  const c = j && j.message && j.message.content;
  if (typeof c === 'string') return c;
  if (Array.isArray(c)) return c.filter(p => p && p.type === 'text' && typeof p.text === 'string').map(p => p.text).join(' ');
  return '';
}
// read only the LAST n bytes (transcripts reach tens of MB; we only need the tail)
function readTail(file, bytes) {
  let fd;
  try {
    const size = fs.statSync(file).size;
    const start = Math.max(0, size - bytes);
    const len = size - start;
    if (len <= 0) return '';
    fd = fs.openSync(file, 'r');
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, start);
    return buf.toString('utf8');
  } catch (e) { return ''; }
  finally { try { if (fd !== undefined) fs.closeSync(fd); } catch (e) {} }
}
// last N user→assistant turns of a session, as a short readable digest
function sessionPreview(file, turns) {
  const out = [];
  try {
    // tail-only: the first line of the slice may be cut mid-JSON — it just fails to parse and is skipped
    const lines = readTail(file, 262144).split(/\r?\n/);
    for (const ln of lines) {
      if (!ln || (ln.indexOf('"type":"user"') === -1 && ln.indexOf('"type":"assistant"') === -1)) continue;
      try {
        const j = JSON.parse(ln); const t = msgText(j).replace(/\s+/g, ' ').trim();
        if (!t) continue;
        const who = j.type === 'user' ? 'you' : 'ai';
        // collapse consecutive same-role lines (assistant often streams several text blocks)
        if (out.length && out[out.length - 1].who === who) out[out.length - 1].t = t;
        else out.push({ who, t });
      } catch (e) {}
    }
  } catch (e) {}
  const want = (turns || 2) * 2;
  const tail = out.slice(-want);
  const cut = s => (s.length > 100 ? s.slice(0, 100) + '…' : s);
  const lines2 = [];
  for (let i = 0; i < tail.length; i++) lines2.push((tail[i].who === 'you' ? '· 你:' : '  我:') + cut(tail[i].t));
  return lines2.join('\n');
}
const shortTime = ms => { const d = new Date(ms); const p = n => String(n).padStart(2, '0'); return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`; };

// run one read-only query in the project's DEDICATED shared query session (any user/time -> same convo).
// Runs in an ISOLATED cwd (qs.cwd) + --add-dir project.path so the transcript stays out of the
// project's --continue pool; the prompt names the project so claude knows where to look.
// current short git HEAD of a project (for AI_GUIDE freshness), '' if not a git repo / no git.
// async — spawnSync would freeze the whole event loop (incl. the WS ACK) for up to its timeout.
function projectGitHash(projectPath) {
  return new Promise((resolve) => {
    try {
      require('child_process').execFile('git', ['-C', projectPath, 'rev-parse', '--short', 'HEAD'],
        { timeout: 3000, windowsHide: true }, (err, stdout) => resolve(err ? '' : String(stdout).trim()));
    } catch (e) { resolve(''); }
  });
}
async function runProjectQuery(chatId, project, prompt, openId) {
  const qs = querySession(project.path, openId);
  const cfg = readConfig();
  // Preferred: a project's AI_GUIDE.md (generate it with the project-tour skill) — a dense, self-
  // contained tour (架构/模块/测试流程/数据格式/FAQ/术语/文档索引). Inject it ONCE when the query
  // session is first created; later --resume calls reuse it from the conversation's prompt cache
  // (cheap). Falls back to "explore docs/" framing for projects that don't have a guide yet.
  let guide = '', staleNote = '';
  if (!qs.started) {
    try {
      guide = fs.readFileSync(path.join(project.path, 'AI_GUIDE.md'), 'utf8');
      // freshness: the guide records the git hash it was built at; warn if the project moved on.
      const rec = (guide.match(/project-tour[^\n]*git\s+([0-9a-f]{6,40})/i) || [])[1];
      const cur = rec ? await projectGitHash(project.path) : '';
      if (rec && cur && rec !== cur) staleNote = `⚠️ 提示:本导览生成于较早的提交(git ${rec}),项目现已到 ${cur}——架构/模块/数据格式/术语等大框架通常仍准,但**具体实现细节请以实际代码为准**,不确定处务必读相关源码后再答。\n\n`;
    } catch (e) {}
  }
  const framed = (guide ? `[项目导览 AI_GUIDE.md,优先据此作答;不足时再按其文末「文档索引」读 1~2 篇文档:]\n${staleNote}${guide}\n\n———\n` : '') +
    `[对项目「${project.name}」(目录:${project.path})的只读提问。请尽量省 token:` +
    (guide
      ? `先看上面的项目导览作答;导览不足时,再按其文档索引读最相关的 1~2 篇文档及关键代码;`
      : `先看该目录下的文档索引(AI_GUIDE.md / docs/ / README / 目录树),定位并只读与问题最相关的 1~2 篇文档及它们引用的关键代码;`) +
    `在本轮内直接简要作答。不要通读整个项目,不要启动子任务/子代理,也不要修改任何文件。]\n\n${prompt}`;
  // SECURITY: --permission-mode plan blocks WRITES but not READS, and reads are NOT confined to the
  // workspace — a query can Read ../../config.json (feishuAppSecret / feishuAuthPassword) and a
  // coworker could then 解锁 <password> to self-promote to owner. (Verified: plan-mode Read happily
  // returns an ancestor file's contents with benign phrasing.) So only an EXPLICITLY-listed owner
  // keeps file tools for queries (the secrets are theirs anyway); everyone else — coworkers, and
  // everyone while the bot is unlocked — gets NO file/exec tools and answers from the injected
  // AI_GUIDE.md only. Mirrors the chat-path defense (test/chat-security.js, test/query-security.js).
  const fullList = (Array.isArray(cfg.feishuAuthOpenIds) ? cfg.feishuAuthOpenIds : []).filter(Boolean);
  const trustedOwner = openId && fullList.indexOf(openId) !== -1;   // listed owner — NOT bootstrap-full
  const disallowed = trustedOwner
    ? ['Task']   // owner: full read tools, just no big sub-agent explore
    : ['Task', 'Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit'];   // others: guide-only, no file access
  const stopHb = startHeartbeat(chatId, project.name + ' 查询');
  const r = await runClaude(qs.cwd, project.name + ' 查询', framed, {
    sessionId: qs.id, sessionExists: qs.started, addDir: project.path, readOnly: true,
    disallowedTools: disallowed, model: runModelFor(openId)   // per-user model; non-owner never on Fable 5
  });
  stopHb();
  // only flip to --resume once claude actually persisted the jsonl — else a failed first query
  // (claude missing / bad flag / limited before write) would poison every later query into --resume.
  if (querySessionExists(qs.id)) markQueryStarted(qs.flag, { id: qs.id, path: project.path, name: project.name });
  return r;
}

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
const inflight = new Set(); // cwd(lower) reserved synchronously to close the check->spawn race
// kill the WHOLE tree: we spawn cmd.exe -> claude.cmd -> node; child.kill() only terminates the
// cmd wrapper on Windows and the grandchild keeps running (verified — a "stopped" test run
// finished anyway and committed). taskkill /T /F takes the tree down.
function killTree(child) {
  try {
    if (process.platform === 'win32' && child && child.pid) {
      spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
    } else if (child) child.kill();
  } catch (e) { try { child.kill(); } catch (e2) {} }
}
// Run long claude work in the BACKGROUND so the event handler returns immediately. The WS layer
// awaits the handler before ACKing the event back to Feishu (sdk: `yield eventDispatcher.invoke`),
// so an awaited 1-4 min claude run means no ACK for minutes -> Feishu stops delivering / re-delivers
// and every tap in that chat looks dead (the "daytime freeze"). key is reserved in `inflight` by the
// CALLER (synchronously, before any await) and released here when the work finishes.
function bg(label, key, work) {
  (async () => {
    try { await work(); }
    catch (e) { logLine(`后台任务异常 [${label}]: ` + (e && (e.stack || e))); }
    finally { if (key) inflight.delete(key); }
  })();
}
function runClaude(cwd, label, prompt, opts) {
  opts = opts || {};
  // offline-test stub: tests that only assert DISPATCH behavior must never spawn a real claude —
  // a "harmless" run once resumed old context under skip-permissions and pushed a git commit.
  if (TEST_MODE && process.env.FEISHU_TEST_NO_CLAUDE) {
    return Promise.resolve({ ok: true, limited: false, text: '(测试桩:未真正执行)', ms: 1 });
  }
  return new Promise((resolve) => {
    const cfg = readConfig();
    try { fs.mkdirSync(cwd, { recursive: true }); } catch (e) {}
    const args = ['/c', CLAUDE_CMD];
    if (opts.sessionId) {
      // pinned session (dedicated per-project query session): create it the first time, resume after
      args.push(opts.sessionExists ? '--resume' : '--session-id', opts.sessionId);
    } else if (opts.useContinue !== false) {
      args.push('--continue');
    }
    if (opts.addDir) args.push('--add-dir', opts.addDir);   // grant read access when cwd != the project
    // block heavy tools for read-only queries (Task spins up a full-project sub-explore = big tokens)
    if (Array.isArray(opts.disallowedTools) && opts.disallowedTools.length) args.push('--disallowedTools', ...opts.disallowedTools);
    // NOTE: prompt is fed via STDIN (below), NOT as a -p argument. A -p arg with newlines gets
    // truncated at the first newline by Windows cmd, so claude only saw the framing and missed the
    // actual question. stdin carries the whole thing verbatim (and also stops the stdin-wait hang).
    args.push('-p', '--output-format', 'stream-json', '--verbose');
    const model = opts.model || cfg.resumeModel;   // opts.model lets chat use its own model
    if (model) { args.push('--model', model); }
    if (opts.readOnly) {
      args.push('--permission-mode', 'plan');       // viewer: analyze/answer, never modify
    } else {
      const skip = (opts.skipPermissions !== undefined) ? opts.skipPermissions : cfg.skipPermissions;
      if (skip) { args.push('--dangerously-skip-permissions'); }
    }
    const timeoutMs = Math.max(1, (parseInt(cfg.perProjectTimeoutMinutes, 10) || 30)) * 60000;
    const key = cwd.toLowerCase();

    let child;
    try { child = spawn(process.env.ComSpec || 'cmd.exe', args, { cwd, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); }
    catch (e) { resolve({ ok: false, limited: false, text: '启动 claude 失败: ' + e.message }); return; }
    running.set(key, child);
    // feed the prompt via stdin (whole thing, newlines preserved), then close stdin so claude runs.
    try { child.stdin.on('error', () => {}); child.stdin.write(prompt, 'utf8'); child.stdin.end(); } catch (e) {}

    let buf = '', resultText = null, isError = null, limited = false, killedForTimeout = false;
    let lastAssistant = null, errBuf = '', usage = null, cost = null;   // + token usage / cost from result
    const t0 = Date.now();
    const to = setTimeout(() => { killedForTimeout = true; killTree(child); }, timeoutMs);
    function scanLine(ln) {
      if (!ln) return;
      if (/"status"\s*:\s*"(blocked|rejected|limited|exceeded)"/.test(ln) ||
          /usage limit|rate limit|limit reached|weekly limit/i.test(ln)) limited = true;
      if (ln.indexOf('"type":"result"') !== -1 || /"type"\s*:\s*"result"/.test(ln)) {
        try { const j = JSON.parse(ln); if (j.type === 'result') { if (typeof j.result === 'string') resultText = j.result; if (typeof j.is_error === 'boolean') isError = j.is_error; if (j.usage) usage = j.usage; if (typeof j.total_cost_usd === 'number') cost = j.total_cost_usd; } } catch (e) {}
      } else if (ln.indexOf('"type":"assistant"') !== -1) {
        // remember the last non-empty assistant text — used as a fallback if no result event arrives
        try { const j = JSON.parse(ln); const c = j && j.message && j.message.content; if (Array.isArray(c)) { for (const p of c) { if (p && p.type === 'text' && typeof p.text === 'string' && p.text.trim()) lastAssistant = p.text; } } } catch (e) {}
      }
    }
    child.stdout.on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\n')) >= 0) { scanLine(buf.slice(0, i)); buf = buf.slice(i + 1); } });
    child.stderr.on('data', d => { errBuf += d.toString('utf8'); if (errBuf.length > 4000) errBuf = errBuf.slice(-4000); });
    child.on('close', (code) => {
      clearTimeout(to); running.delete(key);
      if (buf) scanLine(buf);
      const ms = Date.now() - t0;
      const ot = usage && usage.output_tokens;
      if (killedForTimeout) { resolve({ ok: false, limited, text: `执行超时(> ${timeoutMs / 60000} 分钟),已终止。`, ms }); return; }
      if (resultText !== null && isError !== true) {
        logLine(`完成 [${label}] ${Math.round(ms / 1000)}s${ot ? ' 输出' + ot + ' tokens' : ''}${typeof cost === 'number' ? ' ~$' + cost.toFixed(3) : ''}`);
        resolve({ ok: true, limited, text: resultText, usage, cost, ms }); return;
      }
      if (limited) { resolve({ ok: false, limited: true, text: '又被限流了。可在「Claude续跑」里布防,额度恢复后自动续跑。', ms }); return; }
      // no clean result -> log why (claude's stderr was previously swallowed, leaving failures blind)
      const errTail = errBuf.trim().slice(-600).replace(/\s+/g, ' ');
      logLine(`runClaude 未成功 [${label}] exit=${code} isError=${isError} assistant文本=${lastAssistant ? '有' : '无'} ${Math.round(ms / 1000)}s${errTail ? ' · stderr尾: ' + errTail : ''}`);
      // claude answered but the final result event was missing/dropped -> return what it said
      if (isError !== true && lastAssistant && lastAssistant.trim()) { resolve({ ok: true, limited, text: lastAssistant, usage, cost, ms }); return; }
      resolve({ ok: false, limited, text: resultText || lastAssistant || '执行结束但未拿到成功结果(可能出错)。', ms });
    });
  });
}

// ---- Feishu send helpers ----
// retry a Feishu API call once on transient network errors (the logs show frequent TLS-handshake /
// socket-disconnect blips that make a click look like it did nothing).
async function apiRetry(fn) {
  try { return await fn(); }
  catch (e) {
    if (/socket disconnected|handshake|TLS|ETIMEDOUT|ECONNRESET|ENOTFOUND|EAI_AGAIN|network/i.test(String(e && e.message))) {
      await new Promise(r => setTimeout(r, 250));   // transient blip — retry fast (was 700ms, felt sluggish)
      return await fn();
    }
    throw e;
  }
}
// a target is a chat_id, or 'od:ou_xxx' to deliver into a user's p2p chat by open_id
function sendParams(target) {
  return String(target).startsWith('od:')
    ? { type: 'open_id', id: String(target).slice(3) }
    : { type: 'chat_id', id: target };
}
async function sendText(chatId, text) {
  // Feishu text messages get unwieldy past a few KB; chunk to <=3500 chars, cap 6 parts.
  const MAX = 3500, PARTS = 6;
  let parts = [];
  let s = String(text);
  while (s.length && parts.length < PARTS) { parts.push(s.slice(0, MAX)); s = s.slice(MAX); }
  if (s.length) parts[parts.length - 1] += '\n…(内容过长已截断,完整结果见 VS Code 该项目会话)';
  const tgt = sendParams(chatId);
  for (const p of parts) {
    try {
      await apiRetry(() => client.im.message.create({ params: { receive_id_type: tgt.type }, data: { receive_id: tgt.id, msg_type: 'text', content: JSON.stringify({ text: p }) } }));
    } catch (e) { logLine('发送失败: ' + (e && e.message)); }
  }
  // a text message pushes the control card up out of view -> invalidate it so the next menu tap
  // sends a fresh card at the bottom (fixes "点了没反应" when the live card is scrolled away).
  lastCard.delete(chatId);
}
// one "control card" per chat: all navigation (main menu <-> project sub-menu) updates THIS card
// in place instead of sending a new one, so cards never pile up.
const lastCard = new Map();   // chatId -> message_id of the live control card
const cardHash = new Map();   // message_id -> last content string patched to it (skip no-op patches)
async function sendCard(chatId, card, setLast) {
  try {
    const content = JSON.stringify(card);
    const tgt = sendParams(chatId);
    const res = await apiRetry(() => client.im.message.create({ params: { receive_id_type: tgt.type }, data: { receive_id: tgt.id, msg_type: 'interactive', content } }));
    const mid = res && res.data && res.data.message_id;
    // control cards (menu OR project) become the live lastCard; owner-notify cards pass setLast=falsey
    if (mid && setLast) lastCard.set(chatId, mid);
    if (mid) { cardHash.set(mid, content); if (cardHash.size > 100) cardHash.clear(); }
    return mid;
  } catch (e) { logLine('发送卡片失败: ' + (e && e.message)); return null; }
}
// show the ONE control card (menu or project): patch the live card in place; send a fresh one only
// if there is none / it can't be patched. All navigation flows through here -> exactly one card.
async function showCard(chatId, card) {
  const mid = lastCard.get(chatId);
  if (mid) {
    // NO content-skip here: the live card may have been deleted (user cleared the Feishu chat), so a
    // menu tap MUST still produce visible output. Always try to patch; on ANY failure send a fresh card.
    try { const content = JSON.stringify(card); await apiRetry(() => client.im.message.patch({ path: { message_id: mid }, data: { content } })); cardHash.set(mid, content); return mid; }
    catch (e) { lastCard.delete(chatId); cardHash.delete(mid); }   // gone/too old/deleted -> send a new one
  }
  return await sendCard(chatId, card, true);
}
// which card should this chat be looking at right now (main menu vs the project sub-menu)
function currentCard(chatId, senderOpen) {
  const sess = getSession(chatId);
  return (sess.mode === 'project' && sess.project) ? buildProjectCard(chatId, senderOpen) : buildMenuCard(chatId, senderOpen);
}
// update the clicked card in place so the ✅ (current project / model / mode) moves.
// always pass the card explicitly (built with the operator's senderOpen for role-aware rendering).
async function refreshCard(chatId, messageId, card) {
  if (!messageId || !card) return;
  const content = JSON.stringify(card);
  if (cardHash.get(messageId) === content) { lastCard.set(chatId, messageId); return; }   // no change -> skip patch
  try {
    await apiRetry(() => client.im.message.patch({ path: { message_id: messageId }, data: { content } }));
    lastCard.set(chatId, messageId);   // the refreshed control card (menu OR project) is now the live card
    cardHash.set(messageId, content); if (cardHash.size > 100) cardHash.clear();
  } catch (e) { logLine('更新卡片失败: ' + (e && e.message)); }
}
// a footer line with elapsed time / output tokens / cost, appended to a run's result
function fmtMeta(r) {
  const m = fmtMetaLine(r);
  return m ? '\n\n———\n' + m : '';
}
function fmtMetaLine(r) {
  if (!r) return '';
  const parts = [];
  if (r.ms) parts.push('⏱ ' + Math.round(r.ms / 1000) + 's');
  const ot = r.usage && r.usage.output_tokens;
  if (ot) parts.push('输出 ' + ot + ' tokens');
  if (typeof r.cost === 'number') parts.push('≈ $' + r.cost.toFixed(3));
  return parts.join(' · ');
}
// Feishu PLAIN-TEXT messages don't render markdown — **bold**, ## headings and `code` show as raw
// symbols (user-reported eyesore). Results go out as an interactive card with lark_md instead, and
// we normalize the bits lark_md can't render (headings, bullets, code fences/ticks, tables).
function mdToLark(s) {
  return String(s || '')
    .replace(/\r/g, '')
    .replace(/```[\w-]*\n?([\s\S]*?)```/g, (_, c) => c.replace(/\n/g, '\n'))   // fenced blocks -> plain lines
    .replace(/^\s{0,3}#{1,6}\s+(.*)$/gm, '**$1**')      // # headings -> bold (lark_md has no headings)
    .replace(/^(\s*)[-*+]\s+/gm, '$1• ')                 // - / * bullets -> • (lark_md doesn't bullet them)
    .replace(/^\s*\|(.+)\|\s*$/gm, (line, inner) =>      // table rows -> " a · b · c " (no table support)
      /^[\s:|-]+$/.test(inner) ? '' : '• ' + inner.split('|').map(c => c.trim()).filter(Boolean).join(' · '))
    .replace(/`([^`\n]+)`/g, '$1')                       // inline `code` -> plain (lark_md shows ticks raw)
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}
// send a claude RESULT as a rendered card (title header + lark_md body + gray meta note)
async function sendResult(chatId, title, body, r, template) {
  const MAX = 9000;   // Feishu card JSON caps ~30KB; keep the lark_md body comfortably under
  let b = mdToLark(body || '(无输出)');
  if (b.length > MAX) b = b.slice(0, MAX) + '\n\n…(内容较长已截断,完整结果见 VS Code 该会话)';
  const els = [{ tag: 'div', text: { tag: 'lark_md', content: b } }];
  const meta = fmtMetaLine(r);
  if (meta) els.push({ tag: 'note', elements: [{ tag: 'lark_md', content: meta }] });
  const card = {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: template || 'green', title: { tag: 'plain_text', content: title } },
    elements: els,
  };
  await sendCard(chatId, card, false);
  lastCard.delete(chatId);   // a result card pushes the control card up — invalidate it (same as sendText)
}
// long runs go silent while claude works; send a heartbeat so the user knows it's alive.
// returns a stop() to clear it. First beat at 15s.
function startHeartbeat(chatId, label) {
  let secs = 0;
  const t = setInterval(() => { secs += 15; sendText(chatId, `🤔 「${label}」思考中…(已 ${secs}s,后台没卡,跑完自动回结果)`); }, 15000);
  return () => { try { clearInterval(t); } catch (e) {} };
}
// ---- model registry: keep the newest models one tap away, and NEVER hard-block new ids ----
// buttons cover the current lineup; the text command 「模型 <任意 claude-* id>」 accepts anything,
// so a brand-new model is usable the day it ships with zero code changes.
const MODELS = [
  ['默认', ''],
  ['Fable 5', 'claude-fable-5'],   // newest tier (verified working headless)
  ['Opus', 'opus'],                // CLI aliases always track the latest of each family
  ['Sonnet', 'sonnet'],
  ['Haiku', 'haiku'],
];
function modelLabelOf(v) {
  const hit = MODELS.find(([, id]) => String(id).toLowerCase() === String(v || '').toLowerCase());
  return hit ? hit[0] : (v || '默认');   // unknown custom id -> show it verbatim
}
// standalone model picker — reached from the BOTTOM menu so you can switch models mid-conversation
// (chat / query / modify) WITHOUT leaving your session. It never reads or writes session state, and
// its buttons carry from:'m' so the model action re-renders THIS card, not the main menu.
function buildModelCard(chatId, senderOpen) {
  const cur = getUserModel(senderOpen).toLowerCase();   // YOUR own model (per-user)
  const eq = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
  const mbtns = modelsFor(senderOpen).map(([lbl, v]) => ({   // owner sees Fable 5; others don't
    tag: 'button', text: { tag: 'plain_text', content: (eq(cur, v) ? '✅ ' : '') + lbl },
    type: eq(cur, v) ? 'primary' : 'default', value: { do: 'model', m: v, from: 'm' },
  }));
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: `**你当前的模型:${modelLabelOf(cur)}** — 点一个切换(只影响你自己),切完直接继续对话即可(不打断当前会话)。` } }];
  for (let i = 0; i < mbtns.length; i += 3) elements.push({ tag: 'action', actions: mbtns.slice(i, i + 3) });
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'turquoise', title: { tag: 'plain_text', content: '🤖 切换模型' } },
    elements,
  };
}
// Telegram-style menu: buttons to enter a project / chat / status / switch model.
// Default 'idle' mode does nothing until the user taps a button here. Role-aware: viewers
// (coworkers) only see what they can actually use — chat + projects (read-only query).
function buildMenuCard(chatId, senderOpen) {
  const sess = getSession(chatId);
  const ap = activeProject(chatId);
  const projects = discoverProjects();
  const owner = authLevel(senderOpen) === 'full';
  const curModel = String(readConfig().feishuChatModel || '').toLowerCase();
  let modeLine;
  if (ap) modeLine = `**当前:📂 项目「${ap.name}」** — 直接发消息就在这里续跑。`;
  else if (sess.mode === 'chat') modeLine = '**当前:💬 闲聊模式** — 直接说话就是和我聊天。';
  else modeLine = owner
    ? '**请选择 👇** 点「闲聊模式」开始聊天,或点一个项目进入。选之前我不处理任何消息。'
    : '**请选择 👇** 点一个项目可**只读查询**它的技术细节(不改任何文件),或点「闲聊模式」。';
  const eq = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: modeLine } }];
  const row1 = [
    { tag: 'button', text: { tag: 'plain_text', content: (sess.mode === 'chat' ? '✅ ' : '💬 ') + '闲聊模式' }, type: sess.mode === 'chat' ? 'primary' : 'default', value: { do: 'chat' } },
    { tag: 'button', text: { tag: 'plain_text', content: 'ℹ️ 状态' }, type: 'default', value: { do: 'status' } },
  ];
  if (owner) row1.push({ tag: 'button', text: { tag: 'plain_text', content: '🔑 权限' }, type: 'default', value: { do: 'perm' } });
  elements.push({ tag: 'action', actions: row1 });
  if (owner) {   // model switching is a config op — viewers don't see dead buttons
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: `**模型:${modelLabelOf(curModel)}** — 聊天和项目执行都用它;对话中随时点底部「🤖 模型」也能切,或发「模型 <任意模型id>」用新模型:` } });
    const mbtns = MODELS.map(([lbl, v]) => ({
      tag: 'button', text: { tag: 'plain_text', content: (eq(curModel, v) ? '✅ ' : '') + lbl },
      type: eq(curModel, v) ? 'primary' : 'default', value: { do: 'model', m: v },
    }));
    for (let i = 0; i < mbtns.length; i += 3) elements.push({ tag: 'action', actions: mbtns.slice(i, i + 3) });
  }
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

// project sub-menu (level 1): after entering a project you pick 只读查询 vs 修改项目 FIRST,
// then just type. The chosen mode shows ✅; switching is one tap. Keeps the hierarchy shallow.
function buildProjectCard(chatId, senderOpen) {
  const ap = activeProject(chatId);
  const sub = getSession(chatId).sub;
  const name = ap ? ap.name : '项目';
  const owner = authLevel(senderOpen) === 'full';
  let line, tmpl;
  const work = getSession(chatId).work;
  if (!owner) {
    // coworker view: query-only, no dead buttons. Just ask.
    return {
      config: { wide_screen_mode: true, update_multi: true },
      header: { template: 'blue', title: { tag: 'plain_text', content: '只读查询 · ' + name } },
      elements: [
        { tag: 'div', text: { tag: 'lark_md', content: `**📂「${name}」· 👁 只读查询**\n直接提问即可 — 我读这个项目的代码/文档回答你的技术问题,**绝不改文件**。` } },
        { tag: 'action', actions: [
          { tag: 'button', text: { tag: 'plain_text', content: 'ℹ️ 状态' }, type: 'default', value: { do: 'status' } },
          { tag: 'button', text: { tag: 'plain_text', content: '⬅ 主菜单' }, type: 'default', value: { do: 'home' } },
        ] },
      ],
    };
  }
  if (sub === 'query') { line = `**📂「${name}」· 👁 只读查询**\n直接提问即可 — 我只读代码/答疑,**绝不改文件**。所有查询共用本项目的专属会话。`; tmpl = 'blue'; }
  else if (sub === 'modify') {
    const wt = work ? (workTitle(chatId) || work.slice(0, 8)) : '';
    line = work
      ? `**📂「${name}」· ✏️ 修改项目**\n当前会话:**${wt}**\n直接发指令即可 — 我会真正改动并继续这个会话。想换会话点「🔀 切换会话」。`
      : `**📂「${name}」· ✏️ 修改项目**\n先选一个要继续的会话 👇`;
    tmpl = 'red';
  }
  else { line = `**📂 已进入「${name}」**\n先选操作方式 👇 之后直接发消息即可。`; tmpl = 'grey'; }
  const elements = [
    { tag: 'div', text: { tag: 'lark_md', content: line } },
    { tag: 'action', actions: [
      { tag: 'button', text: { tag: 'plain_text', content: (sub === 'query' ? '✅ ' : '👁 ') + '只读查询' }, type: sub === 'query' ? 'primary' : 'default', value: { do: 'submode', sm: 'query' } },
      { tag: 'button', text: { tag: 'plain_text', content: (sub === 'modify' ? '✅ ' : '✏️ ') + '修改项目' }, type: sub === 'modify' ? 'primary' : 'default', value: { do: 'submode', sm: 'modify' } },
    ] },
  ];
  if (sub === 'modify' && work) {   // switching sessions only makes sense once you're in modify mode
    elements.push({ tag: 'action', actions: [
      { tag: 'button', text: { tag: 'plain_text', content: '🔀 切换会话' }, type: 'default', value: { do: 'sesslist' } },
    ] });
  }
  elements.push({ tag: 'action', actions: [
    { tag: 'button', text: { tag: 'plain_text', content: '🧹 清空查询记忆' }, type: 'default', value: { do: 'clearq' } },
    { tag: 'button', text: { tag: 'plain_text', content: 'ℹ️ 状态' }, type: 'default', value: { do: 'status' } },
    { tag: 'button', text: { tag: 'plain_text', content: '⬅ 主菜单' }, type: 'default', value: { do: 'home' } },
  ] });
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: tmpl, title: { tag: 'plain_text', content: '项目操作 · ' + name } },
    elements,
  };
}
// enter a project from a TEXT command: owners get the 只读/修改 choice card; viewers go straight
// into read-only query (their only capability — no dead sub-mode choice).
async function enterProject(chatId, senderOpen, p) {
  const viewer = authLevel(senderOpen) !== 'full';
  setSession(chatId, { mode: 'project', project: p.path, sub: viewer ? 'query' : undefined });
  await showCard(chatId, buildProjectCard(chatId, senderOpen));
}
// title of the currently picked work session (for the project card), '' if unknown/new
function workTitle(chatId) {
  const sess = getSession(chatId);
  if (!sess.project || !sess.work) return '';
  try {
    const s = listProjectSessions(sess.project, 12).find(x => x.id === sess.work);
    if (s) return s.title.length > 24 ? s.title.slice(0, 24) + '…' : s.title;
  } catch (e) {}
  return '🆕 新会话';   // a fresh uuid has no transcript yet, so it isn't in the list
}
// the session picker: which conversation should ✏️修改 continue?
function buildSessionCard(chatId) {
  const ap = activeProject(chatId);
  const name = ap ? ap.name : '项目';
  const cur = getSession(chatId).work;
  const list = ap ? listProjectSessions(ap.path, 5) : [];
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: `**✏️ 修改「${name}」— 选择要继续的会话**\n选一个继续(会给你最近对话摘要),或新开一个全新会话。` } }];
  if (list.length) {
    for (const s of list) {
      const t = s.title.length > 20 ? s.title.slice(0, 20) + '…' : s.title;
      const on = cur === s.id;
      elements.push({ tag: 'action', actions: [{
        tag: 'button',
        text: { tag: 'plain_text', content: (on ? '✅ ' : '📝 ') + t + ' · ' + shortTime(s.mtime) },
        type: on ? 'primary' : 'default',
        value: { do: 'pick', s: s.id },
      }] });
    }
  } else {
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: '_这个项目还没有历史会话 — 点「🆕 新开会话」开始第一个。_' } });
  }
  elements.push({ tag: 'action', actions: [
    { tag: 'button', text: { tag: 'plain_text', content: '🆕 新开会话' }, type: 'default', value: { do: 'newsess' } },
    { tag: 'button', text: { tag: 'plain_text', content: '⬅ 返回' }, type: 'default', value: { do: 'backproj' } },
  ] });
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'orange', title: { tag: 'plain_text', content: '选择会话 · ' + name } },
    elements,
  };
}

// ---- status / list / help (mode-aware) ----
function statusText(chatId, senderOpen) {
  const cfg = readConfig();
  let st = {};
  try { st = JSON.parse(fs.readFileSync(path.join(APP_DIR, 'state.json'), 'utf8')); } catch (e) {}
  const myModel = modelLabelOf(getUserModel(senderOpen));
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
  return `${mode}\n你的模型:${myModel}\n布防:${cfg.enabled ? '● 已布防' : '○ 未布防'} · 引擎 ${st.phase || 'idle'}\n${reset} · 实探间隔 ${cfg.probeIntervalMinutes || 15}m`;
}
function listText() {
  const ps = discoverProjects();
  if (!ps.length) return '未发现任何项目。先在「Claude续跑」里添加/勾选项目。';
  return '项目列表(回复「进入 编号」进入,例:进入 2):\n' + ps.map((p, i) => `${i + 1}. ${p.name}`).join('\n');
}
function helpText(chatId) {
  const ap = activeProject(chatId);
  const sub = getSession(chatId).sub;
  const cur = ap
    ? `📂 现在在项目「${ap.name}」·${sub === 'query' ? '👁 只读查询' : sub === 'modify' ? '✏️ 修改项目' : '未选模式(先点 只读/修改)'}`
    : '💬 现在是闲聊模式——直接说话就是和我聊天,不碰任何项目。';
  return [
    'Claude 服务器助手', cur, '',
    '用按钮最省事(发「菜单」调出):主菜单点项目 → 弹出「👁只读 / ✏️修改」→ 选一个 → 直接发消息。',
    '(选「✏️修改」会先让你挑要继续哪个会话,并给你最近对话摘要;也可「🆕 新开会话」。)',
    '',
    '文字命令(标 ⌂ 的仅在主菜单/空闲状态生效;进入会话后你打的字都属于会话,发「退出」先回主菜单):',
    '· 项目 / 菜单 → 列出项目(卡片,任何时候可用)',
    '· ⌂ 进入 <编号或名字> / 直接发项目名或编号 → 进入项目',
    '· ⌂ 查询 <问题> → 直接只读问答,不改文件',
    '· 退出 → 回主菜单(任何时候可用)',
    '· 状态 → 布防 / 额度 / 当前模式',
    '· 停止 <项目> → 取消正在跑的指令',
    '· 模型 / 模型 fable → 查看或切换模型;「模型 claude-xxx」可用任何新模型(聊天+项目都用,与软件同步)',
    '· 对话中想换模型不必退出:点底部「🤖 模型」按钮,弹出即选,选完继续对话(不打断会话)',
    '· 权限:默认除机器人主人外,大家都只能只读浏览查询,不能改项目(无需配置)',
    '· 授权 ou_xxx → 额外给某人「可改」权限;取消授权 / 授权列表 管理',
    '· 忘记闲聊 → 清空闲聊记忆;忘记查询 → 清空当前项目的只读查询记忆',
    '',
    '注:✏️修改会继续你在列表里选的那个会话(通常就是 VS Code 里的会话,面板不实时刷新,重开可见);',
    '👁只读走每个项目专属的查询会话,所有人共用,不碰你的工作会话。',
  ].join('\n');
}

// ---- authorization: project/config operations are gated to bound Feishu users ----
// feishuAuthOpenIds empty = not locked (anyone). Once set, only listed open_ids may operate
// on projects / change config; everyone else can still chat. A password (feishuAuthPassword)
// lets a new account self-authorize via 「解锁 <密码>」.
// three levels: 'full' (can modify projects + change config), 'viewer' (query projects
// read-only, no modify/config), 'none' (chat only). feishuAuthOpenIds empty = not locked (all full).
function authLevel(openId) {
  const cfg = readConfig();
  const full = (Array.isArray(cfg.feishuAuthOpenIds) ? cfg.feishuAuthOpenIds : []).filter(Boolean);
  if (openId && full.indexOf(openId) !== -1) return 'full';   // owner(s): modify + config
  if (!full.length) return 'full';               // not locked yet (bootstrap) — all full until you add yourself
  // locked: everyone who isn't an owner is a read-only VIEWER — browse/query projects, never modify.
  // No per-user grant needed. (feishuViewerOpenIds is no longer required; still honored if present is moot
  // since non-owners are viewers anyway.) Chat stays open to all (authLevel doesn't gate chat).
  return 'viewer';
}
const canProject = openId => authLevel(openId) !== 'none';   // enter/query a project (viewer=read-only). now always true when locked
const canConfig  = openId => authLevel(openId) === 'full';   // modify projects / change config / authorize — owner only

// ---- PER-USER model preference ----
// A single global model can't satisfy "I use Fable 5, my coworker uses Haiku" — so each person keeps
// their OWN model. The OWNER's model lives in feishuChatModel (also what the GUI chip shows/sets);
// every non-owner keeps theirs in feishuUserModels[openId] (default '' = CLI default). Runs use the
// caller's own model, so your Fable 5 and a coworker's Haiku never collide.
// Fable 5 is OWNER-ONLY: a non-owner can neither select it (button hidden + command rejected) nor be
// billed for it (effectiveModel caps it defensively).
function effectiveModel(openId, model) {
  if (String(model || '').toLowerCase() === 'claude-fable-5' && authLevel(openId) !== 'full') return '';
  return model;
}
function getUserModel(openId) {
  const cfg = readConfig();
  if (authLevel(openId) === 'full') return String(cfg.feishuChatModel || '');
  const um = cfg.feishuUserModels && cfg.feishuUserModels[openId];
  return String(um || '');
}
function setUserModel(openId, model) {
  const cfg = readConfig();
  if (authLevel(openId) === 'full') { cfg.feishuChatModel = model; }
  else {
    // non-owners can never store Fable 5
    if (String(model || '').toLowerCase() === 'claude-fable-5') return false;
    if (!cfg.feishuUserModels || typeof cfg.feishuUserModels !== 'object') cfg.feishuUserModels = {};
    cfg.feishuUserModels[openId] = model;
  }
  try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(cfg, null, 4), 'utf8'); } catch (e) {}
  return true;
}
// the model a run should actually use for this caller (their own pick + the Fable-5 owner-only cap)
function runModelFor(openId) { return effectiveModel(openId, getUserModel(openId)); }
// which models a caller may SELECT: owner gets all; everyone else gets the lineup minus Fable 5
function modelsFor(openId) {
  return authLevel(openId) === 'full' ? MODELS : MODELS.filter(([, id]) => id !== 'claude-fable-5');
}

const notifiedUnauth = new Set();   // notify the owner at most once per unknown open_id
function notifyOwner(openId, chatId) {
  try {
    const owner = readConfig().feishuChatId;
    if (!owner || owner === chatId || !openId || notifiedUnauth.has(openId)) return;
    notifiedUnauth.add(openId);
    sendCard(owner, {
      config: { wide_screen_mode: true },
      header: { template: 'red', title: { tag: 'plain_text', content: '🔔 有人请求使用机器人' } },
      elements: [
        { tag: 'div', text: { tag: 'lark_md', content: `open_id:\`${openId}\`\n给他什么权限?` } },
        { tag: 'action', actions: [
          { tag: 'button', text: { tag: 'plain_text', content: '✅ 可改项目' }, type: 'primary', value: { do: 'authorize', id: openId } },
          { tag: 'button', text: { tag: 'plain_text', content: '👁 只读查询' }, type: 'default', value: { do: 'viewauth', id: openId } },
          { tag: 'button', text: { tag: 'plain_text', content: '忽略' }, type: 'default', value: { do: 'noop' } },
        ] },
      ],
    });
  } catch (e) {}
}
// gate for project entry/query/status (viewers allowed). returns true if denied.
async function denyProject(openId, chatId) {
  if (canProject(openId)) return false;
  await sendText(chatId, `🔒 无权限:需授权才能查询项目(你可以闲聊)。\n你的 open_id:${openId || '未知'}`);
  logLine('拦截未授权(项目): ' + openId); notifyOwner(openId, chatId); return true;
}
// gate for modify/config/authorize (full only). returns true if denied.
async function denyConfig(openId, chatId) {
  if (canConfig(openId)) return false;
  const lvl = authLevel(openId);
  await sendText(chatId, (lvl === 'viewer'
    ? '🔒 只读:除机器人主人外,大家只能浏览/查询项目,不能修改。'
    : '🔒 无权限。') + `\n(要「可改」权限,把这个 open_id 发给机器人主人)\n你的 open_id:${openId || '未知'}`);
  logLine('拦截未授权(配置): ' + openId);
  notifyOwner(openId, chatId);   // owner gets a one-tap 授权 card (deduped per open_id); everyone is 'viewer' now
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

    // remember every user's own p2p chat (bot-menu events carry no chat_id, so replies need this).
    // P2P ONLY: an @-mention in a GROUP must not poison the mapping, or the user's next bottom-menu
    // tap would post the control card into the group and reset the group's session.
    const isP2P = (msg.chat_type || 'p2p') === 'p2p';
    if (isP2P) rememberUserChat(senderOpen, chatId);
    // feishuChatId is the OWNER's notification chat. Rebind rules (each one load-bearing):
    // - p2p only: an owner @-ing the bot in a group must not leak checker/authorize notifications there;
    // - sender must be EXPLICITLY in feishuAuthOpenIds (bootstrap-unlocked strangers don't count);
    // - bootstrap exception: while unlocked AND no chat is bound yet, the first p2p message binds it.
    try {
      const c = readConfig();
      const fullList = (Array.isArray(c.feishuAuthOpenIds) ? c.feishuAuthOpenIds : []).filter(Boolean);
      const mayBind = fullList.indexOf(senderOpen) !== -1 || (!c.feishuChatId && fullList.length === 0);
      if (chatId && isP2P && c.feishuChatId !== chatId && mayBind) {
        c.feishuChatId = chatId;
        fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8');
        logLine('已记录通知 chatId(owner): ' + chatId);
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

    // password unlock: authorize this account for project/config ops (idle only — inside a
    // conversation a sentence starting with these words belongs to the conversation)
    const um = getSession(chatId).mode === 'idle' ? text.match(/^(解锁|认证|密码|auth|unlock)\s+(.+)$/i) : null;
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

    // permission management (owners only): 授权 / 取消授权 / 只读授权 / 取消只读 / 授权列表
    if (/^(授权列表|权限列表|谁有权限)$/.test(text)) {
      if (await denyConfig(senderOpen, chatId)) return;
      const c = readConfig();
      const full = (c.feishuAuthOpenIds || []).filter(Boolean);
      await sendText(chatId, '✅ 可改项目(仅以下人):\n' + (full.length ? full.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 未锁定,所有人可改)') +
        '\n\n👁 其他所有人 = 只读浏览(自动,无需授权)。');
      return;
    }
    // anchored: keyword alone -> usage, or keyword + ou_id. NOT a bare 只读 (that's the query prefix).
    const am = text.match(/^(授权|取消授权|解除授权|只读授权|取消只读)(?:\s+(ou_[A-Za-z0-9]+))?\s*$/);
    if (am) {
      if (await denyConfig(senderOpen, chatId)) return;
      const id = am[2];
      if (!id) { await sendText(chatId, '用法:\n「授权 ou_xxx」= 可改项目\n「只读授权 ou_xxx」= 只能查询\n「取消授权 / 取消只读 ou_xxx」= 移除\n「授权列表」查看。\n让对方给我发条消息,他会看到自己的 open_id。'); return; }
      const c = readConfig();
      let full = (c.feishuAuthOpenIds || []).filter(Boolean), view = (c.feishuViewerOpenIds || []).filter(Boolean);
      const kind = am[1];
      const hadFull = full.length;
      if (kind === '授权') { if (full.indexOf(id) === -1) full.push(id); view = view.filter(x => x !== id); await sendText(chatId, '✅ 已授权(可改):' + id); }
      else if (kind === '只读授权') { if (view.indexOf(id) === -1) view.push(id); full = full.filter(x => x !== id); await sendText(chatId, '👁 已授权(只读):' + id); }
      else {
        full = full.filter(x => x !== id); view = view.filter(x => x !== id);
        await sendText(chatId, '已移除:' + id);
        if (hadFull && !full.length) await sendText(chatId, '⚠️ 可改名单已空 = 解除锁定,现在所有人都能改你的项目/改配置/授权他人!要保持锁定,请「授权 ou_你自己」。');
      }
      c.feishuAuthOpenIds = full; c.feishuViewerOpenIds = view;
      try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8'); } catch (e) {}
      return;
    }

    // ---- global commands (work in any mode) ----
    if (['帮助', 'help', '?', '？'].indexOf(low) !== -1) { await sendText(chatId, helpText(chatId)); return; }
    if (['状态', 'status', 'zt'].indexOf(low) !== -1) { if (await denyProject(senderOpen, chatId)) return; await sendText(chatId, statusText(chatId, senderOpen)); return; }
    if (['项目', 'list', '项目列表', '列出项目', '所有项目', '菜单', 'menu'].indexOf(low) !== -1) { await showCard(chatId, buildMenuCard(chatId, senderOpen)); return; }
    if (['退出', '返回', 'exit', 'quit', '退出项目', '主菜单'].indexOf(low) !== -1) {
      setSession(chatId, { mode: 'idle' });
      await showCard(chatId, buildMenuCard(chatId, senderOpen));   // back to the main menu (idle)
      return;
    }
    if (['闲聊', '闲聊模式', 'chat'].indexOf(low) !== -1) {
      setSession(chatId, { mode: 'chat' });
      await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。发「退出」回主菜单。');
      return;
    }
    // model: show (模型) or set (模型 <按钮里的名字 / 任意 claude-* 完整 id>) — shared with the GUI chip
    if (['模型', 'model', '闲聊模型'].indexOf(low) !== -1) {
      const cur = getUserModel(senderOpen);   // YOUR own model (per-user)
      const owner = authLevel(senderOpen) === 'full';
      await sendText(chatId, `你当前的模型:${modelLabelOf(cur)}${cur ? `(${cur})` : ''}\n` +
        (owner
          ? '改用:发「模型 fable / opus / sonnet / haiku / 默认」,或完整 id(如「模型 claude-fable-5」);也可点底部「🤖 模型」。'
          : '改用:发「模型 opus / sonnet / haiku / 默认」,或点底部「🤖 模型」。(Fable 5 仅机器人主人可用)'));
      return;
    }
    const setm = text.match(/^(模型|闲聊模型|model)\s+(\S+)$/i);
    if (setm) {
      const owner = authLevel(senderOpen) === 'full';
      const a = setm[2].toLowerCase();
      const alias = { 'fable': 'claude-fable-5', 'fable5': 'claude-fable-5', 'opus': 'opus', 'sonnet': 'sonnet', 'haiku': 'haiku', '默认': '', 'default': '', '清除': '', '空': '', 'none': '' };
      let val;
      if (alias[a] !== undefined) val = alias[a];
      else if (/^claude-[a-z0-9.-]+$/i.test(a)) val = a;   // any full model id — future models work day one
      else { await sendText(chatId, `不认识「${setm[2]}」。可用:${owner ? 'fable / ' : ''}opus / sonnet / haiku / 默认${owner ? ',或完整模型 id(claude- 开头)' : ''}。`); return; }
      // Fable 5 is OWNER-ONLY
      if (String(val).toLowerCase() === 'claude-fable-5' && !owner) {
        await sendText(chatId, '🔒 Fable 5 仅机器人主人可用。你可以选:opus / sonnet / haiku / 默认。');
        return;
      }
      setUserModel(senderOpen, val);   // per-user (owner -> feishuChatModel, others -> feishuUserModels[open_id])
      await sendText(chatId, `你的模型已设为:${modelLabelOf(val)}${val ? `(${val})` : ''}(只影响你自己,下一句生效)。`);
      return;
    }
    // forget chat memory (drop the started flag + the claude session for the chat cwd)
    if (['忘记闲聊', '清空闲聊', '重置闲聊', '忘记记忆', 'forget', 'reset chat'].indexOf(low) !== -1) {
      if (await denyConfig(senderOpen, chatId)) return;
      try { fs.rmSync(path.join(CHAT_DIR, '.started'), { force: true }); } catch (e) {}
      try {
        const proot = path.join(os.homedir(), '.claude', 'projects');
        for (const d of fs.readdirSync(proot)) { if (/ClaudeResume-feishu-chat$/i.test(d)) fs.rmSync(path.join(proot, d), { recursive: true, force: true }); }
      } catch (e) {}
      await sendText(chatId, '已清空闲聊记忆,下次闲聊从头开始。');
      return;
    }
    if (['忘记查询', '清空查询', '重置查询'].indexOf(low) !== -1) {
      if (await denyConfig(senderOpen, chatId)) return;
      const ap = activeProject(chatId);
      if (!ap) { await sendText(chatId, '先进入一个项目,再发「忘记查询」清空它的只读查询记忆。'); return; }
      const n = clearQuerySession(ap.path, senderOpen);   // clears YOUR OWN query session for this project (queries are per-user now)
      await sendText(chatId, `🧹 已清空你在「${ap.name}」的只读查询记忆(删除会话 ${n} 个)。下次查询从头开始。`);
      return;
    }
    if (/^(停止|stop)(\s|$)/i.test(text)) {   // \b never matches between 止 and a space/CJK char
      if (await denyConfig(senderOpen, chatId)) return;   // owner-only: a viewer must not kill your run
      const rest = text.replace(/^(停止|stop)\s*/i, '').trim();
      const p = rest ? findProject(rest) : activeProject(chatId);
      // cover ALL run kinds: the project's modify run, its query run (isolated cwd), and chat
      const keys = [];
      if (p) { keys.push(p.path.toLowerCase(), querySession(p.path, senderOpen).cwd.toLowerCase()); }   // your own query run
      keys.push(CHAT_DIR.toLowerCase());
      const hit = keys.find(k => running.has(k));
      if (hit) { killTree(running.get(hit)); await sendText(chatId, '已请求停止' + (p ? `:${p.name}` : '(闲聊)')); }
      else await sendText(chatId, '没有正在运行的任务。');
      return;
    }

    // ---- FUZZY commands: idle mode ONLY ----
    // Inside a conversation (chat/project) free text BELONGS TO THE CONVERSATION. The old any-mode
    // matching hijacked real answers: replying "选 A" to claude's multiple-choice question matched
    // 「选 <名字>」and dumped the user back into the menu; "1" matched a bare project number; a
    // greeting popped the menu mid-chat. Explicit commands (退出/菜单/停止/模型/帮助…) above still
    // work in every mode.
    const inIdle = getSession(chatId).mode === 'idle';
    // explicit enter: 进入/打开 <编号或名字> (dropped the trigger-happy aliases 选/选择/切换/进)
    const m = inIdle ? text.match(/^(进入|打开|open|use)\s+(.+)$/i) : null;
    if (m) {
      if (await denyProject(senderOpen, chatId)) return;
      const p = findProject(m[2]);
      if (p) { await enterProject(chatId, senderOpen, p); }
      else await sendText(chatId, `没找到项目「${m[2]}」。\n\n` + listText());
      return;
    }

    // greetings: show the button menu (Telegram-style) so it's easy to pick chat vs a project
    if (inIdle && /^(你好|您好|hi|hello|hey|哈喽|在吗|在么|在不在|在|你好呀|嗨|yo|start|开始)$/i.test(text)) {
      await showCard(chatId, buildMenuCard(chatId, senderOpen));
      return;
    }

    // bare project name/number -> enter it (idle only)
    const bare = inIdle ? projectIfBareName(text) : null;
    if (bare) { if (await denyProject(senderOpen, chatId)) return; await enterProject(chatId, senderOpen, bare); return; }
    // "<project> <command>" -> one-off run (idle only), doesn't change the current mode
    const oneoff = inIdle ? oneOffTarget(text) : { project: null };
    if (oneoff.project) {
      if (await denyProject(senderOpen, chatId)) return;
      const qm = oneoff.prompt.match(QUERY_RE);
      const isQuery = authLevel(senderOpen) === 'viewer' || !!qm;   // viewer forced RO; owner opts in via 查询/只读
      const q = qm ? qm[2] : oneoff.prompt;
      if (isQuery) {
        const qk = querySession(oneoff.project.path, senderOpen).cwd.toLowerCase();
        if (running.has(qk) || inflight.has(qk)) { await sendText(chatId, `你对「${oneoff.project.name}」的查询进行中,请稍候。`); return; }
        inflight.add(qk);   // reserve synchronously, then run in the background so the handler ACKs fast
        bg('一次性查询', qk, async () => {
          await sendText(chatId, `🔍 只读查询「${oneoff.project.name}」:${q}\n(读代码/答疑,不改文件 · 你专属的查询会话,别人看不到)`);
          const r = await runProjectQuery(chatId, oneoff.project, q, senderOpen);
          await sendResult(chatId, (r.ok ? '✅ 查询结果 · ' + oneoff.project.name : '⚠️ 查询未完成 · ' + oneoff.project.name), r.text, r, r.ok ? 'blue' : 'red');
          logLine(`一次性查询 ${oneoff.project.name} ok=${r.ok}`);
        });
        return;
      }
      const ok1 = oneoff.project.path.toLowerCase();
      if (running.has(ok1) || inflight.has(ok1)) { await sendText(chatId, `「${oneoff.project.name}」正在执行中,请稍候。`); return; }
      inflight.add(ok1);
      bg('一次性执行', ok1, async () => {
        await sendText(chatId, `📂 一次性在「${oneoff.project.name}」执行:${q}\n(可能要 1-4 分钟,跑完自动回结果)`);
        const stopHb = startHeartbeat(chatId, oneoff.project.name);
        try {
          const r = await runClaude(oneoff.project.path, oneoff.project.name, q, { useContinue: true, model: runModelFor(senderOpen) });
          await sendResult(chatId, (r.ok ? '✅ 完成 · ' + oneoff.project.name : '⚠️ 未完成 · ' + oneoff.project.name), r.text, r, r.ok ? 'green' : 'red');
          logLine(`一次性完成 ${oneoff.project.name} ok=${r.ok}`);
        } finally { stopHb(); }
      });
      return;
    }

    // ---- mode dispatch ----
    const active = activeProject(chatId);
    if (active) {   // project mode: route by the chosen sub-mode (只读查询 / 修改项目)
      if (await denyProject(senderOpen, chatId)) return;
      const level = authLevel(senderOpen);
      let sub = getSession(chatId).sub;
      if (level === 'viewer') sub = 'query';                 // viewers are always read-only
      const qm = text.match(QUERY_RE);                       // 查询/只读 prefix = one-off read-only override
      // no sub-mode chosen yet -> ask via the project card first (unless an explicit 查询 prefix)
      if (!sub && !qm) { await showCard(chatId, buildProjectCard(chatId, senderOpen)); return; }
      if (sub === 'query' || qm) {
        const qk = querySession(active.path, senderOpen).cwd.toLowerCase();   // per-user query cwd, not project.path
        if (running.has(qk) || inflight.has(qk)) { await sendText(chatId, `你对「${active.name}」的查询进行中,请稍候。`); return; }
        inflight.add(qk);
        const q = qm ? qm[2] : text;
        bg('查询', qk, async () => {
          await sendText(chatId, `🔍 只读查询「${active.name}」:${q}\n(读代码/答疑,不改文件 · 你专属的查询会话,别人看不到)`);
          const r = await runProjectQuery(chatId, active, q, senderOpen);
          await sendResult(chatId, (r.ok ? '✅ 查询结果 · ' + active.name : '⚠️ 查询未完成 · ' + active.name), r.text, r, r.ok ? 'blue' : 'red');
          logLine(`查询 ${active.name} ok=${r.ok}`);
        });
        return;
      }
      // ✏️修改 continues a SPECIFIC conversation the user picked from the session list. No pick yet
      // (or it's a fresh entry) -> show the picker instead of guessing.
      const work = getSession(chatId).work;
      if (!work) { await showCard(chatId, buildSessionCard(chatId)); return; }
      const mk = active.path.toLowerCase();
      if (running.has(mk) || inflight.has(mk)) { await sendText(chatId, `「${active.name}」正在执行中,请稍候,或发「停止」取消。`); return; }
      inflight.add(mk);
      bg('执行', mk, async () => {
        await sendText(chatId, `📂 在「${active.name}」执行:${text}\n(可能要 1-4 分钟,跑完自动回结果)`);
        const stopHb = startHeartbeat(chatId, active.name);
        try {
          // an existing session resumes; a freshly-made uuid has no transcript yet -> --session-id creates it
          const r = await runClaude(active.path, active.name, text, {
            sessionId: work, sessionExists: querySessionExists(work), model: runModelFor(senderOpen),
          });
          await sendResult(chatId, (r.ok ? '✅ 完成 · ' + active.name : '⚠️ 未完成 · ' + active.name), r.text, r, r.ok ? 'green' : 'red');
          logLine(`完成 ${active.name} ok=${r.ok} session=${work.slice(0, 8)}`);
        } finally { stopHb(); }
      });
      return;
    }
    if (getSession(chatId).mode === 'chat') {   // chat mode: talk to Claude
      const ck = CHAT_DIR.toLowerCase();
      if (running.has(ck) || inflight.has(ck)) { await sendText(chatId, '上一句还在想,请稍候…'); return; }
      inflight.add(ck);
      // SECURITY: chat is open to everyone, so only the OWNER gets full tools (skip-permissions —
      // WebSearch/Bash/Read like the web app). A non-owner (viewer) gets a read-only chat: plan mode
      // + no file/exec tools, so they can't Bash-modify files or Read the bot's ../config.json secrets.
      const chatOwner = authLevel(senderOpen) === 'full';
      const useCont = chatStarted();
      bg('闲聊', ck, async () => {
        await sendText(chatId, '🤔 正在思考…');
        logLine(`闲聊 思考中: ${text}`);
        const stopHb = startHeartbeat(chatId, '闲聊');
        try {
          const r = await runClaude(CHAT_DIR, '闲聊', text, {
            useContinue: useCont,
            skipPermissions: chatOwner,
            readOnly: !chatOwner,
            disallowedTools: chatOwner ? undefined : ['Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit'],
            model: runModelFor(senderOpen),   // per-user model; non-owner never on Fable 5
          });
          if (r.ok) markChatStarted();
          await sendResult(chatId, r.ok ? '💬 闲聊' : '⚠️ 闲聊', (r.text || '(无输出)') + '\n\n———\n💬 闲聊模式 · 发「菜单」切换', r, r.ok ? 'green' : 'red');
          logLine(`闲聊 完成 ok=${r.ok}`);
        } finally { stopHb(); }
      });
      return;
    }
    // idle mode: don't run anything — show the menu so the user picks a mode first
    await showCard(chatId, buildMenuCard(chatId, senderOpen));
  } catch (e) { logLine('处理消息异常: ' + (e && e.stack || e)); }
}

// ---- interactive card button clicks (card.action.trigger) ----
const cardSeen = new Map(); // dedup rapid Feishu re-deliveries of the same click
const menuSeen = new Map(); // dedup rapid bottom-menu taps
const seenEid = new Set();  // dedup by event_id (genuine Feishu re-deliveries), when the field is present
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
    // Do NOT learn userChats here at all: card events carry no chat_type, so a click in a GROUP would
    // bind userChats[open_id] = group-id and then route the user's private bottom-menu into the group
    // (and reset the group session). userChats is learned ONLY from p2p messages (onMessage, isP2P);
    // a user who has never messaged still gets replies via the 'od:'+open_id fallback in userTarget.
    logLine(`卡片点击 chat=${chatId} sender=${senderOpen}: ${JSON.stringify(val)}`);

    // IMPORTANT: a card callback must respond within a few seconds or Feishu shows
    // "目标回调服务超时未响应". So do ONLY fast local work here (sync file writes) and fire every
    // API call (send/patch) WITHOUT await, then return immediately.
    const projectActs = ['status', 'enter', 'submode'];                        // viewers allowed
    // full only — incl. the modify-session flow (sesslist/pick/newsess/backproj): a viewer must not
    // browse the owner's work-session titles/digests or flip a session's work pointer
    // 'model' is NOT here: everyone may set THEIR OWN model (per-user); the action itself blocks a
    // non-owner from selecting the owner-only Fable 5.
    const configActs = ['perm', 'authorize', 'revoke', 'viewauth', 'viewrevoke', 'clearq', 'sesslist', 'pick', 'newsess', 'backproj'];
    if (projectActs.indexOf(val.do) !== -1 && !canProject(senderOpen)) { denyProject(senderOpen, chatId); return; }
    if (configActs.indexOf(val.do) !== -1 && !canConfig(senderOpen)) { denyConfig(senderOpen, chatId); return; }

    if (val.do === 'chat') { setSession(chatId, { mode: 'chat' }); refreshCard(chatId, messageId, buildMenuCard(chatId, senderOpen)); return; }
    if (val.do === 'home') { setSession(chatId, { mode: 'idle' }); refreshCard(chatId, messageId, buildMenuCard(chatId, senderOpen)); return; }   // leave project mode (avoid accidental typed-modify)
    if (val.do === 'status') { sendText(chatId, statusText(chatId, senderOpen)); return; }
    if (val.do === 'perm') {
      const c = readConfig();
      const full = (c.feishuAuthOpenIds || []).filter(Boolean);
      sendText(chatId, '✅ 可改项目(仅以下人):\n' + (full.length ? full.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 未锁定,所有人可改!建议先发「授权 你的open_id」)') +
        '\n\n👁 其他所有人 = 只读浏览查询(自动,无需授权)。\n\n想让某人也能改:发「授权 ou_xxx」(对方 open_id 会在他给我发消息时显示)。');
      return;
    }
    if (val.do === 'noop') { return; }
    if (val.do === 'model') {
      const mm = String(val.m || '').toLowerCase();
      // only registry values; setUserModel writes it PER-USER (owner -> feishuChatModel, others ->
      // feishuUserModels[open_id]) and refuses Fable 5 for non-owners (defense — the button is hidden
      // for them anyway). Ignore an invalid/blocked pick (keep the current one).
      const v = MODELS.some(([, id]) => String(id).toLowerCase() === mm) ? mm : '';
      setUserModel(senderOpen, v);
      // re-render the SAME card so ✅ moves. from:'m' = the standalone bottom-menu model card — it must
      // NOT fall back to the main menu (that would reset the view and hide the running conversation).
      refreshCard(chatId, messageId, val.from === 'm' ? buildModelCard(chatId, senderOpen) : buildMenuCard(chatId, senderOpen));
      return;
    }
    if (val.do === 'enter') {
      const p = discoverProjects().find(x => x.path.toLowerCase() === String(val.p).toLowerCase()) || (val.p ? { name: path.basename(val.p), path: val.p } : null);
      if (!p) { sendText(chatId, '项目未找到(可能已变化)。发「菜单」重新选。'); return; }
      // owners pick 只读/修改 next; viewers go STRAIGHT to read-only query (their only capability)
      const viewer2 = authLevel(senderOpen) !== 'full';
      setSession(chatId, { mode: 'project', project: p.path, sub: viewer2 ? 'query' : undefined });
      refreshCard(chatId, messageId, buildProjectCard(chatId, senderOpen));
      return;
    }
    if (val.do === 'submode') {
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { refreshCard(chatId, messageId, buildMenuCard(chatId, senderOpen)); return; }
      let sm = (val.sm === 'modify') ? 'modify' : 'query';
      if (sm === 'modify' && authLevel(senderOpen) === 'viewer') { sm = 'query'; sendText(chatId, '👁 你是只读用户,只能查询,不能改项目。'); }
      if (sm === 'modify') {
        // 修改 = continue a specific conversation -> pick which one first (or start a fresh one)
        setSession(chatId, { mode: 'project', project: sess.project, sub: 'modify', work: sess.work });
        refreshCard(chatId, messageId, sess.work ? buildProjectCard(chatId, senderOpen) : buildSessionCard(chatId));
        return;
      }
      setSession(chatId, { mode: 'project', project: sess.project, sub: sm, work: sess.work });
      refreshCard(chatId, messageId, buildProjectCard(chatId, senderOpen));   // ✅ moves to the chosen mode
      return;
    }
    if (val.do === 'sesslist') {   // 🔀 切换会话
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { refreshCard(chatId, messageId, buildMenuCard(chatId, senderOpen)); return; }
      refreshCard(chatId, messageId, buildSessionCard(chatId));
      return;
    }
    if (val.do === 'backproj') { refreshCard(chatId, messageId, buildProjectCard(chatId, senderOpen)); return; }
    if (val.do === 'pick' || val.do === 'newsess') {
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { refreshCard(chatId, messageId, buildMenuCard(chatId, senderOpen)); return; }
      const isNew = val.do === 'newsess';
      const id = isNew ? crypto.randomUUID() : String(val.s || '');
      if (!id) { refreshCard(chatId, messageId, buildSessionCard(chatId)); return; }
      setSession(chatId, { mode: 'project', project: sess.project, sub: 'modify', work: id });
      refreshCard(chatId, messageId, buildProjectCard(chatId, senderOpen));   // back to the project card, session shown
      // digest of the picked conversation so you know where you left off (fire-and-forget: keep the
      // callback fast; reading a transcript is local but can be a few MB)
      bg('会话摘要', null, async () => {
        if (isNew) { await sendText(chatId, '🆕 已开一个**全新会话**,直接发指令即可(它不带任何历史)。'); return; }
        const s = listProjectSessions(sess.project, 12).find(x => x.id === id);
        const head = s ? `📝 已进入会话「${s.title}」(${shortTime(s.mtime)})` : `📝 已进入会话 ${id.slice(0, 8)}`;
        const pv = s ? sessionPreview(s.file, 2) : '';
        await sendText(chatId, head + (pv ? '\n\n最近的对话:\n' + pv : '') + '\n\n直接发指令继续这个会话。');
      });
      return;
    }
    if (val.do === 'clearq') {
      const p = getSession(chatId).project;
      const proj = p ? (discoverProjects().find(x => x.path.toLowerCase() === p.toLowerCase()) || { name: path.basename(p), path: p }) : null;
      if (!proj) { sendText(chatId, '当前不在项目里,无法清空查询记忆。'); return; }
      const n = clearQuerySession(proj.path, senderOpen);   // your own per-user query session
      sendText(chatId, `🧹 已清空你在「${proj.name}」的只读查询记忆(删除会话 ${n} 个)。下次查询从头开始。`);
      return;
    }
    // one-tap grant from the owner-notification card
    if (['authorize', 'revoke', 'viewauth', 'viewrevoke'].indexOf(val.do) !== -1) {
      const id = String(val.id || '');
      if (/^ou_[A-Za-z0-9]+$/.test(id)) {
        const c = readConfig();
        let full = (c.feishuAuthOpenIds || []).filter(Boolean), view = (c.feishuViewerOpenIds || []).filter(Boolean);
        if (val.do === 'authorize') { if (full.indexOf(id) === -1) full.push(id); view = view.filter(x => x !== id); sendText(chatId, '✅ 已授权(可改):' + id); }
        else if (val.do === 'viewauth') { if (view.indexOf(id) === -1) view.push(id); full = full.filter(x => x !== id); sendText(chatId, '👁 已授权(只读):' + id); }
        else { full = full.filter(x => x !== id); view = view.filter(x => x !== id); sendText(chatId, '已移除:' + id); }
        c.feishuAuthOpenIds = full; c.feishuViewerOpenIds = view;
        try { fs.writeFileSync(CONFIG_PATH, JSON.stringify(c, null, 4), 'utf8'); } catch (e) {}
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
    // defensive: Feishu v2 events carry header.event_id / header.create_time. Use them to drop genuine
    // re-deliveries and stale backlog. Harmless no-op if the SDK doesn't pass these fields.
    const evId = ev.event_id || (ev.header && ev.header.event_id) || '';
    const evTime = parseInt((ev.header && ev.header.create_time) || ev.create_time || '0', 10);
    if (evId) { if (seenEid.has(evId)) { logLine('忽略重复投递(eid)'); return; } seenEid.add(evId); if (seenEid.size > 3000) seenEid.clear(); }
    if (evTime && Date.now() - evTime > 60000) { logLine('忽略过期底部菜单事件 age=' + Math.round((Date.now() - evTime) / 1000) + 's'); return; }
    const cfg = readConfig();
    // reply to the OPERATOR's own p2p chat (from userChats, else by open_id). NEVER to feishuChatId
    // — that's the owner's chat, and routing everyone's menus there was the "coworker clicks show up
    // in my chat / coworkers see nothing" bug.
    const chatId = userTarget(senderOpen) || cfg.feishuChatId;
    if (!chatId) { logLine('菜单点击但无法确定回复目标(无 open_id)'); return; }
    const allow = Array.isArray(cfg.feishuAllowOpenIds) ? cfg.feishuAllowOpenIds.filter(Boolean) : [];
    if (allow.length && senderOpen && allow.indexOf(senderOpen) === -1) return;
    // dedup rapid repeat taps. The escape-hatch keys (menu/idle/exit/unknown) use a SHORT window so a
    // deliberate 主菜单 tap a couple seconds later still responds; text keys (chat/status) dedup longer.
    const escapeHatch = (key !== 'chat' && key !== 'status');
    const mkey = (chatId || '') + ':' + key + ':' + (senderOpen || ''); const mnow = Date.now();
    const dwin = escapeHatch ? 1500 : 3000;
    if (menuSeen.get(mkey) && mnow - menuSeen.get(mkey) < dwin) { logLine('忽略重复底部菜单点击: ' + key); return; }
    menuSeen.set(mkey, mnow); if (menuSeen.size > 200) menuSeen.clear();
    logLine('底部菜单点击: ' + key + (evId ? ' eid=…' + String(evId).slice(-6) : ' (无eid)') + (evTime ? ' age=' + Math.round((Date.now() - evTime) / 1000) + 's' : ' (无time)'));
    if (key === 'chat') { setSession(chatId, { mode: 'chat' }); await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。随时点底部「主菜单」回来。'); return; }
    if (key === 'status') { if (await denyProject(senderOpen, chatId)) return; await sendText(chatId, statusText(chatId, senderOpen)); return; }
    // 🤖 switch model mid-conversation: post a STANDALONE model card (does not touch the session, so
    // your chat/project/modify context is untouched). Everyone may set THEIR OWN model — the card
    // hides Fable 5 from non-owners. Match several plausible console event_keys so a typo still works.
    if (['model', 'models', '模型', 'switchmodel', 'switch_model', 'setmodel'].indexOf(key) !== -1) {
      await sendCard(chatId, buildModelCard(chatId, senderOpen), false);
      return;
    }
    // 主菜单 / idle / exit / 未知 —— the ESCAPE HATCH: from ANY state, return to a clean main menu with a
    // FRESH visible card at the bottom. Delete lastCard first so showCard sends a NEW card even when the
    // old control card is still alive but scrolled up (pushed away by a checker/quota notification or the
    // owner-notify card — those append without clearing lastCard). Resets the session so you're unstuck.
    setSession(chatId, { mode: 'idle' });
    lastCard.delete(chatId);
    await showCard(chatId, buildMenuCard(chatId, senderOpen));
  } catch (e) { logLine('底部菜单事件异常: ' + (e && (e.stack || e))); }
}

// ---- boot ----
if (TEST_MODE) {
  module.exports = { onMessage, onCardAction, onBotMenu, client, lastCard, setSession, getSession, discoverProjects, currentCard, querySession, clearQuerySession, listProjectSessions, sessionPreview, buildSessionCard, buildModelCard, effectiveModel, getUserModel, setUserModel, runModelFor, modelsFor, mdToLark, authLevel, shortTime };
  return;   // don't connect to Feishu in tests
}
// on every (re)start — usually right after a deploy — reset all chat sessions to idle. The user often
// clears the Feishu chat while testing, which deletes the old cards; a stale project/chat session +
// deleted-card references would make the next taps look dead. Starting clean makes the first tap work.
try { writeSessions({}); lastCard.clear(); cardHash.clear(); logLine('启动:已重置所有会话为初始状态(idle)'); } catch (e) {}
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
