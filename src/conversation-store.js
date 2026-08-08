'use strict';

/*
  conversation-store.js  ---  AI Resume Stage 1 S1-D: ConversationStore 边界

  本模块是以下现役状态的唯一 owner(在 feishu-agent 进程内单一 writer):
  - feishu-userchats.json:open_id -> 用户与机器人的私聊 chat(底部菜单回复目标)
  - feishu-sessions.json:chat -> idle/chat/project + sub/work/workProfile/workTitle
  - feishu-chat/<hash>/.started 与同 cwd 目录(闲聊 scratch 标记)
  - feishu-query/<hash>.started(只读查询 scratch 标记)
  - feishu-query-cwd/<hash>(只读查询的运行 cwd;目录由 provider runner 实际创建)
  - claudeProjectsDir 内 Claude/DeepSeek scratch JSONL 与同名 artifact 目录的清理

  强制边界:
  - 不依赖 @larksuiteoapi/node-sdk;不读取 config.json、飞书密钥、provider 或卡片结构;
    不持有固定项目列表。profile 解析、项目发现与 od: 迁移副作用全部注入。
  - stateDir / claudeProjectsDir / resolveProfile / profileById / getUserProfile /
    profilesFor / discoverProjects / onOdMigrate 是创建时必须注入的依赖,缺失即抛错;
    log 可选,仅用于记录写失败等容错信息。
  - activeProject(chatId, projects?) 支持可选已发现项目列表:事件入口持有 cfg 快照时,
    飞书层用快照对应列表解析 active project,显式列表存在时绝不调用注入 discoverProjects;
    无第二参数保持现役动态发现语义。
  - 旧状态文件缺失/损坏/写入失败保持现役容错(调用方不崩溃),不扩大为产品行为变化。
  - 文件名、JSON schema、legacy string session、stableSessionId 算法、chat/query 的
    seed/路径、Codex/Claude 初始与 mark 后 sessionId、mark/clear 语义全部与现役一致。
*/
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

