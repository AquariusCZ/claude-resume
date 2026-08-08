'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { profileById, profilesFor } = require('./ai/profiles');
const { createCodexSessions } = require('./ai/codex-sessions');

const DAY_MS = 24 * 60 * 60 * 1000;

function readJson(file, fallback) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { return fallback; }
}

function writeJsonAtomic(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + '.' + process.pid + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(value, null, 2), 'utf8');
  try { fs.renameSync(tmp, file); }
  catch (e) {
    try { fs.rmSync(file, { force: true }); } catch (e2) {}
    fs.renameSync(tmp, file);
  }
}

function readHead(file, bytes) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const buf = Buffer.alloc(bytes || 65536);
    const n = fs.readSync(fd, buf, 0, buf.length, 0);
    return buf.toString('utf8', 0, n);
  } catch (e) { return ''; }
  finally { try { if (fd !== undefined) fs.closeSync(fd); } catch (e) {} }
}

function dirSize(target) {
  try {
    const stat = fs.statSync(target);
    if (!stat.isDirectory()) return stat.size;
    let total = 0;
    for (const name of fs.readdirSync(target)) total += dirSize(path.join(target, name));
    return total;
  } catch (e) { return 0; }
}

function copyTree(src, dst) {
  const stat = fs.statSync(src);
  if (stat.isDirectory()) {
    fs.mkdirSync(dst, { recursive: true });
    for (const name of fs.readdirSync(src)) copyTree(path.join(src, name), path.join(dst, name));
  } else {
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.copyFileSync(src, dst);
  }
}

function moveTree(src, dst) {
  fs.mkdirSync(path.dirname(dst), { recursive: true });
  try { fs.renameSync(src, dst); }
  catch (e) { copyTree(src, dst); fs.rmSync(src, { recursive: true, force: true }); }
}

