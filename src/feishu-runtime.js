/*
  feishu-agent.js  ---  AI Resume 双向飞书助手 (long-connection, no public IP needed)

  Receives messages from Feishu via the official SDK's WSClient (event subscription over a
  persistent WebSocket), routes each turn to the user's selected AI profile, and replies with
  the result. OpenAI resumes native Codex threads; Claude and DeepSeek resume Claude Code JSONL
  sessions. Chat/query scratch sessions are isolated from provider-native project work sessions.

  Config is read from %LOCALAPPDATA%\ClaudeResume\config.json:
    feishuAppId, feishuAppSecret   (required -- from the Feishu 自建应用)
    selected / customProjects      (project routing table, written by the GUI)
    feishuChatProfile / feishuUserProfiles  (default and per-user AI profile)
    openaiBaseUrl / openaiApiKey / deepseekApiKey  (local runtime secrets; never log them)
    feishuQueryTimeoutMinutes / feishuChatTimeoutMinutes  (read-only/chat total limits; modify has no total limit)
    feishuAuthOpenIds  (project-modification owners; empty means unlocked)
    feishuAllowOpenIds  (optional sender allowlist; empty = allow everyone in the chat)

  Feishu app permissions needed: im:message (send/receive) AND im:resource (upload images, so the
  bot can send screenshots/charts an AI run drops in <AppDir>\feishu-out\<hash>). Publish a version
  after adding im:resource, or image sending falls back to a text note naming the file.

  Commands (DM the bot, or @it in a group):
    帮助 / help                 -> usage
    状态 / status               -> armed state, engine phase, exact reset, recent log
    项目 / list                 -> known projects
    停止 [<项目>] / stop         -> cancel a running command (also a bottom-menu 🛑 button, event_key=stop)
    <项目名> <指令>             -> run <指令> in that project (prefix match on name)
    <指令>                      -> run in the default project (the single armed one, or feishuDefaultProject)
*/
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn, spawnSync } = require('child_process');
const {
  DEFAULT_PROFILE_ID, profileById, profileFromLegacyModel, profileLabel, profilesFor,
  getUserProfileId: storedUserProfileId, setUserProfile: storeUserProfile,
  parseProfileInput, fallbackProfiles,
} = require('./ai/profiles');
const { killTree, MAX_TIMEOUT_MS, DEFAULT_AI_TIMEOUT_MS } = require('./ai/runners');
const { createAgentAdapter } = require('./ai/agent-adapter');
const { createTaskOrchestrator } = require('./task-orchestrator');
const { createCodexSessions } = require('./ai/codex-sessions');
const { createSessionManager } = require('./session-manager');
const { probeProviders, providerConfigFingerprint } = require('./provider-health');
const { createAuthorizationPolicy } = require('./authorization-policy');
const completionEvents = require('./completion-events');
const { createCompletionEvents, stableMessageUuid } = completionEvents;
const { createConversationStore } = require('./conversation-store');
// S1-G:业务层不再直接依赖飞书 SDK,所有 Client/WSClient/EventDispatcher 与单次 API 超时/
// 重试都收敛到 ChannelAdapter;SDK 缺失时由 createChannelAdapter fail-fast。
const { createChannelAdapter } = require('./channel-adapter');

const TEST_MODE = !!process.env.FEISHU_TEST;   // offline unit tests: mock client, no WS, export handlers
const APP_DIR = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'ClaudeResume');

// ---- S1-C1 / D-004: FEISHU_TEST 配置与状态隔离 ----
// 显式环境契约:FEISHU_TEST 下必须同时显式提供 FEISHU_TEST_STATE_DIR 与
// FEISHU_TEST_CONFIG_PATH,agent 绝不自行创建默认测试根/marker/config。测试根必须是系统
// temp 的直接子目录、名称含当前 PID、已存在普通目录,并带 owner marker(PID+随机 nonce);
// 测试 config 必须等于 STATE_DIR\config.json、是已存在普通文件且 realpath 包含于 STATE_DIR。
// 缺任一 env、指向真实 config、STATE_DIR 外、缺失/损坏或任意 symlink/junction/reparse 均在
// 任何 mutable state 访问或退出清理钩子注册前 fail-closed。生产路径不变。
const TEST_MARKER_NAME = '.feishu-test-owner';
function testPidToken(name) {
  return new RegExp('(?:^|-)' + String(process.pid) + '(?:$|-)').test(String(name || ''));
}
function readTestMarker(root) {
  const markerPath = path.join(root, TEST_MARKER_NAME);
  let st;
  try { st = fs.lstatSync(markerPath); }
  catch (e) { if (e && e.code === 'ENOENT') return null; throw e; }
  if (st.isSymbolicLink() || !st.isFile()) throw new Error('[S1-C1] owner marker 必须是普通文件');
  let m = null;
  try { m = JSON.parse(fs.readFileSync(markerPath, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { throw new Error('[S1-C1] owner marker 损坏'); }
  if (!m || !Number.isInteger(Number(m.pid)) || typeof m.nonce !== 'string' || m.nonce.length < 8) throw new Error('[S1-C1] owner marker 格式非法');
  return m;
}
// 从卷根到目标逐组件 lstat + realpath,任一组件缺失、重定向或不是普通目录均 fail-closed。
function assertNoReparseDirectoryChain(p) {
  const abs = path.resolve(String(p));
  const parts = [];
  let cur = abs;
  for (;;) {
    const parent = path.dirname(cur);
    if (parent === cur) break;
    parts.unshift(cur);
    cur = parent;
  }
  const check = current => {
    let st;
    try { st = fs.lstatSync(current); }
    catch (e) { if (e && e.code === 'ENOENT') throw new Error('[S1-C1] 路径缺失: ' + current); throw e; }
    if (st.isSymbolicLink()) throw new Error('[S1-C1] 路径含 symlink/junction/reparse: ' + current);
    if (!st.isDirectory()) throw new Error('[S1-C1] 路径不是目录: ' + current);
    if (fs.realpathSync(current).toLowerCase() !== current.toLowerCase()) throw new Error('[S1-C1] 路径被重定向: ' + current);
  };
  check(cur);
  for (const part of parts) check(part);
  return abs;
}
function initTestEnv() {
  const stateEnv = process.env.FEISHU_TEST_STATE_DIR;
  const cfgEnv = process.env.FEISHU_TEST_CONFIG_PATH;
  if (!stateEnv || !cfgEnv) throw new Error('[S1-C1] FEISHU_TEST 必须同时显式提供 FEISHU_TEST_STATE_DIR 与 FEISHU_TEST_CONFIG_PATH');
  const root = path.resolve(stateEnv);
  if (path.dirname(root).toLowerCase() !== path.resolve(os.tmpdir()).toLowerCase()) throw new Error('[S1-C1] 测试根必须是系统 temp 的直接子目录');
  if (!/^claude-resume-(?:agent-)?test-/i.test(path.basename(root)) || !testPidToken(path.basename(root))) throw new Error('[S1-C1] 测试根名称必须包含当前 PID 且命名合法');
  assertNoReparseDirectoryChain(root);
  const marker = readTestMarker(root);
  if (!marker) throw new Error('[S1-C1] 测试根已存在但缺少 owner marker,拒绝使用');
  if (Number(marker.pid) !== process.pid) throw new Error('[S1-C1] owner marker PID 与当前进程不匹配');
  const configPath = path.resolve(cfgEnv);
  if (configPath.toLowerCase() !== path.join(root, 'config.json').toLowerCase()) throw new Error('[S1-C1] 测试 config 必须等于 STATE_DIR\\config.json');
  let st;
  try { st = fs.lstatSync(configPath); }
  catch (e) { if (e && e.code === 'ENOENT') throw new Error('[S1-C1] 测试 config.json 缺失'); throw e; }
  if (st.isSymbolicLink()) throw new Error('[S1-C1] 测试 config.json 是 reparse 点');
  if (!st.isFile()) throw new Error('[S1-C1] 测试 config.json 必须是普通文件');
  if (fs.realpathSync(configPath).toLowerCase() !== configPath.toLowerCase()) throw new Error('[S1-C1] 测试 config.json 指向 STATE_DIR 外');
  return { root, configPath, marker };
}
function findReparseInTree(root) {
  // 不依赖 Dirent:readdir 后对每个条目 lstat;Windows 上 junction/symlink 一律按 reparse 拒绝。
  const stack = [root];
  while (stack.length) {
    const dir = stack.pop();
    let names;
    try { names = fs.readdirSync(dir); }
    catch (e) { if (e && e.code === 'ENOENT') continue; throw e; }
    for (const name of names) {
      const full = path.join(dir, name);
      let entrySt;
      try { entrySt = fs.lstatSync(full); }
      catch (e) { throw new Error('[S1-C1] 无法 lstat 测试树条目,拒绝判定为安全: ' + full + ': ' + (e && e.message || e)); }
      if (entrySt.isSymbolicLink()) return full;
      if (entrySt.isDirectory()) stack.push(full);
    }
  }
  return null;
}
function assertTestConfigFile() {
  const abs = path.resolve(CONFIG_PATH);
  const state = path.resolve(STATE_DIR);
  assertNoReparseDirectoryChain(state);
  if (abs.toLowerCase() !== path.join(state, 'config.json').toLowerCase()) throw new Error('[S1-C1] 测试 config 必须位于 STATE_DIR 内');
  let st;
  try { st = fs.lstatSync(abs); }
  catch (e) { if (e && e.code === 'ENOENT') throw new Error('[S1-C1] 测试 config.json 缺失'); throw e; }
  if (st.isSymbolicLink()) throw new Error('[S1-C1] 测试 config.json 是 reparse 点');
  if (!st.isFile()) throw new Error('[S1-C1] 测试 config.json 必须是普通文件');
  if (fs.realpathSync(abs).toLowerCase() !== abs.toLowerCase()) throw new Error('[S1-C1] 测试 config.json 指向 STATE_DIR 外');
}
const testEnv = TEST_MODE ? initTestEnv() : null;
const STATE_DIR = TEST_MODE ? testEnv.root : APP_DIR;
const CONFIG_PATH = TEST_MODE ? testEnv.configPath : path.join(APP_DIR, 'config.json');
const CONFIG_LOCK_PATH = CONFIG_PATH + '.write.lock';
// 测试模式下 Claude home/.claude/projects 整体重定向到测试根,绝不触碰真实 Claude home。
const CLAUDE_PROJECTS_DIR = path.join(TEST_MODE ? path.join(STATE_DIR, 'claude-home') : os.homedir(), '.claude', 'projects');
const LOG_DIR = path.join(STATE_DIR, 'logs');
const COMPLETION_QUEUE_DIR = path.join(STATE_DIR, 'completion-events');
const COMPLETION_SEEN_PATH = path.join(STATE_DIR, 'completion-events-seen.json');
const running = new Map(); // runKey(lower) -> active provider child
const CHILD_REGISTRY_PATH = path.join(STATE_DIR, 'feishu-ai-children.json');
const registeredChildren = new Map(); // pid -> crash-recovery metadata
// 缺失/非法 PID 的旧登记不能进入 pid-keyed Map，但仍是必须持久化并阻断运行的真身。
// 单独保留，所有 registry 写入都与 registeredChildren 合并。
const unkeyedRegisteredChildren = [];
const orphanPlaceholders = new Map(); // runKey -> fail-closed placeholder in running
let legacyOrphanBlock = false;
let childRegistryCorrupt = false;
let registryPersistRetryTimer = null;
let shuttingDown = false;

if (TEST_MODE) {
  // 测试 config 缺失/损坏/reparse/指向 STATE_DIR 外已在 initTestEnv 校验;此处解析校验 JSON。
  try { readJson(CONFIG_PATH); }
  catch (e) { throw new Error('[S1-C1] 测试 config.json 损坏: ' + (e && e.message || e)); }
  // Claude Code 配置目录重定向;不再从真实 AppDir 复制 sessions/userchats 等 mutable state。
  process.env.CLAUDE_CONFIG_DIR = path.join(STATE_DIR, 'claude-home', '.claude');
  // 退出清理同样受控:记录加载时 marker 原 nonce,仅当路径仍合法、realpath 一致、
  // marker PID+原 nonce 精确匹配、树内无 reparse 时才递归删除;否则宁可残留。
  process.once('exit', () => {
    try {
      assertNoReparseDirectoryChain(testEnv.root);
      const marker = readTestMarker(testEnv.root);
      if (!marker || Number(marker.pid) !== process.pid || marker.nonce !== testEnv.marker.nonce) return;
      if (fs.realpathSync(testEnv.root).toLowerCase() !== testEnv.root.toLowerCase()) return;
      if (findReparseInTree(testEnv.root)) return;
      fs.rmSync(testEnv.root, { recursive: true, force: true });
    } catch (e) {}
  });
}

function readJson(p) {
  // strip a UTF-8 BOM: PowerShell may write config/state with one, which JSON.parse rejects
  return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, ''));
}
let lastGoodConfig = null;
let testConfigReadFailure = false;
let testChildRegistryWriteFailure = false;   // 窄测试注入:仅 FEISHU_TEST 模式生效,不改变生产行为。
let testChildRegistryWriteFailureAfterTmp = false;   // 同上,但在临时文件已创建后才失败,覆盖 tmp 残留清理路径。
function setChildRegistryWriteFailureForTest(value, afterTmp) {
  testChildRegistryWriteFailure = !!value && !afterTmp;
  testChildRegistryWriteFailureAfterTmp = !!value && !!afterTmp;
}
function loadConfig() {
  if (TEST_MODE) assertTestConfigFile();
  if (TEST_MODE && testConfigReadFailure) throw new Error('mock config read failure');
  const cfg = readJson(CONFIG_PATH);
  if (!cfg || typeof cfg !== 'object' || Array.isArray(cfg)) throw new Error('config.json root must be an object');
  lastGoodConfig = cfg;
  return cfg;
}
function readConfig() {
  try { return loadConfig(); }
  catch (e) { return lastGoodConfig || {}; }
}
function readConfigForAccess() {
  try { return { ok: true, config: loadConfig(), error: null }; }
  catch (error) { return { ok: false, config: lastGoodConfig || {}, error }; }
}
// Stage 1 S1-A:纯授权决策统一入口。读取失败(抛错/缺失)时直接向 policy 抛错,由 policy
// fail-closed(none / 拒绝),绝不把 lastGoodConfig 伪装成有效配置继续授权。
const authorizationPolicy = createAuthorizationPolicy({
  readConfig() {
    const access = readConfigForAccess();
    if (!access.ok) throw access.error || new Error('config.json 不可读取');
    return access.config;
  },
});
function sleepSync(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}
function acquireConfigWriteLock(timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const fd = fs.openSync(CONFIG_LOCK_PATH, 'wx');
      fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, createdUtc: new Date().toISOString() }), 'utf8');
      fs.fsyncSync(fd);
      return () => {
        try { fs.closeSync(fd); } catch (e) {}
        try { fs.unlinkSync(CONFIG_LOCK_PATH); } catch (e) {}
      };
    } catch (e) {
      if (e && e.code !== 'EEXIST' && e.code !== 'EACCES' && e.code !== 'EPERM') throw e;
      try {
        const age = Date.now() - fs.statSync(CONFIG_LOCK_PATH).mtimeMs;
        if (age > 30000) {
          const staleFd = fs.openSync(CONFIG_LOCK_PATH, 'r+');
          fs.closeSync(staleFd);
          fs.unlinkSync(CONFIG_LOCK_PATH);
          continue;
        }
      } catch (ignore) {}
      sleepSync(20);
    }
  }
  throw new Error('config write lock timeout');
}
function writeConfigAtomic(cfg) {
  const tmp = `${CONFIG_PATH}.tmp-${process.pid}-${Date.now()}`;
  let fd;
  try {
    fd = fs.openSync(tmp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(cfg, null, 4), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    fs.renameSync(tmp, CONFIG_PATH);
  } finally {
    if (fd != null) try { fs.closeSync(fd); } catch (e) {}
    try { fs.unlinkSync(tmp); } catch (e) {}
  }
}
function writeJsonAtomicPath(target, value) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const tmp = `${target}.tmp-${process.pid}-${Date.now()}-${crypto.randomBytes(4).toString('hex')}`;
  let fd;
  try {
    fd = fs.openSync(tmp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(value, null, 2), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    try { fs.renameSync(tmp, target); }
    catch (e) { fs.rmSync(target, { force: true }); fs.renameSync(tmp, target); }
  } finally {
    if (fd != null) try { fs.closeSync(fd); } catch (e) {}
    try { fs.unlinkSync(tmp); } catch (e) {}
  }
}
function updateConfig(mutator) {
  if (TEST_MODE) assertTestConfigFile();
  const release = acquireConfigWriteLock();
  try {
    const cfg = readJson(CONFIG_PATH);
    const result = mutator(cfg);
    if (result === false) return false;
    writeConfigAtomic(cfg);
    return result === undefined ? true : result;
  } finally { release(); }
}
function logLine(msg) {
  const d = new Date(), p = n => String(n).padStart(2, '0');   // LOCAL time (was UTC via toISOString)
  const day = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`;
  const ts = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  const line = `[${ts}] ${msg}\r\n`;
  try { fs.mkdirSync(LOG_DIR, { recursive: true }); fs.appendFileSync(path.join(LOG_DIR, 'feishu-' + day + '.log'), line, 'utf8'); } catch (e) {}
  process.stdout.write(line);
}

function terminateRunningChildren(reason) {
  // D-001:只终止当前 agent 真实持有的非 orphan child;orphan placeholder 不得交给 killTree,
  // 也不得从 running/orphan lock 中清除(登记文件是真身,由下次启动/定时重试继续三态核验)。
  const children = new Set();
  shuttingDown = true;
  for (const [key, child] of running) {
    if (child && child.orphan === true) continue;
    children.add(child);
  }
  for (const child of children) killTree(child);
  if (reason && children.size) logLine(`终止运行中的 AI 子进程(${reason}):${children.size}`);
  return children.size;
}

function registryCandidates(registryPath) {
  const dir = path.dirname(registryPath), base = path.basename(registryPath);
  const out = [registryPath, registryPath + '.bak'];
  try {
    for (const name of fs.readdirSync(dir)) if (name.startsWith(base + '.tmp-')) out.push(path.join(dir, name));
  } catch (e) {}
  return out;
}

function childRegistryMainIdentity(registryPath) {
  try {
    const stat = fs.lstatSync(registryPath);
    if (stat.isSymbolicLink() || !stat.isFile()) return { state: 'invalid' };
    const bytes = fs.readFileSync(registryPath);
    return { state: 'file', sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
  } catch (e) {
    if (e && e.code === 'ENOENT') return { state: 'absent' };
    return { state: 'unverifiable' };
  }
}

function sameChildRegistryIdentity(left, right) {
  if (!left || !right || left.state !== right.state) return false;
  if (left.state === 'absent') return true;
  return left.state === 'file' && left.sha256 === right.sha256;
}

function readChildRegistry(registryPath) {
  const records = [];
  let sawFile = false;
  for (const candidate of registryCandidates(registryPath)) {
    const kind = candidate === registryPath
      ? 'main'
      : candidate === registryPath + '.bak' ? 'backup' : 'generation';
    let record = { path: candidate, kind, valid: false, mtimeMs: Infinity, score: -Infinity, data: null };
    try {
      const stat = fs.lstatSync(candidate);
      sawFile = true;
      record.mtimeMs = stat.mtimeMs;
      if (stat.isSymbolicLink() || !stat.isFile()) {
        records.push(record);
        continue;
      }
      const data = JSON.parse(fs.readFileSync(candidate, 'utf8').replace(/^\uFEFF/, ''));
      const rawChildren = data && Array.isArray(data.children) ? data.children : null;
      if (rawChildren && !rawChildren.some(raw => !raw || typeof raw !== 'object' || Array.isArray(raw))) {
        record = Object.assign(record, {
          valid: true,
          data,
          score: Date.parse(String(data.updatedAt || '')) || record.mtimeMs,
        });
      }
    } catch (e) {
      if (e && e.code === 'ENOENT') continue;
      sawFile = true;
    }
    records.push(record);
  }

  const main = records.find(record => record.kind === 'main');
  const backup = records.find(record => record.kind === 'backup');
  const generations = records.filter(record => record.kind === 'generation');
  const invalid = records.filter(record => !record.valid && record.kind !== 'backup');
  const invalidFrontier = invalid.reduce((latest, record) => Math.max(latest, record.mtimeMs), -Infinity);
  let eligible;
  if (main) {
    // D-001:A:主登记存在但损坏时,备份无论多新都不能替代主真身。只有一个完整 generation
    // 同时严格晚于损坏主登记和所有损坏 generation,才足以证明它是崩溃前发布的新代次。
    eligible = main.valid ? [main, ...generations.filter(record => record.valid)] : generations.filter(record => record.valid);
  } else {
    // 主登记缺失符合 Windows 替换窗口；此时才允许最后一次已提交备份参与恢复。
    eligible = [...generations.filter(record => record.valid), ...(backup && backup.valid ? [backup] : [])];
  }
  if (invalid.length) eligible = eligible.filter(record => record.mtimeMs > invalidFrontier);
  eligible.sort((a, b) => b.mtimeMs - a.mtimeMs || b.score - a.score);
  const best = eligible[0] || null;
  if (!best) {
    return { entries: [], valid: 0, sawFile, corrupt: true, mainIdentity: childRegistryMainIdentity(registryPath) };
  }
  // D-001:A:所有 child(含 pid/agentPid/startedAt/provider 缺失或非法)都必须进入三态处理,
  // 缺失/非法 => unverifiable,由 reap 保留登记与 runKey/legacy 锁,绝不在此过滤掉。
  const entries = [];
  for (const raw of best.data.children) {
    entries.push(Object.assign({}, raw, { agentPid: Number(raw.agentPid || best.data.agentPid) || 0 }));
  }
  return { entries, valid: eligible.length, sawFile, corrupt: false, mainIdentity: childRegistryMainIdentity(registryPath) };
}

function writeChildRegistry(entries, registryPath = CHILD_REGISTRY_PATH, expectedMainIdentity = null) {
  // 窄测试注入:仅 FEISHU_TEST 模式生效,确定性模拟落盘失败且不触碰文件系统。
  if (TEST_MODE && testChildRegistryWriteFailure) return false;
  if (expectedMainIdentity && !sameChildRegistryIdentity(expectedMainIdentity, childRegistryMainIdentity(registryPath))) {
    logLine('AI 子进程登记在写回前发生代次变化,已拒绝覆盖并等待下次重新核验。');
    return false;
  }
  const values = Array.from(entries || []);
  const candidates = registryCandidates(registryPath);
  if (!values.length) {
    let removed = true;
    for (const candidate of candidates) {
      try { fs.rmSync(candidate, { force: true }); }
      catch (e) { removed = false; }
    }
    return removed;
  }
  const tmp = `${registryPath}.tmp-${process.pid}-${Date.now()}`;
  const data = JSON.stringify({
    agentPid: process.pid,
    updatedAt: new Date().toISOString(),
    children: values,
  });
  let fd;
  try {
    fs.mkdirSync(path.dirname(registryPath), { recursive: true });
    fd = fs.openSync(tmp, 'w');
    fs.writeFileSync(fd, data, 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    if (TEST_MODE && testChildRegistryWriteFailureAfterTmp) {
      const injected = new Error('test-injected-after-tmp');
      injected.code = 'ETESTINJ';
      throw injected;
    }
    try {
      if (fs.existsSync(registryPath)) {
        JSON.parse(fs.readFileSync(registryPath, 'utf8').replace(/^\uFEFF/, ''));
        fs.copyFileSync(registryPath, registryPath + '.bak');
      }
    } catch (e) {}
    try { fs.renameSync(tmp, registryPath); }
    catch (e) { fs.rmSync(registryPath, { force: true }); fs.renameSync(tmp, registryPath); }
    for (const candidate of registryCandidates(registryPath)) {
      if (candidate !== registryPath && candidate !== registryPath + '.bak') try { fs.rmSync(candidate, { force: true }); } catch (e) {}
    }
    return true;
  } catch (e) {
    try { if (fd !== undefined && fd !== null) fs.closeSync(fd); } catch (e2) {}
    // 只清理本次自建的临时文件:残留的更新损坏 tmp 会把完好主登记升级为损坏前沿并永久锁存。
    try { fs.rmSync(tmp, { force: true }); } catch (e2) {}
    logLine(`AI 子进程登记写入失败:${e.code || e.message || 'unknown'}`);
    return false;
  }
}

function persistChildRegistry() {
  if (childRegistryCorrupt) return false;
  const current = readChildRegistry(CHILD_REGISTRY_PATH);
  if (current.sawFile && (!current.valid || current.corrupt)) {
    childRegistryCorrupt = true;
    logLine('AI 子进程登记在写入前校验失败,已锁定 AI 启动并保留原文件等待人工检查。');
    return false;
  }
  return writeChildRegistry([
    ...registeredChildren.values(),
    ...unkeyedRegisteredChildren,
  ], CHILD_REGISTRY_PATH, current.mainIdentity);
}

function scheduleRegistryPersistRetry() {
  if (childRegistryCorrupt || registryPersistRetryTimer) return;
  registryPersistRetryTimer = setTimeout(() => {
    registryPersistRetryTimer = null;
    if (persistChildRegistry()) logLine('AI 子进程登记重试写入成功。');
    else scheduleRegistryPersistRetry();
  }, 15000);
  if (registryPersistRetryTimer.unref) registryPersistRetryTimer.unref();
}

function registerAIChild(child, meta) {
  if (childRegistryCorrupt) return false;
  const pid = Number(child && child.pid);
  if (!Number.isInteger(pid) || pid <= 0) return;
  registeredChildren.set(pid, {
    pid,
    startedAt: Date.now(),
    agentPid: process.pid,
    runKey: String(meta && meta.runKey || '').toLowerCase(),
    taskKind: String(meta && meta.taskKind || ''),
    cwd: String(meta && meta.cwd || ''),
    provider: String(meta && meta.provider || ''),
    profileId: String(meta && meta.profileId || ''),
  });
  if (persistChildRegistry()) return true;
  scheduleRegistryPersistRetry();
  return false;
}

function unregisterAIChild(child) {
  const pid = Number(child && child.pid);
  if (Number.isInteger(pid) && registeredChildren.delete(pid) && !childRegistryCorrupt) persistChildRegistry();
}

function inspectWindowsProcess(pid) {
  if (process.platform !== 'win32') return { state: 'failed', reason: 'unsupported' };
  const script = [
    `$p=Get-CimInstance Win32_Process -Filter \"ProcessId = ${pid}\" -ErrorAction SilentlyContinue`,
    'if($p){[pscustomobject]@{ProcessId=[int]$p.ProcessId;ParentProcessId=[int]$p.ParentProcessId;Name=[string]$p.Name;CommandLine=[string]$p.CommandLine;CreationDate=$p.CreationDate.ToUniversalTime().ToString("o")}|ConvertTo-Json -Compress}',
  ].join(';');
  try {
    const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
      windowsHide: true, encoding: 'utf8', timeout: 5000,
    });
    if (result.error || result.status !== 0) return { state: 'failed', reason: result.error && result.error.code || `status-${result.status}` };
    const text = String(result.stdout || '').trim();
    return text ? { state: 'found', process: JSON.parse(text) } : { state: 'gone' };
  } catch (e) { return { state: 'failed', reason: e.code || e.message || 'inspect-error' }; }
}

