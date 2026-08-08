'use strict';
/*
  S1-D ConversationStore 单元测试:纯临时目录,不联网、不读生产配置、不启动任何 AI。
  覆盖:stateDir/必要依赖注入校验、od: 迁移与不覆盖已存在真实 chat session、userchat
  持久化与 fallback、legacy/full session round-trip(含 work 字段)、activeProject 命中/
  大小写/未知旧路径 fallback、chat/query 按 user/profile/project 稳定隔离(seed/路径冻结)、
  Codex/Claude 初始与 mark 后 sessionId、Claude JSONL+同名 artifact 清理与 Codex 不误删、
  missing/malformed/写失败容错。
  安装清单 fail-fast 由 test/install-deploy.ps1 单独覆盖。
  运行:node test/conversation-store.js
*/
const assert = require('assert');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { createConversationStore } = require('../src/conversation-store');

const CLAUDE = { id: 'claude-default', engine: 'claude', provider: 'claude', label: '默认', fullLabel: 'Claude · 默认' };
const DEEPSEEK = { id: 'deepseek-v4', engine: 'claude', provider: 'deepseek', label: 'V4', fullLabel: 'DeepSeek · V4' };
const CODEX = { id: 'openai-sol', engine: 'codex', provider: 'openai', label: 'Sol', fullLabel: 'OpenAI · Sol' };
const PROFILES = [CLAUDE, DEEPSEEK, CODEX];
const PROFILE_BY_ID = new Map(PROFILES.map(p => [p.id, p]));
function profileById(id) { return PROFILE_BY_ID.get(String(id || '').toLowerCase()) || null; }