function normalizeTime(value, fallback) {
  const n = typeof value === 'number' ? value : Date.parse(value || '');
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

function profileInfo(profileId, engineHint) {
  const profile = profileById(profileId);
  if (profile) return profile;
  return {
    id: profileId || (engineHint === 'codex' ? 'openai-sol' : 'claude-default'),
    fullLabel: profileId || (engineHint === 'codex' ? 'OpenAI' : 'Claude/DeepSeek'),
    provider: engineHint === 'codex' ? 'openai' : 'claude',
    engine: engineHint || 'claude',
  };
}

function sessionTitle(file) {
  let aiTitle = '', firstUser = '', cwd = '';
  for (const line of readHead(file, 65536).split(/\r?\n/)) {
    if (!line) continue;
    try {
      const row = JSON.parse(line);
      if (!cwd && row.cwd) cwd = String(row.cwd);
      if (!aiTitle && row.aiTitle) aiTitle = String(row.aiTitle);
      if (!firstUser && row.type === 'user') {
        const content = row.message && row.message.content;
        if (typeof content === 'string') firstUser = content;
        else if (Array.isArray(content)) firstUser = content.filter(x => x && x.type === 'text').map(x => x.text || '').join(' ');
      }
    } catch (e) {}
    if (cwd && aiTitle) break;
  }
  return {
    cwd,
    title: String(aiTitle || firstUser || '(无标题)').replace(/\s+/g, ' ').trim().slice(0, 160),
  };
}

function createSessionManager(options) {
  const opts = options || {};
  const appDir = opts.appDir || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'ClaudeResume');
  const claudeRoot = opts.claudeRoot || path.join(os.homedir(), '.claude', 'projects');
  const configPath = opts.configPath || path.join(appDir, 'config.json');
  const archiveRoot = opts.archiveRoot || path.join(appDir, 'session-archive');
  const manifestPath = opts.manifestPath || path.join(appDir, 'session-archive.json');
  const lockPath = opts.lockPath || path.join(appDir, 'session-archive.lock');
  const codex = opts.codexSessions || createCodexSessions({ logLine: opts.logLine || (() => {}) });
  const nowFn = opts.now || (() => Date.now());
  const chatDir = path.join(appDir, 'feishu-chat');
  const queryDir = path.join(appDir, 'feishu-query');
  const queryCwdBase = path.join(appDir, 'feishu-query-cwd');

  function config() {
    const c = readJson(configPath, {}) || {};
    return {
      enabled: c.sessionAutoCleanup !== false,
      archiveDays: Math.max(1, Number(c.feishuSessionArchiveDays || 14)),
      deleteDays: Math.max(1, Number(c.feishuSessionDeleteDays || 30)),
      intervalHours: Math.max(1, Number(c.sessionCleanupIntervalHours || 6)),
    };
  }

  async function withLock(fn) {
    fs.mkdirSync(appDir, { recursive: true });
    let handle;
    for (let i = 0; i < 80; i++) {
      try { handle = fs.openSync(lockPath, 'wx'); break; }
      catch (e) {
        if (e.code !== 'EEXIST') throw e;
        try {
          const age = nowFn() - fs.statSync(lockPath).mtimeMs;
          if (age > 5 * 60 * 1000) fs.unlinkSync(lockPath);
        } catch (e2) {}
        await new Promise(resolve => setTimeout(resolve, 50));
      }
    }
    if (handle === undefined) throw new Error('会话管理正被另一个进程使用');
    try { return await fn(); }
    finally {
      try { fs.closeSync(handle); } catch (e) {}
      try { fs.unlinkSync(lockPath); } catch (e) {}
    }
  }

  function manifest() {
    const value = readJson(manifestPath, { version: 1, records: [] });
    return value && Array.isArray(value.records) ? value : { version: 1, records: [] };
  }

  function saveManifest(value) { writeJsonAtomic(manifestPath, value); }

  function claudeParts(sessionId) {
    const out = [];
    if (!sessionId || !fs.existsSync(claudeRoot)) return out;
    for (const folder of fs.readdirSync(claudeRoot)) {
      const base = path.join(claudeRoot, folder);
      const transcript = path.join(base, sessionId + '.jsonl');
      const artifacts = path.join(base, sessionId);
      if (fs.existsSync(transcript)) out.push({ source: transcript, relative: path.relative(claudeRoot, transcript), size: dirSize(transcript) });
      if (fs.existsSync(artifacts)) out.push({ source: artifacts, relative: path.relative(claudeRoot, artifacts), size: dirSize(artifacts) });
    }
    return out;
  }

  function scratchRecords() {
    const records = [];
    if (fs.existsSync(chatDir)) {
      for (const hash of fs.readdirSync(chatDir)) {
        const cwdPath = path.join(chatDir, hash);
        const markerPath = path.join(cwdPath, '.started');
        if (!fs.existsSync(markerPath)) continue;
        const meta = readJson(markerPath, {}) || {};
        const stat = fs.statSync(markerPath);
        const profile = profileInfo(meta.profileId, meta.engine);
        const sessionId = meta.sessionId || meta.id || '';
        const parts = profile.engine === 'claude' ? claudeParts(sessionId) : [];
        records.push({
          key: 'chat:' + hash, state: 'active', kind: 'chat', engine: profile.engine,
          provider: profile.provider, profileId: profile.id, profileLabel: profile.fullLabel,
          sessionId, openId: meta.openId || '', title: '飞书闲聊', projectPath: '', projectName: '',
          lastUsedAt: normalizeTime(meta.updatedAt, stat.mtimeMs), sizeBytes: dirSize(cwdPath) + parts.reduce((n, p) => n + p.size, 0),
          markerPath, cwdPath,
        });
      }
    }
    if (fs.existsSync(queryDir)) {
      for (const name of fs.readdirSync(queryDir).filter(x => x.endsWith('.started'))) {
        const markerPath = path.join(queryDir, name);
        const meta = readJson(markerPath, {}) || {};
        const stat = fs.statSync(markerPath);
        const profile = profileInfo(meta.profileId, meta.engine);
        const sessionId = meta.sessionId || meta.id || '';
        const hash = name.replace(/\.started$/i, '');
        const cwdPath = path.join(queryCwdBase, hash);
        const parts = profile.engine === 'claude' ? claudeParts(sessionId) : [];
        records.push({
          key: 'query:' + hash, state: 'active', kind: 'query', engine: profile.engine,
          provider: profile.provider, profileId: profile.id, profileLabel: profile.fullLabel,
          sessionId, openId: meta.openId || '', title: meta.name ? '查询: ' + meta.name : '飞书只读查询',
          projectPath: meta.path || '', projectName: meta.name || (meta.path ? path.basename(meta.path) : ''),
          lastUsedAt: normalizeTime(meta.updatedAt, stat.mtimeMs), sizeBytes: dirSize(markerPath) + dirSize(cwdPath) + parts.reduce((n, p) => n + p.size, 0),
          markerPath, cwdPath,
        });
      }
    }
    return records;
  }

  function excludedCwd(cwd) {
    if (!cwd) return true;
    const value = path.resolve(cwd).toLowerCase();
    const app = path.resolve(appDir).toLowerCase();
    const temp = path.resolve(os.tmpdir()).toLowerCase();
    const win = String(process.env.WINDIR || 'C:\\Windows').toLowerCase();
    return value === app || value.startsWith(app + path.sep) || value === temp || value.startsWith(temp + path.sep) || value === win || value.startsWith(win + path.sep);
  }

  function claudeWorkRecords(scratchIds) {
    const out = [];
    if (!fs.existsSync(claudeRoot)) return out;
    for (const folder of fs.readdirSync(claudeRoot)) {
      const base = path.join(claudeRoot, folder);
      let files = [];
      try { files = fs.readdirSync(base).filter(x => x.endsWith('.jsonl')); } catch (e) { continue; }
      for (const name of files) {
        const sessionId = name.replace(/\.jsonl$/i, '');
        if (scratchIds.has(sessionId)) continue;
        const file = path.join(base, name);
        const info = sessionTitle(file);
        if (excludedCwd(info.cwd)) continue;
        const stat = fs.statSync(file);
        const artifact = path.join(base, sessionId);
        out.push({
          key: 'work:claude:' + crypto.createHash('sha1').update(file.toLowerCase()).digest('hex'),
          state: 'active', kind: 'work', engine: 'claude', provider: 'claude',
          profileId: 'claude-session', profileLabel: 'Claude / DeepSeek', sessionId,
          openId: '', title: info.title, projectPath: info.cwd, projectName: path.basename(info.cwd || ''),
          lastUsedAt: stat.mtimeMs, sizeBytes: stat.size + dirSize(artifact), markerPath: '', cwdPath: info.cwd,
          transcriptPath: file,
        });
      }
    }
    return out.sort((a, b) => b.lastUsedAt - a.lastUsedAt);
  }

  async function codexRecords(archived, scratchIds) {
    const rows = await codex.listAll({ archived: !!archived, limit: 2000 });
    return rows.filter(row => !scratchIds.has(row.id) && !excludedCwd(row.cwd)).map(row => ({
      key: (archived ? 'native-archive:codex:' : 'work:codex:') + row.id,
      state: archived ? 'archived' : 'active', kind: 'work', engine: 'codex', provider: 'openai',
      profileId: 'openai-sol', profileLabel: 'OpenAI · GPT-5.6 Sol', sessionId: row.id,
      openId: '', title: row.title, projectPath: row.cwd || '', projectName: path.basename(row.cwd || ''),
      lastUsedAt: row.mtime || row.createdAt || 0, sizeBytes: 0, markerPath: '', cwdPath: row.cwd || '', nativeArchive: !!archived,
    }));
  }

  function publicRecord(record) {
    const copy = { ...record };
    delete copy.markerPath;
    delete copy.cwdPath;
    delete copy.transcriptPath;
    delete copy.payload;
    return copy;
  }

  async function report() {
    const scratch = scratchRecords();
    const scratchIds = new Set(scratch.map(x => x.sessionId).filter(Boolean));
    const [codexActive, codexArchived] = await Promise.all([codexRecords(false, scratchIds), codexRecords(true, scratchIds)]);
    const managed = manifest().records || [];
    const managedCodexIds = new Set(managed.filter(x => x.engine === 'codex').map(x => x.sessionId));
    const archives = managed.map(x => ({ ...x, key: 'archive:' + x.archiveId, state: 'archived' }));
    const native = codexArchived.filter(x => !managedCodexIds.has(x.sessionId));
    const records = [...scratch, ...claudeWorkRecords(scratchIds), ...codexActive, ...archives, ...native]
      .sort((a, b) => (b.lastUsedAt || 0) - (a.lastUsedAt || 0));
    const summary = {
      activeChat: records.filter(x => x.state === 'active' && x.kind === 'chat').length,
      activeQuery: records.filter(x => x.state === 'active' && x.kind === 'query').length,
      activeWork: records.filter(x => x.state === 'active' && x.kind === 'work').length,
      archived: records.filter(x => x.state === 'archived').length,
      sizeBytes: records.reduce((n, x) => n + Number(x.sizeBytes || 0), 0),
      archiveDays: config().archiveDays, deleteDays: config().deleteDays,
    };
    return { ok: true, summary, records: records.map(publicRecord) };
  }

  async function findActive(key) {
    const scratch = scratchRecords();
    let found = scratch.find(x => x.key === key);
    if (found) return found;
    const ids = new Set(scratch.map(x => x.sessionId).filter(Boolean));
    found = claudeWorkRecords(ids).find(x => x.key === key);
    if (found) return found;
    if (key.startsWith('work:codex:')) {
      const id = key.slice('work:codex:'.length);
      const rows = await codexRecords(false, ids);
      return rows.find(x => x.sessionId === id) || null;
    }
    if (key.startsWith('native-archive:codex:')) {
      const id = key.slice('native-archive:codex:'.length);
      const rows = await codexRecords(true, ids);
      return rows.find(x => x.sessionId === id) || null;
    }
    return null;
  }

  function archiveId() { return new Date(nowFn()).toISOString().replace(/[-:.TZ]/g, '').slice(0, 14) + '-' + crypto.randomBytes(4).toString('hex'); }

  function markerSnapshot(record) {
    if (!record.markerPath || !fs.existsSync(record.markerPath)) return null;
    return { relative: path.relative(appDir, record.markerPath), data: readJson(record.markerPath, {}) || {} };
  }

  function removeScratchFiles(record) {
    if (record.markerPath) { try { fs.rmSync(record.markerPath, { force: true }); } catch (e) {} }
    if (record.cwdPath && path.resolve(record.cwdPath).toLowerCase().startsWith(path.resolve(appDir).toLowerCase() + path.sep)) {
      try { fs.rmSync(record.cwdPath, { recursive: true, force: true }); } catch (e) {}
    }
  }

  async function archiveRecord(record, manifestValue) {
    const id = archiveId();
    const entry = {
      archiveId: id, kind: record.kind, engine: record.engine, provider: record.provider,
      profileId: record.profileId, profileLabel: record.profileLabel, sessionId: record.sessionId,
      openId: record.openId || '', title: record.title, projectPath: record.projectPath || '', projectName: record.projectName || '',
      lastUsedAt: record.lastUsedAt || nowFn(), archivedAt: nowFn(), sizeBytes: record.sizeBytes || 0,
      marker: markerSnapshot(record), payload: [], restorable: true,
    };
    if (record.engine === 'codex') {
      try { await codex.archive(record.sessionId); }
      catch (e) {
        if (!/no rollout|not found/i.test(String(e && e.message))) throw e;
        entry.restorable = false;
        entry.note = 'Codex 原生线程不存在，仅保留归档元数据';
      }
    } else {
      const parts = record.transcriptPath
        ? [{ source: record.transcriptPath, relative: path.relative(claudeRoot, record.transcriptPath), size: dirSize(record.transcriptPath) }, ...claudeParts(record.sessionId).filter(x => x.source !== record.transcriptPath)]
        : claudeParts(record.sessionId);
      for (const part of parts) {
        const target = path.join(archiveRoot, id, 'claude', part.relative);
        moveTree(part.source, target);
        entry.payload.push({ relative: part.relative, size: part.size });
      }
      if (!entry.payload.length) { entry.restorable = false; entry.note = '未找到 Claude 会话文件，仅保留归档元数据'; }
    }
    if (record.kind === 'chat' || record.kind === 'query') removeScratchFiles(record);
    manifestValue.records.push(entry);
    return entry;
  }

  async function archive(key) {
    return withLock(async () => {
      const value = manifest();
      const record = await findActive(key);
      if (!record || record.state !== 'active') throw new Error('找不到可归档的活动会话');
      const entry = await archiveRecord(record, value);
      saveManifest(value);
      return { ok: true, action: 'archive', record: publicRecord({ ...entry, key: 'archive:' + entry.archiveId, state: 'archived' }) };
    });
  }

  async function restore(key) {
    return withLock(async () => {
      if (key.startsWith('native-archive:codex:')) {
        const id = key.slice('native-archive:codex:'.length);
        await codex.unarchive(id);
        return { ok: true, action: 'restore', sessionId: id };
      }
      const id = key.replace(/^archive:/, '');
      const value = manifest();
      const index = value.records.findIndex(x => x.archiveId === id);
      if (index < 0) throw new Error('找不到归档记录');
      const entry = value.records[index];
      if (!entry.restorable) throw new Error(entry.note || '该记录没有可恢复的底层会话');
      if (entry.engine === 'codex') await codex.unarchive(entry.sessionId);
      else {
        for (const part of (entry.payload || [])) {
          const source = path.join(archiveRoot, entry.archiveId, 'claude', part.relative);
          const target = path.join(claudeRoot, part.relative);
          if (fs.existsSync(source)) moveTree(source, target);
        }
        try { fs.rmSync(path.join(archiveRoot, entry.archiveId), { recursive: true, force: true }); } catch (e) {}
      }
      if (entry.marker) {
        const markerPath = path.join(appDir, entry.marker.relative);
        const data = { ...(entry.marker.data || {}), updatedAt: new Date(nowFn()).toISOString() };
        fs.mkdirSync(path.dirname(markerPath), { recursive: true });
        fs.writeFileSync(markerPath, JSON.stringify(data), 'utf8');
        if (entry.kind === 'chat') fs.mkdirSync(path.dirname(markerPath), { recursive: true });
        if (entry.kind === 'query') {
          const hash = path.basename(markerPath).replace(/\.started$/i, '');
          fs.mkdirSync(path.join(queryCwdBase, hash), { recursive: true });
        }
      }
      value.records.splice(index, 1);
      saveManifest(value);
      return { ok: true, action: 'restore', sessionId: entry.sessionId };
    });
  }

  async function deleteRecord(record) {
    if (record.engine === 'codex' && record.sessionId) {
      try { await codex.remove(record.sessionId); }
      catch (e) { if (!/no rollout|not found/i.test(String(e && e.message))) throw e; }
    } else if (record.transcriptPath) {
      try { fs.rmSync(record.transcriptPath, { force: true }); } catch (e) {}
      try { fs.rmSync(path.join(path.dirname(record.transcriptPath), record.sessionId), { recursive: true, force: true }); } catch (e) {}
    } else {
      for (const part of claudeParts(record.sessionId)) { try { fs.rmSync(part.source, { recursive: true, force: true }); } catch (e) {} }
    }
    if (record.kind === 'chat' || record.kind === 'query') removeScratchFiles(record);
  }

  async function remove(key) {
    return withLock(async () => {
      if (key.startsWith('archive:')) {
        const id = key.slice('archive:'.length);
        const value = manifest();
        const index = value.records.findIndex(x => x.archiveId === id);
        if (index < 0) throw new Error('找不到归档记录');
        const entry = value.records[index];
        if (entry.engine === 'codex' && entry.sessionId) {
          try { await codex.remove(entry.sessionId); }
          catch (e) { if (!/no rollout|not found/i.test(String(e && e.message))) throw e; }
        }
        try { fs.rmSync(path.join(archiveRoot, entry.archiveId), { recursive: true, force: true }); } catch (e) {}
        value.records.splice(index, 1); saveManifest(value);
        return { ok: true, action: 'delete', deleted: 1 };
      }
      const record = await findActive(key);
      if (!record) throw new Error('找不到要删除的会话');
      await deleteRecord(record);
      return { ok: true, action: 'delete', deleted: 1 };
    });
  }

  async function forgetChat(openId) {
    return withLock(async () => {
      const legacyKeys = new Set();
      if (openId) {
        for (const profile of profilesFor(true)) {
          const seed = `chat|${openId}|${profile.id}`;
          legacyKeys.add('chat:' + crypto.createHash('sha1').update(seed).digest('hex'));
        }
      }
      const records = scratchRecords().filter(x => x.kind === 'chat'
        && (!openId || x.openId === openId || legacyKeys.has(x.key)));
      for (const record of records) await deleteRecord(record);
      return { ok: true, action: 'forget-chat', deleted: records.length };
    });
  }

  async function clearQuery(projectPath, openId, profileId) {
    const wantPath = String(projectPath || '').toLowerCase();
    return withLock(async () => {
      const legacyKeys = new Set();
      if (wantPath && openId) {
        const profiles = profileId ? [profileInfo(profileId)] : profilesFor(true);
        for (const profile of profiles) {
          const seed = wantPath + '|' + openId + '|' + profile.id;
          legacyKeys.add('query:' + crypto.createHash('sha1').update(seed).digest('hex'));
        }
      }
      const records = scratchRecords().filter(x => x.kind === 'query'
        && (!wantPath || String(x.projectPath || '').toLowerCase() === wantPath)
        && (!openId || x.openId === openId || legacyKeys.has(x.key))
        && (!profileId || x.profileId === profileId));
      for (const record of records) await deleteRecord(record);
      return { ok: true, action: 'clear-query', deleted: records.length };
    });
  }

  function safeClaudeGarbage(scratchIds) {
    const now = nowFn();
    const records = [];
    if (!fs.existsSync(claudeRoot)) return records;
    const app = path.resolve(appDir).toLowerCase();
    const temp = path.resolve(os.tmpdir()).toLowerCase();
    const win = String(process.env.WINDIR || 'C:\\Windows').toLowerCase();
    for (const folder of fs.readdirSync(claudeRoot)) {
      const base = path.join(claudeRoot, folder);
      let files = [];
      try { files = fs.readdirSync(base).filter(x => x.endsWith('.jsonl')); } catch (e) { continue; }
      for (const name of files) {
        const id = name.replace(/\.jsonl$/i, '');
        if (scratchIds.has(id)) continue;
        const file = path.join(base, name);
        const stat = fs.statSync(file);
        const cwd = path.resolve(sessionTitle(file).cwd || '').toLowerCase();
        let threshold = 0;
        if (cwd === app) threshold = 20 * 60 * 1000;
        else if ((cwd === temp || cwd.startsWith(temp + path.sep)) && /claude-resume|provider-test|tourtest/i.test(cwd)) threshold = 60 * 60 * 1000;
        else if (cwd === win || cwd.startsWith(win + path.sep)) threshold = DAY_MS;
        else if (cwd.startsWith(app + path.sep)) threshold = DAY_MS;
        if (threshold && now - stat.mtimeMs >= threshold) records.push({ sessionId: id, transcriptPath: file });
      }
    }
    return records;
  }

  async function cleanup() {
    return withLock(async () => {
      const c = config();
      const value = manifest();
      const summary = { archived: 0, deleted: 0, safeDeleted: 0, skipped: 0 };
      if (c.enabled) {
        const now = nowFn();
        for (const record of scratchRecords()) {
          const age = now - record.lastUsedAt;
          if (age >= c.deleteDays * DAY_MS) { await deleteRecord(record); summary.deleted++; }
          else if (age >= c.archiveDays * DAY_MS) { await archiveRecord(record, value); summary.archived++; }
          else summary.skipped++;
        }
        for (let i = value.records.length - 1; i >= 0; i--) {
          const entry = value.records[i];
          if (entry.kind !== 'chat' && entry.kind !== 'query') continue;
          if (now - Number(entry.lastUsedAt || entry.archivedAt || now) < c.deleteDays * DAY_MS) continue;
          if (entry.engine === 'codex' && entry.sessionId) {
            try { await codex.remove(entry.sessionId); }
            catch (e) { if (!/no rollout|not found/i.test(String(e && e.message))) throw e; }
          }
          try { fs.rmSync(path.join(archiveRoot, entry.archiveId), { recursive: true, force: true }); } catch (e) {}
          value.records.splice(i, 1); summary.deleted++;
        }
      }
      const activeIds = new Set(scratchRecords().map(x => x.sessionId).filter(Boolean));
      for (const garbage of safeClaudeGarbage(activeIds)) {
        try { fs.rmSync(garbage.transcriptPath, { force: true }); } catch (e) {}
        try { fs.rmSync(path.join(path.dirname(garbage.transcriptPath), garbage.sessionId), { recursive: true, force: true }); } catch (e) {}
        summary.safeDeleted++;
      }
      saveManifest(value);
      return { ok: true, action: 'cleanup', summary };
    });
  }

  return { config, report, archive, restore, remove, forgetChat, clearQuery, cleanup, scratchRecords };
}

async function cli() {
  const manager = createSessionManager();
  const command = process.argv[2] || 'report';
  let result;
  if (command === 'report') result = await manager.report();
  else if (command === 'cleanup') result = await manager.cleanup();
  else if (command === 'archive') result = await manager.archive(process.argv[3] || '');
  else if (command === 'restore') result = await manager.restore(process.argv[3] || '');
  else if (command === 'delete') result = await manager.remove(process.argv[3] || '');
  else if (command === 'forget-chat') result = await manager.forgetChat(process.argv[3] || '');
  else if (command === 'clear-query') result = await manager.clearQuery(process.argv[3] || '', process.argv[4] || '', process.argv[5] || '');
  else throw new Error('未知命令: ' + command);
  process.stdout.write(JSON.stringify(result));
}

if (require.main === module) {
  cli().catch(error => {
    process.stdout.write(JSON.stringify({ ok: false, error: String(error && error.message || error) }));
    process.exitCode = 1;
  });
}

module.exports = { createSessionManager, DAY_MS };