// D-001:孤儿 AI 进程身份三态分类(纯函数,无 I/O、不依赖平台)。
//   matched       = 全部必要元数据可核验且 PID/父 PID/5 秒启动时间窗口/provider 命令签名均匹配,
//                   同时不是 feishu-agent 自身 —— 唯一允许 killProcessTree 的状态。
//   mismatched    = 元数据完整且能肯定证明至少一个身份维度与登记不同(PID 复用/父 PID 不符/
//                   启动时间超窗/命令签名不符/是 feishu-agent 自身)—— 移除陈旧登记与锁,
//                   但绝不终止该进程。
//   unverifiable  = 任何必要字段缺失/非法/无法解析、provider 未知或监控结果不可信 —— fail-closed
//                   保留登记与 runKey/legacy 锁,不 kill,等待后台重试。
// reason 只使用稳定短码,不含 PID、路径、命令行等敏感信息。
const PROVIDER_COMMAND = { openai: 'codex', claude: 'claude', deepseek: 'claude' };

function classifyRegisteredAIProcess(info, entry) {
  const unverifiable = reason => ({ verdict: 'unverifiable', reason });
  const pid = Number(entry && entry.pid);
  const agentPid = Number(entry && entry.agentPid);
  const startedAt = Number(entry && entry.startedAt);
  const provider = String(entry && entry.provider || '');
  if (!Number.isInteger(pid) || pid <= 0 || !Number.isInteger(agentPid) || agentPid <= 0
      || !Number.isFinite(startedAt) || startedAt <= 0) return unverifiable('registry-metadata-invalid');
  const expectedCommand = PROVIDER_COMMAND[provider];
  if (!expectedCommand) return unverifiable('provider-unknown');
  if (!info || typeof info !== 'object') return unverifiable('process-info-missing');
  const infoPid = Number(info.ProcessId);
  const infoParentPid = Number(info.ParentProcessId);
  const name = typeof info.Name === 'string' ? info.Name : '';
  const commandLine = typeof info.CommandLine === 'string' ? info.CommandLine : '';
  const createdAt = Date.parse(String(info.CreationDate || ''));
  if (!Number.isInteger(infoPid) || infoPid <= 0 || !Number.isInteger(infoParentPid) || infoParentPid <= 0
      || !name || !commandLine || !Number.isFinite(createdAt)) return unverifiable('process-info-incomplete');
  if (infoPid !== pid) return { verdict: 'mismatched', reason: 'pid-reused' };
  if (infoParentPid !== agentPid) return { verdict: 'mismatched', reason: 'parent-mismatch' };
  if (Math.abs(createdAt - startedAt) > 5000) return { verdict: 'mismatched', reason: 'start-time-mismatch' };
  const signature = `${name} ${commandLine}`;
  const commandPattern = new RegExp(`(?:^|[\\\\\\/\\s\"])(?:${expectedCommand})(?:\\.exe|\\.cmd)?(?:[\\\\\\/\\s\"]|$)`, 'i');
  if (!commandPattern.test(signature)) return { verdict: 'mismatched', reason: 'command-signature-mismatch' };
  if (/feishu-agent\.js/i.test(signature)) return { verdict: 'mismatched', reason: 'agent-self' };
  return { verdict: 'matched', reason: 'matched' };
}

// 布尔兼容面:内部必须复用三态分类。
function isRegisteredAIProcess(info, entry) {
  return classifyRegisteredAIProcess(info, entry).verdict === 'matched';
}

function syncOrphanLocks(entries) {
  for (const [key, placeholder] of orphanPlaceholders) {
    if (running.get(key) === placeholder) running.delete(key);
  }
  orphanPlaceholders.clear();
  legacyOrphanBlock = false;
  for (const entry of entries || []) {
    const key = String(entry && entry.runKey || '').toLowerCase();
    if (!key) { legacyOrphanBlock = true; continue; }
    const placeholder = { pid: Number(entry.pid), orphan: true, runKey: key };
    if (!running.has(key)) running.set(key, placeholder);
    if (running.get(key) === placeholder) orphanPlaceholders.set(key, placeholder);
  }
}

function orphanBlocksRun(runKey, taskKind) {
  if (childRegistryCorrupt) return true;
  if (legacyOrphanBlock && taskKind === 'modify') return true;
  return orphanPlaceholders.has(String(runKey || '').toLowerCase());
}

function reapOrphanedAIChildren(options = {}) {
  const registryPath = options.registryPath || CHILD_REGISTRY_PATH;
  const inspectProcess = options.inspectProcess || inspectWindowsProcess;
  const killProcessTree = options.killProcessTree || (pid => {
    if (process.platform === 'win32') {
      const result = spawnSync('taskkill.exe', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 10000 });
      return !result.error && result.status === 0;
    }
    return false;
  });
  const loaded = readChildRegistry(registryPath);
  if (!loaded.valid || loaded.corrupt) {
    if (loaded.sawFile) {
      if (registryPath === CHILD_REGISTRY_PATH) childRegistryCorrupt = true;
      logLine('AI 子进程登记损坏,已锁定 AI 启动并保留原文件等待人工检查。');
    }
    return 0;
  }
  let killed = 0;
  const onlyOrphans = options.onlyOrphans === true;
  const remaining = onlyOrphans ? loaded.entries.filter(entry => !entry.orphan) : [];
  const attempts = onlyOrphans ? loaded.entries.filter(entry => entry.orphan) : loaded.entries;
  for (const entry of attempts) {
    const pid = Number(entry && entry.pid);
    // D-001:A:pid 缺失/非法 = unverifiable——保留原登记(可安全增加 orphan:true)与
    // runKey/legacy 锁,绝不 inspect 非法 PID,绝不 kill。
    if (!Number.isInteger(pid) || pid <= 0) {
      remaining.push(Object.assign({}, entry, { orphan: true }));
      continue;
    }
    let inspected;
    try { inspected = inspectProcess(pid); }
    catch (e) { remaining.push(Object.assign({}, entry, { orphan: true })); continue; }  // 监控异常 = unverifiable
    const state = inspected && inspected.state ? inspected.state : inspected ? 'found' : 'failed';
    if (state === 'gone') continue;   // 明确 gone:移除
    if (state !== 'found') { remaining.push(Object.assign({}, entry, { orphan: true })); continue; }
    const verdict = classifyRegisteredAIProcess(inspected.process || inspected, entry).verdict;
    if (verdict === 'unverifiable') { remaining.push(Object.assign({}, entry, { orphan: true })); continue; }
    if (verdict === 'mismatched') continue;   // 移除陈旧登记/锁,绝不 kill
    // 只有 matched 才调用 killProcessTree。
    let stopped = false;
    try { stopped = killProcessTree(pid) === true; } catch (e) {}
    if (!stopped) {
      // kill 失败必须再次 inspect:仅 gone 可确认结束并移除;mismatched 可移除但不计 killed;
      // failed/unverifiable/仍 matched 均保留。
      let after;
      try { after = inspectProcess(pid); }
      catch (e) { remaining.push(Object.assign({}, entry, { orphan: true })); continue; }
      const afterState = after && after.state ? after.state : after ? 'found' : 'failed';
      if (afterState === 'gone') { killed++; continue; }
      if (afterState === 'found' && classifyRegisteredAIProcess(after.process || after, entry).verdict === 'mismatched') continue;
      remaining.push(Object.assign({}, entry, { orphan: true }));
      continue;
    }
    killed++;
  }
  const persisted = writeChildRegistry(remaining, registryPath, loaded.mainIdentity);
  if (registryPath !== CHILD_REGISTRY_PATH) return killed;
  if (!persisted) {
    // D-001:B:写盘失败时不得按未落盘的 remaining 更新内存;从本次已加载的旧 entries 恢复
    // 内存 registeredChildren 与孤儿锁,本次尝试过的登记一律按 orphan fail-closed 保留,
    // 不得短暂放行 runKey 或 legacy modify。删除结果(remaining)绝不重试写盘,旧盘真身由
    // 下一次 reaper 重新核验;只通过既有持久化重试把 fail-closed 保留标记落盘。
    restoreRegistryAfterFailedWrite(loaded.entries, attempts);
    return killed;
  }
  if (!onlyOrphans) registeredChildren.clear();
  else for (const [pid, entry] of registeredChildren) if (entry.orphan) registeredChildren.delete(pid);
  // 无合法 PID 的登记只来自本次加载的磁盘真身；成功写盘后 remaining 是其完整新真身。
  unkeyedRegisteredChildren.length = 0;
  for (const entry of remaining) {
    const pid = Number(entry && entry.pid);
    if (Number.isInteger(pid) && pid > 0) registeredChildren.set(pid, entry);
    else unkeyedRegisteredChildren.push(entry);
  }
  syncOrphanLocks(remaining.filter(entry => entry.orphan));
  return killed;
}

// D-001:B:登记写盘失败后的内存恢复(fail-closed)。
// - registeredChildren 恢复为本次加载的旧盘真身;本次尝试过的登记按 orphan 保留,未尝试且
//   不在盘上的新登记(落盘失败尚未持久化的活跃子进程)不得被旧盘回滚吞掉。
// - orphanPlaceholders/legacy 锁按本次尝试过的登记全部视作 orphan 重建,绝不短暂放行。
// - 只复用既有持久化重试把 fail-closed 保留标记落盘;本次的删除结果不调度重试写盘。
function restoreRegistryAfterFailedWrite(loadedEntries, attempts) {
  const attemptedEntries = new Set(attempts);
  const loadedPids = new Set();
  const loadedByPid = new Map();
  const loadedUnkeyed = [];
  for (const entry of loadedEntries) {
    const pid = Number(entry && entry.pid);
    if (!Number.isInteger(pid) || pid <= 0) {
      loadedUnkeyed.push(attemptedEntries.has(entry) ? Object.assign({}, entry, { orphan: true }) : entry);
      continue;
    }
    loadedPids.add(pid);
    loadedByPid.set(pid, entry);
  }
  const attemptedPids = new Set();
  for (const entry of attempts) {
    const pid = Number(entry && entry.pid);
    if (Number.isInteger(pid) && pid > 0) attemptedPids.add(pid);
  }
  const newer = [];
  for (const [pid, entry] of registeredChildren) {
    if (!loadedPids.has(pid) && !attemptedPids.has(pid)) newer.push([pid, entry]);
  }
  registeredChildren.clear();
  for (const [pid, entry] of loadedByPid) {
    registeredChildren.set(pid, attemptedPids.has(pid) ? Object.assign({}, entry, { orphan: true }) : entry);
  }
  for (const [pid, entry] of newer) registeredChildren.set(pid, entry);
  unkeyedRegisteredChildren.splice(0, unkeyedRegisteredChildren.length, ...loadedUnkeyed);
  syncOrphanLocks(attempts.map(entry => Object.assign({}, entry, { orphan: true })));
  logLine('AI 子进程登记写盘失败,已保留旧登记与任务锁,等待持久化重试。');
  scheduleRegistryPersistRetry();
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
  const reaped = reapOrphanedAIChildren();
  if (reaped) logLine(`清理上次异常退出遗留的 AI 子进程:${reaped}`);
  if (orphanPlaceholders.size || legacyOrphanBlock) {
    logLine(`遗留 AI 子进程尚未确认结束,已锁定相关任务并启用定时重试:已知任务=${orphanPlaceholders.size} 旧格式全局修改锁=${legacyOrphanBlock}`);
    const retryOrphans = () => {
      const before = orphanPlaceholders.size + (legacyOrphanBlock ? 1 : 0);
      const cleaned = reapOrphanedAIChildren({ onlyOrphans: true });
      const after = orphanPlaceholders.size + (legacyOrphanBlock ? 1 : 0);
      if (cleaned || after !== before) logLine(`遗留 AI 子进程重试:清理=${cleaned} 待确认=${after}`);
    };
    const firstRetry = setTimeout(() => {
      retryOrphans();
      const interval = setInterval(retryOrphans, 60000);
      if (interval.unref) interval.unref();
    }, 30000);
    if (firstRetry.unref) firstRetry.unref();
  }
  try { fs.mkdirSync(APP_DIR, { recursive: true }); fs.writeFileSync(PID_PATH, String(process.pid)); } catch (e) {}
  process.on('exit', () => {
    terminateRunningChildren();
    try { if (parseInt(fs.readFileSync(PID_PATH, 'utf8'), 10) === process.pid) fs.unlinkSync(PID_PATH); } catch (e) {}
  });
  let shutdownStarted = false;
  const shutdown = signal => {
    if (shutdownStarted) return;
    shutdownStarted = true;
    terminateRunningChildren(signal);
    setTimeout(() => process.exit(0), 500);
  };
  process.on('SIGTERM', () => shutdown('SIGTERM'));
  process.on('SIGINT', () => shutdown('SIGINT'));
}

const cfg0 = readConfig();
const APP_ID = cfg0.feishuAppId || '';
const APP_SECRET = cfg0.feishuAppSecret || '';
if (!TEST_MODE && (!APP_ID || !APP_SECRET)) { logLine('config.json 缺少 feishuAppId / feishuAppSecret,退出。'); process.exit(1); }