// 冻结校验:stableSessionId 与 chat/query 的 seed 算法必须与现役完全一致。
function stableId(seed) {
  const h = crypto.createHash('sha1').update(seed).digest('hex');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`;
}
function sha1(seed) { return crypto.createHash('sha1').update(seed).digest('hex'); }

const userProfiles = new Map();
const roots = [];
function makeRoot(label) {
  const dir = path.join(os.tmpdir(), `conversation-store-test-${process.pid}-${Date.now()}-${label}-${Math.random().toString(36).slice(2, 8)}`);
  fs.mkdirSync(dir, { recursive: true });
  roots.push(dir);
  return dir;
}
function writeJson(file, value) { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify(value, null, 2), 'utf8'); }
function hasJsonl(claudeProjectsDir, id) {
  try { return fs.readdirSync(claudeProjectsDir).some(d => fs.existsSync(path.join(claudeProjectsDir, d, id + '.jsonl'))); }
  catch (e) { return false; }
}
function makeEnv(root, overrides) {
  const stateDir = path.join(root, 'state');
  const claudeProjectsDir = path.join(root, 'claude-home', '.claude', 'projects');
  fs.mkdirSync(stateDir, { recursive: true });
  fs.mkdirSync(claudeProjectsDir, { recursive: true });
  const projects = (overrides && overrides.projects) || [];
  const migrations = [];
  const logs = [];
  const store = createConversationStore(Object.assign({
    stateDir,
    claudeProjectsDir,
    resolveProfile: (profileValue, openId) => profileById(profileValue) || (userProfiles.get(openId) || CLAUDE),
    profileById,
    getUserProfile: openId => userProfiles.get(openId) || CLAUDE,
    profilesFor: () => PROFILES.slice(),
    discoverProjects: () => projects.slice(),
    onOdMigrate: (odTarget, chatId) => migrations.push({ odTarget, chatId }),
    log: msg => logs.push(String(msg)),
  }, overrides || {}));
  return { store, stateDir, claudeProjectsDir, projects, migrations, logs };
}

let failed = 0;
const check = (name, cond, detail) => {
  console.log((cond ? '  ✓ ' : '  ✗ ') + name + (cond ? '' : ' — ' + String(detail)));
  if (!cond) failed++;
};
const section = name => console.log('\n' + name);

function main() {
  section('A. stateDir / 必要依赖注入校验');
  {
    const root = makeRoot('inject');
    const stateDir = path.join(root, 'state');
    fs.mkdirSync(stateDir, { recursive: true });
    const validBase = {
      stateDir,
      claudeProjectsDir: path.join(root, 'claude-projects'),
      resolveProfile: () => CLAUDE,
      profileById: () => null,
      getUserProfile: () => CLAUDE,
      profilesFor: () => [],
      discoverProjects: () => [],
      onOdMigrate: () => {},
    };
    check('A1 缺 stateDir 抛错', (() => { try { createConversationStore({ claudeProjectsDir: 'x', onOdMigrate: () => {} }); return false; } catch (e) { return /stateDir/.test(e.message); } })());
    check('A2 缺 claudeProjectsDir 抛错', (() => { try { createConversationStore({ stateDir: 'x', onOdMigrate: () => {} }); return false; } catch (e) { return /claudeProjectsDir/.test(e.message); } })());
    let missing = [];
    for (const name of ['resolveProfile', 'profileById', 'getUserProfile', 'profilesFor', 'discoverProjects', 'onOdMigrate']) {
      const opts = Object.assign({}, validBase);
      delete opts[name];
      try { createConversationStore(opts); missing.push(name + ':no-throw'); }
      catch (e) { if (!new RegExp(name).test(e.message)) missing.push(name + ':wrong-msg'); }
    }
    check('A3 缺必要回调均抛错', missing.length === 0, missing.join(','));
    let ok = true;
    try { createConversationStore(Object.assign({}, validBase, { log: () => {} })); } catch (e) { ok = false; }
    check('A4 完整依赖可创建(log 可选)', ok);
  }

  section('B. od: 迁移与不覆盖已存在真实 chat session');
  {
    const root = makeRoot('od');
    const env = makeEnv(root);
    // legacy string session 也走迁移
    writeJson(path.join(env.stateDir, 'feishu-sessions.json'), { 'od:ou_a': 'C:\\Old\\Proj' });
    env.store.rememberUserChat('ou_a', 'oc_a');
    const s1 = JSON.parse(fs.readFileSync(path.join(env.stateDir, 'feishu-sessions.json'), 'utf8'));
    check('B1 od 会话迁移到真实 chat', s1['oc_a'] === 'C:\\Old\\Proj' && !('od:ou_a' in s1), JSON.stringify(s1));
    check('B2 legacy 迁移后 getSession 为 project', env.store.getSession('oc_a').mode === 'project' && env.store.getSession('oc_a').project === 'C:\\Old\\Proj');
    check('B3 onOdMigrate 回调收到 (od:<open_id>, chatId)', env.migrations.length === 1 && env.migrations[0].odTarget === 'od:ou_a' && env.migrations[0].chatId === 'oc_a', JSON.stringify(env.migrations));
    check('B4 userchats.json 已持久化', JSON.parse(fs.readFileSync(path.join(env.stateDir, 'feishu-userchats.json'), 'utf8'))['ou_a'] === 'oc_a');
    // 已存在真实 chat session 时不被 od 覆盖
    writeJson(path.join(env.stateDir, 'feishu-sessions.json'), {
      'od:ou_b': { mode: 'chat' },
      'oc_b': { mode: 'project', project: 'C:\\P', sub: 'query' },
    });
    env.store.rememberUserChat('ou_b', 'oc_b');
    const s2 = JSON.parse(fs.readFileSync(path.join(env.stateDir, 'feishu-sessions.json'), 'utf8'));
    check('B5 已存在真实 chat 会话不被覆盖', s2['oc_b'] && s2['oc_b'].mode === 'project' && !('od:ou_b' in s2), JSON.stringify(s2));
    check('B6 无 od 会话时 lastCard 迁移回调仍触发', env.migrations.length === 2 && env.migrations[1].odTarget === 'od:ou_b', JSON.stringify(env.migrations));
    // 重复 remember 不再触发迁移
    env.store.rememberUserChat('ou_a', 'oc_a');
    check('B7 相同映射重复调用不触发回调', env.migrations.length === 2, String(env.migrations.length));
  }

  section('C. userchat 持久化与 fallback');
  {
    const root = makeRoot('userchat');
    const env = makeEnv(root);
    env.store.rememberUserChat('ou_a', 'oc_a');
    const reloaded = makeEnv(root).store;
    check('C1 新 store 实例读到持久化映射', reloaded.userTarget('ou_a') === 'oc_a');
    check('C2 未知用户回退 od:<open_id>', env.store.userTarget('ou_unknown') === 'od:ou_unknown');
    check('C3 缺 open_id 返回 null', env.store.userTarget(null) === null && env.store.userTarget('') === null);
  }

  section('D. legacy / full session round-trip(含 work 字段)');
  {
    const root = makeRoot('session');
    const env = makeEnv(root);
    env.store.setSession('oc_legacy', 'C:\\Old\\Proj');
    const legacy = env.store.getSession('oc_legacy');
    check('D1 legacy string -> project 模式', legacy.mode === 'project' && legacy.project === 'C:\\Old\\Proj' && !('sub' in legacy), JSON.stringify(legacy));
    const full = { mode: 'project', project: 'C:\\P', sub: 'modify', work: 'uuid-1', workProfile: 'claude-default', workTitle: '标题' };
    env.store.setSession('oc_full', full);
    check('D2 full session round-trip(含 work 字段)', JSON.stringify(env.store.getSession('oc_full')) === JSON.stringify(full), JSON.stringify(env.store.getSession('oc_full')));
    env.store.setSession('oc_chat', { mode: 'chat' });
    check('D3 chat 模式 round-trip', env.store.getSession('oc_chat').mode === 'chat');
    check('D4 缺失 session 默认 idle', env.store.getSession('oc_missing').mode === 'idle');
    env.store.setSession('oc_empty', '');
    check('D5 空字符串 legacy 默认 idle', env.store.getSession('oc_empty').mode === 'idle');
    check('D6 会话写盘后可读', JSON.parse(fs.readFileSync(path.join(env.stateDir, 'feishu-sessions.json'), 'utf8'))['oc_full'].work === 'uuid-1');
    env.store.resetSessions();
    check('D7 resetSessions 清空为 idle', env.store.getSession('oc_full').mode === 'idle' && JSON.parse(fs.readFileSync(path.join(env.stateDir, 'feishu-sessions.json'), 'utf8'))['oc_full'] === undefined);
  }

  section('E. activeProject 命中 / 大小写 / 未知旧路径 fallback');
  {
    const root = makeRoot('active');
    const env = makeEnv(root, { projects: [{ name: 'Alpha', path: 'C:\\Repo\\Alpha' }, { name: 'Beta', path: 'C:\\Repo\\Beta' }] });
    env.store.setSession('oc1', { mode: 'project', project: 'c:\\repo\\ALPHA' });
    const hit = env.store.activeProject('oc1');
    check('E1 动态列表大小写不敏感命中', !!hit && hit.name === 'Alpha' && hit.path === 'C:\\Repo\\Alpha', JSON.stringify(hit));
    env.store.setSession('oc2', { mode: 'project', project: 'D:\\Gone\\old' });
    const fallback = env.store.activeProject('oc2');
    check('E2 旧路径不在动态列表 -> basename fallback', !!fallback && fallback.name === 'old' && fallback.path === 'D:\\Gone\\old', JSON.stringify(fallback));
    env.store.setSession('oc3', { mode: 'chat' });
    check('E3 chat 模式 activeProject 为 null', env.store.activeProject('oc3') === null);
    check('E4 idle 模式 activeProject 为 null', env.store.activeProject('oc_none') === null);
  }

  section('E2. activeProject 显式项目列表不调用注入 discoverProjects');
  {
    const root = makeRoot('active-explicit');
    let injected = 0;
    const projects = [{ name: 'Alpha', path: 'C:\\Repo\\Alpha' }, { name: 'Beta', path: 'C:\\Repo\\Beta' }];
    const env = makeEnv(root, {
      projects,
      discoverProjects: () => { injected++; return projects.slice(); },
    });
    env.store.setSession('oc_e5', { mode: 'project', project: 'c:\\repo\\ALPHA' });
    const hit = env.store.activeProject('oc_e5', projects);
    check('E5 显式列表大小写不敏感命中且不调用注入 discoverProjects', !!hit && hit.name === 'Alpha' && hit.path === 'C:\\Repo\\Alpha' && injected === 0, JSON.stringify(hit) + ' injected=' + injected);
    env.store.setSession('oc_e6', { mode: 'project', project: 'D:\\Gone\\old' });
    const fb = env.store.activeProject('oc_e6', []);
    check('E6 显式空列表未命中 -> fail-closed 且不调用注入', fb === null && injected === 0, JSON.stringify(fb) + ' injected=' + injected);
    env.store.setSession('oc_e7a', { mode: 'chat' });
    env.store.setSession('oc_e7b', { mode: 'idle' });
    check('E7 chat/idle 显式列表仍返回 null 且不调用注入', env.store.activeProject('oc_e7a', projects) === null && env.store.activeProject('oc_e7b', projects) === null && injected === 0);
    env.store.setSession('oc_e8', { mode: 'project', project: 'C:\\Repo\\Beta' });
    const diff = env.store.activeProject('oc_e8', [{ name: 'BetaX', path: 'C:\\Repo\\Beta' }]);
    check('E8 显式列表内容优先于注入列表(注入不被调用)', !!diff && diff.name === 'BetaX' && diff.path === 'C:\\Repo\\Beta' && injected === 0, JSON.stringify(diff) + ' injected=' + injected);
    env.store.setSession('oc_e9', { mode: 'project', project: 'C:\\Repo\\Alpha' });
    const noarg = env.store.activeProject('oc_e9');
    check('E9 无显式列表时保持注入 discoverProjects 语义', !!noarg && noarg.name === 'Alpha' && injected === 1, JSON.stringify(noarg) + ' injected=' + injected);
  }

  section('F. chat/query 按 user/profile/project 稳定隔离(seed/路径冻结)');
  {
    const root = makeRoot('iso');
    const env = makeEnv(root);
    const cs1 = env.store.chatSession('ou_a', 'claude-default');
    const cs1b = env.store.chatSession('ou_a', 'claude-default');
    check('F1 chat session 稳定(同 user+profile)', cs1.id === cs1b.id && cs1.cwd === cs1b.cwd && cs1.flag === cs1b.flag);
    const chatSeed = 'chat|ou_a|claude-default';
    check('F2 chat seed/id/cwd 与现役算法一致', cs1.id === stableId(chatSeed) && cs1.cwd === path.join(env.stateDir, 'feishu-chat', sha1(chatSeed)) && cs1.flag === path.join(cs1.cwd, '.started'), JSON.stringify(cs1));
    check('F3 chat 不同用户隔离', env.store.chatSession('ou_b', 'claude-default').id !== cs1.id);
    check('F4 chat 不同 profile 隔离', env.store.chatSession('ou_a', 'openai-sol').id !== cs1.id);
    const q1 = env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'claude-default');
    const q1b = env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'claude-default');
    check('F5 query session 稳定(同 project+user+profile)', q1.id === q1b.id && q1.cwd === q1b.cwd);
    const querySeed = 'c:\\repo\\alpha|ou_a|claude-default';
    check('F6 query seed/flag/cwd 与现役算法一致', q1.id === stableId(querySeed) && q1.flag === path.join(env.stateDir, 'feishu-query', sha1(querySeed) + '.started') && q1.cwd === path.join(env.stateDir, 'feishu-query-cwd', sha1(querySeed)), JSON.stringify(q1));
    check('F7 query 不同项目隔离', env.store.querySession('C:\\Repo\\Beta', 'ou_a', 'claude-default').id !== q1.id);
    check('F8 query 不同用户隔离', env.store.querySession('C:\\Repo\\Alpha', 'ou_b', 'claude-default').id !== q1.id);
    check('F9 query 不同 profile 隔离', env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'openai-sol').id !== q1.id);
    check('F10 chat 与 query 使用不同 seed(互不串会话)', cs1.id !== q1.id);
  }

  section('G. Codex/Claude 初始与 mark 后 sessionId');
  {
    const root = makeRoot('mark');
    const env = makeEnv(root);
    const claude = env.store.chatSession('ou_a', 'claude-default');
    const codex = env.store.chatSession('ou_a', 'openai-sol');
    check('G1 Claude 初始 sessionId = 确定性 id', claude.sessionId === claude.id && claude.started === false);
    check('G2 Codex 初始 sessionId = null', codex.sessionId === null && codex.started === false);
    env.store.markChatStarted('ou_a', 'claude-default', { sessionId: 'c-uuid-1' });
    const claudeAfter = env.store.chatSession('ou_a', 'claude-default');
    check('G3 Claude mark 后 sessionId 持久化', claudeAfter.sessionId === 'c-uuid-1' && claudeAfter.started === true && claudeAfter.meta.kind === 'chat' && claudeAfter.meta.openId === 'ou_a' && claudeAfter.meta.profileId === 'claude-default' && claudeAfter.meta.engine === 'claude' && !!claudeAfter.meta.updatedAt);
    env.store.markChatStarted('ou_a', 'openai-sol', { sessionId: 'x-uuid-1' });
    check('G4 Codex mark 后 sessionId 持久化', env.store.chatSession('ou_a', 'openai-sol').sessionId === 'x-uuid-1');
    const qClaude = env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'claude-default');
    const qCodex = env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'openai-sol');
    check('G5 query Claude 初始 sessionId = 确定性 id', qClaude.sessionId === qClaude.id && qClaude.started === false);
    check('G6 query Codex 初始 sessionId = null', qCodex.sessionId === null);
    env.store.markQueryStarted(qClaude.flag, { id: qClaude.id, sessionId: 'q-uuid-1', profileId: 'claude-default', engine: 'claude', openId: 'ou_a', path: 'C:\\Repo\\Alpha', name: 'Alpha' });
    const qClaudeAfter = env.store.querySession('C:\\Repo\\Alpha', 'ou_a', 'claude-default');
    check('G7 query mark 后 sessionId/started 生效', qClaudeAfter.sessionId === 'q-uuid-1' && qClaudeAfter.started === true && qClaudeAfter.meta.kind === 'query');
  }

  section('H. Claude JSONL + artifact clear,Codex 不误删');
  {
    const root = makeRoot('clear');
    const env = makeEnv(root);
    const proj = 'C:\\Repo\\Alpha';
    // 三个 profile 的 chat scratch 目录
    env.store.markChatStarted('ou_a', 'claude-default', { sessionId: 'c-1' });
    env.store.markChatStarted('ou_a', 'deepseek-v4', { sessionId: 'd-1' });
    env.store.markChatStarted('ou_a', 'openai-sol', { sessionId: 'x-1' });
    const folder = path.join(env.claudeProjectsDir, 'folder1');
    fs.mkdirSync(folder, { recursive: true });
    const claudeChat = env.store.chatSession('ou_a', 'claude-default');
    const deepseekChat = env.store.chatSession('ou_a', 'deepseek-v4');
    const codexChat = env.store.chatSession('ou_a', 'openai-sol');
    for (const s of [claudeChat, deepseekChat, codexChat]) {
      fs.writeFileSync(path.join(folder, s.id + '.jsonl'), 'x');
      if (s.profile.engine === 'claude') {
        fs.mkdirSync(path.join(folder, s.id), { recursive: true });
        fs.writeFileSync(path.join(folder, s.id, 'artifact.txt'), 'x');
      }
    }
    const deleted = env.store.clearChatSessions('ou_a');
    check('H1 clearChatSessions 删除全部 chat scratch 目录', deleted === 3, String(deleted));
    check('H2 Claude JSONL 被删', !hasJsonl(env.claudeProjectsDir, claudeChat.id));
    check('H3 Claude 同名 artifact 目录被删', !fs.existsSync(path.join(folder, claudeChat.id)));
    check('H4 DeepSeek(engine=claude)JSONL 被删', !hasJsonl(env.claudeProjectsDir, deepseekChat.id));
    check('H5 Codex JSONL 不误删', hasJsonl(env.claudeProjectsDir, codexChat.id));
    check('H6 Codex chat .started/cwd 已被清(仅 JSONL 保留)', !fs.existsSync(codexChat.flag) && !fs.existsSync(codexChat.cwd));
    // query 清理
    const qs = env.store.querySession(proj, 'ou_a', 'claude-default');
    const qsCodex = env.store.querySession(proj, 'ou_a', 'openai-sol');
    env.store.markQueryStarted(qs.flag, { id: qs.id, sessionId: 'q-1' });
    env.store.markQueryStarted(qsCodex.flag, { id: qsCodex.id, sessionId: 'qx-1' });
    for (const s of [qs, qsCodex]) {
      fs.writeFileSync(path.join(folder, s.id + '.jsonl'), 'x');
      if (s.profile.engine === 'claude') {
        fs.mkdirSync(path.join(folder, s.id), { recursive: true });
        fs.writeFileSync(path.join(folder, s.id, 'artifact.txt'), 'x');
      }
    }
    const qDeleted = env.store.clearQuerySession(proj, 'ou_a', 'claude-default');
    check('H7 clearQuerySession(claude)删除 1 个 jsonl', qDeleted === 1, String(qDeleted));
    check('H8 query Claude JSONL + artifact + flag 已清', !hasJsonl(env.claudeProjectsDir, qs.id) && !fs.existsSync(path.join(folder, qs.id)) && !fs.existsSync(qs.flag));
    const qCodexDeleted = env.store.clearQuerySession(proj, 'ou_a', 'openai-sol');
    check('H9 clearQuerySession(codex)只删 flag 不删 jsonl', qCodexDeleted === 0 && hasJsonl(env.claudeProjectsDir, qsCodex.id) && !fs.existsSync(qsCodex.flag), String(qCodexDeleted));
  }

  section('I. missing / malformed / 写失败容错');
  {
    const root = makeRoot('tolerance');
    const env = makeEnv(root);
    check('I1 全新目录 getSession 默认 idle', env.store.getSession('oc_x').mode === 'idle');
    check('I2 全新目录 userTarget 走 od: 回退', env.store.userTarget('ou_x') === 'od:ou_x');
    const cs = env.store.chatSession('ou_x', 'claude-default');
    check('I3 全新目录 chat started=false', cs.started === false && cs.sessionId === cs.id);
    // malformed sessions / userchats
    fs.writeFileSync(path.join(env.stateDir, 'feishu-sessions.json'), '{broken-json');
    const malformed = makeEnv(root);
    check('I4 malformed sessions -> idle 不崩溃', malformed.store.getSession('oc_x').mode === 'idle');
    fs.writeFileSync(path.join(env.stateDir, 'feishu-userchats.json'), 'not-json');
    const malformedUc = makeEnv(root);
    check('I5 malformed userchats -> od: 回退不崩溃', malformedUc.store.userTarget('ou_y') === 'od:ou_y');
    // malformed chat flag
    fs.mkdirSync(cs.cwd, { recursive: true });
    fs.writeFileSync(cs.flag, '{oops');
    const csBad = env.store.chatSession('ou_x', 'claude-default');
    check('I6 malformed .started -> meta={} 且 Claude 回退确定性 id', csBad.started === true && csBad.meta.kind === undefined && csBad.sessionId === csBad.id);
    // sessions 写失败(sessions 路径被目录占用)
    fs.unlinkSync(path.join(env.stateDir, 'feishu-sessions.json'));
    fs.mkdirSync(path.join(env.stateDir, 'feishu-sessions.json'));
    const logsBefore = env.logs.length;
    let threw = false;
    try { env.store.setSession('oc_w', { mode: 'chat' }); } catch (e) { threw = true; }
    check('I7 sessions 写失败不抛给调用方', !threw && env.logs.length > logsBefore, String(env.logs.slice(logsBefore)));
    fs.rmdirSync(path.join(env.stateDir, 'feishu-sessions.json'));
    // userchats 写失败
    fs.unlinkSync(path.join(env.stateDir, 'feishu-userchats.json'));
    fs.mkdirSync(path.join(env.stateDir, 'feishu-userchats.json'));
    const logsBefore2 = env.logs.length;
    threw = false;
    try { env.store.rememberUserChat('ou_w', 'oc_w'); } catch (e) { threw = true; }
    check('I8 userchats 写失败不抛且内存映射可用', !threw && env.store.userTarget('ou_w') === 'oc_w' && env.logs.length > logsBefore2);
    fs.rmdirSync(path.join(env.stateDir, 'feishu-userchats.json'));
    // 日志回调本身异常也不能把原有软 I/O 容错升级为调用方异常
    const throwingLogRoot = makeRoot('throwing-log');
    const throwingLogEnv = makeEnv(throwingLogRoot, { log: () => { throw new Error('log failed'); } });
    fs.mkdirSync(path.join(throwingLogEnv.stateDir, 'feishu-sessions.json'));
    threw = false;
    try { throwingLogEnv.store.setSession('oc_log', { mode: 'chat' }); } catch (e) { threw = true; }
    check('I9 写失败时日志回调抛错仍不向调用方传播', !threw);
    // querySessionExists:claude 需真实 jsonl;codex 恒 false
    const qs = env.store.querySession('C:\\Repo\\Alpha', 'ou_x', 'claude-default');
    check('I10 querySessionExists(claude)先 false', env.store.querySessionExists(qs.id, 'claude-default') === false);
    fs.mkdirSync(path.join(env.claudeProjectsDir, 'f2'), { recursive: true });
    fs.writeFileSync(path.join(env.claudeProjectsDir, 'f2', qs.id + '.jsonl'), 'x');
    check('I11 querySessionExists(claude)真实 jsonl 后 true', env.store.querySessionExists(qs.id, 'claude-default') === true);
    check('I12 querySessionExists(codex)恒 false', env.store.querySessionExists(qs.id, 'openai-sol') === false);
  }

  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exitCode = failed ? 1 : 0;
}

try { main(); }
catch (e) { console.error(e); process.exitCode = 1; }
finally {
  for (const root of roots) { try { fs.rmSync(root, { recursive: true, force: true }); } catch (e) {} }
}
process.once('exit', () => { for (const root of roots) { try { fs.rmSync(root, { recursive: true, force: true }); } catch (e) {} } });
