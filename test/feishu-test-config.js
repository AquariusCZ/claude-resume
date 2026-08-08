'use strict';
/*
  S1-C1 / D-004: FEISHU_TEST 配置与状态隔离基础设施。

  唯一正常入口 prepareTestConfig(options):
  - 每进程最多一次先执行 24h 陈旧目录严格清扫(自动触发,不依赖测试手动调用);
  - 在系统 temp 直接子目录安全创建带 owner marker(PID+随机 nonce)的测试根;
  - real:true 时对真实 AppDir/config 从卷根到文件逐组件 lstat,拒绝 symlink/junction/
    reparse/非普通文件,证明 realpath 一致后才只读读取;真实文件绝不写入、备份、恢复或打印;
  - 递归清空 secret/password/token/apiKey/webhook/credential/privateKey/Authorization/
    x-api-key/api_key 与 aiProxy(可能含 URL userinfo/query token)等凭据;仅显式 keepSecrets 时允许顶层 openaiApiKey/deepseekApiKey
    注入当前进程环境(临时 JSON 仍清空),cleanup 恢复所有环境变量并验证真实 config SHA 不变;
  - 写临时 config;设置 FEISHU_TEST_STATE_DIR/FEISHU_TEST_CONFIG_PATH/USERPROFILE/
    CLAUDE_CONFIG_DIR/CODEX_HOME/LOCALAPPDATA;构造中任一步骤失败恢复已改环境,且只在 marker
    PID+原 nonce 匹配、树内无 reparse 时删除本次新建根。
  - no-reparse 树检查对 readdir 的每个条目 lstat,不只依赖 Dirent.isSymbolicLink;清理含
    junction 的 fixture 前必须先 lstat+unlink junction 本身,确认树无 reparse 后才递归
    删除;禁止 fs.rmSync 递归穿过仍存在的 junction。
*/
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

const MARKER_NAME = '.feishu-test-owner';
const ROOT_NAME_RE = /^claude-resume-(?:agent-)?test-/i;
const SECRET_KEY_RE = /secret|password|token|api[-_]?key|webhook|credential|private[-_]?key|authorization|x-api-key/i;
const ENV_KEY_MAP = { openaiApiKey: 'CLAUDE_RESUME_OPENAI_API_KEY', deepseekApiKey: 'DEEPSEEK_API_KEY' };
let sweepRan = false;

// 加载本模块时捕获真实 config 路径:prepareTestConfig 会改写 LOCALAPPDATA/USERPROFILE,
// 但真实 config 的只读校验与前后 SHA 比对必须始终针对原始路径。
const REAL_APP_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'ClaudeResume'
);
const REAL_CONFIG_PATH = path.join(REAL_APP_DIR, 'config.json');

function realConfigPath() { return REAL_CONFIG_PATH; }