// S1-G:Feishu SDK 只经 ChannelAdapter 访问。TEST_MODE 下工厂返回录制式 mock client
// (仍通过 module.exports.client 暴露 __calls/__reset/__setBehavior,保持测试兼容);
// 生产模式下 SDK 缺失或构造失败在此 fail-fast 退出。
let channel;
try {
  channel = createChannelAdapter({ testMode: TEST_MODE, appId: APP_ID, appSecret: APP_SECRET, onLog: logLine });
} catch (e) {
  console.error(e.message || String(e));
  process.exit(1);
}
const testHooks = { sessionList: null, sessionPreview: null };

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
let _discCache = null, _discAt = 0, _discCfgKey = null;   // memo: one card click may call this several times
// D-003:项目发现只受 hiddenProjects/customProjects 两个配置字段影响。显式传入 cfg 快照时
// 绝不二次 readConfig;缓存必须同时按时间(3s)与影响发现结果的配置指纹区分,禁止复用
// 另一配置快照的发现结果。无参调用保持现役行为(自行 readConfig + 3s 缓存)。
function discoveryCfgKey(cfg) {
  return JSON.stringify([
    Array.isArray(cfg && cfg.hiddenProjects) ? cfg.hiddenProjects : [],
    Array.isArray(cfg && cfg.customProjects) ? cfg.customProjects : [],
  ]);
}
function discoverProjects(cfg) {
  if (_discCache && (Date.now() - _discAt) < 3000) {
    // 无参调用保持现役 3s 缓存语义;显式快照仅在与缓存同指纹时命中,指纹不同必须重算。
    if (cfg === undefined || _discCfgKey === discoveryCfgKey(cfg)) return _discCache;
  }
  const effective = cfg === undefined ? readConfig() : cfg;
  const hidden = new Set((Array.isArray(effective.hiddenProjects) ? effective.hiddenProjects : []).map(h => String(h).toLowerCase()));
  const appDir = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume').toLowerCase();
  const tempDir = path.resolve(os.tmpdir()).toLowerCase();
  const excluded = cwd => {
    const l = path.resolve(String(cwd)).toLowerCase();
    const testOwned = TEST_MODE && (l === path.resolve(STATE_DIR).toLowerCase() || l.startsWith(path.resolve(STATE_DIR).toLowerCase() + path.sep));
    return hidden.has(l) || l.startsWith(appDir) || (!testOwned && (l === tempDir || l.startsWith(tempDir + path.sep))) || /^[a-z]:\\windows/i.test(cwd);
  };
  const disc = []; // {name, path, mtime}
  try {
    const root = CLAUDE_PROJECTS_DIR;
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
  for (const p of (Array.isArray(effective.customProjects) ? effective.customProjects : [])) {
    if (p && p.path && fs.existsSync(p.path) && !excluded(p.path) && !seen.has(p.path.toLowerCase())) {
      list.push({ name: p.name || path.basename(p.path), path: p.path });
      seen.add(p.path.toLowerCase());
    }
  }
  _discCache = list; _discAt = Date.now(); _discCfgKey = discoveryCfgKey(effective);
  return list;
}

// ---- local AI completion notifications (Stage 1 S1-B: CompletionAdmission 边界) ----
// 事件结构校验、受控 source -> 客户端标签、项目解析、通知格式、七天去重、稳定 UUID,
// 队列 claim/recovery/process 已抽取到 completion-events.js;本文件只负责装配注入。
const completionEventsRunner = createCompletionEvents({
  appDir: APP_DIR,
  queueDir: COMPLETION_QUEUE_DIR,
  seenPath: COMPLETION_SEEN_PATH,
  config: () => readConfig(),
  send: sendText,
  discoverProjects: () => discoverProjects(),
  log: logLine,
});

// ---- S1-D ConversationStore:聊天/项目/查询/用户聊天状态的单一 owner ----
// 文件名、JSON schema、stableSessionId、seed/路径与 mark/clear 语义全部收敛到
// conversation-store.js;本文件只装配依赖:profile 解析、项目发现、od: 迁移副作用与日志。
const conversationStore = createConversationStore({
  stateDir: STATE_DIR,
  claudeProjectsDir: CLAUDE_PROJECTS_DIR,
  resolveProfile: (profileValue, openId) => profileById(profileValue) || getUserProfile(openId),
  profileById,
  getUserProfile,
  profilesFor,
  discoverProjects: () => discoverProjects(),
  onOdMigrate: (odTarget, chatId) => {
    // 卡片 Map 属于 agent(不入 store):把曾在 'od:<open_id>' 伪目标下的控制卡迁到真实 chat。
    if (lastCard.has(odTarget)) { lastCard.set(chatId, lastCard.get(odTarget)); lastCard.delete(odTarget); }
  },
  log: logLine,
});
// 现役调用点继续使用同一批函数名;真身已由 store 单一持有。
const { getSession, setSession, activeProject, querySession, chatSession, markChatStarted, markQueryStarted, clearChatSessions, clearQuerySession, rememberUserChat, userTarget, querySessionExists } = conversationStore;
// ---- outbound image channel (机器人 -> 用户) ----
// claude can't hand an image back through stream-json (we only capture its text result). Convention:
// it SAVES images it wants sent into this dir, and after the run we upload+send them. The dir lives
// in AppDir (NOT inside the project) so a modify run that also `git add`s can't accidentally commit
// the screenshots; keyed by cwd so concurrent runs in different projects don't collide.
const IMG_OUT_BASE = path.join(STATE_DIR, 'feishu-out');
const IMG_EXTS = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp']);
function imageOutDir(cwd) {
  const h = crypto.createHash('sha1').update(String(cwd || '').toLowerCase()).digest('hex').slice(0, 16);
  return path.join(IMG_OUT_BASE, h);
}
// empty the dir BEFORE a run so a leftover image from a failed drain can't be re-sent next time
function prepImageOut(cwd) {
  const dir = imageOutDir(cwd);
  try { fs.mkdirSync(dir, { recursive: true }); } catch (e) {}
  try { for (const f of fs.readdirSync(dir)) { try { fs.unlinkSync(path.join(dir, f)); } catch (e) {} } } catch (e) {}
  return dir;
}
// a short standing note prepended to the prompt so claude knows HOW to hand an image back to Feishu
function imageHint(dir) {
  return `[系统提示:如果本轮需要把图片/截图发给用户,请把图片文件(png/jpg 等)保存到目录 ${dir}(可用 Write/Bash 创建该目录并写入),保存后系统会自动上传并发送到飞书;不需要发图时忽略本提示,不要向用户提及它。]\n\n`;
}
// ---- inbound image channel (用户 -> 机器人) ----
// A Feishu image arrives as its OWN message with no caption, so we can't act on it alone: download
// it, park it, and attach it to the user's NEXT text message (claude reads it with the Read tool).
const IMG_IN_BASE = path.join(STATE_DIR, 'feishu-in');
const pendingImages = new Map();   // chatId + senderOpen -> standalone images waiting for that user's next text
const activeInboundImages = new Set();
const PENDING_IMG_MAX = 6;
const INBOUND_IMAGE_MAX_BYTES = 10 * 1024 * 1024;
const INBOUND_IMAGE_TTL_MS = 24 * 60 * 60 * 1000;
const POST_LOCALE_PRIORITY = ['zh_cn', 'en_us', 'ja_jp'];
function imageQueueKey(chatId, openId) { return String(chatId || '') + '\n' + String(openId || ''); }
function imageInDir(chatId, openId) {
  const h = crypto.createHash('sha1').update(imageQueueKey(chatId, openId)).digest('hex').slice(0, 16);
  const dir = path.join(IMG_IN_BASE, h);
  try { fs.mkdirSync(dir, { recursive: true }); } catch (e) {}
  return dir;
}
// download a message's image resource (needs the im:resource permission) -> local file path
async function downloadMessageImage(messageId, fileKey, chatId, openId, ext) {
  const dir = imageInDir(chatId, openId);
  const file = path.join(dir, `${Date.now()}_${String(fileKey || '').slice(-8).replace(/[^\w]/g, '')}${ext || '.png'}`);
  // 资源下载请求(含现役单次网络重试)由 ChannelAdapter 负责;结果原样返回,落盘与大小/TTL 归本层。
  const res = await channel.getMessageResource(messageId, fileKey);
  // The SDK returns a helper with writeFile(); fall back to stream/buffer shapes across versions.
  // The HTTP response timeout does not cover a later hung disk/stream write, so bound that stage too.
  try {
    if (res && typeof res.writeFile === 'function') {
      const write = Promise.resolve().then(() => res.writeFile(file));
      try { await channel.withTimeout(write, channel.resourceTimeoutMs, '保存下载图片'); }
      catch (e) { write.finally(() => { try { fs.unlinkSync(file); } catch (e2) {} }).catch(() => {}); throw e; }
    } else if (res && res.data && typeof res.data.pipe === 'function') {
      await new Promise((resolve, reject) => {
        const input = res.data;
        const output = fs.createWriteStream(file);
        const timer = setTimeout(() => {
          const error = new Error(`保存下载图片 超时(${channel.resourceTimeoutMs}ms)`);
          error.code = 'AI_RESUME_FEISHU_TIMEOUT';
          if (typeof input.destroy === 'function') input.destroy(error);
          output.destroy(error);
          reject(error);
        }, channel.resourceTimeoutMs);
        const done = fn => value => { clearTimeout(timer); fn(value); };
        input.pipe(output);
        output.on('finish', done(resolve)); output.on('error', done(reject)); input.on('error', done(reject));
      });
    } else if (res && Buffer.isBuffer(res.data)) fs.writeFileSync(file, res.data);
    else if (Buffer.isBuffer(res)) fs.writeFileSync(file, res);
    else throw new Error('未知的下载返回格式');
    const size = fs.statSync(file).size;
    if (!size || size > INBOUND_IMAGE_MAX_BYTES) {
      const error = new Error(!size ? '下载图片为空' : `下载图片超过 ${Math.round(INBOUND_IMAGE_MAX_BYTES / 1024 / 1024)}MB 上限`);
      error.code = 'AI_RESUME_IMAGE_SIZE';
      throw error;
    }
    return file;
  } catch (e) {
    try { fs.unlinkSync(file); } catch (e2) {}
    throw e;
  }
}
function addPendingImage(chatId, openId, file) {
  const key = imageQueueKey(chatId, openId);
  const list = (pendingImages.get(key) || []).filter(f => { try { return fs.existsSync(f); } catch (e) { return false; } });
  if (list.length >= PENDING_IMG_MAX) {
    try { fs.unlinkSync(file); } catch (e) {}
    pendingImages.set(key, list);
    return { accepted: false, count: list.length };
  }
  list.push(file); pendingImages.set(key, list);
  return { accepted: true, count: list.length };
}
function takePendingImages(chatId, openId) {
  const key = imageQueueKey(chatId, openId);
  const list = pendingImages.get(key) || [];
  pendingImages.delete(key);
  return list.filter(f => { try { return fs.existsSync(f); } catch (e) { return false; } });
}
function cleanupInboundImages(files) {
  for (const f of (files || [])) {
    activeInboundImages.delete(f);
    try { fs.unlinkSync(f); } catch (e) {}
  }
}
function cleanupOldInboundImages(now) {
  const cutoff = Number(now || Date.now()) - INBOUND_IMAGE_TTL_MS;
  let removed = 0;
  try {
    if (fs.existsSync(IMG_IN_BASE)) {
      for (const d of fs.readdirSync(IMG_IN_BASE, { withFileTypes: true })) {
        if (!d.isDirectory()) continue;
        const dir = path.join(IMG_IN_BASE, d.name);
        for (const f of fs.readdirSync(dir, { withFileTypes: true })) {
          if (!f.isFile()) continue;
          const full = path.join(dir, f.name);
          if (activeInboundImages.has(full)) continue;
          try { if (fs.statSync(full).mtimeMs < cutoff) { fs.unlinkSync(full); removed++; } } catch (e) {}
        }
        try { if (!fs.readdirSync(dir).length) fs.rmdirSync(dir); } catch (e) {}
      }
    }
  } catch (e) {}
  // Disk cleanup must also compact the in-memory queues. Otherwise every expired chat/sender pair
  // leaves a dead Map key behind for the lifetime of the agent process.
  for (const [key, list] of pendingImages) {
    const kept = (list || []).filter(f => { try { return fs.existsSync(f); } catch (e) { return false; } });
    if (kept.length) pendingImages.set(key, kept); else pendingImages.delete(key);
  }
  return removed;
}
// prefix that tells claude the user's message came with images and where to read them
function inboundImageNote(files) {
  if (!files || !files.length) return '';
  return `[用户随这条消息发来 ${files.length} 张图片,已保存在本机。请先使用当前 AI 可用的本地图片读取能力(例如 view_image / Read)查看这些图片,再结合下面的文字作答:\n` +
    files.map(f => '  ' + f).join('\n') + ']\n\n';
}
// Atomically claim this sender's parked standalone images plus the current post's inline images.
// Claiming happens before the handler starts background work, so a later image event cannot leak into
// the earlier question. Callers must cleanup `files` when the AI run ends (or immediately if rejected).
function withPendingImages(chatId, openId, canRead, text, inlineFiles) {
  // Current post resources are more tightly bound to the caption than older standalone images, so
  // keep them first when the combined request exceeds the per-turn limit.
  const files = (inlineFiles || []).filter(Boolean).concat(takePendingImages(chatId, openId));
  const unique = Array.from(new Set(files)).filter(f => { try { return fs.existsSync(f); } catch (e) { return false; } });
  if (!unique.length) return { prompt: text, n: 0, omitted: 0, blocked: false, files: [] };
  if (!canRead) { cleanupInboundImages(unique); return { prompt: text, n: unique.length, omitted: 0, blocked: true, files: [] }; }
  const selected = unique.slice(0, PENDING_IMG_MAX);
  const omitted = unique.slice(PENDING_IMG_MAX);
  cleanupInboundImages(omitted);
  for (const f of selected) activeInboundImages.add(f);
  return { prompt: inboundImageNote(selected) + text, n: selected.length, omitted: omitted.length, blocked: false, files: selected };
}

// Feishu encodes a picture with a caption in one chat bubble as `message_type=post`, not `image`.
// The documented/live shape is usually {zh_cn:{title,content:[[elements...]]}}, while forwarded or
// older payloads may already be the flat {title,content} body. Keep parsing local and defensive so a
// new locale key or harmless rich-text tag cannot make the whole user question disappear.
function parsePostContent(raw) {
  let parsed;
  try { parsed = typeof raw === 'string' ? JSON.parse(raw || '{}') : raw; }
  catch (e) { return { ok: false, text: '', imageKeys: [], error: 'invalid_json' }; }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, text: '', imageKeys: [], error: 'invalid_body' };
  }
  const isBody = value => value && typeof value === 'object' && !Array.isArray(value) && Array.isArray(value.content);
  const hasPayload = value => isBody(value) && (
    (typeof value.title === 'string' && value.title.trim()) ||
    value.content.some(paragraph => Array.isArray(paragraph) && paragraph.some(el => el && typeof el === 'object' && (
      (typeof el.image_key === 'string' && el.image_key.trim()) ||
      (typeof el.text === 'string' && el.text.trim()) ||
      (typeof el.href === 'string' && el.href.trim()) ||
      (typeof el.user_name === 'string' && el.user_name.trim()) ||
      (typeof el.user_id === 'string' && el.user_id.trim())
    )))
  );
  let body = isBody(parsed) ? parsed : null;
  if (!body) {
    const localeBodies = POST_LOCALE_PRIORITY.map(locale => parsed[locale]).filter(isBody);
    const otherBodies = Object.keys(parsed).filter(k => POST_LOCALE_PRIORITY.indexOf(k) === -1).map(k => parsed[k]).filter(isBody);
    body = localeBodies.concat(otherBodies).find(hasPayload) || localeBodies[0] || otherBodies[0] || null;
  }
  if (!body || !Array.isArray(body.content)) {
    return { ok: false, text: '', imageKeys: [], error: 'missing_content' };
  }

  const lines = [];
  const title = typeof body.title === 'string' ? body.title.trim() : '';
  if (title) lines.push(title);
  const imageKeys = [];
  const seenImages = new Set();
  for (const paragraph of body.content) {
    if (!Array.isArray(paragraph)) continue;
    const parts = [];
    for (const el of paragraph) {
      if (!el || typeof el !== 'object') continue;
      const tag = String(el.tag || '');
      if (tag === 'img') {
        const key = typeof el.image_key === 'string' ? el.image_key.trim() : '';
        if (key && !seenImages.has(key)) { seenImages.add(key); imageKeys.push(key); }
        continue;
      }
      if (tag === 'at') {
        const name = typeof el.user_name === 'string' ? el.user_name.trim() : '';
        const userId = typeof el.user_id === 'string' ? el.user_id.trim() : '';
        if (name) parts.push('@' + name);
        else if (userId) parts.push(userId === 'all' ? '@all' : '@' + userId);
        continue;
      }
      if (tag === 'hr') { parts.push('---'); continue; }
      if (tag === 'code_block') {
        if (typeof el.text === 'string') parts.push(el.text);
        continue;
      }
      if (tag === 'a') {
        if (typeof el.text === 'string' && el.text) parts.push(el.text);
        else if (typeof el.href === 'string') parts.push(el.href);
        continue;
      }
      // text / md and future text-carrying tags all degrade to their text field.
      if (typeof el.text === 'string') parts.push(el.text);
    }
    const line = parts.join('').trim();
    if (line) lines.push(line);
  }
  return { ok: true, text: lines.join('\n').trim(), imageKeys, error: null };
}

async function downloadPostImages(messageId, imageKeys, chatId, openId) {
  const unique = Array.from(new Set((imageKeys || []).filter(Boolean)));
  const selected = unique.slice(0, PENDING_IMG_MAX);
  const settled = await Promise.all(selected.map(async key => {
    try { return { key, file: await downloadMessageImage(messageId, key, chatId, openId, '.png') }; }
    catch (error) { return { key, error }; }
  }));
  const files = [];
  const failures = [];
  for (const item of settled) {
    if (item.file) files.push(item.file);
    else failures.push(item);
  }
  return { requested: unique.length, downloaded: files.length, files, failures, omitted: Math.max(0, unique.length - selected.length) };
}
// prefix that makes a full user's message a READ-ONLY query (viewers are always read-only).
// requires a separator (space or colon) after the keyword so "只读xxx"-style prose in a modify
// conversation is not silently rerouted into the shared query session.
const QUERY_RE = /^\s*(查询|只读查询|只读|query)(?:\s+|[:：])\s*([\s\S]+)$/i;