function createConversationStore(options) {
  const opts = options || {};
  const stateDir = opts.stateDir;
  const claudeProjectsDir = opts.claudeProjectsDir;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const resolveProfile = opts.resolveProfile;
  const profileById = opts.profileById;
  const getUserProfile = opts.getUserProfile;
  const profilesFor = opts.profilesFor;
  const discoverProjects = opts.discoverProjects;
  const onOdMigrate = opts.onOdMigrate;

  if (!stateDir || typeof stateDir !== 'string') {
    throw new Error('conversation-store: stateDir 必须注入为字符串');
  }
  if (!claudeProjectsDir || typeof claudeProjectsDir !== 'string') {
    throw new Error('conversation-store: claudeProjectsDir 必须注入为字符串');
  }
  for (const name of ['resolveProfile', 'profileById', 'getUserProfile', 'profilesFor', 'discoverProjects', 'onOdMigrate']) {
    if (typeof opts[name] !== 'function') {
      throw new Error(`conversation-store: ${name} 必须注入为函数`);
    }
  }

  const SESSIONS_PATH = path.join(stateDir, 'feishu-sessions.json');
  const USERCHATS_PATH = path.join(stateDir, 'feishu-userchats.json');
  const CHAT_DIR = path.join(stateDir, 'feishu-chat');
  const QUERY_DIR = path.join(stateDir, 'feishu-query');        // per-project .started flags
  const QUERY_CWD_BASE = path.join(stateDir, 'feishu-query-cwd'); // queries run HERE, never in project.path

  function safeLog(message) {
    try { log(message); } catch (e) {}
  }

  function stripBom(text) { return String(text || '').replace(/^\uFEFF/, ''); }
  function readJson(p, fallback) {
    try { return JSON.parse(stripBom(fs.readFileSync(p, 'utf8'))); }
    catch (e) { return fallback; }
  }

  // open_id -> 该用户与机器人的私聊 chat。底部菜单事件只带 open_id,回复必须经此映射
  // (之前全部发给了 owner 的 chat)。未知用户回退 'od:<open_id>' 伪目标。
  let userChats = readJson(USERCHATS_PATH, {}) || {};
  function rememberUserChat(openId, chatId) {
    if (!openId || !chatId || userChats[openId] === chatId) return;
    userChats[openId] = chatId;
    try { fs.writeFileSync(USERCHATS_PATH, JSON.stringify(userChats, null, 2), 'utf8'); }
    catch (e) { safeLog(`feishu-userchats.json 写入失败:${e.code || e.message || 'unknown'}`); }
    // 新用户可能在发消息前就点过底部菜单:其会话/卡片当时落在 'od:<open_id>' 伪目标。
    // 迁移到真实 chat,让菜单选好的模式在其第一条消息后延续(而不是分裂状态)。
    try {
      const od = 'od:' + openId;
      const s = readSessions();
      if (s[od]) { if (!s[chatId]) s[chatId] = s[od]; delete s[od]; writeSessions(s); }
      // 卡片 Map 属于 agent(不入 store):通过注入回调迁移 lastCard 等副作用。
      onOdMigrate(od, chatId);
    } catch (e) { safeLog(`od: 会话迁移失败:${e.code || e.message || 'unknown'}`); }
  }
  // 无 chat_id 事件时的回复目标:已知私聊,否则按 open_id 发('od:ou_xxx' 由通道层翻译)。
  function userTarget(openId) { return userChats[openId] || (openId ? 'od:' + openId : null); }

  // 三种模式:'idle'(默认——用户点卡之前不处理)、'chat'、'project'。
  function readSessions() { return readJson(SESSIONS_PATH, {}); }
  function writeSessions(o) {
    try { fs.writeFileSync(SESSIONS_PATH, JSON.stringify(o, null, 2), 'utf8'); }
    catch (e) { safeLog(`feishu-sessions.json 写入失败:${e.code || e.message || 'unknown'}`); }
  }
  function getSession(chatId) {
    const s = readSessions(); const v = s[chatId];
    if (!v) return { mode: 'idle' };
    if (typeof v === 'string') return v ? { mode: 'project', project: v } : { mode: 'idle' };   // legacy
    // sub (project sub-mode): 'query' | 'modify' | undefined (not chosen yet -> ask first)
    // work: the provider-native session id that ✏️修改 continues. A new Claude session starts with a
    // UUID; a new Codex session uses 'new' until its first run returns a thread id.
    return {
      mode: v.mode || (v.project ? 'project' : 'idle'), project: v.project, sub: v.sub,
      work: v.work, workProfile: v.workProfile, workTitle: v.workTitle,
    };
  }
  function setSession(chatId, sess) { const s = readSessions(); s[chatId] = sess; writeSessions(s); }
  // D-003:可选第二参数 projects 为事件入口 cfg 快照下已发现的项目列表;显式传入时
  // 绝不再次调用注入的 discoverProjects,避免事件中途二次读配置 TOCTOU。
  // 无第二参数保持现役行为:内部调用注入的 discoverProjects 解析动态项目。
  function activeProject(chatId, projects) {
    const sess = getSession(chatId);
    if (sess.mode !== 'project' || !sess.project) return null;
    // 动态项目列表大小写不敏感命中。显式列表代表事件入口 cfg 快照下的授权项目集合，
    // 未命中必须 fail-closed；只有未显式传入列表的 legacy 调用保留 basename fallback。
    const explicit = Array.isArray(projects);
    const list = explicit ? projects : discoverProjects();
    const found = list.find(x => x.path.toLowerCase() === sess.project.toLowerCase());
    return found || (explicit ? null : { name: path.basename(sess.project), path: sess.project });
  }

  function readSessionFlag(flag) { return readJson(flag, {}) || {}; }
  function stableSessionId(seed) {
    const h = crypto.createHash('sha1').update(seed).digest('hex');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`;
  }
  function chatSession(openId, profileValue) {
    const profile = resolveProfile(profileValue, openId);
    const seed = `chat|${String(openId || 'anon')}|${profile.id}`;
    const h = crypto.createHash('sha1').update(seed).digest('hex');
    const cwd = path.join(CHAT_DIR, h);
    const flag = path.join(cwd, '.started');
    const meta = readSessionFlag(flag);
    const fixedId = stableSessionId(seed);
    return {
      id: fixedId, sessionId: meta.sessionId || (profile.engine === 'claude' ? fixedId : null),
      flag, cwd, profile, meta, started: fs.existsSync(flag),
    };
  }
  function chatStarted(openId, profileValue) { return chatSession(openId, profileValue).started; }
  function markChatStarted(openId, profileValue, meta) {
    const cs = chatSession(openId, profileValue);
    const payload = { ...(meta || {}), kind: 'chat', openId: openId || '', profileId: cs.profile.id, engine: cs.profile.engine, updatedAt: new Date().toISOString() };
    try { fs.mkdirSync(cs.cwd, { recursive: true }); fs.writeFileSync(cs.flag, JSON.stringify(payload), 'utf8'); }
    catch (e) { safeLog(`feishu-chat .started 写入失败:${e.code || e.message || 'unknown'}`); }
  }
  function clearChatSessions(openId) {
    let deleted = 0;
    const ids = new Set(profilesFor(true).map(p => p.id));
    ids.add(getUserProfile(openId).id);
    for (const id of ids) {
      const cs = chatSession(openId, id);
      try { if (fs.existsSync(cs.cwd)) { fs.rmSync(cs.cwd, { recursive: true, force: true }); deleted++; } } catch (e) {}
      // 只有 Claude/DeepSeek(engine=claude)才删 JSONL 与同名 artifact;Codex 不删。
      if (cs.profile.engine === 'claude') {
        try {
          for (const d of fs.readdirSync(claudeProjectsDir)) {
            const f = path.join(claudeProjectsDir, d, cs.id + '.jsonl');
            try { if (fs.existsSync(f)) fs.unlinkSync(f); } catch (e) {}
            try { fs.rmSync(path.join(claudeProjectsDir, d, cs.id), { recursive: true, force: true }); } catch (e) {}
          }
        } catch (e) {}
      }
    }
    return deleted;
  }

  // 只读查询会话按 项目 + 用户 + AI profile 隔离。Claude 风格用确定性 id;Codex 首次
  // 运行后保存原生 thread id。与工作会话(project work)完全分离。
  function querySession(projectPath, openId, profileValue) {
    const profile = resolveProfile(profileValue, openId);
    // Key by (project, USER, PROFILE): queries are private per caller, and switching provider cannot
    // resume another provider's transcript. openId falls back to 'anon' only for degenerate callers.
    const seed = String(projectPath).toLowerCase() + '|' + String(openId || 'anon') + '|' + profile.id;
    const h = crypto.createHash('sha1').update(seed).digest('hex');
    const id = stableSessionId(seed);
    const flag = path.join(QUERY_DIR, h + '.started');
    // isolated cwd so the query transcript does NOT land in project.path's session folder — otherwise
    // a later modify `--continue` would resume the query session instead of the VS Code work session.
    const cwd = path.join(QUERY_CWD_BASE, h);
    const meta = readSessionFlag(flag);
    return {
      id, sessionId: meta.sessionId || (profile.engine === 'claude' ? id : null), flag, cwd,
      profile, meta, started: (() => { try { return fs.existsSync(flag); } catch (e) { return false; } })(),
    };
  }
  // did claude actually persist this session's jsonl? (so we only flip to --resume once it truly exists)
  function querySessionExists(id, profileValue) {
    const profile = profileById(profileValue) || profileById('claude-default');
    if (profile.engine !== 'claude') return false;
    try {
      return fs.readdirSync(claudeProjectsDir).some(d => { try { return fs.existsSync(path.join(claudeProjectsDir, d, id + '.jsonl')); } catch (e) { return false; } });
    } catch (e) { return false; }
  }
  // flag content = {id, path, name} so the GUI's "清空查询记忆" can locate & delete the session jsonl
  function markQueryStarted(flag, meta) {
    const payload = { ...(meta || {}), kind: 'query', updatedAt: new Date().toISOString() };
    try { fs.mkdirSync(QUERY_DIR, { recursive: true }); fs.writeFileSync(flag, JSON.stringify(payload), 'utf8'); }
    catch (e) { safeLog(`feishu-query .started 写入失败:${e.code || e.message || 'unknown'}`); }
  }
  // wipe one caller/profile's project query session: remove the flag AND Claude session jsonl(s) with
  // that id (must delete the jsonl — --session-id on an existing id errors "already in use").
  function clearQuerySession(projectPath, openId, profileValue) {
    const qs = querySession(projectPath, openId, profileValue);
    let deleted = 0;
    try { fs.unlinkSync(qs.flag); } catch (e) {}
    if (qs.profile.engine !== 'claude') return deleted;
    try {
      for (const d of fs.readdirSync(claudeProjectsDir)) {
        const f = path.join(claudeProjectsDir, d, qs.id + '.jsonl');
        try { if (fs.existsSync(f)) { fs.unlinkSync(f); deleted++; } } catch (e) {}
        try { fs.rmSync(path.join(claudeProjectsDir, d, qs.id), { recursive: true, force: true }); } catch (e) {}
      }
    } catch (e) {}
    return deleted;
  }

  // 每次(重)启动把全部聊天会话重置为 idle(部署/清聊天后旧卡引用已失效)。
  function resetSessions() { writeSessions({}); }

  return {
    rememberUserChat, userTarget, readSessions, writeSessions, getSession, setSession,
    activeProject, chatSession, chatStarted, markChatStarted, clearChatSessions,
    querySession, querySessionExists, markQueryStarted, clearQuerySession, resetSessions,
    paths: { sessionsPath: SESSIONS_PATH, userChatsPath: USERCHATS_PATH, chatDir: CHAT_DIR, queryDir: QUERY_DIR, queryCwdBase: QUERY_CWD_BASE },
  };
}

module.exports = { createConversationStore };