// 从卷根到目标逐组件 lstat:拒绝 symlink/junction/reparse/非目录(最终文件须为普通文件),
// 并证明每级 realpath 一致后才允许只读访问。组件缺失一律视为路径非法。
function assertNoReparseChain(abs, { finalIsFile = false } = {}) {
  abs = path.resolve(String(abs));
  const parts = [];
  let cur = abs;
  for (;;) {
    const parent = path.dirname(cur);
    if (parent === cur) break;          // 到达卷根(如 C:\)
    parts.unshift(cur);
    cur = parent;
  }
  const check = (p, isLast) => {
    let st;
    try { st = fs.lstatSync(p); }
    catch (e) { if (e && e.code === 'ENOENT') throw new Error('[S1-C1] 路径缺失: ' + p); throw e; }
    if (st.isSymbolicLink()) throw new Error('[S1-C1] 路径含 symlink/junction/reparse: ' + p);
    if (isLast && finalIsFile) { if (!st.isFile()) throw new Error('[S1-C1] 路径不是普通文件: ' + p); }
    else if (!st.isDirectory()) throw new Error('[S1-C1] 路径不是目录: ' + p);
    if (fs.realpathSync(p).toLowerCase() !== p.toLowerCase()) throw new Error('[S1-C1] 路径被重定向: ' + p);
  };
  check(cur, false);
  for (let i = 0; i < parts.length; i++) check(parts[i], i === parts.length - 1);
  return abs;
}
function readRealConfigBytes() {
  assertNoReparseChain(REAL_APP_DIR);
  assertNoReparseChain(REAL_CONFIG_PATH, { finalIsFile: true });
  const bytes = fs.readFileSync(REAL_CONFIG_PATH);   // 只读,绝不写入/备份/恢复/打印
  return { path: REAL_CONFIG_PATH, bytes, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
}
function realConfigSha256() { return readRealConfigBytes().sha256; }

function isSecretKey(key) {
  const normalized = String(key).replace(/[-_]/g, '').toLowerCase();
  return normalized === 'aiproxy' || SECRET_KEY_RE.test(String(key));
}
// 递归脱敏:命中密钥键名即整体清空(字符串 '' / 数组 [] / 对象 {}),不再下钻,避免部分披露。
function redactSecretsDeep(value) {
  if (Array.isArray(value)) return value.map(redactSecretsDeep);
  if (value && typeof value === 'object') {
    const out = {};
    for (const key of Object.keys(value)) {
      if (isSecretKey(key)) out[key] = Array.isArray(value[key]) ? [] : (value[key] && typeof value[key] === 'object' ? {} : '');
      else out[key] = redactSecretsDeep(value[key]);
    }
    return out;
  }
  return value;
}
// 仅显式 keepSecrets:顶层 openaiApiKey/deepseekApiKey 注入当前进程环境;其余一律清空。
function stripSecretsForEnv(source, keepSecrets, setEnv) {
  if (!keepSecrets || !source || typeof source !== 'object') return;
  for (const [cfgKey, envKey] of Object.entries(ENV_KEY_MAP)) {
    const value = source[cfgKey];
    if (typeof value === 'string' && value.trim()) setEnv(envKey, value);
  }
}
function buildTestBaseline({ real = true, keepSecrets = false, source = null } = {}) {
  let sha256 = null, parsed = null;
  if (source !== null) parsed = source;
  else {
    const info = readRealConfigBytes();
    sha256 = info.sha256;
    parsed = JSON.parse(info.bytes.toString('utf8').replace(/^\uFEFF/, ''));
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('[S1-C1] 测试基线 config 根必须是对象');
  const envBackup = new Map();
  const setEnv = (key, value) => { if (!envBackup.has(key)) envBackup.set(key, process.env[key]); if (value === undefined) delete process.env[key]; else process.env[key] = value; };
  stripSecretsForEnv(parsed, keepSecrets, setEnv);
  const restoreEnv = () => { for (const [key, value] of envBackup) { if (value === undefined) delete process.env[key]; else process.env[key] = value; } };
  return { sha256, config: redactSecretsDeep(parsed), restoreEnv };
}

function pidToken(name, pid) {
  return new RegExp('(?:^|-)' + String(pid) + '(?:$|-)').test(String(name || ''));
}
// 不跟随链接:逐级 lstat 已存在组件 + realpath 一致性。组件缺失返回 false,reparse/非目录抛错。
function noFollowChain(root, { requireExists = false } = {}) {
  const tmp = path.resolve(os.tmpdir());
  const abs = path.resolve(String(root));
  assertNoReparseChain(tmp);
  if (path.dirname(abs).toLowerCase() !== tmp.toLowerCase()) throw new Error('[S1-C1] 测试根必须是系统 temp 的直接子目录');
  const base = path.basename(abs);
  if (!ROOT_NAME_RE.test(base) || !pidToken(base, process.pid)) throw new Error('[S1-C1] 测试根名称必须含当前 PID 且命名合法');
  let cur = abs;
  while (cur.toLowerCase() !== tmp.toLowerCase()) {
    let st = null;
    try { st = fs.lstatSync(cur); }
    catch (e) {
      if (e && e.code === 'ENOENT') { if (requireExists && cur === abs) throw new Error('[S1-C1] 测试根缺失'); cur = path.dirname(cur); continue; }
      throw e;
    }
    if (st.isSymbolicLink()) throw new Error('[S1-C1] 路径含 symlink/junction/reparse: ' + cur);
    if (!st.isDirectory()) throw new Error('[S1-C1] 路径不是目录: ' + cur);
    if (fs.realpathSync(cur).toLowerCase() !== cur.toLowerCase()) throw new Error('[S1-C1] 路径被重定向: ' + cur);
    cur = path.dirname(cur);
  }
  return abs;
}
// no-reparse 树检查:readdir 后对每个条目 lstat(不依赖 Dirent.isSymbolicLink),目录再下钻。
function findReparse(root) {
  const stack = [root];
  while (stack.length) {
    const dir = stack.pop();
    let names;
    try { names = fs.readdirSync(dir); }
    catch (e) { if (e && e.code === 'ENOENT') continue; throw e; }
    for (const name of names) {
      const full = path.join(dir, name);
      let st;
      try { st = fs.lstatSync(full); }
      catch (e) { throw new Error('[S1-C1] 无法 lstat 测试树条目,拒绝判定为安全: ' + full + ': ' + (e && e.message || e)); }
      if (st.isSymbolicLink()) return full;
      if (st.isDirectory()) stack.push(full);
    }
  }
  return null;
}
// 递归遍历,只 unlink junction/symlink 链接本身(绝不跟随);目录再下钻。
function unlinkReparseEntries(root) {
  const stack = [root];
  while (stack.length) {
    const dir = stack.pop();
    let names;
    try { names = fs.readdirSync(dir); }
    catch (e) { if (e && e.code === 'ENOENT') continue; throw e; }
    for (const name of names) {
      const full = path.join(dir, name);
      let st;
      try { st = fs.lstatSync(full); }
      catch (e) { throw new Error('[S1-C1] 无法 lstat fixture 条目,拒绝继续清理: ' + full + ': ' + (e && e.message || e)); }
      if (st.isSymbolicLink()) { fs.unlinkSync(full); continue; }
      if (st.isDirectory()) stack.push(full);
    }
  }
}
// 仅供测试清理系统 temp 直接子目录下、claude-resume- 前缀的受控 fixture。reparse
// 链接只 unlink 链接本身;目录先 unlink 内部 junction/symlink,确认树无 reparse 后再递归删除。
function removeFixtureSafe(target) {
  const abs = path.resolve(String(target));
  const tmp = path.resolve(os.tmpdir());
  assertNoReparseChain(tmp);
  if (path.dirname(abs).toLowerCase() !== tmp.toLowerCase() || !/^claude-resume-/i.test(path.basename(abs))) {
    throw new Error('[S1-C1] fixture 清理目标必须是系统 temp 直接子目录且使用 claude-resume- 前缀: ' + abs);
  }
  let st;
  try { st = fs.lstatSync(abs); }
  catch (e) { if (e && e.code === 'ENOENT') return; throw e; }
  if (st.isSymbolicLink()) { fs.unlinkSync(abs); return; }
  if (st.isDirectory()) {
    unlinkReparseEntries(abs);
    const reparse = findReparse(abs);
    if (reparse) throw new Error('[S1-C1] 拒绝递归删除仍含 reparse 的 fixture: ' + reparse);
    fs.rmSync(abs, { recursive: true, force: true });
    return;
  }
  fs.unlinkSync(abs);
}
function writeMarkerAtomic(markerPath, marker) {
  const tmp = `${markerPath}.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`;
  let fd = null;
  try {
    fd = fs.openSync(tmp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(marker), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    fs.renameSync(tmp, markerPath);
  } finally {
    if (fd !== null) try { fs.closeSync(fd); } catch (e) {}
    try { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); } catch (e) {}
  }
}
function readMarkerRaw(root) {
  const markerPath = path.join(root, MARKER_NAME);
  let st;
  try { st = fs.lstatSync(markerPath); }
  catch (e) { if (e && e.code === 'ENOENT') return null; throw e; }
  if (st.isSymbolicLink() || !st.isFile()) throw new Error('[S1-C1] owner marker 必须是普通文件');
  let marker = null;
  try { marker = JSON.parse(fs.readFileSync(markerPath, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { throw new Error('[S1-C1] owner marker 损坏'); }
  if (!marker || !Number.isInteger(Number(marker.pid)) || typeof marker.nonce !== 'string' || marker.nonce.length < 8) throw new Error('[S1-C1] owner marker 格式非法');
  return marker;
}

// 创建系统 temp 直接子目录测试根(PID 命名 + owner marker),返回带 cleanup 的句柄。
function createTestRoot() {
  const nonce = crypto.randomBytes(8).toString('hex');
  const root = path.join(os.tmpdir(), `claude-resume-test-${process.pid}-${nonce}`);
  noFollowChain(root);
  fs.mkdirSync(root);
  const marker = { pid: process.pid, nonce: crypto.randomBytes(16).toString('hex'), createdAtUtc: new Date().toISOString() };
  writeMarkerAtomic(path.join(root, MARKER_NAME), marker);
  return {
    root, pid: process.pid, nonce: marker.nonce, marker,
    markerPath: path.join(root, MARKER_NAME),
    cleanup: () => cleanupTestRoot(root, marker),
  };
}
// 严格 owner cleanup:路径合法 + realpath 一致 + marker PID/原 nonce 精确匹配;
// 树内出现 junction/symlink 或无法完整检查时宁可残留,绝不替调用方修改后再递归删除。
function cleanupTestRoot(root, expectedMarker) {
  try {
    noFollowChain(root, { requireExists: true });
    const marker = readMarkerRaw(root);
    if (!marker) return { removed: false, reason: 'marker missing' };
    if (Number(marker.pid) !== Number(expectedMarker && expectedMarker.pid) || marker.nonce !== (expectedMarker && expectedMarker.nonce)) {
      return { removed: false, reason: 'marker PID/nonce mismatch' };
    }
    if (fs.realpathSync(root).toLowerCase() !== path.resolve(root).toLowerCase()) return { removed: false, reason: 'realpath mismatch' };
    const reparse = findReparse(root);
    if (reparse) return { removed: false, reason: 'reparse: ' + reparse };
    fs.rmSync(root, { recursive: true, force: true });
    return { removed: true, reason: null };
  } catch (e) {
    return { removed: false, reason: (e && e.message) || String(e) };
  }
}

function writeTestConfig(root, cfg) {
  noFollowChain(root, { requireExists: true });
  const p = path.join(root, 'config.json');
  const tmp = `${p}.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`;
  let fd = null;
  try {
    fd = fs.openSync(tmp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(cfg, null, 4), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    fs.renameSync(tmp, p);
  } finally {
    if (fd !== null) try { fs.closeSync(fd); } catch (e) {}
    try { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); } catch (e) {}
  }
  return p;
}
function readTestConfig(root) {
  noFollowChain(root, { requireExists: true });
  const p = path.join(root, 'config.json');
  let st;
  try { st = fs.lstatSync(p); }
  catch (e) { if (e && e.code === 'ENOENT') throw new Error('[S1-C1] 测试 config.json 缺失'); throw e; }
  if (st.isSymbolicLink() || !st.isFile()) throw new Error('[S1-C1] 测试 config.json 必须是非链接普通文件');
  if (fs.realpathSync(p).toLowerCase() !== p.toLowerCase()) throw new Error('[S1-C1] 测试 config.json 被重定向');
  return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
}

function isPidActive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (e) { return !!(e && e.code === 'EPERM'); }
}
// 每进程最多一次:仅系统 temp 直接子目录、合法命名、marker PID 与目录名 PID 一致、
// PID 已不活跃且树内无 reparse 的 24h 以上陈旧目录才删除。
function sweepStaleTestDirs({ maxAgeMs = 24 * 3600 * 1000, force = false } = {}) {
  if (sweepRan && !force) return { ran: false, removed: [] };
  sweepRan = true;
  const tmp = path.resolve(os.tmpdir());
  const removed = [];
  try { assertNoReparseChain(tmp); } catch (e) { return { ran: true, removed }; }
  let names = [];
  try { names = fs.readdirSync(tmp); } catch (e) { return { ran: true, removed }; }
  for (const name of names) {
    if (!ROOT_NAME_RE.test(name)) continue;
    const pidMatch = /^claude-resume-(?:agent-)?test-(\d+)/i.exec(name);
    if (!pidMatch) continue;
    const dirPid = Number(pidMatch[1]);
    if (!pidToken(name, dirPid)) continue;
    const full = path.join(tmp, name);
    let st;
    try { st = fs.lstatSync(full); } catch (e) { continue; }
    if (st.isSymbolicLink() || !st.isDirectory()) continue;
    try { if (Date.now() - fs.statSync(full).mtimeMs < maxAgeMs) continue; } catch (e) { continue; }
    let marker = null;
    try { marker = readMarkerRaw(full); } catch (e) { continue; }
    if (!marker || Number(marker.pid) !== dirPid) continue;
    if (isPidActive(dirPid)) continue;
    try { if (findReparse(full)) continue; } catch (e) { continue; }
    try { fs.rmSync(full, { recursive: true, force: true }); removed.push(full); } catch (e) {}
  }
  return { ran: true, removed };
}

// 唯一正常入口:每进程最多一次先清扫 24h 陈旧目录,再创建测试根、写临时 config、设置环境。
// 任一步骤失败恢复已改环境,并只在 marker PID+原 nonce 匹配、树无 reparse 时删除新建根。
function prepareTestConfig(options = {}) {
  const { real = false, keepSecrets = false, source = null } = options;
  const envBackup = new Map();
  const setEnv = (key, value) => {
    if (!envBackup.has(key)) envBackup.set(key, process.env[key]);
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  };
  const restoreEnv = () => { for (const [key, value] of envBackup) { if (value === undefined) delete process.env[key]; else process.env[key] = value; } };
  let created = null;
  try {
    sweepStaleTestDirs();   // 自动清扫,不依赖测试手动调用
    created = createTestRoot();
    let sha256 = null, parsed = null;
    if (source !== null) parsed = source;
    else if (real) {
      const info = readRealConfigBytes();   // 含逐组件 no-reparse 证明,只读
      sha256 = info.sha256;
      parsed = JSON.parse(info.bytes.toString('utf8').replace(/^\uFEFF/, ''));
    } else {
      throw new Error('[S1-C1] prepareTestConfig 必须提供 source(合成)或 real:true(只读真实基线)');
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('[S1-C1] 测试基线 config 根必须是对象');
    stripSecretsForEnv(parsed, keepSecrets, setEnv);
    const config = redactSecretsDeep(parsed);
    const configPath = writeTestConfig(created.root, config);
    const userProfile = path.join(created.root, 'user-profile');
    fs.mkdirSync(userProfile);
    const claudeHome = path.join(created.root, 'claude-home', '.claude');
    fs.mkdirSync(claudeHome, { recursive: true });
    const codexHome = path.join(created.root, 'codex-home');
    fs.mkdirSync(codexHome);
    const localApp = path.join(created.root, 'local');
    fs.mkdirSync(localApp);
    setEnv('FEISHU_TEST_STATE_DIR', created.root);
    setEnv('FEISHU_TEST_CONFIG_PATH', configPath);
    setEnv('USERPROFILE', userProfile);
    setEnv('CLAUDE_CONFIG_DIR', claudeHome);
    setEnv('CODEX_HOME', codexHome);
    setEnv('LOCALAPPDATA', localApp);
    return {
      root: created.root, configPath, config, sha256, marker: created.marker,
      cleanup() {
        let shaError = null;
        if (sha256) {
          try { if (realConfigSha256() !== sha256) shaError = new Error('[S1-C1] 真实 config SHA 在测试期间发生变化'); }
          catch (e) { shaError = e; }
        }
        restoreEnv();
        const res = cleanupTestRoot(created.root, created.marker);
        if (shaError) throw shaError;
        return res;
      },
    };
  } catch (e) {
    restoreEnv();
    if (created) { try { cleanupTestRoot(created.root, created.marker); } catch (e2) {} }
    throw e;
  }
}

module.exports = {
  MARKER_NAME, realConfigPath, readRealConfigBytes, realConfigSha256,
  isSecretKey, redactSecretsDeep, buildTestBaseline,
  createTestRoot, cleanupTestRoot, writeTestConfig, readTestConfig,
  noFollowChain, findReparse, readMarkerRaw, sweepStaleTestDirs,
  removeFixtureSafe, prepareTestConfig,
};