// ---- a project's provider-native WORK sessions (what ✏️修改 continues) ----
// Each *.jsonl in the project's ~/.claude/projects/<encoded-cwd>/ folder is one conversation (the
// ones your VS Code sessions create). Read-only queries live in an isolated cwd, so they never show
// up here. Folder names are lossy, so find the folder by reading each session's real cwd.
const _sessDirCache = new Map();   // projectPath(lower) -> folder; the mapping never changes
function projectSessionDir(projectPath) {
  return projectSessionDirResult(projectPath).dir;
}
function projectSessionDirResult(projectPath) {
  const ck = String(projectPath).toLowerCase();
  if (_sessDirCache.has(ck)) return { dir: _sessDirCache.get(ck), error: null };
  const found = findProjectSessionDirResult(projectPath);
  if (found.dir) _sessDirCache.set(ck, found.dir);
  return found;
}
function findProjectSessionDir(projectPath) {
  return findProjectSessionDirResult(projectPath).dir;
}
function findProjectSessionDirResult(projectPath) {
  try {
    const base = CLAUDE_PROJECTS_DIR;
    const want = String(projectPath).toLowerCase();
    for (const d of fs.readdirSync(base)) {
      const full = path.join(base, d);
      let files; try { files = fs.readdirSync(full).filter(f => f.endsWith('.jsonl')); } catch (e) { continue; }
      if (!files.length) continue;
      const head = readHead(path.join(full, files[0]), 65536).split(/\r?\n/).slice(0, 60);
      for (const ln of head) {
        if (ln.indexOf('"cwd"') === -1) continue;
        try { const j = JSON.parse(ln); if (j.cwd && String(j.cwd).toLowerCase() === want) return { dir: full, error: null }; } catch (e) {}
        break;
      }
    }
  } catch (e) {
    if (!(e && e.code === 'ENOENT')) return { dir: null, error: e };
  }
  return { dir: null, error: null };
}
// [{id, title, mtime}] newest first. Title prefers claude's own `ai-title`, else the first user line.
function listProjectSessions(projectPath, limit, diagnostics) {
  const located = projectSessionDirResult(projectPath);
  if (located.error) { if (diagnostics) diagnostics.error = located.error; return []; }
  const dir = located.dir;
  if (!dir) return [];
  let out = [];
  try {
    for (const f of fs.readdirSync(dir)) {
      if (!f.endsWith('.jsonl')) continue;
      const full = path.join(dir, f);
      let mtime = 0; try { mtime = fs.statSync(full).mtimeMs; } catch (e) { continue; }
      out.push({ id: f.replace(/\.jsonl$/i, ''), title: '', mtime, file: full });
    }
  } catch (e) { if (diagnostics) diagnostics.error = e; return []; }
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
async function listSessionsForProfileResult(projectPath, profile, limit, options) {
  const testDelay = TEST_MODE ? Math.max(0, Number(process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS || 0)) : 0;
  if (testDelay) await new Promise((resolve, reject) => {
    const signal = options && options.signal;
    let timer;
    const onAbort = () => {
      clearTimeout(timer);
      const error = new Error('会话读取已取消'); error.code = 'AI_RESUME_CANCELLED'; reject(error);
    };
    if (signal && signal.aborted) { onAbort(); return; }
    timer = setTimeout(() => { if (signal) signal.removeEventListener('abort', onAbort); resolve(); }, testDelay);
    if (signal) signal.addEventListener('abort', onAbort, { once: true });
  });
  try {
    if (TEST_MODE && typeof testHooks.sessionList === 'function') return await testHooks.sessionList(projectPath, profile, limit, options || {});
    if (profile.engine === 'codex') return await codexSessions.listResult(projectPath, limit, options);
    const diagnostics = {};
    const sessions = listProjectSessions(projectPath, limit, diagnostics);
    return { sessions, error: diagnostics.error || null };
  } catch (e) {
    logLine(`读取 ${profile.fullLabel || profile.id} 会话失败: ` + (e && e.message));
    return { sessions: [], error: e };
  }
}
async function listSessionsForProfile(projectPath, profile, limit) {
  return (await listSessionsForProfileResult(projectPath, profile, limit)).sessions;
}
async function sessionPreviewFor(profile, session, turns) {
  if (!session) return '';
  if (TEST_MODE && typeof testHooks.sessionPreview === 'function') return await testHooks.sessionPreview(profile, session, turns);
  return profile.engine === 'codex'
    ? await codexSessions.preview(session.id, turns)
    : sessionPreview(session.file, turns);
}
const shortTime = ms => { const d = new Date(ms); const p = n => String(n).padStart(2, '0'); return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`; };

// run one read-only query in the caller's dedicated project/profile query session.
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
function isListedOwner(openId, cfg) {
  // 兼容 wrapper:cfg 快照直接透传给纯策略;undefined 时才由策略走注入 readConfig。
  return authorizationPolicy.isExplicitOwner(openId, cfg);
}
function readProjectGuide(projectPath) {
  const root = assertNoReparseDirectoryChain(projectPath);
  const guidePath = path.join(root, 'AI_GUIDE.md');
  let before;
  try { before = fs.lstatSync(guidePath); }
  catch (e) {
    if (e && e.code === 'ENOENT') return '';
    const err = new Error('project-guide-unreadable'); err.code = 'project_guide_unreadable'; throw err;
  }
  if (before.isSymbolicLink() || !before.isFile() || Number(before.nlink) > 1) {
    const err = new Error('project-guide-reparse-or-nonregular'); err.code = 'unsafe_project_guide'; throw err;
  }
  const rootReal = fs.realpathSync(root);
  const guideReal = fs.realpathSync(guidePath);
  const rootPrefix = rootReal.toLowerCase() + path.sep;
  if (!guideReal.toLowerCase().startsWith(rootPrefix)) {
    const err = new Error('project-guide-outside-root'); err.code = 'unsafe_project_guide'; throw err;
  }
  let fd;
  try {
    fd = fs.openSync(guidePath, 'r');
    const opened = fs.fstatSync(fd);
    if (!opened.isFile() || Number(opened.nlink) > 1
        || (before.dev !== undefined && opened.dev !== before.dev)
        || (before.ino !== undefined && opened.ino !== before.ino)) {
      const err = new Error('project-guide-changed-during-open'); err.code = 'unsafe_project_guide'; throw err;
    }
    if (fs.realpathSync(guidePath).toLowerCase() !== guideReal.toLowerCase()) {
      const err = new Error('project-guide-path-changed'); err.code = 'unsafe_project_guide'; throw err;
    }
    return fs.readFileSync(fd, 'utf8');
  } catch (e) {
    if (!e.code || !/^project_|^unsafe_/.test(e.code)) {
      const err = new Error('project-guide-unreadable'); err.code = 'project_guide_unreadable'; throw err;
    }
    throw e;
  } finally { if (fd !== undefined) try { fs.closeSync(fd); } catch (e) {} }
}
function senderIsAllowed(openId, cfg) {
  return authorizationPolicy.senderIsAllowed(openId, cfg);
}
async function runProjectQuery(chatId, project, prompt, openId, cfg) {
  const c = cfg === undefined ? readConfig() : cfg;
  const trustedOwner = isListedOwner(openId, c);   // listed owner — NOT bootstrap-full
  // Preferred: a project's AI_GUIDE.md (generate it with the project-tour skill) — a dense, self-
  // contained tour (架构/模块/测试流程/数据格式/FAQ/术语/文档索引). Inject it ONCE when the query
  // session is first created; later --resume calls reuse it from the conversation's prompt cache
  // (cheap). Falls back to "explore docs/" framing for projects that don't have a guide yet.
  let guide = '', staleNote = '';
  try {
    guide = readProjectGuide(project.path);
    // A fallback provider may start a different conversation, so guide injection is decided per
    // provider session below rather than only from the primary session.
    const rec = (guide.match(/project-tour[^\n]*git\s+([0-9a-f]{6,40})/i) || [])[1];
    const cur = rec ? await projectGitHash(project.path) : '';
    if (rec && cur && rec !== cur) staleNote = `⚠️ 提示:本导览生成于较早的提交(git ${rec}),项目现已到 ${cur}——架构/模块/数据格式/术语等大框架通常仍准,但**具体实现细节请以实际代码为准**,不确定处务必读相关源码后再答。\n\n`;
  } catch (e) {
    logLine(`拒绝读取不安全或不可核验的项目导览:${e && e.code || 'project_guide_unreadable'}`);
    return {
      ok: false, limited: false, retryable: false,
      errorCode: e && e.code || 'project_guide_unreadable',
      text: '项目导览文件无法通过本地路径安全校验，已在启动 AI 前拒绝本次查询。请检查项目路径和 AI_GUIDE.md 是否为普通文件。',
      ms: 0, sessionId: null, usage: null, cost: null, sideEffectsStarted: false, childPending: false,
    };
  }
  const questionFrame = `[对项目「${project.name}」(目录:${project.path})的只读提问。请尽量省 token:` +
    (guide
      ? `先看上面的项目导览作答;导览不足时,再按其文档索引读最相关的 1~2 篇文档及关键代码;`
      : `先看该目录下的文档索引(AI_GUIDE.md / docs/ / README / 目录树),定位并只读与问题最相关的 1~2 篇文档及它们引用的关键代码;`) +
    `在本轮内直接简要作答。不要通读整个项目,不要启动子任务/子代理,也不要修改任何文件。]\n\n${prompt}`;
  const framed = (guide ? `[项目导览 AI_GUIDE.md,优先据此作答;不足时再按其文末「文档索引」读 1~2 篇文档:]\n${staleNote}${guide}\n\n———\n` : '') + questionFrame;
  // SECURITY: --permission-mode plan blocks WRITES but not READS, and reads are NOT confined to the
  // workspace — a query can Read ../../config.json (feishuAppSecret / feishuAuthPassword) and a
  // coworker could then 解锁 <password> to self-promote to owner. (Verified: plan-mode Read happily
  // returns an ancestor file's contents with benign phrasing.) So only an EXPLICITLY-listed owner
  // keeps file tools for queries (the secrets are theirs anyway); everyone else — coworkers, and
  // everyone while the bot is unlocked — gets NO file/exec tools and answers from the injected
  // AI_GUIDE.md only. Mirrors the chat-path defense (test/chat-security.js, test/query-security.js).
  const disallowed = trustedOwner
    ? ['Task']   // owner: full read tools, just no big sub-agent explore
    : ['Task', 'Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit'];   // others: guide-only, no file access
  const prog = await startProgress(chatId, project.name + ' 查询');
  const done = trackRun(chatId, project.name + ' 查询', '查询');
  let r;
  const primaryQs = querySession(project.path, openId, getUserProfile(openId, c).id);
  try {
    r = await runForUser(primaryQs.cwd, project.name + ' 查询', framed, openId, {
      taskKind: 'query',
      runKey: primaryQs.cwd, readOnly: true,
      forProfile: profile => {
        const qs = querySession(project.path, openId, profile.id);
        const sessionExists = qs.profile.engine === 'codex' ? qs.started : (qs.started && querySessionExists(qs.sessionId, profile.id));
        return {
          cwd: qs.cwd, sessionId: qs.sessionId,
          sessionExists,
          prompt: sessionExists ? questionFrame : framed,
          addDir: project.path, disallowedTools: disallowed,
          noTools: !trustedOwner,
        };
      },
    });
    await prog.stop(r);
  } finally { done(); await prog.stop(); }   // stop() is idempotent — the finally kills the ticker even if the run throws
  if (r && r.ok && r.profile && r.sessionId) {
    const qs = querySession(project.path, openId, r.profile.id);
    markQueryStarted(qs.flag, { id: qs.id, sessionId: r.sessionId, profileId: r.profile.id, engine: r.profile.engine, openId: openId || '', path: project.path, name: project.name });
  }
  return r;
}

// find a project by 1-based number, exact name, or fuzzy (startsWith/includes)
// D-003:cfg 为事件入口快照时,发现必须基于该快照,绝不回退二次 readConfig。
function findProject(query, cfg) {
  const ps = discoverProjects(cfg); const q = String(query || '').trim(); if (!q) return null;
  if (/^\d+$/.test(q)) { const i = parseInt(q, 10) - 1; return (i >= 0 && i < ps.length) ? ps[i] : null; }
  const low = q.toLowerCase();
  return ps.find(p => p.name.toLowerCase() === low)
      || ps.find(p => p.name.toLowerCase().startsWith(low))
      || ps.find(p => p.name.toLowerCase().includes(low)) || null;
}
// a bare message that is exactly a project number or full name -> that project (else null)
function projectIfBareName(text, cfg) {
  const ps = discoverProjects(cfg); const q = text.trim();
  if (/^\d+$/.test(q)) { const i = parseInt(q, 10) - 1; return (i >= 0 && i < ps.length) ? ps[i] : null; }
  return ps.find(p => p.name.toLowerCase() === q.toLowerCase()) || null;
}
// "<project name> <command>" via longest-name-prefix (no default fallback)
function oneOffTarget(text, cfg) {
  const t = text.trim();
  const byLen = discoverProjects(cfg).slice().sort((a, b) => b.name.length - a.name.length);
  for (const p of byLen) {
    const n = p.name.toLowerCase();
    if (t.toLowerCase().startsWith(n)) {
      const rest = t.slice(p.name.length).replace(/^\s*[:：,，]?\s*/, '');
      if (rest) return { project: p, prompt: rest };
    }
  }
  return { project: null };
}

// ---- run the selected AI in a cwd (Claude Code / Codex / DeepSeek via Claude Code) ----
// S1-E: provider attempts are assembled and invoked only through the AgentAdapter boundary; the
// standalone killTree import stays for crash/exit child-tree cleanup below.
// S1-F: runKey 同步预占/释放与 busy 判断、provider 候选/fallback、健康预检等待与取消、legacy
// 总时限统一由 task-orchestrator 装配;飞书层只保留 Feishu 表面、权限与项目/session key。
const agentAdapter = createAgentAdapter({
  readConfig, logLine, running, testMode: TEST_MODE,
  onChildStart: registerAIChild,
  onChildEnd: unregisterAIChild,
});
const taskOrchestrator = createTaskOrchestrator({
  agentAdapter, running, readConfig, getUserProfile,
  canUseOwnerOnlyProfile: (openId, cfg) => authorizationPolicy.canUseOwnerOnlyProfile(openId, cfg),
  profileById, fallbackProfiles, defaultProfileId: DEFAULT_PROFILE_ID,
  ensureFreshProviderHealth, providerHealth, providerReasonText, orphanBlocksRun,
  isShuttingDown: () => shuttingDown,
  maxTimeoutMs: MAX_TIMEOUT_MS, defaultAiTimeoutMs: DEFAULT_AI_TIMEOUT_MS,
  logLine,
  onAttempt: TEST_MODE ? attempt => { testHooks.lastRun = attempt; } : undefined,
});
const codexSessions = createCodexSessions({ codexCmd: agentAdapter.codexCmd, logLine });
const sessionManager = createSessionManager({ appDir: STATE_DIR, claudeRoot: CLAUDE_PROJECTS_DIR, codexSessions, logLine });
const CLAUDE_CMD = agentAdapter.claudeCmd;
const CODEX_CMD = agentAdapter.codexCmd;

// Model selection is derived from observed provider health, never from key presence or a static
// registry alone. The picker refreshes a stale snapshot before exposing buttons; old cards and text
// commands are checked against the same snapshot so hidden providers cannot be selected indirectly.
const PROVIDER_ORDER = ['openai', 'deepseek', 'claude'];
const PROVIDER_LABEL = { openai: 'OpenAI', deepseek: 'DeepSeek', claude: 'Claude' };
const PROVIDER_HEALTH_TTL_MS = Math.max(30000, Number(process.env.FEISHU_PROVIDER_HEALTH_TTL_MS || 5 * 60 * 1000));
const PROVIDER_FAILURE_TTL_MS = Math.max(5000, Number(process.env.FEISHU_PROVIDER_FAILURE_TTL_MS || 30000));
const initiallyAvailable = status => ({ status, reason: status === 'available' ? 'ok' : 'checking', ms: 0, route: status === 'available' ? 'direct' : null });
let providerHealthState = {
  probedAt: TEST_MODE ? new Date().toISOString() : null,
  refreshedAt: TEST_MODE ? Date.now() : 0,
  refreshing: false,
  providers: Object.fromEntries(PROVIDER_ORDER.map(name => [name, initiallyAvailable(TEST_MODE ? 'available' : 'checking')])),
};
let providerHealthPromise = null;

function setProviderHealthState(next) {
  const source = next && next.providers || {};
  providerHealthState = {
    probedAt: next && next.probedAt || new Date().toISOString(),
    refreshedAt: Date.now(),
    refreshing: false,
    providers: Object.fromEntries(PROVIDER_ORDER.map(name => {
      const item = source[name] || {};
      return [name, {
        status: String(item.status || 'unavailable'),
        reason: String(item.reason || 'unknown'),
        ms: Math.max(0, Number(item.ms) || 0),
        route: item.route === 'direct' || item.route === 'proxy'
          ? item.route
          : (item.status === 'available' && name !== 'claude' ? 'direct' : null),
        attemptedRoutes: Array.isArray(item.attemptedRoutes) ? item.attemptedRoutes.filter(route => route === 'direct' || route === 'proxy') : [],
        configFingerprint: String(item.configFingerprint || ''),
        childPending: !!item.childPending,
      }];
    })),
  };
  return providerHealthState;
}

function providerHealth(name) {
  return providerHealthState.providers[name] || { status: 'unavailable', reason: 'unknown', ms: 0, route: null, attemptedRoutes: [], configFingerprint: '', childPending: false };
}

function providerIsAvailable(name) {
  return providerHealth(name).status === 'available';
}

function providerHealthIsStale(name) {
  if (!providerHealthState.refreshedAt) return true;
  const age = Date.now() - providerHealthState.refreshedAt;
  const names = name ? [name] : PROVIDER_ORDER;
  if (names.some(provider => providerHealth(provider).childPending)) return false;
  let cfg;
  try { cfg = readConfig(); } catch (e) { return true; }
  for (const provider of names) {
    const item = providerHealth(provider);
    if (item.configFingerprint && item.configFingerprint !== providerConfigFingerprint(cfg, provider)) return true;
  }
  if (names.some(provider => providerHealth(provider).status !== 'available')) return age > PROVIDER_FAILURE_TTL_MS;
  return age > PROVIDER_HEALTH_TTL_MS;
}

function settlePendingProviderHealth(provider, configFingerprint) {
  const item = providerHealth(provider);
  if (!item.childPending || item.configFingerprint !== String(configFingerprint || '')) return false;
  item.childPending = false;
  providerHealthState.refreshedAt = 0;
  logLine(`AI 可用性挂起子进程已结束:${PROVIDER_LABEL[provider] || provider},下次请求将重新探测`);
  return true;
}

function providerReasonText(item) {
  const reason = String(item && item.reason || 'unknown');
  if (item && item.status === 'unconfigured') return '未配置';
  if (reason === 'auth') return '认证或登录失效';
  if (reason === 'rate_limit') return '额度或限流';
  if (reason === 'model_unavailable') return '模型不可用';
  if (reason === 'command_missing') return '本机命令不可用';
  if (reason === 'proxy_unavailable') return '直连及备用代理均异常';
  if (reason === 'transient') return '网络或服务异常';
  if (reason === 'checking') return '检测中';
  return '当前不可用';
}

function providerAvailabilityText(item) {
  if (!item || item.status !== 'available') return providerReasonText(item);
  if (item.route === 'direct') return '直连可用';
  if (item.route === 'proxy') return '代理可用';
  return '可用';
}

function profileGroupsFor(senderOpen, cfg) {
  const allowed = profilesFor(canUseOwnerOnlyProfile(senderOpen, cfg));
  return PROVIDER_ORDER
    .filter(providerIsAvailable)
    .map(provider => ({ provider, label: PROVIDER_LABEL[provider], profiles: allowed.filter(p => p.provider === provider) }))
    .filter(group => group.profiles.length);
}

function refreshProviderHealth(reason) {
  if (providerHealthPromise) return providerHealthPromise;
  providerHealthState.refreshing = true;
  providerHealthPromise = probeProviders({ runner: agentAdapter, readConfig, includeClaude: true, timeoutMs: 60000 })
    .then(result => {
      setProviderHealthState(result);
      for (const pending of result.pendingSettlements || []) {
        pending.promise.then(() => settlePendingProviderHealth(pending.provider, pending.configFingerprint)).catch(() => {});
      }
      logLine('AI 可用性实探(' + reason + '):' + PROVIDER_ORDER.map(name => {
        const item = providerHealth(name);
        return `${PROVIDER_LABEL[name]}=${item.status}/${item.reason}${item.route ? '/' + item.route : ''}`;
      }).join(' '));
      return providerHealthState;
    })
    .catch(e => {
      setProviderHealthState({ providers: Object.fromEntries(PROVIDER_ORDER.map(name => [name, { status: 'unavailable', reason: 'transient', ms: 0 }])) });
      logLine('AI 可用性实探失败(' + reason + '):' + (e && e.message || 'unknown'));
      return providerHealthState;
    })
    .finally(() => { providerHealthPromise = null; providerHealthState.refreshing = false; });
  return providerHealthPromise;
}

function ensureFreshProviderHealth(reason, provider) {
  return providerHealthIsStale(provider) ? refreshProviderHealth(reason) : Promise.resolve(providerHealthState);
}

function setProviderHealthForTest(providers) {
  if (!TEST_MODE) throw new Error('provider health test hook is test-only');
  return setProviderHealthState({ providers });
}
function ageProviderHealthForTest(ms) {
  if (!TEST_MODE) throw new Error('provider health test hook is test-only');
  providerHealthState.refreshedAt = Date.now() - Math.max(0, Number(ms) || 0);
}

// Run long claude work in the BACKGROUND so the event handler returns immediately. The WS layer
// awaits the handler before ACKing the event back to Feishu (sdk: `yield eventDispatcher.invoke`),
// so an awaited 1-4 min claude run means no ACK for minutes -> Feishu stops delivering / re-delivers
// and every tap in that chat looks dead (the "daytime freeze"). key is reserved via
// taskOrchestrator.tryReserve by the CALLER (synchronously, before any await) and released here
// when the work finishes.
function bg(label, key, work) {
  setImmediate(async () => {
    try { await work(); }
    catch (e) { logLine(`后台任务异常 [${label}]: ` + (e && (e.stack || e))); }
    finally { if (key) taskOrchestrator.release(key); }
  });
}
function eventDispatchKey(data) {
  const msg = data && data.message;
  if (msg && msg.chat_id) return String(msg.chat_id);
  const cardChat = data && ((data.context && data.context.open_chat_id) || data.open_chat_id);
  if (cardChat) return String(cardChat);
  const openId = data && ((data.operator && data.operator.operator_id && data.operator.operator_id.open_id)
    || (data.operator && data.operator.open_id));
  return openId ? String(userTarget(openId) || ('od:' + openId)) : '__global__';
}
// ---- S1-F compatibility surface ----
// 编排逻辑已迁至 task-orchestrator.js;以下薄包装保持既有测试面与调用方行为完全一致
// （参数/动态 forProfile/候选顺序/route/remaining timeout/attemptedProfiles/fallbackFrom
// 与现役一致，terminal 结果原样交还）。
function taskTimeoutMs(taskKind, cfg) {
  return taskOrchestrator.taskTimeoutMs(taskKind, cfg);
}
async function runForUser(cwd, label, prompt, openId, opts, cfg) {
  return taskOrchestrator.run(cwd, label, prompt, openId, opts, cfg);
}
function cancelProviderPreflight(keys) {
  return taskOrchestrator.cancelPreflight(keys);
}

// Compatibility wrapper for the existing test surface and legacy callers. New flows call
// runForUser so provider choice and fallback stay explicit.
function runClaude(cwd, label, prompt, opts) {
  const options = opts || {};
  const profile = options.profile || profileFromLegacyModel(options.model);
  return agentAdapter.run(cwd, label, prompt, Object.assign({}, options, { profile }));
}

// ---- Feishu send helpers ----
// 每次飞书请求都有界(单次请求超时、至多一次网络重试、超时/上传不重试、create 稳定 uuid
// 重试复用)均收敛到 ChannelAdapter;本层只做分片、业务文案与卡片状态。
async function sendText(chatId, text, options) {
  // Feishu text messages get unwieldy past a few KB; chunk to <=3500 chars, cap 6 parts.
  const MAX = 3500, PARTS = 6;
  let parts = [];
  let s = String(text);
  while (s.length && parts.length < PARTS) { parts.push(s.slice(0, MAX)); s = s.slice(MAX); }
  if (s.length) parts[parts.length - 1] += '\n…(内容过长已截断,完整结果见 VS Code 该项目会话)';
  let ok = true;
  for (let i = 0; i < parts.length; i++) {
    const p = parts[i];
    try {
      const uuid = options && options.uuidSeed ? stableMessageUuid(options.uuidSeed, i) : crypto.randomUUID();
      await channel.createMessage(chatId, 'text', JSON.stringify({ text: p }), { label: '发送文字', uuid });
    } catch (e) { ok = false; logLine('发送失败: ' + (e && e.message)); }
  }
  // a text message pushes the control card up out of view -> invalidate it so the next menu tap
  // sends a fresh card at the bottom (fixes "点了没反应" when the live card is scrolled away).
  invalidateControlCard(chatId);
  return ok;
}
// one "control card" per chat: all navigation (main menu <-> project sub-menu) updates THIS card
// in place instead of sending a new one, so cards never pile up.
const lastCard = new Map();   // chatId -> message_id of the live control card
const cardHash = new Map();   // message_id -> last content string patched to it (skip no-op patches)
const controlCardEpoch = new Map();   // increments whenever a normal message makes the live card non-visible
function cardEpoch(chatId) { return controlCardEpoch.get(chatId) || 0; }
function invalidateControlCard(chatId, expectedMessageId) {
  if (expectedMessageId && lastCard.get(chatId) !== expectedMessageId) return false;
  controlCardEpoch.set(chatId, cardEpoch(chatId) + 1);
  lastCard.delete(chatId);
  return true;
}
async function sendCard(chatId, card, setLast) {
  const startedEpoch = cardEpoch(chatId);
  try {
    const content = JSON.stringify(card);
    const res = await channel.createMessage(chatId, 'interactive', content, { label: '发送卡片' });
    const mid = res && res.data && res.data.message_id;
    // control cards (menu OR project) become the live lastCard; owner-notify cards pass setLast=falsey
    if (mid && setLast && cardEpoch(chatId) === startedEpoch) lastCard.set(chatId, mid);
    if (mid) { cardHash.set(mid, content); if (cardHash.size > 100) cardHash.clear(); }
    return mid;
  } catch (e) { logLine('发送卡片失败: ' + (e && e.message)); return null; }
}
// which card should this chat be looking at right now (main menu vs the project sub-menu)
function currentCard(chatId, senderOpen) {
  const sess = getSession(chatId);
  return (sess.mode === 'project' && sess.project) ? buildProjectCard(chatId, senderOpen) : buildMenuCard(chatId, senderOpen);
}
// update the clicked card in place so the ✅ (current project / model / mode) moves.
// always pass the card explicitly (built with the operator's senderOpen for role-aware rendering).
async function refreshCard(chatId, messageId, card, options) {
  if (!messageId || !card) return null;
  const opts = options || {};
  const promoteToLive = opts.promoteToLive !== false;
  const startedEpoch = cardEpoch(chatId);
  const content = JSON.stringify(card);
  if (cardHash.get(messageId) === content) {
    if (promoteToLive && cardEpoch(chatId) === startedEpoch) lastCard.set(chatId, messageId);
    return messageId;
  }   // no change -> skip patch
  try {
    await channel.patchMessage(messageId, content, { label: '更新卡片' });
    // A plain/result message may have completed while this PATCH was in flight. PATCH does not move a
    // card to the bottom, so a pre-message card must never become live again across that visibility epoch.
    if (promoteToLive && cardEpoch(chatId) === startedEpoch) lastCard.set(chatId, messageId);
    cardHash.set(messageId, content); if (cardHash.size > 100) cardHash.clear();
    return messageId;
  } catch (e) { logLine('更新卡片失败: ' + (e && e.message)); return null; }
}
async function presentControlCard(chatId, messageId, card, options) {
  const opts = options || {};
  const promoteToLive = opts.promoteToLive !== false;
  const target = messageId || lastCard.get(chatId);
  if (target) {
    const updated = await refreshCard(chatId, target, card, { promoteToLive });
    if (updated) return updated;
    if (promoteToLive) invalidateControlCard(chatId, target);
    cardHash.delete(target);
  }
  if (opts.allowFallback === false) return null;
  // The old PATCH may still complete after our local timeout. The replacement becomes the live card;
  // session-picker tokens make every older picker harmless, and the per-chat queue keeps later UI
  // actions ordered after this replacement.
  return await sendCard(chatId, card, promoteToLive);
}
const controlCardWrites = new Map();   // chatId -> serialized control-card write chain
function enqueueControlCard(chatId, messageId, card, options) {
  const opts = options || {};
  const previous = controlCardWrites.get(chatId) || Promise.resolve();
  const current = previous.catch(() => {}).then(async () => {
    if (opts.forceNew) {
      invalidateControlCard(chatId);
      return await sendCard(chatId, card, true);
    }
    const live = lastCard.get(chatId) || null;
    // A promoted navigation write always follows the live card at EXECUTION time. This also covers the
    // replacement window where a failed old patch has cleared lastCard and the replacement CREATE is
    // still in flight when another click is queued. Standalone cards keep their explicit message id.
    const target = opts.promoteToLive !== false ? (live || messageId) : messageId;
    return await presentControlCard(chatId, target, card, opts);
  });
  controlCardWrites.set(chatId, current);
  return current.finally(() => { if (controlCardWrites.get(chatId) === current) controlCardWrites.delete(chatId); });
}
// a footer line with elapsed time / output tokens / cost, appended to a run's result
function fmtMeta(r) {
  const m = fmtMetaLine(r);
  return m ? '\n\n———\n' + m : '';
}
function fmtMetaLine(r) {
  if (!r) return '';
  const parts = [];
  if (r.profile && r.profile.fullLabel) parts.push('AI ' + r.profile.fullLabel);
  if (r.fallbackFrom) parts.push('由 ' + r.fallbackFrom.fullLabel + ' 自动切换');
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
  const mid = await sendCard(chatId, card, false);
  // NEVER lose a result: sendCard swallows API failures (bad lark_md, size, rate limit) and returns
  // null — that silently ate the whole answer. Fall back to plain text, which chunks and always goes.
  if (!mid) {
    logLine('结果卡片发送失败,回退纯文本');
    await sendText(chatId, title + '\n\n' + (body || '(无输出)') + fmtMeta(r));
  }
  invalidateControlCard(chatId);   // a result card pushes the control card up — invalidate it (same as sendText)
}
// ---- image sending: upload a local file to Feishu (needs im:resource) -> image_key, then send it
async function uploadImage(filePath) {
  return channel.uploadImage(filePath);
}
async function sendImage(target, imageKey) {
  await channel.createMessage(target, 'image', JSON.stringify({ image_key: imageKey }), { label: '发送图片' });
}
const IMG_MAX_BYTES = 10 * 1024 * 1024;   // Feishu image upload cap
const IMG_MAX_COUNT = 9;                   // don't flood the chat if a run dumps dozens
// after a run, send any images claude dropped in the cwd's image-out dir, then clear them. Returns
// the count sent. Best-effort: an upload failure (e.g. missing im:resource) becomes a text note
// naming the file and never throws into the caller.
async function drainImageOut(target, cwd) {
  const dir = imageOutDir(cwd);
  let names;
  try { names = fs.readdirSync(dir); } catch (e) { return 0; }
  const imgs = names
    .filter(f => IMG_EXTS.has(path.extname(f).toLowerCase()))
    .map(f => { const p = path.join(dir, f); let mt = 0, sz = 0; try { const st = fs.statSync(p); mt = st.mtimeMs; sz = st.size; } catch (e) {} return { p, f, mt, sz }; })
    .sort((a, b) => a.mt - b.mt);   // oldest first — send in the order claude created them
  if (!imgs.length) return 0;
  let sent = 0;
  for (const img of imgs.slice(0, IMG_MAX_COUNT)) {
    let remove = false;
    try {
      if (img.sz <= 0) {
        await sendText(target, `(图片「${img.f}」为空,未发送)`);
        remove = true;
      } else if (img.sz > IMG_MAX_BYTES) {
        await sendText(target, `(图片「${img.f}」超过 10MB,未发送;文件保留在 ${img.p})`);
      } else {
        const key = await uploadImage(img.p);
        if (key) { await sendImage(target, key); sent++; remove = true; }
        // no key and no throw: never drop it in silence (that hid the res.data shape bug for weeks)
        else { logLine('上传图片未返回 image_key: ' + img.f); await sendText(target, `(图片「${img.f}」上传没拿到 image_key,未发送——请检查机器人的 im:resource 权限是否已发布)`); }
      }
    } catch (e) {
      logLine('发送图片失败 ' + img.f + ': ' + (e && e.message || e));
      try { await sendText(target, `(图片「${img.f}」已生成但发送失败,可能未开启 im:resource 权限;文件在 ${img.p})`); } catch (e2) {}
    }
    if (remove) { try { fs.unlinkSync(img.p); } catch (e) {} }
  }
  const extra = imgs.length - IMG_MAX_COUNT;
  if (extra > 0) {
    try { await sendText(target, `(另有 ${extra} 张图片未发送,避免刷屏;文件保留在 ${dir})`); } catch (e) {}
  }
  if (sent) logLine(`发送图片 ${sent} 张 (${path.basename(cwd)})`);
  return sent;
}
// Long runs go silent while claude works. The old heartbeat sent a NEW message every 15s — a 12-min
// run buried the chat under ~48 messages (user-reported). Now: ONE progress card, PATCHED in place,
// so a run costs exactly one message no matter how long it takes. stop(r) turns it into a compact
// done line (the full result is sent right after as its own card, so it's always at the bottom).
function startProgress(chatId, label) {
  const t0 = Date.now();
  const secs = () => Math.round((Date.now() - t0) / 1000);
  const body = () => {
    const s = secs();
    const hint = s >= 600 ? '\n(这轮比较久,claude 仍在跑;发「停止」或点底部「🛑 停止」可取消)'
      : s >= 120 ? '\n(大任务通常 1-4 分钟,别关窗口,跑完自动回结果)' : '';
    return `**⏳ 「${label}」进行中** · 已 ${s < 60 ? s + 's' : Math.floor(s / 60) + 'm' + String(s % 60).padStart(2, '0') + 's'}${hint}`;
  };
  const card = (content, tmpl) => ({
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: tmpl || 'wathet', title: { tag: 'plain_text', content: '⏳ 运行中 · ' + label } },
    elements: [{ tag: 'div', text: { tag: 'lark_md', content } }],
  });
  let mid = null, timer = null, stopped = false, progressWriteFailed = false;
  let writeChain = Promise.resolve();
  const enqueueProgressWrite = (nextCard, finalWrite) => {
    const current = writeChain.catch(() => {}).then(async () => {
      if (!mid) return null;
      // A timed-out SDK PATCH may still complete later because the SDK call cannot be cancelled. Never
      // send the completion state to that tainted message: post a new final card so a late tick can only
      // mutate the older message above it, not overwrite the visible completion result.
      if (finalWrite && progressWriteFailed) return await sendCard(chatId, nextCard, false);
      const updated = await refreshCard(chatId, mid, nextCard, { promoteToLive: false });
      if (!updated) {
        progressWriteFailed = true;
        if (finalWrite) return await sendCard(chatId, nextCard, false);
      }
      return updated;
    });
    writeChain = current;
    return current;
  };
  const state = {
    async stop(r) {
      if (stopped) return; stopped = true;
      if (timer) { try { clearInterval(timer); } catch (e) {} timer = null; }
      if (!mid) return;
      const s = secs();
      const line = r
        ? `**${r.ok ? '✅' : '⚠️'} 「${label}」${r.ok ? '已完成' : '未完成'}** · 用时 ${s}s${r.ok ? '(结果见下方)' : ''}`
        : `**「${label}」已结束** · 用时 ${s}s`;
      try { await enqueueProgressWrite(card(line, r && r.ok ? 'green' : 'grey'), true); } catch (e) {}
    },
  };
  // send the initial card, then tick. Both are best-effort: progress must NEVER break the run.
  return (async () => {
    try { mid = await sendCard(chatId, card(body()), false); } catch (e) { mid = null; }
    if (mid && !stopped) {
      const intervalMs = Math.max(20, Number(process.env.FEISHU_TEST_PROGRESS_INTERVAL_MS || 20000));
      timer = setInterval(() => {
        if (!stopped) enqueueProgressWrite(card(body())).catch(() => {});
      }, intervalMs);
    }
    return state;
  })();
}
// ---- crash-safe run tracking ----
// Everything about an in-flight run lives in memory, so a restart (deploy / watchdog / crash) makes
// it vanish: the user last saw "进行中" and then silence forever — exactly the reported "太久就没结果
// 了". Record in-flight runs on disk; on boot, tell whoever was waiting that their run was cut off.
const INFLIGHT_PATH = path.join(STATE_DIR, 'feishu-inflight.json');
let inflightRuns = {};
function saveInflight() { try { fs.writeFileSync(INFLIGHT_PATH, JSON.stringify(inflightRuns, null, 2), 'utf8'); } catch (e) {} }
function trackRun(chatId, label, kind) {
  const id = crypto.randomUUID();
  inflightRuns[id] = { chatId, label, kind, startedAt: Date.now() };
  saveInflight();
  return () => { delete inflightRuns[id]; saveInflight(); };
}
// on boot: report (once) any run that was still in flight when the process died, then clear
async function reportInterruptedRuns() {
  let prev = {};
  try { prev = JSON.parse(fs.readFileSync(INFLIGHT_PATH, 'utf8').replace(/^﻿/, '')) || {}; } catch (e) { return; }
  const items = Object.values(prev).filter(v => v && v.chatId);
  try { fs.unlinkSync(INFLIGHT_PATH); } catch (e) {}
  for (const it of items) {
    const mins = Math.max(1, Math.round((Date.now() - (it.startedAt || Date.now())) / 60000));
    logLine(`上次运行被中断: ${it.label} (${it.kind}) 已跑 ${mins}m`);
    try {
      await sendText(it.chatId,
        `⚠️ 上一次「${it.label}」的${it.kind === '执行' ? '执行' : it.kind}在机器人重启时被打断(已跑约 ${mins} 分钟),结果没能发给你。\n` +
        (it.kind === '执行'
          ? '注意:AI 当时可能已经改了一部分文件——建议在本地客户端打开该项目会话看看做到哪了,或直接再发一次指令继续。'
          : '可以直接再问一次。'));
    } catch (e) {}
  }
}
// ---- unified provider/model registry ----
// GPT-5.6 Sol and both DeepSeek tiers are available to every Feishu user. Only Fable 5 remains
// owner-only. Legacy model ids are still accepted and mirrored into the old config fields.
function modelLabelOf(v) {
  return profileLabel(v, true);
}
// Two-level model picker: first choose a provider, then one of that provider's models. Only providers
// whose latest real probe succeeded get buttons. origin='m' is standalone (bottom/text menu), while
// origin='nav' temporarily replaces the main control card and returns to it after a model is chosen.
function buildModelCard(chatId, senderOpen, options, cfg) {
  const opts = options || {};
  const origin = opts.origin === 'm' ? 'm' : 'nav';
  const cur = getUserProfile(senderOpen, cfg);
  const currentState = providerHealth(cur.provider);
  const currentText = providerAvailabilityText(currentState);
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: `**当前 AI:${cur.fullLabel}** · ${currentText}\n只影响你自己；跨提供商修改项目时会新建对应服务的原生会话。` } }];
  const groups = profileGroupsFor(senderOpen, cfg);

  if (providerHealthState.refreshing) {
    elements.push({ tag: 'hr' });
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: '**正在实测 AI 服务…**\n检测完成前不提供模型按钮，避免把“已配置”误当成“可用”。' } });
  } else {
    const selected = groups.find(group => group.provider === opts.provider);
    elements.push({ tag: 'hr' });
    if (selected) {
      elements.push({ tag: 'div', text: { tag: 'lark_md', content: `**${selected.label}**\n选择一个已通过服务实探的模型：` } });
      const buttons = selected.profiles.map(p => ({
        tag: 'button', text: { tag: 'plain_text', content: (cur.id === p.id ? '✅ ' : '') + p.label },
        type: cur.id === p.id ? 'primary' : 'default', value: { do: 'model', p: p.id, provider: selected.provider, from: origin },
      }));
      for (let i = 0; i < buttons.length; i += 3) elements.push({ tag: 'action', actions: buttons.slice(i, i + 3) });
      elements.push({ tag: 'action', actions: [{ tag: 'button', text: { tag: 'plain_text', content: '⬅ 返回 AI 服务' }, type: 'default', value: { do: 'modelmenu', from: origin } }] });
    } else if (groups.length) {
      elements.push({ tag: 'div', text: { tag: 'lark_md', content: '**选择 AI 服务**\n只列出最近一次真实请求成功的服务：' } });
      const buttons = groups.map(group => ({
        tag: 'button', text: { tag: 'plain_text', content: (cur.provider === group.provider ? '✅ ' : '') + `${group.label} · ${group.profiles.length} 个模型` },
        type: cur.provider === group.provider ? 'primary' : 'default', value: { do: 'modelprovider', provider: group.provider, from: origin },
      }));
      for (let i = 0; i < buttons.length; i += 2) elements.push({ tag: 'action', actions: buttons.slice(i, i + 2) });
    } else {
      elements.push({ tag: 'div', text: { tag: 'lark_md', content: '**当前没有通过实测的 AI 服务。**\n请在本机 AI Resume 更新登录或 API Key，然后重新打开模型菜单。' } });
    }

    const hidden = PROVIDER_ORDER.filter(name => !providerIsAvailable(name));
    if (hidden.length) {
      elements.push({ tag: 'note', elements: [{ tag: 'plain_text', content: '已隐藏不可用服务：' + hidden.map(name => `${PROVIDER_LABEL[name]}（${providerReasonText(providerHealth(name))}）`).join('、') }] });
    }
    if (origin === 'nav' && !selected) {
      elements.push({ tag: 'action', actions: [{ tag: 'button', text: { tag: 'plain_text', content: '⬅ 返回主菜单' }, type: 'default', value: { do: 'modelclose' } }] });
    }
  }
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'turquoise', title: { tag: 'plain_text', content: '🤖 选择可用 AI' } },
    elements,
  };
}

async function openStandaloneModelPicker(chatId, senderOpen, cfg) {
  const pending = providerHealthIsStale() ? refreshProviderHealth('打开飞书模型菜单') : null;
  const modelCardId = await sendCard(chatId, buildModelCard(chatId, senderOpen, { origin: 'm' }, cfg), false);
  if (modelCardId) invalidateControlCard(chatId);
  if (pending) {
    await pending;
    if (modelCardId) await refreshCard(chatId, modelCardId, buildModelCard(chatId, senderOpen, { origin: 'm' }, cfg), { promoteToLive: false });
  }
  return modelCardId;
}

async function openNavigationModelPicker(chatId, messageId, senderOpen, provider, cfg) {
  const pending = providerHealthIsStale() ? refreshProviderHealth('打开主菜单模型选择') : null;
  await enqueueControlCard(chatId, messageId, buildModelCard(chatId, senderOpen, { origin: 'nav', provider }, cfg));
  if (pending) {
    await pending;
    await enqueueControlCard(chatId, messageId, buildModelCard(chatId, senderOpen, { origin: 'nav', provider }, cfg));
  }
}
// Telegram-style menu: buttons to enter a project / chat / status / switch model.
// Default 'idle' mode does nothing until the user taps a button here. Role-aware: viewers
// (coworkers) only see what they can actually use — chat + projects (read-only query).
function buildMenuCard(chatId, senderOpen, cfg) {
  const sess = getSession(chatId);
  // D-003:owner 配置缺失/malformed(level=none)时,主菜单只保留闲聊和模型入口,不显示
  // 状态/权限/项目按钮,也不调用项目发现;有效解锁/锁定配置行为不变。
  const denied = authLevel(senderOpen, cfg) === 'none';
  // D-003:项目列表与 activeProject 解析必须基于同一 cfg 快照(入口 access.config),
  // 禁止 discoverProjects() 无参二次读配置造成授权/渲染分叉。
  const projects = denied ? [] : discoverProjects(cfg);
  const ap = denied ? null : activeProject(chatId, projects);
  const owner = canConfig(senderOpen, cfg);
  const curProfile = getUserProfile(senderOpen, cfg);
  let modeLine;
  if (denied) modeLine = (sess.mode === 'chat'
    ? '**当前:💬 闲聊模式** — 直接说话就是和我聊天。'
    : '**请选择 👇** 点「闲聊模式」开始聊天,或点下方按钮。');
  else if (ap) modeLine = `**当前:📂 项目「${ap.name}」** — 直接发消息就在这里续跑。`;
  else if (sess.mode === 'chat') modeLine = '**当前:💬 闲聊模式** — 直接说话就是和我聊天。';
  else modeLine = owner
    ? '**请选择 👇** 点「闲聊模式」开始聊天,或点一个项目进入。选之前我不处理任何消息。'
    : '**请选择 👇** 点一个项目可**只读查询**它的技术细节(不改任何文件),或点「闲聊模式」。';
  const eq = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: modeLine } }];
  const row1 = [
    { tag: 'button', text: { tag: 'plain_text', content: (sess.mode === 'chat' ? '✅ ' : '💬 ') + '闲聊模式' }, type: sess.mode === 'chat' ? 'primary' : 'default', value: { do: 'chat' } },
  ];
  if (!denied) row1.push({ tag: 'button', text: { tag: 'plain_text', content: 'ℹ️ 状态' }, type: 'default', value: { do: 'status' } });
  if (owner) row1.push({ tag: 'button', text: { tag: 'plain_text', content: '🔑 权限' }, type: 'default', value: { do: 'perm' } });
  elements.push({ tag: 'action', actions: row1 });
  const currentHealth = providerHealth(curProfile.provider);
  const currentHealthText = providerAvailabilityText(currentHealth);
  elements.push({ tag: 'div', text: { tag: 'lark_md', content: `**AI:${curProfile.fullLabel}** · ${currentHealthText} — 每个人的选择互不影响。` } });
  elements.push({ tag: 'action', actions: [{ tag: 'button', text: { tag: 'plain_text', content: '🤖 选择可用 AI' }, type: 'default', value: { do: 'modelmenu', from: 'nav' } }] });
  if (!denied && projects.length) {
    elements.push({ tag: 'hr' });
    const btns = projects.slice(0, 15).map(p => ({
      tag: 'button',
      text: { tag: 'plain_text', content: ((ap && eq(ap.path, p.path)) ? '✅ ' : '📂 ') + p.name },
      type: (ap && eq(ap.path, p.path)) ? 'primary' : 'default',
      value: { do: 'enter', p: p.path },
    }));
    for (let i = 0; i < btns.length; i += 3) elements.push({ tag: 'action', actions: btns.slice(i, i + 3) });
  } else if (!denied) {
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: '_未发现项目。先在「AI Resume」里添加或勾选。_' } });
  }
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'orange', title: { tag: 'plain_text', content: '多 AI 项目助手 · 选择操作' } },
    elements,
  };
}

// project sub-menu (level 1): after entering a project you pick 只读查询 vs 修改项目 FIRST,
// then just type. The chosen mode shows ✅; switching is one tap. Keeps the hierarchy shallow.
function buildProjectCard(chatId, senderOpen, cfg) {
  // D-003:level=none(owner 配置缺失/malformed)绝不解析旧 project session,直接回退
  // 隐藏项目的主菜单,不调用 activeProject/discoverProjects。
  if (authLevel(senderOpen, cfg) === 'none') return buildMenuCard(chatId, senderOpen, cfg);
  // D-003:activeProject 用入口 cfg 快照对应的项目列表解析,不触发二次项目发现。
  const ap = activeProject(chatId, discoverProjects(cfg));
  const sub = getSession(chatId).sub;
  const name = ap ? ap.name : '项目';
  const projectKey = sessionProjectKey(ap && ap.path);
  const owner = canConfig(senderOpen, cfg);
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
  if (sub === 'query') { line = `**📂「${name}」· 👁 只读查询**\n直接提问即可 — 我只读代码/答疑,**绝不改文件**。查询记忆按你本人和当前 AI 隔离。`; tmpl = 'blue'; }
  else if (sub === 'modify') {
    const wt = work ? (workTitle(chatId) || work.slice(0, 8)) : '';
    const wp = profileById(getSession(chatId).workProfile) || getUserProfile(senderOpen, cfg);
    line = work
      ? `**📂「${name}」· ✏️ 修改项目**\n当前 AI:**${wp.fullLabel}** · 会话:**${wt}**\n直接发指令即可 — 我会真正改动并继续这个会话。想换会话点「🔀 切换会话」。`
      : `**📂「${name}」· ✏️ 修改项目**\n先选一个 ${getUserProfile(senderOpen, cfg).fullLabel} 会话 👇`;
    tmpl = 'red';
  }
  else { line = `**📂 已进入「${name}」**\n先选操作方式 👇 之后直接发消息即可。`; tmpl = 'grey'; }
  const elements = [
    { tag: 'div', text: { tag: 'lark_md', content: line } },
    { tag: 'action', actions: [
      { tag: 'button', text: { tag: 'plain_text', content: (sub === 'query' ? '✅ ' : '👁 ') + '只读查询' }, type: sub === 'query' ? 'primary' : 'default', value: { do: 'submode', sm: 'query', pr: projectKey } },
      { tag: 'button', text: { tag: 'plain_text', content: (sub === 'modify' ? '✅ ' : '✏️ ') + '修改项目' }, type: sub === 'modify' ? 'primary' : 'default', value: { do: 'submode', sm: 'modify', pr: projectKey } },
    ] },
  ];
  if (sub === 'modify' && work) {   // switching sessions only makes sense once you're in modify mode
    elements.push({ tag: 'action', actions: [
      { tag: 'button', text: { tag: 'plain_text', content: '🔀 切换会话' }, type: 'default', value: { do: 'sesslist', pr: projectKey } },
    ] });
  }
  elements.push({ tag: 'action', actions: [
    { tag: 'button', text: { tag: 'plain_text', content: '🧹 清空查询记忆' }, type: 'default', value: { do: 'clearq', pr: projectKey } },
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
async function enterProject(chatId, senderOpen, p, cfg) {
  cancelSessionCardLoad(chatId, '文字命令切换项目');
  const viewer = !canConfig(senderOpen, cfg);
  setSession(chatId, { mode: 'project', project: p.path, sub: viewer ? 'query' : undefined });
  await enqueueControlCard(chatId, null, buildProjectCard(chatId, senderOpen, cfg));
}
// title of the currently picked work session (for the project card), '' if unknown/new
function workTitle(chatId) {
  const sess = getSession(chatId);
  if (!sess.project || !sess.work) return '';
  if (sess.workTitle) return sess.workTitle.length > 24 ? sess.workTitle.slice(0, 24) + '…' : sess.workTitle;
  if (sess.work === 'new') return '🆕 新会话';
  const profile = profileById(sess.workProfile) || profileById('claude-default');
  if (profile.engine === 'codex') return sess.work.slice(0, 8);
  try {
    const s = listProjectSessions(sess.project, 12).find(x => x.id === sess.work);
    if (s) return s.title.length > 24 ? s.title.slice(0, 24) + '…' : s.title;
  } catch (e) {}
  return '🆕 新会话';   // a fresh uuid has no transcript yet, so it isn't in the list
}
const sessionPickerTokens = new Map();   // chatId -> token bound to exactly one project + AI profile
const consumedPickerActions = new Map();   // token + message id -> accepted picker action (double-click guard)
function pickerActionKey(val, messageId) { return `${String(val && val.k || '')}:${String(messageId || '')}`; }
function rememberPickerAction(val, messageId) {
  if (!(val && val.k)) return;
  consumedPickerActions.set(pickerActionKey(val, messageId), Date.now());
  if (consumedPickerActions.size > 500) {
    const cutoff = Date.now() - 10 * 60 * 1000;
    for (const [key, at] of consumedPickerActions) if (at < cutoff) consumedPickerActions.delete(key);
    if (consumedPickerActions.size > 500) consumedPickerActions.clear();
  }
}
function wasPickerActionConsumed(val, messageId) {
  const at = consumedPickerActions.get(pickerActionKey(val, messageId));
  return !!(at && Date.now() - at < 10 * 60 * 1000);
}
function sessionProjectKey(projectPath) {
  return crypto.createHash('sha1').update(String(projectPath || '').toLowerCase()).digest('hex').slice(0, 12);
}
function validProjectCard(chatId, val) {
  const sess = getSession(chatId);
  return !!(val && val.pr && sess.mode === 'project' && sess.project && val.pr === sessionProjectKey(sess.project));
}
function buildExpiredSessionCard() {
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'grey', title: { tag: 'plain_text', content: '此会话卡已失效' } },
    elements: [
      { tag: 'div', text: { tag: 'lark_md', content: '项目、AI 或页面状态已经变化。为避免把旧会话接到错误项目，本卡不再执行操作。' } },
      { tag: 'action', actions: [
        { tag: 'button', text: { tag: 'plain_text', content: '回到主菜单' }, type: 'default', value: { do: 'home' } },
      ] },
    ],
  };
}
function buildSessionLoadingCard(projectName, profile) {
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'grey', title: { tag: 'plain_text', content: '正在读取会话' } },
    elements: [{ tag: 'div', text: { tag: 'lark_md', content: `正在读取 **${projectName || '项目'}** 的 **${profile.fullLabel}** 历史会话…` } }],
  };
}
function pickerValue(picker, extra) {
  return Object.assign({ k: picker && picker.token, pr: picker && picker.projectKey, p: picker && picker.profileId }, extra || {});
}
// the session picker: which conversation should ✏️修改 continue?
async function buildSessionCard(chatId, senderOpen, snapshot, cfg) {
  const fixed = snapshot || {};
  // D-003:会话卡的项目解析必须基于事件 cfg 快照的项目列表,不二次 readConfig。
  const projects = discoverProjects(cfg);
  const ap = fixed.projectPath
    ? (projects.find(x => x.path.toLowerCase() === fixed.projectPath.toLowerCase()) || { name: fixed.projectName || path.basename(fixed.projectPath), path: fixed.projectPath })
    : activeProject(chatId, projects);
  const name = ap ? ap.name : '项目';
  const sess = fixed.session || getSession(chatId);
  const cur = sess.work;
  const profile = fixed.profile || getUserProfile(senderOpen, cfg);
  const picker = fixed.picker || sessionPickerTokens.get(chatId) || {};
  const result = ap ? await listSessionsForProfileResult(ap.path, profile, 5, { signal: fixed.signal }) : { sessions: [], error: null };
  const list = result.sessions;
  const elements = [{ tag: 'div', text: { tag: 'lark_md', content: `**✏️ 修改「${name}」— 选择 ${profile.fullLabel} 会话**\n选一个继续(会给你最近对话摘要),或新开一个全新会话。` } }];
  if (result.error) {
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: `⚠️ 暂时无法读取 **${profile.fullLabel}** 会话。没有把它误判成“无历史”；可重新加载或直接新开会话。` } });
  } else if (list.length) {
    for (const s of list) {
      const t = s.title.length > 20 ? s.title.slice(0, 20) + '…' : s.title;
      const on = cur === s.id && sess.workProfile === profile.id;
      elements.push({ tag: 'action', actions: [{
        tag: 'button',
        text: { tag: 'plain_text', content: (on ? '✅ ' : '📝 ') + t + ' · ' + shortTime(s.mtime) },
        type: on ? 'primary' : 'default',
        value: pickerValue(picker, { do: 'pick', s: s.id, t: s.title }),
      }] });
    }
  } else {
    elements.push({ tag: 'div', text: { tag: 'lark_md', content: '_这个项目还没有历史会话 — 点「🆕 新开会话」开始第一个。_' } });
  }
  elements.push({ tag: 'action', actions: [
    ...(result.error ? [{ tag: 'button', text: { tag: 'plain_text', content: '↻ 重新加载' }, type: 'default', value: pickerValue(picker, { do: 'sesslist' }) }] : []),
    { tag: 'button', text: { tag: 'plain_text', content: '🆕 新开会话' }, type: 'default', value: pickerValue(picker, { do: 'newsess' }) },
    { tag: 'button', text: { tag: 'plain_text', content: '⬅ 返回' }, type: 'default', value: pickerValue(picker, { do: 'backproj' }) },
  ] });
  return {
    config: { wide_screen_mode: true, update_multi: true },
    header: { template: 'orange', title: { tag: 'plain_text', content: '选择会话 · ' + name } },
    elements,
  };
}

const sessionCardLoads = new Map();   // chatId -> the only current session-picker load for this chat
let sessionCardLoadSeq = 0;
function cancelSessionCardLoad(chatId, reason) {
  const job = sessionCardLoads.get(chatId);
  if (job) {
    sessionCardLoads.delete(chatId);
    if (job.controller) job.controller.abort();
    logLine(`会话列表作废 #${job.id}` + (reason ? ` (${reason})` : ''));
  }
  sessionPickerTokens.delete(chatId);
}
function validSessionPicker(chatId, val, senderOpen, cfg) {
  const picker = sessionPickerTokens.get(chatId);
  const sess = getSession(chatId);
  if (!picker || !val || !val.k || val.k !== picker.token || val.pr !== picker.projectKey || val.p !== picker.profileId) return null;
  if (sess.mode !== 'project' || !sess.project || sess.project.toLowerCase() !== picker.projectPath.toLowerCase()) return null;
  if (getUserProfile(senderOpen, cfg).id !== picker.profileId) return null;
  return picker;
}
function listedSessionProject(chatId, cfg) {
  const sess = getSession(chatId);
  if (sess.mode !== 'project' || !sess.project) return null;
  return discoverProjects(cfg).find(item => item.path.toLowerCase() === sess.project.toLowerCase()) || null;
}
function rejectStaleSessionPicker(chatId, messageId, senderOpen, val) {
  if (wasPickerActionConsumed(val, messageId)) {
    logLine(`忽略已消费会话卡的重复点击 action=${val && val.do}`);
    return;
  }
  logLine(`拒绝过期会话卡 action=${val && val.do} token=${String(val && val.k || '').slice(0, 8)}`);
  // This is an old standalone message, not the current control card. Explain locally without promoting
  // it to lastCard or creating another card if the old message can no longer be patched.
  enqueueControlCard(chatId, messageId, buildExpiredSessionCard(), { promoteToLive: false, allowFallback: false });
}
function requestSessionCard(chatId, messageId, senderOpen, cfg) {
  // D-003:level=none 不进入会话选择(它依赖 activeProject 项目发现),回退隐藏项目的主菜单。
  if (!canProject(senderOpen, cfg)) {
    cancelSessionCardLoad(chatId, '无项目权限');
    enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg));
    return false;
  }
  const sess = getSession(chatId);
  // D-003:activeProject 用事件 cfg 快照的项目列表解析,不触发二次项目发现。
  const ap = activeProject(chatId, discoverProjects(cfg));
  if (!ap || sess.mode !== 'project') {
    cancelSessionCardLoad(chatId, '项目状态已失效');
    enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg));
    return false;
  }
  const profile = getUserProfile(senderOpen, cfg);
  const existing = sessionCardLoads.get(chatId);
  if (existing && existing.projectPath.toLowerCase() === ap.path.toLowerCase() && existing.profileId === profile.id) {
    logLine(`会话列表仍在读取 #${existing.id},忽略重复点击`);
    return true;
  }
  cancelSessionCardLoad(chatId, '被新请求替代');
  const controller = new AbortController();
  const picker = {
    token: crypto.randomUUID(),
    projectKey: sessionProjectKey(ap.path),
    projectPath: ap.path,
    profileId: profile.id,
  };
    const job = {
      id: ++sessionCardLoadSeq,
      projectPath: ap.path,
      projectName: ap.name,
      profileId: profile.id,
      messageId: messageId || lastCard.get(chatId) || null,
      messageEpoch: cardEpoch(chatId),
      controller,
      picker,
      loadingWrite: null,
  };
  sessionCardLoads.set(chatId, job);
  sessionPickerTokens.set(chatId, picker);
  // Fast local/Claude listings usually finish before this fires. Slow Codex/provider reads get a visible
  // state after 250ms, while the same per-chat queue guarantees that the final picker or a later page
  // change wins in order. No independent loading card is created, so there is no duplicate-card race.
  const loadingDelayMs = Math.max(20, Number(process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS || 250));
  job.loadingTimer = setTimeout(() => {
    if (sessionCardLoads.get(chatId) !== job || sessionPickerTokens.get(chatId) !== picker) return;
    const target = cardEpoch(chatId) === job.messageEpoch ? job.messageId : null;
    job.loadingWrite = enqueueControlCard(chatId, target, buildSessionLoadingCard(job.projectName, profile))
      .then(mid => {
        if (mid && sessionCardLoads.get(chatId) === job) {
          // A user message may have advanced the visibility epoch while this write was in flight. In
          // that case refreshCard deliberately did not promote the old message, so the final picker must
          // not reuse it merely because the network call itself succeeded.
          job.messageId = lastCard.get(chatId) === mid ? mid : null;
          job.messageEpoch = cardEpoch(chatId);
        }
        return mid;
      });
  }, loadingDelayMs);
  const snapshot = {
    projectPath: ap.path,
    projectName: ap.name,
    profile,
    picker,
    signal: controller.signal,
    session: { mode: 'project', project: ap.path, sub: 'modify', work: sess.work, workProfile: sess.workProfile, workTitle: sess.workTitle },
  };
  bg('读取会话列表', null, async () => {
    const started = Date.now();
    logLine(`会话列表开始 #${job.id} project=${ap.name} ai=${profile.id}`);
    try {
      const card = await buildSessionCard(chatId, senderOpen, snapshot, cfg);
      // Enumeration is done. Cancel the delayed loading write before the final card has to wait behind
      // any earlier per-chat write; otherwise the timer could enqueue loading AFTER the final picker.
      clearTimeout(job.loadingTimer); job.loadingTimer = null;
      if (job.loadingWrite) await job.loadingWrite;
      const currentJob = sessionCardLoads.get(chatId);
      const current = getSession(chatId);
      const stillCurrent = currentJob === job
        && current.mode === 'project'
        && current.sub === 'modify'
        && current.project
        && current.project.toLowerCase() === job.projectPath.toLowerCase()
        && getUserProfile(senderOpen, cfg).id === job.profileId;
      if (!stillCurrent) {
        logLine(`会话列表丢弃 #${job.id}:用户已切换状态`);
        return;
      }
      const target = cardEpoch(chatId) === job.messageEpoch ? job.messageId : null;
      const finalMid = await enqueueControlCard(chatId, target, card);
      if (!finalMid) {
        sessionPickerTokens.delete(chatId);
        await sendText(chatId, '⚠️ 会话已读取,但飞书卡片更新失败。请点底部「主菜单」后重试。');
      }
      const pickerStillCurrent = sessionPickerTokens.get(chatId) === picker && sessionCardLoads.get(chatId) === job;
      logLine(`${pickerStillCurrent ? '会话列表完成' : '会话列表写入后已过期'} #${job.id} ms=${Date.now() - started} visible=${!!finalMid}`);
    } catch (e) {
      if (!(e && e.code === 'AI_RESUME_CANCELLED')) logLine(`会话列表异常 #${job.id}: ` + (e && (e.stack || e)));
      if (sessionCardLoads.get(chatId) === job) {
        sessionPickerTokens.delete(chatId);
        await sendText(chatId, '⚠️ 读取会话时发生异常。请点底部「主菜单」后重试。');
      }
    } finally {
      clearTimeout(job.loadingTimer);
      if (sessionCardLoads.get(chatId) === job) sessionCardLoads.delete(chatId);
    }
  });
  return true;
}

