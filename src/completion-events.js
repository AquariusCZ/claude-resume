'use strict';

/*
  completion-events.js  ---  AI Resume Stage 1 S1-B: CompletionAdmission 边界

  Codex / Claude Code / Cline hooks 只向队列写入小的规范化事件文件;本模块在 agent 侧负责:
  事件结构校验、受控 source -> 客户端标签、项目解析、通知格式、七天去重、稳定 UUID,
  以及队列的 claim/recovery/process。

  强制边界:
  - 不依赖 @larksuiteoapi/node-sdk;不读取 config.json、飞书密钥或任何 provider key。
  - config / send / knownProjects / discoverProjects / log / appDir / queueDir / seenPath
    全部通过 createCompletionEvents(options) 注入;缺少 send 或 target 时事件保留供重试。
  - 单轮最多 20 个文件;单文件 64 KiB;未来超过 5 分钟、无效 source/version/eventId/
    projectRoots 的事件拒绝;七天旧事件与 seen 去重按现役语义。
  - UNC / 设备路径在任何 exists/stat 前拒绝;显示客户端只由 source enum 决定,不信任 event.client。
  - 发送成功后才写 seen 并删除队列;失败恢复原文件;稳定 uuidSeed 保持 completion:<eventId>。
  - 并发处理 settle-once;即使 config()、knownProjects() 或初始化抛错,运行锁也必须在
    finally 中释放,后续调用可继续。
*/
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');

const COMPLETION_EVENT_MAX_BYTES = 64 * 1024;
const COMPLETION_EVENT_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const COMPLETION_SEEN_MAX = 2000;
const COMPLETION_EVENT_MAX_FILES_PER_ROUND = 20;
const COMPLETION_EVENT_MAX_FUTURE_MS = 5 * 60 * 1000;

function readJson(p) {
  // strip a UTF-8 BOM: PowerShell may write config/state with one, which JSON.parse rejects
  return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
}

function writeJsonAtomicPath(target, value) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const tmp = `${target}.tmp-${process.pid}-${Date.now()}-${crypto.randomBytes(4).toString('hex')}`;
  const generation = `${target}.gen-${Date.now()}-${crypto.randomBytes(4).toString('hex')}`;
  let fd;
  try {
    fd = fs.openSync(tmp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(value, null, 2), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    // 先把完整状态原子发布为不可覆盖的 generation。canonical 文件只用于兼容旧版本；
    // 即使 Windows 覆盖写或进程中断导致 canonical 损坏，读取端仍能从 generation 恢复。
    fs.renameSync(tmp, generation);
    try { fs.copyFileSync(generation, target); } catch (e) {}
    try {
      const prefix = path.basename(target) + '.gen-';
      const generations = fs.readdirSync(path.dirname(target))
        .filter(name => name.startsWith(prefix))
        .map(name => ({ name, full: path.join(path.dirname(target), name), mtime: fs.statSync(path.join(path.dirname(target), name)).mtimeMs }))
        .sort((a, b) => b.mtime - a.mtime);
      for (const old of generations.slice(3)) try { fs.unlinkSync(old.full); } catch (e) {}
    } catch (e) {}
  } finally {
    if (fd != null) try { fs.closeSync(fd); } catch (e) {}
    try { fs.unlinkSync(tmp); } catch (e) {}
  }
}