// ---- status / list / help (mode-aware) ----
function statusText(chatId, senderOpen, cfg) {
  const c = cfg === undefined ? readConfig() : cfg;
  let st = {};
  try { st = JSON.parse(fs.readFileSync(path.join(STATE_DIR, 'state.json'), 'utf8')); } catch (e) {}
  const myProfile = getUserProfile(senderOpen, c);
  let reset = '额度未接近上限';
  if (st.realFiveHourResetUtc && st.realResetProbedUtc) {
    const rr = st.realFiveHourResetUtc * 1000, now = Date.now();
    if (rr > now && (now - st.realResetProbedUtc * 1000) < 5 * 3600e3) {
      const s = Math.floor((rr - now) / 1000), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
      reset = `5h 距重置 ${h}h ${String(m).padStart(2, '0')}m`;
    }
  }
  // D-003:level=none 时不调用 activeProject(它内部会触发项目发现),也不显示当前项目;
  // 有项目权限时 activeProject 必须用同一 cfg 快照的项目列表,不二次读配置。
  const ap = canProject(senderOpen, c) ? activeProject(chatId, discoverProjects(c)) : null;
  const mode = ap ? `📂 当前项目:「${ap.name}」` : (getSession(chatId).mode === 'chat' ? '💬 当前:闲聊模式' : '🅾️ 当前:待选(发「菜单」选择)');
  return `${mode}\n你的 AI:${myProfile.fullLabel}\n布防:${c.enabled ? '● 已布防' : '○ 未布防'} · Claude 额度引擎 ${st.phase || 'idle'}\n${reset} · 实探间隔 ${c.probeIntervalMinutes || 15}m`;
}
function listText(cfg) {
  const ps = discoverProjects(cfg);
  if (!ps.length) return '未发现任何项目。先在「AI Resume」里添加或勾选项目。';
  return '项目列表(回复「进入 编号」进入,例:进入 2):\n' + ps.map((p, i) => `${i + 1}. ${p.name}`).join('\n');
}
function helpText(chatId, senderOpen, cfg) {
  // D-003:level=none 时不调用 activeProject,也不显示当前项目;只保留通用帮助。
  const denied = authLevel(senderOpen, cfg) === 'none';
  // D-003:activeProject 用事件 cfg 快照的项目列表解析,不二次读配置。
  const ap = denied ? null : activeProject(chatId, discoverProjects(cfg));
  const sub = getSession(chatId).sub;
  const cur = denied
    ? '🅾️ 当前:仅支持闲聊——直接说话就是和我聊天,不碰任何项目。'
    : (ap
      ? `📂 现在在项目「${ap.name}」·${sub === 'query' ? '👁 只读查询' : sub === 'modify' ? '✏️ 修改项目' : '未选模式(先点 只读/修改)'}`
      : '💬 现在是闲聊模式——直接说话就是和我聊天,不碰任何项目。');
  return [
    '多 AI 项目助手', cur, '',
    '用按钮最省事(发「菜单」调出):主菜单点项目 → 弹出「👁只读 / ✏️修改」→ 选一个 → 直接发消息。',
    '(选「✏️修改」会先让你挑要继续哪个会话,并给你最近对话摘要;也可「🆕 新开会话」。)',
    '',
    '文字命令(标 ⌂ 的仅在主菜单/空闲状态生效;进入会话后你打的字都属于会话,发「退出」先回主菜单):',
    '· 项目 / 菜单 → 列出项目(卡片,任何时候可用)',
    '· ⌂ 进入 <编号或名字> / 直接发项目名或编号 → 进入项目',
    '· ⌂ 查询 <问题> → 直接只读问答,不改文件',
    '· 退出 → 回主菜单(任何时候可用)',
    '· 状态 → 布防 / 额度 / 当前模式',
    '· 停止 / 停止 <项目> → 取消正在跑的指令(也可点底部「🛑 停止」按钮,免打字)',
    '· 模型 / 模型 sol / v4 / v4pro / claude → 查看或切换 AI;每个人的选择互不影响',
    '· 对话中想换 AI 不必退出:点底部「🤖 模型」按钮;跨提供商时会新建对应会话继续',
    '· 权限:默认除机器人主人外,大家都只能只读浏览查询,不能改项目(无需配置)',
    '· 授权 ou_xxx → 额外给某人「可改」权限;取消授权 / 授权列表 管理',
    '· 忘记闲聊 → 清空闲聊记忆;忘记查询 → 清空当前项目的只读查询记忆',
    '',
    '注:✏️修改会继续你在当前 AI 的会话列表里选的会话;Claude/DeepSeek 与 Codex 会话分别管理。',
    '👁只读按“项目 + 用户 + AI”隔离,别人看不到你的查询上下文,也不碰工作会话。',
  ].join('\n');
}

// ---- authorization: project/config operations are gated to bound Feishu users ----
// feishuAuthOpenIds empty = not locked (anyone). Once set, only listed open_ids may operate
// on projects / change config; everyone else can still chat. A password (feishuAuthPassword)
// lets a new account self-authorize via 「解锁 <密码>」.
// three levels: 'full' (can modify projects + change config), 'viewer' (query projects
// read-only, no modify/config), 'none' (chat only). feishuAuthOpenIds empty = not locked (all full).
//
// Stage 1 S1-A:以下均为兼容 wrapper——纯决策逻辑在 authorization-policy.js(读取失败一律
// fail-closed:none/拒绝),这里不再复制任何 owner/viewer/allowlist 判断。
function authLevel(openId, cfg) {
  return authorizationPolicy.level(openId, cfg);
}
const canProject = (openId, cfg) => authorizationPolicy.canProject(openId, cfg);   // enter/query a project (viewer=read-only)
const canConfig  = (openId, cfg) => authorizationPolicy.canConfig(openId, cfg);    // modify projects / change config / authorize — owner only
// 文件/执行工具与 owner-only profile 只授予「显式列出的 owner」;未锁定时的 bootstrap-full
// 陌生人不能获得(见 authorization-policy 冻结语义 #6)。
const canUsePrivilegedTools = (openId, cfg) => authorizationPolicy.canUsePrivilegedTools(openId, cfg);
const canUseOwnerOnlyProfile = (openId, cfg) => authorizationPolicy.canUseOwnerOnlyProfile(openId, cfg);
// owner 通知 chat 仅 p2p 可绑定;未锁定且尚无 feishuChatId 时首个有身份发送者可 bootstrap。
function canBindOwnerChat(sender, cfg) { return authorizationPolicy.canBindOwnerChat(sender, cfg); }