async function withCompletionSeenLock(pathname, fn) {
  const lockPath = pathname + '.lock';
  fs.mkdirSync(path.dirname(lockPath), { recursive: true });
  let handle;
  for (let attempt = 0; attempt < 200; attempt++) {
    try {
      handle = fs.openSync(lockPath, 'wx');
      fs.writeFileSync(handle, JSON.stringify({ pid: process.pid, createdAt: new Date().toISOString() }), 'utf8');
      fs.fsyncSync(handle);
      break;
    } catch (e) {
      if (handle !== undefined) {
        try { fs.closeSync(handle); } catch (closeError) {}
        handle = undefined;
        try { fs.unlinkSync(lockPath); } catch (unlinkError) {}
      }
      if (e && e.code !== 'EEXIST') throw e;
      try {
        const age = Date.now() - fs.statSync(lockPath).mtimeMs;
        const owner = readJson(lockPath);
        const ownerPid = Number(owner && owner.pid);
        let ownerGone = false;
        if (Number.isInteger(ownerPid) && ownerPid > 0) {
          try { process.kill(ownerPid, 0); }
          catch (probeError) { ownerGone = !!(probeError && probeError.code === 'ESRCH'); }
        }
        if (age > 60 * 1000 && ownerGone) fs.unlinkSync(lockPath);
      } catch (staleError) {}
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  }
  if (handle === undefined) throw new Error('完成通知去重索引正被另一个进程使用');
  try { return await fn(); }
  finally {
    try { fs.closeSync(handle); } catch (e) {}
    try { fs.unlinkSync(lockPath); } catch (e) {}
  }
}

async function mergeAndWriteCompletionSeen(pathname, pending) {
  return withCompletionSeenLock(pathname, async () => {
    const merged = pruneCompletionSeen(Object.assign({}, completionSeen(pathname), pending || {}));
    writeJsonAtomicPath(pathname, merged);
    return merged;
  });
}

async function sendCompletionOnce(pathname, eventId, send) {
  return withCompletionSeenLock(pathname, async () => {
    const current = pruneCompletionSeen(completionSeen(pathname));
    if (current[eventId]) return { status: 'duplicate', seen: current };
    const ok = await send();
    if (!ok) return { status: 'retry', seen: current };
    current[eventId] = new Date().toISOString();
    const next = pruneCompletionSeen(current);
    writeJsonAtomicPath(pathname, next);
    return { status: 'sent', seen: next };
  });
}

function cleanDisplay(value, fallback, maxLength = 120) {
  const text = String(value || '').replace(/[\u0000-\u001f\u007f]+/g, ' ').replace(/\s+/g, ' ').trim();
  return (text || fallback || '').slice(0, maxLength);
}

function usableCompletionRoot(value, deps = {}) {
  if (!value) return null;
  let resolved;
  try {
    const raw = String(value).trim();
    if (!raw || /^(?:\\\\|\/\/|\\\\[?.]\\)/.test(raw) || !path.isAbsolute(raw)) return null;
    resolved = path.resolve(raw);
    if (!fs.existsSync(resolved)) return null;
    if (!fs.statSync(resolved).isDirectory()) resolved = path.dirname(resolved);
  } catch (e) { return null; }
  const lower = resolved.toLowerCase();
  const home = path.resolve(String(deps.homeDir || os.homedir())).toLowerCase();
  const temp = path.resolve(String(deps.tempDir || os.tmpdir())).toLowerCase();
  const app = deps.appDir ? path.resolve(String(deps.appDir)).toLowerCase() : '';
  const desktop = deps.desktopDir
    ? path.resolve(String(deps.desktopDir)).toLowerCase()
    : path.resolve(path.join(home, 'Desktop')).toLowerCase();
  const documents = deps.documentsDir
    ? path.resolve(String(deps.documentsDir)).toLowerCase()
    : path.resolve(path.join(home, 'Documents')).toLowerCase();
  if ((app && (lower === app || lower.startsWith(app + path.sep))) || lower === temp || lower.startsWith(temp + path.sep)) return null;
  if (lower === home || lower === desktop || lower === documents || /^[a-z]:\\windows(?:\\|$)/i.test(resolved)) return null;
  return resolved;
}

function gitRootFrom(start) {
  let current = start;
  while (current && path.dirname(current) !== current) {
    try { if (fs.existsSync(path.join(current, '.git'))) return current; } catch (e) {}
    current = path.dirname(current);
  }
  return start;
}

function completionProjectSource(knownProjects, deps) {
  if (knownProjects !== undefined && knownProjects !== null) return knownProjects;
  if (deps.knownProjects !== undefined && deps.knownProjects !== null) return deps.knownProjects;
  return deps.discoverProjects;
}

function resolveCompletionProject(event, knownProjects, deps = {}) {
  const source = completionProjectSource(knownProjects, deps);
  let projects = (typeof source === 'function' ? source() : source) || [];
  if (!Array.isArray(projects)) projects = [];
  const known = projects.map(project => ({
    name: cleanDisplay(project && project.name, ''),
    path: usableCompletionRoot(project && project.path, deps),
  })).filter(project => project.path);
  const roots = (Array.isArray(event && event.projectRoots) ? event.projectRoots : []).map(root => usableCompletionRoot(root, deps)).filter(Boolean);
  for (const root of roots) {
    const lower = root.toLowerCase();
    const matches = known.filter(project => {
      const candidate = project.path.toLowerCase();
      return lower === candidate || lower.startsWith(candidate + path.sep);
    }).sort((a, b) => b.path.length - a.path.length);
    if (matches.length) return { name: matches[0].name || path.basename(matches[0].path), path: matches[0].path };
  }
  if (roots.length) {
    const projectRoot = gitRootFrom(roots[0]);
    return { name: cleanDisplay(path.basename(projectRoot), '未识别项目'), path: projectRoot };
  }
  return { name: '未识别项目', path: '' };
}

function completionClientLabel(event) {
  const source = String(event && event.source || '');
  const client = source === 'codex' ? 'Codex' : source === 'claude' ? 'Claude Code' : source === 'cline' ? 'Cline' : 'AI';
  const provider = cleanDisplay(event && event.provider, '', 40).toLowerCase();
  const model = cleanDisplay(event && event.model, '', 100).toLowerCase();
  if (/deepseek/.test(provider) || /deepseek/.test(model)) return `${client}（DeepSeek）`;
  return client;
}

function localMinute(value) {
  const date = new Date(value);
  const d = Number.isNaN(date.getTime()) ? new Date() : date;
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function formatCompletionNotification(event, project) {
  const heading = event && event.source === 'cline' ? '本地 AI 任务已完成' : '本地 AI 本轮响应已结束';
  return `${heading}\n项目：${cleanDisplay(project && project.name, '未识别项目')}\n执行端：${completionClientLabel(event)}\n时间：${localMinute(event && event.createdAt)}`;
}

function validCompletionEvent(event) {
  if (!event || typeof event !== 'object' || Array.isArray(event)) return false;
  if (!['codex', 'claude', 'cline'].includes(String(event.source || ''))) return false;
  if (Number(event.version) !== 1) return false;
  if (!event.eventId || String(event.eventId).length > 500) return false;
  if (!Array.isArray(event.projectRoots) || event.projectRoots.length > 10) return false;
  const created = Date.parse(String(event.createdAt || ''));
  if (!Number.isFinite(created) || created > Date.now() + COMPLETION_EVENT_MAX_FUTURE_MS) return false;
  return true;
}

function completionSeen(pathname) {
  const dir = path.dirname(pathname), base = path.basename(pathname);
  const candidates = [pathname];
  try {
    for (const name of fs.readdirSync(dir)) if (name.startsWith(base + '.gen-')) candidates.push(path.join(dir, name));
  } catch (e) {}
  const valid = [];
  for (const candidate of candidates) {
    try {
      const parsed = readJson(candidate);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) valid.push({ parsed, mtime: fs.statSync(candidate).mtimeMs });
    } catch (e) {}
  }
  valid.sort((a, b) => a.mtime - b.mtime);
  return Object.assign({}, ...valid.map(item => item.parsed));
}

function pruneCompletionSeen(seen, now = Date.now()) {
  const entries = Object.entries(seen || {}).filter(([, timestamp]) => {
    const time = Date.parse(String(timestamp || ''));
    return Number.isFinite(time) && now - time <= COMPLETION_EVENT_MAX_AGE_MS;
  }).sort((a, b) => Date.parse(String(b[1])) - Date.parse(String(a[1]))).slice(0, COMPLETION_SEEN_MAX);
  return Object.fromEntries(entries);
}

function stableMessageUuid(seed, partIndex) {
  const hex = crypto.createHash('sha256').update(`${seed}:${partIndex}`).digest('hex').slice(0, 32).split('');
  hex[12] = '5';
  hex[16] = ((parseInt(hex[16], 16) & 3) | 8).toString(16);
  const value = hex.join('');
  return `${value.slice(0, 8)}-${value.slice(8, 12)}-${value.slice(12, 16)}-${value.slice(16, 20)}-${value.slice(20)}`;
}

function completionQueueFiles(queueDir) {
  try {
    fs.mkdirSync(queueDir, { recursive: true });
    const now = Date.now();
    for (const name of fs.readdirSync(queueDir)) {
      const marker = name.indexOf('.json.processing-');
      if (marker < 0) continue;
      const processing = path.join(queueDir, name);
      try {
        if (now - fs.statSync(processing).mtimeMs < 30000) continue;
        const original = path.join(queueDir, name.slice(0, marker + 5));
        if (!fs.existsSync(original)) fs.renameSync(processing, original);
        else fs.unlinkSync(processing);
      } catch (e) {}
    }
    return fs.readdirSync(queueDir).filter(name => name.endsWith('.json')).sort();
  } catch (e) { return []; }
}

function createCompletionEvents(deps) {
  const defaults = Object.assign({
    appDir: null, queueDir: null, seenPath: null, config: null, target: null,
    send: null, knownProjects: null, discoverProjects: null, log: null,
    tempDir: null, homeDir: null, desktopDir: null, documentsDir: null,
  }, deps || {});
  let running = false;

  async function processCompletionEvents(options) {
    if (running) return { processed: 0, skipped: true };
    running = true;
    const opts = options || {};
    const queueDir = opts.queueDir || defaults.queueDir;
    const seenPath = opts.seenPath || defaults.seenPath;
    const log = opts.log || defaults.log || function () {};
    const sender = Object.prototype.hasOwnProperty.call(opts, 'send') ? opts.send : defaults.send;
    const exclusionDeps = {
      appDir: opts.appDir || defaults.appDir,
      tempDir: opts.tempDir || defaults.tempDir,
      homeDir: opts.homeDir || defaults.homeDir,
      desktopDir: opts.desktopDir || defaults.desktopDir,
      documentsDir: opts.documentsDir || defaults.documentsDir,
    };
    let processed = 0;
    try {
      let cfg = {};
      let configFailed = false;
      try {
        let configSource = Object.prototype.hasOwnProperty.call(opts, 'config') ? opts.config : defaults.config;
        if (configSource === undefined || configSource === null) configSource = defaults.config;
        cfg = (typeof configSource === 'function' ? configSource() : configSource) || {};
        if (typeof cfg !== 'object' || Array.isArray(cfg)) cfg = {};
      } catch (e) {
        configFailed = true;
        log('读取完成通知配置失败: ' + (e && e.message));
      }
      let target;
      if (Object.prototype.hasOwnProperty.call(opts, 'target')) target = opts.target;
      else if (typeof defaults.target === 'function') target = defaults.target();
      else if (defaults.target !== undefined && defaults.target !== null) target = defaults.target;
      else target = cfg && cfg.feishuChatId;
      if (!configFailed) {
        let seen = pruneCompletionSeen(completionSeen(seenPath));
        for (const name of completionQueueFiles(queueDir).slice(0, COMPLETION_EVENT_MAX_FILES_PER_ROUND)) {
          const original = path.join(queueDir, name);
          const claimed = `${original}.processing-${process.pid}`;
          try { fs.renameSync(original, claimed); } catch (e) { continue; }
          let remove = false;
          let restore = false;
          try {
            if (fs.statSync(claimed).size > COMPLETION_EVENT_MAX_BYTES) { remove = true; continue; }
            let event;
            try { event = readJson(claimed); }
            catch (e) {
              remove = true;
              log('丢弃无法解析的完成通知事件: ' + name);
              continue;
            }
            if (!validCompletionEvent(event)) { remove = true; continue; }
            const eventId = String(event.eventId);
            const created = Date.parse(String(event.createdAt || ''));
            if (seen[eventId] || (Number.isFinite(created) && Date.now() - created > COMPLETION_EVENT_MAX_AGE_MS)) { remove = true; continue; }
            if (cfg.completionNotifyEnabled === false) { remove = true; continue; }
            if (!target || sender === undefined || sender === null) { restore = true; break; }
            let project;
            try {
              project = resolveCompletionProject(event, opts.knownProjects, Object.assign({}, exclusionDeps, {
                knownProjects: defaults.knownProjects,
                discoverProjects: defaults.discoverProjects,
              }));
            } catch (e) {
              restore = true;
              log('完成通知项目解析失败: ' + (e && e.message));
              break;
            }
            const delivery = await sendCompletionOnce(seenPath, eventId, () =>
              sender(target, formatCompletionNotification(event, project), { uuidSeed: `completion:${eventId}` }));
            seen = delivery.seen;
            if (delivery.status === 'duplicate') { remove = true; continue; }
            if (delivery.status !== 'sent') { restore = true; break; }
            processed++;
            remove = true;
            log(`完成通知已发送: ${project.name} / ${completionClientLabel(event)}`);
          } catch (e) {
            restore = true;
            log('处理完成通知失败: ' + (e && e.message));
          } finally {
            if (remove) { try { fs.unlinkSync(claimed); } catch (e) {} }
            else if (restore) { try { fs.renameSync(claimed, original); } catch (e) {} }
          }
          if (restore) break;
        }
      }
      return { processed };
    } finally { running = false; }
  }

  return { processCompletionEvents };
}

module.exports = {
  COMPLETION_EVENT_MAX_BYTES,
  COMPLETION_EVENT_MAX_AGE_MS,
  COMPLETION_SEEN_MAX,
  COMPLETION_EVENT_MAX_FILES_PER_ROUND,
  cleanDisplay,
  usableCompletionRoot,
  gitRootFrom,
  resolveCompletionProject,
  completionClientLabel,
  formatCompletionNotification,
  validCompletionEvent,
  completionSeen,
  pruneCompletionSeen,
  mergeAndWriteCompletionSeen,
  sendCompletionOnce,
  stableMessageUuid,
  completionQueueFiles,
  createCompletionEvents,
};