// ---- PER-USER AI preference ----
// New config uses feishuChatProfile / feishuUserProfiles. The old Claude-only fields are mirrored so
// existing GUI versions and tests keep working during migration.
function effectiveModel(openId, model, cfg) {
  if (String(model || '').toLowerCase() === 'claude-fable-5' && !canUseOwnerOnlyProfile(openId, cfg)) return '';
  return model;
}
function getUserProfile(openId, cfg) {
  // 显式 owner(canUseOwnerOnlyProfile)才继承/写入 owner-only 全局 profile;未锁定时的
  // bootstrap-full 陌生人即使可修改/配置,模型选择也按用户存储。
  let c = cfg;
  if (c === undefined) {
    const access = readConfigForAccess();
    if (!access.ok) return profileById(DEFAULT_PROFILE_ID);   // 读取失败 = 无配置,fail-closed 默认
    c = access.config;
  }
  const owner = canUseOwnerOnlyProfile(openId, c);
  return profileById(storedUserProfileId(c, openId, owner)) || profileById(DEFAULT_PROFILE_ID);
}
function setUserProfileId(openId, profileId) {
  try {
    // owner 判定在写锁内用同一个 c 快照,禁止锁内再次读取 config。
    return !!updateConfig(c => storeUserProfile(c, openId, canUseOwnerOnlyProfile(openId, c), profileId));
  }
  catch (e) { logLine('保存用户模型失败:' + (e && e.message || e)); return false; }
}
function getUserModel(openId) {
  const p = getUserProfile(openId);
  return p.provider === 'claude' ? p.model : '';
}
function setUserModel(openId, model) {
  const p = profileFromLegacyModel(model);
  return setUserProfileId(openId, p.id);
}
// the model a run should actually use for this caller (their own pick + the Fable-5 owner-only cap)
function runModelFor(openId, cfg) { return effectiveModel(openId, getUserModel(openId), cfg); }
// Compatibility shape used by existing tests: [display label, profile id].
function modelsFor(openId, cfg) {
  return profilesFor(canUseOwnerOnlyProfile(openId, cfg)).map(p => [p.fullLabel, p.id]);
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
async function denyProject(openId, chatId, cfg) {
  if (canProject(openId, cfg)) return false;
  await sendText(chatId, `🔒 无权限:需授权才能查询项目(你可以闲聊)。\n你的 open_id:${openId || '未知'}`);
  logLine('拦截未授权(项目): ' + openId); notifyOwner(openId, chatId); return true;
}
// gate for modify/config/authorize (full only). returns true if denied.
async function denyConfig(openId, chatId, cfg) {
  if (canConfig(openId, cfg)) return false;
  const lvl = authLevel(openId, cfg);
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

// Stop only work this caller is allowed to own. Everyone can stop their private chat/query; only a
// full user can stop the shared project modification process.
async function stopRuns(chatId, senderOpen, targetName, cfg) {
  // D-003:level=none(canProject=false)不得调用 findProject/activeProject/querySession,只构造
  // 调用者私有 chat key;viewer 只可停止自己的 query/chat,只有 full 才加入共享 modify key。
  let p = null;
  const keys = [];
  if (canProject(senderOpen, cfg)) {
    // D-003:项目解析必须基于事件 cfg 快照,不二次读配置。
    p = targetName ? findProject(targetName, cfg) : activeProject(chatId, discoverProjects(cfg));
    if (p) {
      if (authLevel(senderOpen, cfg) === 'full') keys.push(p.path.toLowerCase());
      keys.push(querySession(p.path, senderOpen, getUserProfile(senderOpen, cfg).id).cwd.toLowerCase());
    }
  }
  keys.push(chatSession(senderOpen, getUserProfile(senderOpen, cfg).id).cwd.toLowerCase());
  // 取消决策委托给 taskOrchestrator:按 key 顺序先找活动 child,命中只取消一次;命中 orphan
  // 占位返回 kind='orphan' 且绝不取消 preflight;已接纳但尚未 spawn 的任务返回 reservation;
  // 没有活动 child/reservation 才取消纯健康预检。
  const outcome = taskOrchestrator.cancel(keys);
  if (outcome.kind === 'orphan') await sendText(chatId, '🛑 该任务上次异常退出的 AI 进程身份尚未安全核验，未按 PID 强制终止；将保留任务锁并在后台自动重试确认。');
  else if (outcome.kind === 'child') await sendText(chatId, '🛑 已请求停止' + (p ? `:${p.name}` : '(闲聊)') + '。');
  else if (outcome.kind === 'reservation') await sendText(chatId, '🛑 已停止尚未启动的任务，正式 AI 进程不会启动。');
  else if (outcome.kind === 'preflight') await sendText(chatId, '🛑 已停止等待 AI 线路检测，正式任务不会启动。');
  else await sendText(chatId, '当前没有正在运行的任务。');
}

async function onMessage(data) {
  let unclaimedInlineImages = [];
  try {
    const msg = data.message || {};
    const mid = msg.message_id;
    if (!mid || seen.has(mid)) return;
    seen.add(mid); if (seen.size > 500) seen.clear();
    const chatId = msg.chat_id;
    const senderOpen = data.sender && data.sender.sender_id && data.sender.sender_id.open_id;

    // Authorization precedes resource downloads. Without this early boundary an excluded sender could
    // still make the bot fetch images even though their later text would be rejected.
    const access = readConfigForAccess();
    if (!access.ok) {
      logLine('拒绝消息:config.json 当前不可读取: ' + (access.error && access.error.message || access.error)); return;
    }
    const accessCfg = access.config;
    if (!senderIsAllowed(senderOpen, accessCfg)) {
      logLine('拒绝未授权发送者: ' + (senderOpen || '(missing open_id)')); return;
    }
    const claimImages = (canRead, text) => {
      const claimed = withPendingImages(chatId, senderOpen, canRead, text, unclaimedInlineImages);
      unclaimedInlineImages = [];
      return claimed;
    };
    const reportClaimOmission = async claimed => {
      if (!claimed || !claimed.omitted) return;
      try { await sendText(chatId, `⚠️ 本次图文合计超过最多 ${PENDING_IMG_MAX} 张,另有 ${claimed.omitted} 张未交给 AI。当前消息里的图片优先保留。`); }
      catch (e) { logLine('图文合并上限告警发送失败,继续处理主请求: ' + (e && (e.message || e))); }
    };

    // A photo plus caption/question sent as one Feishu bubble arrives as a localized rich-text `post`.
    // Download up to the bounded per-message image limit, retain them on THIS event, then continue through the exact
    // same text/router/security path as a normal message so the model receives one atomic question.
    if (msg.message_type === 'post') {
      const post = parsePostContent(msg.content);
      if (!post.ok) {
        logLine(`图文消息解析失败 chat=${chatId} reason=${post.error}`);
        await sendText(chatId, '⚠️ 这条图文消息的结构无法解析。请重新发送,或把图片和问题分成两条消息。');
        return;
      }
      let downloaded = { requested: 0, downloaded: 0, files: [], failures: [], omitted: 0 };
      if (post.imageKeys.length) {
        downloaded = await downloadPostImages(mid, post.imageKeys, chatId, senderOpen);
        unclaimedInlineImages = downloaded.files.slice();
        logLine(`收到图文消息 chat=${chatId} sender=${senderOpen} images=${downloaded.downloaded}/${downloaded.requested} text=${post.text.length}`);
        if (downloaded.failures.length || downloaded.omitted) {
          const notes = [];
          if (downloaded.failures.length) notes.push(`${downloaded.failures.length} 张下载失败`);
          if (downloaded.omitted) notes.push(`${downloaded.omitted} 张超过单条最多 ${PENDING_IMG_MAX} 张的处理上限`);
          try {
            await sendText(chatId, `⚠️ 图文消息中${notes.join('、')}。${post.text ? '我会继续处理文字和已下载的图片。' : '请重新发送未处理的图片。'}`);
          } catch (e) {
            // This warning is secondary. A transient notification failure must not discard the valid
            // caption and resources already bound to this event.
            logLine('图文图片告警发送失败,继续处理主请求: ' + (e && (e.message || e)));
          }
        }
      } else {
        logLine(`收到纯文字 post chat=${chatId} sender=${senderOpen} text=${post.text.length}`);
      }
      if (!post.text) {
        if (downloaded.downloaded) {
          let count = 0, rejected = 0;
          for (const file of downloaded.files) {
            const parked = addPendingImage(chatId, senderOpen, file);
            if (parked.accepted) count = parked.count; else rejected++;
          }
          unclaimedInlineImages = [];
          await sendText(chatId, `🖼 收到图文消息里的图片(共 ${count} 张待处理)。\n接着发一条文字说明你想让我做什么,我会带上这些图片一起看。` +
            (rejected ? `\n⚠️ 另有 ${rejected} 张超过待处理上限,已丢弃。` : ''));
        } else if (!post.imageKeys.length) {
          await sendText(chatId, '⚠️ 这条图文消息里没有可处理的文字或图片。');
        }
        return;
      }
      msg.message_type = 'text';
      msg.content = JSON.stringify({ text: post.text });
    }

    // IMAGES: a Feishu image is its own caption-less message. Download it, park it, and attach it to
    // the user's NEXT text message — silently dropping it (the old behavior) looked like "发不了图片".
    if (msg.message_type === 'image') {
      if ((msg.chat_type || 'p2p') === 'p2p') rememberUserChat(senderOpen, chatId);
      let key = '';
      try { key = JSON.parse(msg.content || '{}').image_key || ''; } catch (e) {}
      if (!key) return;
      logLine(`收到图片 chat=${chatId} sender=${senderOpen} key=${String(key).slice(-8)}`);
      try {
        const file = await downloadMessageImage(mid, key, chatId, senderOpen, '.png');
        const parked = addPendingImage(chatId, senderOpen, file);
        if (parked.accepted) {
          await sendText(chatId, `🖼 收到图片(共 ${parked.count} 张待处理)。\n接着发一条文字说明你想让我做什么,我会带上这些图片一起看。`);
        } else {
          await sendText(chatId, `⚠️ 已有 ${parked.count} 张图片待处理,达到上限。请先发送文字处理它们,再继续发图。`);
        }
      } catch (e) {
        logLine('下载图片失败: ' + (e && (e.message || e)));
        await sendText(chatId, '⚠️ 图片收到了但下载失败,可能是机器人缺少「im:resource」权限、图片超过 10MB 或下载超时。\n' + (e && e.message ? '(' + e.message + ')' : ''));
      }
      return;
    }
    if (msg.message_type !== 'text') {
      // any other rich type (file/audio/post…): say so instead of going silent
      logLine('收到不支持的消息类型: ' + msg.message_type);
      await sendText(chatId, `暂时只支持文字和图片(收到的是 ${msg.message_type})。图片可以直接发,我会带进下一条消息一起看。`);
      return;
    }

    // remember every user's own p2p chat (bot-menu events carry no chat_id, so replies need this).
    // P2P ONLY: an @-mention in a GROUP must not poison the mapping, or the user's next bottom-menu
    // tap would post the control card into the group and reset the group's session.
    const isP2P = (msg.chat_type || 'p2p') === 'p2p';
    if (isP2P) rememberUserChat(senderOpen, chatId);
    // feishuChatId is the OWNER's notification chat. Rebind rules (each one load-bearing):
    // - p2p only: an owner @-ing the bot in a group must not leak checker/authorize notifications there;
    // - sender must be EXPLICITLY in feishuAuthOpenIds (bootstrap-unlocked strangers don't count);
    // - bootstrap exception: while unlocked AND no chat is bound yet, the first p2p message binds it.
    const bindRequest = { openId: senderOpen, chatId, isP2P, currentFeishuChatId: accessCfg.feishuChatId };
    // 同一事件先受入口快照约束:none/viewer/已绑定时不进入写锁,避免磁盘配置随后变化
    // 导致本事件获得新的 owner 绑定副作用。真正写入时仍在锁内重读并复核最新配置。
    if (canBindOwnerChat(bindRequest, accessCfg)) {
      try {
        let bound = false;
        updateConfig(c => {
          if (!canBindOwnerChat({ ...bindRequest, currentFeishuChatId: c.feishuChatId }, c)) return false;
          c.feishuChatId = chatId; bound = true; return true;
        });
        if (bound) logLine('已记录通知 chatId(owner): ' + chatId);
      } catch (e) { logLine('记录通知 chatId 失败:' + (e && e.message || e)); }
    }

    let text = '';
    try { text = JSON.parse(msg.content || '{}').text || ''; } catch (e) {}
    text = stripMentions(text, msg.mentions);
    if (!text) return;
    // The user's own message is now below the old control card. Advance the visibility generation before
    // any async handler can reuse that old message id; later navigation must create a fresh bottom card.
    invalidateControlCard(chatId);
    logLine(`收到消息 chat=${chatId} sender=${senderOpen}: ${text}`);
    const low = text.toLowerCase();

    // password unlock: authorize this account for project/config ops (idle only — inside a
    // conversation a sentence starting with these words belongs to the conversation)
    const um = getSession(chatId).mode === 'idle' ? text.match(/^(解锁|认证|密码|auth|unlock)\s+(.+)$/i) : null;
    if (um) {
      let unlocked = false, hasPassword = false;
      try {
        updateConfig(c2 => {
          hasPassword = !!c2.feishuAuthPassword;
          if (!hasPassword || um[2].trim() !== String(c2.feishuAuthPassword)) return false;
          const list = Array.isArray(c2.feishuAuthOpenIds) ? c2.feishuAuthOpenIds.slice() : [];
          if (senderOpen && list.indexOf(senderOpen) === -1) list.push(senderOpen);
          c2.feishuAuthOpenIds = list; unlocked = true; return true;
        });
      } catch (e) { logLine('解锁写配置失败:' + (e && e.message || e)); }
      if (unlocked) await sendText(chatId, '✅ 已授权本账号,现在可以操作项目了。');
      else await sendText(chatId, hasPassword ? '❌ 密码错误。' : '未设置解锁密码(在服务器 config.json 的 feishuAuthPassword 里设)。');
      return;
    }

    // permission management (owners only): 授权 / 取消授权 / 只读授权 / 取消只读 / 授权列表
    if (/^(授权列表|权限列表|谁有权限)$/.test(text)) {
      if (await denyConfig(senderOpen, chatId, accessCfg)) return;
      const full = (accessCfg.feishuAuthOpenIds || []).filter(Boolean);
      await sendText(chatId, '✅ 可改项目(仅以下人):\n' + (full.length ? full.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 未锁定,所有人可改)') +
        '\n\n👁 其他所有人 = 只读浏览(自动,无需授权)。');
      return;
    }
    // anchored: keyword alone -> usage, or keyword + ou_id. NOT a bare 只读 (that's the query prefix).
    const am = text.match(/^(授权|取消授权|解除授权|只读授权|取消只读)(?:\s+(ou_[A-Za-z0-9]+))?\s*$/);
    if (am) {
      if (await denyConfig(senderOpen, chatId, accessCfg)) return;
      const id = am[2];
      if (!id) { await sendText(chatId, '用法:\n「授权 ou_xxx」= 可改项目\n「只读授权 ou_xxx」= 只能查询\n「取消授权 / 取消只读 ou_xxx」= 移除\n「授权列表」查看。\n让对方给我发条消息,他会看到自己的 open_id。'); return; }
      const kind = am[1];
      let unlocked = false;
      try {
        updateConfig(c => {
          let full = (c.feishuAuthOpenIds || []).filter(Boolean), view = (c.feishuViewerOpenIds || []).filter(Boolean);
          const hadFull = full.length;
          if (kind === '授权') { if (full.indexOf(id) === -1) full.push(id); view = view.filter(x => x !== id); }
          else if (kind === '只读授权') { if (view.indexOf(id) === -1) view.push(id); full = full.filter(x => x !== id); }
          else { full = full.filter(x => x !== id); view = view.filter(x => x !== id); unlocked = !!(hadFull && !full.length); }
          c.feishuAuthOpenIds = full; c.feishuViewerOpenIds = view;
        });
      } catch (e) { await sendText(chatId, '❌ 权限配置保存失败,请稍后重试。'); return; }
      if (kind === '授权') await sendText(chatId, '✅ 已授权(可改):' + id);
      else if (kind === '只读授权') await sendText(chatId, '👁 已授权(只读):' + id);
      else {
        await sendText(chatId, '已移除:' + id);
        if (unlocked) await sendText(chatId, '⚠️ 可改名单已空 = 解除锁定,现在所有人都能改你的项目/改配置/授权他人!要保持锁定,请「授权 ou_你自己」。');
      }
      return;
    }

    // ---- global commands (work in any mode) ----
    if (['帮助', 'help', '?', '？'].indexOf(low) !== -1) { await sendText(chatId, helpText(chatId, senderOpen, accessCfg)); return; }
    if (['状态', 'status', 'zt'].indexOf(low) !== -1) { if (await denyProject(senderOpen, chatId, accessCfg)) return; await sendText(chatId, statusText(chatId, senderOpen, accessCfg)); return; }
    if (['项目', 'list', '项目列表', '列出项目', '所有项目', '菜单', 'menu'].indexOf(low) !== -1) { cancelSessionCardLoad(chatId, '文字命令返回菜单'); setSession(chatId, { mode: 'idle' }); await enqueueControlCard(chatId, null, buildMenuCard(chatId, senderOpen, accessCfg)); return; }
    if (['退出', '返回', 'exit', 'quit', '退出项目', '主菜单'].indexOf(low) !== -1) {
      cancelSessionCardLoad(chatId, '文字命令退出');
      setSession(chatId, { mode: 'idle' });
      await enqueueControlCard(chatId, null, buildMenuCard(chatId, senderOpen, accessCfg));   // back to the main menu (idle)
      return;
    }
    if (['闲聊', '闲聊模式', 'chat'].indexOf(low) !== -1) {
      cancelSessionCardLoad(chatId, '文字命令进入闲聊');
      setSession(chatId, { mode: 'chat' });
      await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。发「退出」回主菜单。');
      return;
    }
    // AI: show or set one caller's provider/model. The picker refreshes real provider health and
    // exposes only available services; text commands are checked against that same result.
    if (['模型', 'model', '闲聊模型'].indexOf(low) !== -1) {
      await openStandaloneModelPicker(chatId, senderOpen, accessCfg);
      return;
    }
    const setm = text.match(/^(模型|闲聊模型|model)\s+(\S+)$/i);
    if (setm) {
      const owner = canUseOwnerOnlyProfile(senderOpen, accessCfg);
      const picked = parseProfileInput(setm[2], owner);
      if (!picked) {
        await sendText(chatId, `不认识或无权使用「${setm[2]}」。可用:sol / v4 / v4pro / claude / opus / sonnet / haiku${owner ? ' / fable,或完整 claude-* id' : ''}。`);
        return;
      }
      await ensureFreshProviderHealth('文字命令选择模型');
      if (!providerIsAvailable(picked.provider)) {
        await sendText(chatId, `${picked.fullLabel} 当前不可用（${providerReasonText(providerHealth(picked.provider))}），未切换。发「模型」查看实测可用的 AI。`);
        return;
      }
      cancelSessionCardLoad(chatId, '文字命令切换 AI');
      setUserProfileId(senderOpen, picked.id);
      await sendText(chatId, `你的 AI 已设为:${picked.fullLabel}(只影响你自己,下一句生效)。`);
      return;
    }
    // forget chat memory (drop the started flag + the claude session for the chat cwd)
    if (['忘记闲聊', '清空闲聊', '重置闲聊', '忘记记忆', 'forget', 'reset chat'].indexOf(low) !== -1) {
      const cleared = await sessionManager.forgetChat(senderOpen);
      await sendText(chatId, `已清空你自己的闲聊记忆(删除会话 ${cleared.deleted} 个),下次闲聊从头开始。`);
      return;
    }
    if (['忘记查询', '清空查询', '重置查询'].indexOf(low) !== -1) {
      // D-003:level=none 不解析 activeProject(避免项目发现),也不清空项目查询记忆。
      if (!canProject(senderOpen, accessCfg)) {
        await sendText(chatId, '先进入一个项目,再发「忘记查询」清空它的只读查询记忆。');
        return;
      }
      // D-003:activeProject 用事件 cfg 快照的项目列表解析,不二次读配置。
      const ap = activeProject(chatId, discoverProjects(accessCfg));
      if (!ap) { await sendText(chatId, '先进入一个项目,再发「忘记查询」清空它的只读查询记忆。'); return; }
      const cleared = await sessionManager.clearQuery(ap.path, senderOpen, getUserProfile(senderOpen, accessCfg).id);
      await sendText(chatId, `🧹 已清空你在「${ap.name}」的只读查询记忆(删除会话 ${cleared.deleted} 个)。下次查询从头开始。`);
      return;
    }
    if (/^(停止|stop)(\s|$)/i.test(text)) {   // \b never matches between 止 and a space/CJK char
      const rest = text.replace(/^(停止|stop)\s*/i, '').trim();
      await stopRuns(chatId, senderOpen, rest || null, accessCfg);
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
      if (await denyProject(senderOpen, chatId, accessCfg)) return;
      const p = findProject(m[2], accessCfg);
      if (p) { await enterProject(chatId, senderOpen, p, accessCfg); }
      else await sendText(chatId, `没找到项目「${m[2]}」。\n\n` + listText(accessCfg));
      return;
    }

    // greetings: show the button menu (Telegram-style) so it's easy to pick chat vs a project
    if (inIdle && /^(你好|您好|hi|hello|hey|哈喽|在吗|在么|在不在|在|你好呀|嗨|yo|start|开始)$/i.test(text)) {
      await enqueueControlCard(chatId, null, buildMenuCard(chatId, senderOpen, accessCfg));
      return;
    }

    // bare project name/number -> enter it (idle only)
    // D-003:level=none 必须先判权再扫描,绝不调用 projectIfBareName。
    const bare = (inIdle && canProject(senderOpen, accessCfg)) ? projectIfBareName(text, accessCfg) : null;
    if (bare) { if (await denyProject(senderOpen, chatId, accessCfg)) return; await enterProject(chatId, senderOpen, bare, accessCfg); return; }
    // "<project> <command>" -> one-off run (idle only), doesn't change the current mode
    // D-003:level=none 必须先判权再扫描,绝不调用 oneOffTarget。
    const oneoff = (inIdle && canProject(senderOpen, accessCfg)) ? oneOffTarget(text, accessCfg) : { project: null };
    if (oneoff.project) {
      if (await denyProject(senderOpen, chatId, accessCfg)) return;
      const qm = oneoff.prompt.match(QUERY_RE);
      const isQuery = authLevel(senderOpen, accessCfg) === 'viewer' || !!qm;   // viewer forced RO; owner opts in via 查询/只读
      const q = qm ? qm[2] : oneoff.prompt;
      if (isQuery) {
        const qk = querySession(oneoff.project.path, senderOpen, getUserProfile(senderOpen, accessCfg).id).cwd.toLowerCase();
        const qimg = claimImages(isListedOwner(senderOpen, accessCfg), q);
        await reportClaimOmission(qimg);
        if (qimg.blocked) await sendText(chatId, '(你发的图片我看不了:只读用户的项目查询没有读取本机图片的权限,已忽略。)');
        // S1-F:running+reservation 在同一个同步操作内原子检查并预占,然后后台运行保证 handler 秒回。
        if (!taskOrchestrator.tryReserve(qk)) { cleanupInboundImages(qimg.files); await sendText(chatId, `你对「${oneoff.project.name}」的查询进行中,请稍候。`); return; }
        bg('一次性查询', qk, async () => {
          try {
            await sendText(chatId, `🔍 只读查询「${oneoff.project.name}」:${q}` + (qimg.n && !qimg.blocked ? `\n(带上你发的 ${qimg.n} 张图片)` : '') + `\n(读代码/答疑,不改文件 · 你专属的查询会话,别人看不到)`);
            const r = await runProjectQuery(chatId, oneoff.project, qimg.prompt, senderOpen, accessCfg);
            await sendResult(chatId, (r.ok ? '✅ 查询结果 · ' + oneoff.project.name : '⚠️ 查询未完成 · ' + oneoff.project.name), r.text, r, r.ok ? 'blue' : 'red');
            logLine(`一次性查询 ${oneoff.project.name} ok=${r.ok}`);
          } finally { cleanupInboundImages(qimg.files); }
        });
        return;
      }
      const ok1 = oneoff.project.path.toLowerCase();
      const oimg = claimImages(true, q);
      await reportClaimOmission(oimg);
      if (!taskOrchestrator.tryReserve(ok1)) { cleanupInboundImages(oimg.files); await sendText(chatId, `「${oneoff.project.name}」正在执行中,请稍候。`); return; }
      bg('一次性执行', ok1, async () => {
        let prog = null, done = null;
        try {
          await sendText(chatId, `📂 一次性在「${oneoff.project.name}」执行:${q}` + (oimg.n ? `\n(带上你发的 ${oimg.n} 张图片)` : '') + `\n(可能要 1-4 分钟,跑完自动回结果)`);
          const outDir = prepImageOut(oneoff.project.path);
          prog = await startProgress(chatId, oneoff.project.name);
          done = trackRun(chatId, oneoff.project.name, '执行');
          const ids = new Map();
          const r = await runForUser(oneoff.project.path, oneoff.project.name, imageHint(outDir) + oimg.prompt, senderOpen, {
            taskKind: 'modify',
            forProfile: profile => {
              if (!ids.has(profile.id)) ids.set(profile.id, profile.engine === 'claude' ? crypto.randomUUID() : null);
              return { sessionId: ids.get(profile.id), sessionExists: false, useContinue: false };
            },
          }, accessCfg);
          await prog.stop(r);
          await sendResult(chatId, (r.ok ? '✅ 完成 · ' + oneoff.project.name : '⚠️ 未完成 · ' + oneoff.project.name), r.text, r, r.ok ? 'green' : 'red');
          if (r.ok) await drainImageOut(chatId, oneoff.project.path);
          logLine(`一次性完成 ${oneoff.project.name} ok=${r.ok}`);
        } finally {
          if (prog) await prog.stop();
          if (done) done();
          cleanupInboundImages(oimg.files);
        }
      });
      return;
    }

    // ---- mode dispatch ----
    // D-003:level=none 不解析 activeProject(其内部触发项目发现),旧 project session 按无项目处理。
    const active = canProject(senderOpen, accessCfg) ? activeProject(chatId, discoverProjects(accessCfg)) : null;
    if (active) {   // project mode: route by the chosen sub-mode (只读查询 / 修改项目)
      if (await denyProject(senderOpen, chatId, accessCfg)) return;
      const level = authLevel(senderOpen, accessCfg);
      let sub = getSession(chatId).sub;
      if (level === 'viewer') sub = 'query';                 // viewers are always read-only
      const qm = text.match(QUERY_RE);                       // 查询/只读 prefix = one-off read-only override
      // no sub-mode chosen yet -> ask via the project card first (unless an explicit 查询 prefix)
      if (!sub && !qm) { await enqueueControlCard(chatId, null, buildProjectCard(chatId, senderOpen, accessCfg)); return; }
      if (sub === 'query' || qm) {
        const qk = querySession(active.path, senderOpen, getUserProfile(senderOpen, accessCfg).id).cwd.toLowerCase();   // per-user query cwd, not project.path
        const q = qm ? qm[2] : text;
        const qimg = claimImages(isListedOwner(senderOpen, accessCfg), q);
        await reportClaimOmission(qimg);
        if (qimg.blocked) await sendText(chatId, '(你发的图片我看不了:只读用户的项目查询没有读取本机图片的权限,已忽略。)');
        if (!taskOrchestrator.tryReserve(qk)) { cleanupInboundImages(qimg.files); await sendText(chatId, `你对「${active.name}」的查询进行中,请稍候。`); return; }
        bg('查询', qk, async () => {
          try {
            await sendText(chatId, `🔍 只读查询「${active.name}」:${q}` + (qimg.n && !qimg.blocked ? `\n(带上你发的 ${qimg.n} 张图片)` : '') + `\n(读代码/答疑,不改文件 · 你专属的查询会话,别人看不到)`);
            const r = await runProjectQuery(chatId, active, qimg.prompt, senderOpen, accessCfg);
            await sendResult(chatId, (r.ok ? '✅ 查询结果 · ' + active.name : '⚠️ 查询未完成 · ' + active.name), r.text, r, r.ok ? 'blue' : 'red');
            logLine(`查询 ${active.name} ok=${r.ok}`);
          } finally { cleanupInboundImages(qimg.files); }
        });
        return;
      }
      // ✏️修改 continues a SPECIFIC conversation the user picked from the session list. No pick yet
      // (or it's a fresh entry) -> show the picker instead of guessing.
      const selected = getSession(chatId);
      const work = selected.work;
      if (!work) { requestSessionCard(chatId, lastCard.get(chatId), senderOpen, accessCfg); return; }
      const mk = active.path.toLowerCase();
      const img = claimImages(true, text);   // modify is owner-only -> local image tools available
      await reportClaimOmission(img);
      if (!taskOrchestrator.tryReserve(mk)) { cleanupInboundImages(img.files); await sendText(chatId, `「${active.name}」正在执行中,请稍候,或发「停止」/点底部「🛑 停止」取消。`); return; }
      bg('执行', mk, async () => {
        let prog = null, done = null;
        try {
          await sendText(chatId, `📂 在「${active.name}」执行:${text}` + (img.n ? `\n(带上你发的 ${img.n} 张图片)` : ''));
          const outDir = prepImageOut(active.path);
          prog = await startProgress(chatId, active.name);
          done = trackRun(chatId, active.name, '执行');
          const currentProfile = getUserProfile(senderOpen, accessCfg);
          const selectedProfile = profileById(selected.workProfile) || currentProfile;
          let runPrompt = imageHint(outDir) + img.prompt;
          if (selected.workProfile && selected.workProfile !== currentProfile.id && work !== 'new') {
            let digest = '';
            try {
              const old = (await listSessionsForProfile(active.path, selectedProfile, 20)).find(x => x.id === work);
              digest = old ? await sessionPreviewFor(selectedProfile, old, 2) : '';
            } catch (e) {}
            runPrompt = `[AI 提供商已切换] 上一个工作会话属于「${selectedProfile.fullLabel}」,当前改用「${currentProfile.fullLabel}」。不同提供商不能直接复用同一会话 ID,请根据项目当前文件状态和下面的最近摘要继续。\n${digest || '(未读取到旧会话摘要)'}\n\n${runPrompt}`;
          }
          const ids = new Map();
          const r = await runForUser(active.path, active.name, runPrompt, senderOpen, {
            taskKind: 'modify',
            forProfile: profile => {
              const canResumePicked = profile.id === selectedProfile.id && work !== 'new';
              if (!ids.has(profile.id)) ids.set(profile.id, canResumePicked ? work : (profile.engine === 'claude' ? crypto.randomUUID() : null));
              const sessionId = ids.get(profile.id);
              return { sessionId, sessionExists: canResumePicked && querySessionExists(sessionId, profile.id), useContinue: false };
            },
          }, accessCfg);
          if (r.ok && r.profile && r.sessionId) {
            setSession(chatId, {
              mode: 'project', project: active.path, sub: 'modify', work: r.sessionId,
              workProfile: r.profile.id,
              workTitle: r.profile.id === selected.workProfile ? selected.workTitle : `${r.profile.label} 新会话`,
            });
          }
          await prog.stop(r);
          await sendResult(chatId, (r.ok ? '✅ 完成 · ' + active.name : '⚠️ 未完成 · ' + active.name), r.text, r, r.ok ? 'green' : 'red');
          if (r.ok) await drainImageOut(chatId, active.path);
          logLine(`完成 ${active.name} ok=${r.ok} session=${String(r.sessionId || work).slice(0, 8)} ai=${r.profile && r.profile.id}`);
        } finally {
          if (prog) await prog.stop();
          if (done) done();
          cleanupInboundImages(img.files);
        }
      });
      return;
    }
    if (getSession(chatId).mode === 'chat') {   // chat mode: talk to the caller's selected AI
      const primaryChat = chatSession(senderOpen, getUserProfile(senderOpen, accessCfg).id);
      const ck = primaryChat.cwd.toLowerCase();
      // SECURITY: chat is open to everyone, so only the OWNER gets full tools (skip-permissions —
      // WebSearch/Bash/Read like the web app). A non-owner (viewer) gets a read-only chat: plan mode
      // + no file/exec tools, so they can't Bash-modify files or Read the bot's ../config.json secrets.
      const chatOwner = canUsePrivilegedTools(senderOpen, accessCfg);   // 文件/执行工具只给显式 owner
      const cimg = claimImages(chatOwner, text);   // viewer chat has no local image tools
      await reportClaimOmission(cimg);
      if (cimg.blocked) await sendText(chatId, '(你发的图片我看不了:只读用户的闲聊没有读取文件的权限,已忽略。)');
      if (!taskOrchestrator.tryReserve(ck)) { cleanupInboundImages(cimg.files); await sendText(chatId, '上一句还在想,请稍候…'); return; }
      bg('闲聊', ck, async () => {
        let prog = null, done = null;
        try {
          await sendText(chatId, '🤔 正在思考…' + (cimg.n && !cimg.blocked ? `(带上你发的 ${cimg.n} 张图片)` : ''));
          logLine(`闲聊 思考中: ${text}`);
          // only the owner's chat has file tools (skip-permissions) and so can produce an image to send;
          // a non-owner chat is read-only (no Write/Bash) and can't, so skip the image channel for them.
          const outDir = chatOwner ? prepImageOut(primaryChat.cwd) : null;
          prog = await startProgress(chatId, '闲聊');
          done = trackRun(chatId, '闲聊', '闲聊');
          const r = await runForUser(primaryChat.cwd, '闲聊', (outDir ? imageHint(outDir) : '') + cimg.prompt, senderOpen, {
            taskKind: 'chat',
            runKey: primaryChat.cwd,
            skipPermissions: chatOwner, readOnly: !chatOwner,
            disallowedTools: chatOwner ? undefined : ['Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit'],
            noTools: !chatOwner,
            forProfile: profile => {
              const cs = chatSession(senderOpen, profile.id);
              return {
                cwd: cs.cwd, sessionId: cs.sessionId,
                sessionExists: cs.profile.engine === 'codex' ? cs.started : (cs.started && querySessionExists(cs.sessionId, profile.id)),
                useContinue: false,
              };
            },
          }, accessCfg);
          if (r.ok && r.profile && r.sessionId) markChatStarted(senderOpen, r.profile.id, { sessionId: r.sessionId });
          await prog.stop(r);
          await sendResult(chatId, r.ok ? '💬 闲聊' : '⚠️ 闲聊', (r.text || '(无输出)') + '\n\n———\n💬 闲聊模式 · 发「菜单」切换', r, r.ok ? 'green' : 'red');
          if (outDir && r.ok) await drainImageOut(chatId, primaryChat.cwd);
          logLine(`闲聊 完成 ok=${r.ok}`);
        } finally {
          if (prog) await prog.stop();
          if (done) done();
          cleanupInboundImages(cimg.files);
        }
      });
      return;
    }
    // idle mode: don't run anything — show the menu so the user picks a mode first
    await enqueueControlCard(chatId, null, buildMenuCard(chatId, senderOpen, accessCfg));
  } catch (e) { logLine('处理消息异常: ' + (e && e.stack || e)); }
  finally { cleanupInboundImages(unclaimedInlineImages); }
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
    const evId = ev.event_id || (ev.header && ev.header.event_id) || '';
    const evTime = parseInt((ev.header && ev.header.create_time) || ev.create_time || '0', 10);
    if (evId) {
      if (seenEid.has(evId)) { logLine('忽略重复卡片事件(eid)'); return; }
      seenEid.add(evId); if (seenEid.size > 3000) seenEid.clear();
    }
    if (evTime && Date.now() - evTime > 60000) { logLine('忽略过期卡片事件 age=' + Math.round((Date.now() - evTime) / 1000) + 's'); return; }
    const key = chatId + ':' + (messageId || '') + ':' + JSON.stringify(val) + ':' + (senderOpen || '');
    const now = Date.now();
    // event_id is the real redelivery key. Without it, keep only a very short debounce for SDKs that
    // omit headers; a 4s window swallowed legitimate sesslist -> back -> sesslist round trips.
    if (!evId && cardSeen.get(key) && now - cardSeen.get(key) < 350) return;
    cardSeen.set(key, now); if (cardSeen.size > 300) cardSeen.clear();

    const access = readConfigForAccess();
    if (!access.ok) { logLine('拒绝卡片点击:config.json 当前不可读取: ' + (access.error && access.error.message || access.error)); return; }
    const cfg = access.config;
    if (!senderIsAllowed(senderOpen, cfg)) { logLine('拒绝未授权点击: ' + (senderOpen || '(missing open_id)')); return; }
    // Do NOT learn userChats here at all: card events carry no chat_type, so a click in a GROUP would
    // bind userChats[open_id] = group-id and then route the user's private bottom-menu into the group
    // (and reset the group session). userChats is learned ONLY from p2p messages (onMessage, isP2P);
    // a user who has never messaged still gets replies via the 'od:'+open_id fallback in userTarget.
    logLine(`卡片点击 chat=${chatId} sender=${senderOpen}: ${JSON.stringify(val)}`);

    // The WS registration boundary dispatches this handler through bg() and returns immediately. Keep
    // state transitions synchronous where possible; bounded background I/O may safely await internally.
    const projectActs = ['status', 'enter', 'submode', 'clearq'];              // viewers allowed; clearq 属于 project-gated
    // full only — incl. the modify-session flow (sesslist/pick/newsess/backproj): a viewer must not
    // browse the owner's work-session titles/digests or flip a session's work pointer
    // 'model' is NOT here: everyone may set THEIR OWN model (per-user); the action itself blocks a
    // non-owner from selecting the owner-only Fable 5.
    const configActs = ['perm', 'authorize', 'revoke', 'viewauth', 'viewrevoke', 'sesslist', 'pick', 'newsess', 'backproj'];
    // D-003:本事件已读取的 cfg 快照必须用于本项目/config action gate,避免二次读配置 TOCTOU。
    if (projectActs.indexOf(val.do) !== -1 && !canProject(senderOpen, cfg)) { denyProject(senderOpen, chatId, cfg); return; }
    if (configActs.indexOf(val.do) !== -1 && !canConfig(senderOpen, cfg)) { denyConfig(senderOpen, chatId, cfg); return; }
    const boundProjectActs = ['submode', 'sesslist', 'clearq'];
    if (boundProjectActs.indexOf(val.do) !== -1 && !val.k && !validProjectCard(chatId, val)) {
      rejectStaleSessionPicker(chatId, messageId, senderOpen, val);
      return;
    }

    if (val.do === 'chat') { cancelSessionCardLoad(chatId, '进入闲聊'); setSession(chatId, { mode: 'chat' }); enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg)); return; }
    if (val.do === 'home') { cancelSessionCardLoad(chatId, '返回主菜单'); setSession(chatId, { mode: 'idle' }); enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg)); return; }   // leave project mode (avoid accidental typed-modify)
    if (val.do === 'status') { sendText(chatId, statusText(chatId, senderOpen, cfg)); return; }
    if (val.do === 'perm') {
      // D-003:渲染复用本事件 cfg 快照,不再次读配置。
      const full = (cfg.feishuAuthOpenIds || []).filter(Boolean);
      sendText(chatId, '✅ 可改项目(仅以下人):\n' + (full.length ? full.map((x, i) => `${i + 1}. ${x}`).join('\n') : '(无 — 未锁定,所有人可改!建议先发「授权 你的open_id」)') +
        '\n\n👁 其他所有人 = 只读浏览查询(自动,无需授权)。\n\n想让某人也能改:发「授权 ou_xxx」(对方 open_id 会在他给我发消息时显示)。');
      return;
    }
    const sessionProjectActs = ['submode', 'sesslist', 'clearq', 'backproj', 'pick', 'newsess'];
    let boundProject = null;
    if (sessionProjectActs.indexOf(val.do) !== -1) {
      boundProject = listedSessionProject(chatId, cfg);
      if (!boundProject) {
        cancelSessionCardLoad(chatId, '项目已隐藏或移除');
        setSession(chatId, { mode: 'idle' });
        await enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg));
        return;
      }
    }
    if (val.do === 'noop') { return; }
    if (val.do === 'modelclose') {
      enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg));
      return;
    }
    if (val.do === 'modelmenu') {
      cancelSessionCardLoad(chatId, '打开 AI 选择');
      const origin = val.from === 'm' ? 'm' : 'nav';
      if (origin === 'm') {
        const pending = providerHealthIsStale() ? refreshProviderHealth('返回 AI 服务列表') : null;
        await enqueueControlCard(chatId, messageId, buildModelCard(chatId, senderOpen, { origin: 'm' }, cfg), { promoteToLive: false });
        if (pending) {
          await pending;
          await enqueueControlCard(chatId, messageId, buildModelCard(chatId, senderOpen, { origin: 'm' }, cfg), { promoteToLive: false });
        }
      } else {
        await openNavigationModelPicker(chatId, messageId, senderOpen, cfg);
      }
      return;
    }
    if (val.do === 'modelprovider') {
      cancelSessionCardLoad(chatId, '选择 AI 服务');
      await ensureFreshProviderHealth('选择 AI 服务');
      const origin = val.from === 'm' ? 'm' : 'nav';
      const provider = providerIsAvailable(val.provider) ? val.provider : undefined;
      await enqueueControlCard(
        chatId,
        messageId,
        buildModelCard(chatId, senderOpen, { origin, provider }, cfg),
        origin === 'm' ? { promoteToLive: false } : undefined
      );
      return;
    }
    if (val.do === 'model') {
      const owner = canUseOwnerOnlyProfile(senderOpen, cfg);
      const raw = val.p !== undefined ? val.p : val.m;   // val.m keeps old cards functional
      const parsed = profileById(raw) || parseProfileInput(raw, owner);
      const picked = parsed && (!parsed.ownerOnly || owner) ? parsed : null;
      await ensureFreshProviderHealth('点击选择模型');
      const available = picked && providerIsAvailable(picked.provider);
      if (available) {
        cancelSessionCardLoad(chatId, '切换 AI');
        setUserProfileId(senderOpen, picked.id);
      } else {
        const label = picked ? picked.fullLabel : String(raw || '该模型');
        sendText(chatId, `${label} 当前不可用或无权使用，未切换。模型卡已按最新实测结果刷新。`);
      }
      // Standalone cards stay in their provider child page. Main-menu selection completes the flow
      // and returns to the main card; old direct-button cards follow the same rules.
      const origin = val.from === 'm' ? 'm' : 'nav';
      // D-003:授权判定(canUseOwnerOnlyProfile)已用本事件 cfg;选择成功后返回卡需反映刚写入的
      // 模型,因此只在此处刷新一次渲染快照(绝不逐决策重复读配置;读失败时回退本事件 cfg)。
      const renderCfg = available ? readConfig() : cfg;
      enqueueControlCard(
        chatId,
        messageId,
        origin === 'm'
          ? buildModelCard(chatId, senderOpen, { origin: 'm', provider: available ? picked.provider : undefined }, renderCfg)
          : buildMenuCard(chatId, senderOpen, renderCfg),
        origin === 'm' ? { promoteToLive: false } : undefined
      );
      return;
    }
    if (val.do === 'enter') {
      cancelSessionCardLoad(chatId, '切换项目');
      // D-003:入口 cfg 快照对应项目列表解析,不二次 readConfig。
      const p = discoverProjects(cfg).find(x => x.path.toLowerCase() === String(val.p).toLowerCase()) || null;
      if (!p) { sendText(chatId, '项目未找到(可能已变化)。发「菜单」重新选。'); return; }
      // owners pick 只读/修改 next; viewers go STRAIGHT to read-only query (their only capability)
      const viewer2 = authLevel(senderOpen, cfg) !== 'full';
      setSession(chatId, { mode: 'project', project: p.path, sub: viewer2 ? 'query' : undefined });
      enqueueControlCard(chatId, messageId, buildProjectCard(chatId, senderOpen, cfg));
      return;
    }
    if (val.do === 'submode') {
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg)); return; }
      let sm = (val.sm === 'modify') ? 'modify' : 'query';
      if (sm === 'modify' && authLevel(senderOpen, cfg) === 'viewer') { sm = 'query'; sendText(chatId, '👁 你是只读用户,只能查询,不能改项目。'); }
      if (sm === 'modify') {
        // 修改 = continue a specific conversation -> pick which one first (or start a fresh one)
        setSession(chatId, { mode: 'project', project: sess.project, sub: 'modify', work: sess.work, workProfile: sess.workProfile, workTitle: sess.workTitle });
        if (sess.work) {
          cancelSessionCardLoad(chatId, '已有工作会话');
          enqueueControlCard(chatId, messageId, buildProjectCard(chatId, senderOpen, cfg));
        } else requestSessionCard(chatId, messageId, senderOpen, cfg);
        return;
      }
      cancelSessionCardLoad(chatId, '切换到只读查询');
      setSession(chatId, { mode: 'project', project: sess.project, sub: sm, work: sess.work, workProfile: sess.workProfile, workTitle: sess.workTitle });
      enqueueControlCard(chatId, messageId, buildProjectCard(chatId, senderOpen, cfg));   // ✅ moves to the chosen mode
      return;
    }
    if (val.do === 'sesslist') {   // 🔀 切换会话
      if (val.k) {
        if (!validSessionPicker(chatId, val, senderOpen, cfg)) { rejectStaleSessionPicker(chatId, messageId, senderOpen, val); return; }
        rememberPickerAction(val, messageId);
      }
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg)); return; }
      setSession(chatId, { mode: 'project', project: sess.project, sub: 'modify', work: sess.work, workProfile: sess.workProfile, workTitle: sess.workTitle });
      requestSessionCard(chatId, messageId, senderOpen, cfg);
      return;
    }
    if (val.do === 'backproj') {
      if (!validSessionPicker(chatId, val, senderOpen, cfg)) { rejectStaleSessionPicker(chatId, messageId, senderOpen, val); return; }
      rememberPickerAction(val, messageId);
      cancelSessionCardLoad(chatId, '返回项目'); enqueueControlCard(chatId, messageId, buildProjectCard(chatId, senderOpen, cfg)); return;
    }
    if (val.do === 'pick' || val.do === 'newsess') {
      const picker = validSessionPicker(chatId, val, senderOpen, cfg);
      if (!picker) { rejectStaleSessionPicker(chatId, messageId, senderOpen, val); return; }
      rememberPickerAction(val, messageId);
      cancelSessionCardLoad(chatId, val.do === 'pick' ? '已选择会话' : '新建会话');
      const sess = getSession(chatId);
      if (sess.mode !== 'project' || !sess.project) { enqueueControlCard(chatId, messageId, buildMenuCard(chatId, senderOpen, cfg)); return; }
      const isNew = val.do === 'newsess';
      const profile = profileById(picker.profileId) || getUserProfile(senderOpen, cfg);
      const id = isNew ? (profile.engine === 'claude' ? crypto.randomUUID() : 'new') : String(val.s || '');
      if (!id) { requestSessionCard(chatId, messageId, senderOpen, cfg); return; }
      setSession(chatId, { mode: 'project', project: sess.project, sub: 'modify', work: id, workProfile: profile.id, workTitle: isNew ? '🆕 新会话' : String(val.t || '') });
      enqueueControlCard(chatId, messageId, buildProjectCard(chatId, senderOpen, cfg));   // back to the project card, session shown
      const selectedProject = sess.project;
      const selectionStillCurrent = () => {
        const current = getSession(chatId);
        // D-003:本回调在事件返回后的后台执行,必须观察「之后」发生的模型切换(用户在摘要
        // 读取期间换 AI 时旧摘要不得晚到);授权判定仍用本事件 cfg,此处属于跨事件活体检查。
        return current.mode === 'project'
          && current.sub === 'modify'
          && current.project
          && current.project.toLowerCase() === selectedProject.toLowerCase()
          && current.work === id
          && current.workProfile === profile.id
          && getUserProfile(senderOpen).id === profile.id;
      };
      // digest of the picked conversation so you know where you left off (fire-and-forget: keep the
      // callback fast; reading a transcript is local but can be a few MB)
      bg('会话摘要', null, async () => {
        if (isNew) {
          if (selectionStillCurrent()) await sendText(chatId, '🆕 已开一个**全新会话**,直接发指令即可(它不带任何历史)。');
          return;
        }
        const s = (await listSessionsForProfile(selectedProject, profile, 12)).find(x => x.id === id);
        const head = s ? `📝 已进入会话「${s.title}」(${shortTime(s.mtime)})` : `📝 已进入会话 ${id.slice(0, 8)}`;
        const pv = s ? await sessionPreviewFor(profile, s, 2) : '';
        if (selectionStillCurrent()) await sendText(chatId, head + (pv ? '\n\n最近的对话:\n' + pv : '') + '\n\n直接发指令继续这个会话。');
      });
      return;
    }
    if (val.do === 'clearq') {
      // 入口处已按本事件 cfg 快照验证并解析当前项目；隐藏/移除后旧卡不得恢复路径。
      const proj = boundProject;
      if (!proj) { sendText(chatId, '当前不在项目里,无法清空查询记忆。'); return; }
      const profileId = getUserProfile(senderOpen, cfg).id;
      // The production dispatch wrapper already ACKs immediately and serializes same-chat handlers.
      // Await deletion so a query arriving right after this click cannot resume the thread being deleted.
      const cleared = await sessionManager.clearQuery(proj.path, senderOpen, profileId);
      await sendText(chatId, `🧹 已清空你在「${proj.name}」的只读查询记忆(删除会话 ${cleared.deleted} 个)。下次查询从头开始。`);
      return;
    }
    // one-tap grant from the owner-notification card
    if (['authorize', 'revoke', 'viewauth', 'viewrevoke'].indexOf(val.do) !== -1) {
      const id = String(val.id || '');
      if (/^ou_[A-Za-z0-9]+$/.test(id)) {
        try {
          updateConfig(c => {
            let full = (c.feishuAuthOpenIds || []).filter(Boolean), view = (c.feishuViewerOpenIds || []).filter(Boolean);
            if (val.do === 'authorize') { if (full.indexOf(id) === -1) full.push(id); view = view.filter(x => x !== id); }
            else if (val.do === 'viewauth') { if (view.indexOf(id) === -1) view.push(id); full = full.filter(x => x !== id); }
            else { full = full.filter(x => x !== id); view = view.filter(x => x !== id); }
            c.feishuAuthOpenIds = full; c.feishuViewerOpenIds = view;
          });
          if (val.do === 'authorize') sendText(chatId, '✅ 已授权(可改):' + id);
          else if (val.do === 'viewauth') sendText(chatId, '👁 已授权(只读):' + id);
          else sendText(chatId, '已移除:' + id);
        } catch (e) { sendText(chatId, '❌ 权限配置保存失败,请稍后重试。'); }
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
    const access = readConfigForAccess();
    if (!access.ok) { logLine('拒绝底部菜单:config.json 当前不可读取: ' + (access.error && access.error.message || access.error)); return; }
    const cfg = access.config;
    if (!senderIsAllowed(senderOpen, cfg)) { logLine('拒绝未授权菜单点击: ' + (senderOpen || '(missing open_id)')); return; }
    // reply to the OPERATOR's own p2p chat (from userChats, else by open_id). NEVER to feishuChatId
    // — that's the owner's chat, and routing everyone's menus there was the "coworker clicks show up
    // in my chat / coworkers see nothing" bug.
    const chatId = userTarget(senderOpen) || cfg.feishuChatId;
    if (!chatId) { logLine('菜单点击但无法确定回复目标(无 open_id)'); return; }
    // dedup rapid repeat taps. The escape-hatch keys (menu/idle/exit/unknown) use a SHORT window so a
    // deliberate 主菜单 tap a couple seconds later still responds; text keys (chat/status) dedup longer.
    const escapeHatch = (key !== 'chat' && key !== 'status');
    const mkey = (chatId || '') + ':' + key + ':' + (senderOpen || ''); const mnow = Date.now();
    const dwin = escapeHatch ? 1500 : 3000;
    if (menuSeen.get(mkey) && mnow - menuSeen.get(mkey) < dwin) { logLine('忽略重复底部菜单点击: ' + key); return; }
    menuSeen.set(mkey, mnow); if (menuSeen.size > 200) menuSeen.clear();
    logLine('底部菜单点击: ' + key + (evId ? ' eid=…' + String(evId).slice(-6) : ' (无eid)') + (evTime ? ' age=' + Math.round((Date.now() - evTime) / 1000) + 's' : ' (无time)'));
    if (key === 'chat') { cancelSessionCardLoad(chatId, '底部菜单进入闲聊'); setSession(chatId, { mode: 'chat' }); await sendText(chatId, '已进入 💬 闲聊模式,直接说话就是和我聊天。随时点底部「主菜单」回来。'); return; }
    if (key === 'status') { if (await denyProject(senderOpen, chatId, cfg)) return; await sendText(chatId, statusText(chatId, senderOpen, cfg)); return; }
    // 🤖 switch model mid-conversation: post a STANDALONE model card (does not touch the session, so
    // your chat/project/modify context is untouched). Everyone may set THEIR OWN model — the card
    // hides Fable 5 from non-owners. Match several plausible console event_keys so a typo still works.
    if (['model', 'models', '模型', 'switchmodel', 'switch_model', 'setmodel'].indexOf(key) !== -1) {
      cancelSessionCardLoad(chatId, '打开 AI 选择');
      await openStandaloneModelPicker(chatId, senderOpen, cfg);
      return;
    }
    // 🛑 stop the running task straight from the bottom menu (no need to recall the 停止 command).
    // Owner-gated inside stopRuns. Match several plausible console event_keys so a console typo works.
    if (['stop', '停止', 'halt', 'cancel', 'abort', 'stoprun', 'stop_run', 'kill'].indexOf(key) !== -1) {
      await stopRuns(chatId, senderOpen, null, cfg);
      return;
    }
    // 主菜单 / idle / exit / 未知 —— the ESCAPE HATCH: from ANY state, return to a clean main menu with a
    // FRESH visible card at the bottom. Delete lastCard first so the control-card writer sends a NEW card even when the
    // old control card is still alive but scrolled up (pushed away by a checker/quota notification or the
    // owner-notify card — those append without clearing lastCard). Resets the session so you're unstuck.
    cancelSessionCardLoad(chatId, '底部菜单返回主菜单');
    setSession(chatId, { mode: 'idle' });
    invalidateControlCard(chatId);
    await enqueueControlCard(chatId, null, buildMenuCard(chatId, senderOpen, cfg), { forceNew: true });
  } catch (e) { logLine('底部菜单事件异常: ' + (e && (e.stack || e))); }
}

// ---- boot ----
if (TEST_MODE) {
   module.exports = { onMessage, onCardAction, onBotMenu, dispatchEvent: (label, handler) => channel.dispatchEvent(label, handler, eventDispatchKey), client: channel.client, testHooks, lastCard, controlCardWrites, sessionCardLoads, sessionPickerTokens, sessionProjectKey, setSession, getSession, discoverProjects, currentCard, querySession, clearQuerySession, clearChatSessions, listProjectSessions, listSessionsForProfile, sessionPreview, buildSessionCard, buildModelCard, buildMenuCard, effectiveModel, getUserModel, setUserModel, getUserProfile, setUserProfileId, updateConfig, runModelFor, runForUser, taskTimeoutMs, modelsFor, mdToLark, authLevel, canProject, canConfig, canUsePrivilegedTools, canUseOwnerOnlyProfile, canBindOwnerChat, shortTime, imageOutDir, prepImageOut, imageHint, uploadImage, sendImage, drainImageOut, startProgress, trackRun, reportInterruptedRuns, sendResult, parsePostContent, withPendingImages, imageQueueKey, imageInDir, pendingImages, cleanupInboundImages, cleanupOldInboundImages, setConfigReadFailureForTest: value => { testConfigReadFailure = !!value; }, setChildRegistryWriteFailureForTest, persistChildRegistryForTest: () => TEST_MODE && persistChildRegistry(), childRegistryCorruptForTest: () => childRegistryCorrupt, shuttingDownForTest: () => shuttingDown, resetShuttingDownForTest: () => { if (TEST_MODE) shuttingDown = false; }, providerHealth, providerIsAvailable, providerHealthIsStale, settlePendingProviderHealthForTest: settlePendingProviderHealth, setProviderHealthForTest, ageProviderHealthForTest, cancelProviderPreflightForTest: cancelProviderPreflight, running, registeredChildren, terminateRunningChildren, reapOrphanedAIChildren, isRegisteredAIProcess, classifyRegisteredAIProcess, orphanBlocksRun, childRegistryPath: CHILD_REGISTRY_PATH, testRoot: STATE_DIR, testConfigPath: CONFIG_PATH, testConfigLockPath: CONFIG_LOCK_PATH, claudeProjectsDir: CLAUDE_PROJECTS_DIR, resolveCompletionProject: completionEvents.resolveCompletionProject, formatCompletionNotification: completionEvents.formatCompletionNotification, validCompletionEvent: completionEvents.validCompletionEvent, pruneCompletionSeen: completionEvents.pruneCompletionSeen, stableMessageUuid, processCompletionEvents: completionEventsRunner.processCompletionEvents, completionQueueDir: COMPLETION_QUEUE_DIR, completionSeenPath: COMPLETION_SEEN_PATH };
  return;   // don't connect to Feishu in tests
}
const removedInboundImages = cleanupOldInboundImages();
if (removedInboundImages) logLine(`清理过期入站图片 ${removedInboundImages} 张`);
const inboundImageCleanupTimer = setInterval(() => {
  const removed = cleanupOldInboundImages();
  if (removed) logLine(`清理过期入站图片 ${removed} 张`);
}, 60 * 60 * 1000);
if (typeof inboundImageCleanupTimer.unref === 'function') inboundImageCleanupTimer.unref();
// on every (re)start — usually right after a deploy — reset all chat sessions to idle. The user often
// clears the Feishu chat while testing, which deletes the old cards; a stale project/chat session +
// deleted-card references would make the next taps look dead. Starting clean makes the first tap work.
try { conversationStore.resetSessions(); lastCard.clear(); cardHash.clear(); logLine('启动:已重置所有会话为初始状态(idle)'); } catch (e) {}
// register both v1 and v2 of the receive-message event so whichever the console offers works
const handlers = {
  'im.message.receive_v1': channel.dispatchEvent('飞书消息事件', onMessage, eventDispatchKey),
  'im.message.receive_v2': channel.dispatchEvent('飞书消息事件(v2)', onMessage, eventDispatchKey),
  'card.action.trigger': channel.dispatchEvent('飞书卡片事件', onCardAction, eventDispatchKey),   // card button clicks
  'application.bot.menu_v6': channel.dispatchEvent('飞书底部菜单事件', onBotMenu, eventDispatchKey),   // bottom-menu clicks
};
// no-op handlers for other events the console may have subscribed (read/reaction/recall/mute),
// so the SDK doesn't log "no handle" warnings for events we don't act on
const _noop = async () => {};
for (const k of ['im.message.message_read_v1', 'im.message.reaction.created_v1', 'im.message.reaction.deleted_v1', 'im.message.recalled_v1', 'im.message.bot_muted_v1']) { handlers[k] = _noop; }
// EventDispatcher 创建、v2 注册失败回退与 WSClient.start 全部由 ChannelAdapter 负责。
// 安装器只接受同一 PID + 精确 startedAt 代次的结构化 READY,不依赖 SDK 通用日志文本。
function readAgentBootGeneration() {
  const challengePath = path.join(STATE_DIR, 'feishu-agent.boot-challenge');
  try {
    const stat = fs.lstatSync(challengePath);
    if (!stat.isSymbolicLink() && stat.isFile()) {
      const value = fs.readFileSync(challengePath, 'utf8').trim().toLowerCase();
      if (/^[0-9a-f]{32}$/.test(value)) return value;
    }
  } catch (e) {}
  return crypto.randomBytes(16).toString('hex');
}
const agentProcessStartedAt = Date.now() - Math.round(process.uptime() * 1000);
const agentBootGeneration = readAgentBootGeneration();
logLine(`AI_RESUME_AGENT_BOOT pid=${process.pid} startedAt=${agentProcessStartedAt} generation=${agentBootGeneration}`);
channel.start({
  handlers,
  onReady: () => logLine(`AI_RESUME_AGENT_READY pid=${process.pid} startedAt=${agentProcessStartedAt} generation=${agentBootGeneration}`),
});
logLine('feishu-agent 启动,连接飞书长连接…  claude=' + CLAUDE_CMD + ' codex=' + CODEX_CMD);
const bootTimeoutConfig = readConfig();
logLine(`AI 任务超时策略:项目修改=无上限 查询=${Math.round(taskTimeoutMs('query', bootTimeoutConfig) / 60000)}分钟 闲聊=${Math.round(taskTimeoutMs('chat', bootTimeoutConfig) / 60000)}分钟`);
refreshProviderHealth('启动').catch(() => {});
const completionDelay = setTimeout(() => { completionEventsRunner.processCompletionEvents().catch(e => logLine('完成通知启动处理失败: ' + (e && e.message))); }, 7000);
if (completionDelay.unref) completionDelay.unref();
const completionTimer = setInterval(() => { completionEventsRunner.processCompletionEvents().catch(e => logLine('完成通知定时处理失败: ' + (e && e.message))); }, 5000);
if (completionTimer.unref) completionTimer.unref();
async function runSessionCleanup(reason) {
  try {
    const result = await sessionManager.cleanup();
    const s = result.summary || {};
    if (s.archived || s.deleted || s.safeDeleted) logLine(`会话清理(${reason}):归档 ${s.archived || 0},删除 ${s.deleted || 0},安全垃圾 ${s.safeDeleted || 0}`);
  } catch (e) { logLine('会话清理失败: ' + (e && e.message)); }
}
const cleanupDelay = setTimeout(() => { runSessionCleanup('启动'); }, 10000);
if (cleanupDelay.unref) cleanupDelay.unref();
const cleanupEvery = Math.max(1, sessionManager.config().intervalHours) * 60 * 60 * 1000;
const cleanupTimer = setInterval(() => { runSessionCleanup('定时'); }, cleanupEvery);
if (cleanupTimer.unref) cleanupTimer.unref();
// tell anyone whose run died with the previous process (deploy / watchdog restart / crash) — without
// this they just saw "进行中" and then silence forever. Delay so the WS is up before we send.
setTimeout(() => { reportInterruptedRuns().catch(e => logLine('中断汇报失败: ' + (e && e.message))); }, 6000);
// keep the process alive
process.on('uncaughtException', e => logLine('uncaughtException: ' + (e && e.stack || e)));
process.on('unhandledRejection', e => logLine('unhandledRejection: ' + (e && (e.stack || e))));
