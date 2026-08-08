'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');

const DEFAULT_APP_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'ClaudeResume',
);

function parseJson(value) {
  if (!value || typeof value !== 'string') return null;
  try {
    const parsed = JSON.parse(value.replace(/^\uFEFF/, ''));
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
  } catch (e) { return null; }
}

function readConfig(appDir = DEFAULT_APP_DIR) {
  try {
    return JSON.parse(fs.readFileSync(path.join(appDir, 'config.json'), 'utf8').replace(/^\uFEFF/, ''));
  } catch (e) { return {}; }
}

function readCompletionContext(appDir = DEFAULT_APP_DIR) {
  try {
    return JSON.parse(fs.readFileSync(path.join(appDir, 'completion-context.json'), 'utf8').replace(/^\uFEFF/, ''));
  } catch (e) { return {}; }
}

function firstValue(object, names) {
  for (const name of names) {
    if (object && object[name] !== undefined && object[name] !== null && object[name] !== '') {
      return object[name];
    }
  }
  return null;
}

function stringList(value) {
  if (!Array.isArray(value)) return [];
  return value.map(v => {
    if (v && typeof v === 'object') return String(firstValue(v, ['path', 'fsPath', 'uri']) || '').trim();
    return String(v || '').trim();
  }).filter(Boolean);
}

function payloadFrom(source, args, stdinText) {
  if (source !== 'codex') {
    const stdinPayload = parseJson(stdinText);
    if (stdinPayload) return { payload: stdinPayload, raw: stdinText };
  }
  for (let i = args.length - 1; i >= 0; i--) {
    const parsed = parseJson(args[i]);
    if (parsed) return { payload: parsed, raw: args[i] };
  }
  return { payload: null, raw: '' };
}

function normalizedSourceLabel(source, payload, env) {
  if (source === 'codex') return { client: 'Codex', provider: 'openai', model: '' };
  if (source === 'claude') {
    const deepseek = /deepseek/i.test(String(env.ANTHROPIC_BASE_URL || ''))
      || /deepseek/i.test(String(env.ANTHROPIC_MODEL || ''));
    return {
      client: 'Claude Code',
      provider: deepseek ? 'deepseek' : 'claude',
      model: String(env.ANTHROPIC_MODEL || firstValue(payload, ['model', 'model_name']) || ''),
    };
  }
  const model = payload && payload.model && typeof payload.model === 'object' ? payload.model : {};
  return {
    client: 'Cline',
    provider: String(firstValue(model, ['provider']) || firstValue(payload, ['provider']) || ''),
    model: String(firstValue(model, ['slug', 'model']) || firstValue(payload, ['model_id']) || ''),
  };
}

function stableEventId(source, payload, paths, sessionId, taskId, turnId) {
  const explicit = firstValue(payload, ['event_id', 'eventId']);
  if (explicit) return `${source}:${explicit}`;
  if (turnId) return `${source}:${sessionId || 'session'}:${turnId}`;
  const timestamp = firstValue(payload, ['timestamp', 'created_at', 'createdAt']);
  if (taskId && timestamp) return `${source}:${taskId}:${timestamp}`;

  let transcriptMtime = '';
  const transcript = firstValue(payload, ['transcript_path', 'transcriptPath']);
  if (transcript) {
    try { transcriptMtime = String(Math.floor(fs.statSync(String(transcript)).mtimeMs)); } catch (e) {}
  }
  const assistant = String(firstValue(payload, ['last_assistant_message', 'lastAssistantMessage']) || '');
  const basis = JSON.stringify({ source, sessionId, paths, transcriptMtime, assistant });
  return `${source}:${crypto.createHash('sha1').update(basis).digest('hex')}`;
}

function normalizeEvent(source, payload, env = process.env) {
  if (!['codex', 'claude', 'cline'].includes(source) || !payload || env.AI_RESUME_INTERNAL_RUN === '1') return null;

  let projectRoots = [];
  if (source === 'cline') {
    projectRoots = stringList(firstValue(payload, ['workspaceRoots', 'workspace_roots']));
  }
  const cwd = firstValue(payload, ['cwd', 'working_directory', 'workingDirectory']);
  if (cwd && !projectRoots.includes(String(cwd))) projectRoots.unshift(String(cwd));
  if (!projectRoots.length && env.PWD) projectRoots.push(String(env.PWD));

  const sessionId = String(firstValue(payload, ['thread-id', 'thread_id', 'threadId', 'session_id', 'sessionId']) || '');
  const taskId = String(firstValue(payload, ['task_id', 'taskId']) || '');
  const turnId = String(firstValue(payload, ['turn-id', 'turn_id', 'turnId']) || '');
  const labels = normalizedSourceLabel(source, payload, env);
  const timestamp = firstValue(payload, ['timestamp', 'created_at', 'createdAt']);
  const createdAt = timestamp && !Number.isNaN(Date.parse(String(timestamp)))
    ? new Date(String(timestamp)).toISOString()
    : new Date().toISOString();

  return {
    version: 1,
    eventId: stableEventId(source, payload, projectRoots, sessionId, taskId, turnId),
    source,
    client: labels.client,
    provider: labels.provider,
    model: labels.model,
    status: 'finished',
    createdAt,
    projectRoots,
    sessionId,
    taskId,
    turnId,
  };
}

function codexHome(env = process.env) {
  return String(env.AI_RESUME_CODEX_HOME || env.CODEX_HOME || path.join(env.USERPROFILE || env.HOME || os.homedir(), '.codex'));
}

function codexDocumentsRoot(env = process.env) {
  return path.resolve(String(env.AI_RESUME_CODEX_DOCUMENTS_ROOT
    || path.join(env.USERPROFILE || env.HOME || os.homedir(), 'Documents', 'Codex')));
}

function codexThreadDateParts(threadId) {
  const match = /^([0-9a-f]{8})-([0-9a-f]{4})-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.exec(String(threadId || ''));
  if (!match) return null;
  const timestamp = Number.parseInt(match[1] + match[2], 16);
  if (!Number.isSafeInteger(timestamp)) return null;
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime()) || date.getUTCFullYear() < 2020 || date.getUTCFullYear() > 2200) return null;
  return [String(date.getUTCFullYear()), String(date.getUTCMonth() + 1).padStart(2, '0'), String(date.getUTCDate()).padStart(2, '0')];
}

function hasGitBoundary(start, stop) {
  let current;
  const boundary = path.resolve(stop);
  try { current = path.resolve(start); } catch (e) { return false; }
  while (current !== path.dirname(current)) {
    try { if (fs.existsSync(path.join(current, '.git'))) return true; } catch (e) {}
    if (current.toLowerCase() === boundary.toLowerCase()) break;
    const parent = path.dirname(current);
    if (!parent.toLowerCase().startsWith(boundary.toLowerCase())) break;
    current = parent;
  }
  return false;
}

function isCodexProjectlessRoot(value, env = process.env) {
  if (!value || !path.isAbsolute(String(value))) return false;
  const base = codexDocumentsRoot(env);
  let resolved, relative;
  try {
    resolved = path.resolve(String(value));
    relative = path.relative(base, resolved);
  } catch (e) { return false; }
  if (!relative || path.isAbsolute(relative) || relative === '..' || relative.startsWith('..' + path.sep)) return false;
  const parts = relative.split(path.sep).filter(Boolean);
  if (parts.length < 2 || !/^\d{4}-\d{2}-\d{2}$/.test(parts[0])) return false;
  return !hasGitBoundary(resolved, base);
}

function findRolloutInTree(root, suffix, maxDepth) {
  const stack = [{ dir: root, depth: 0 }];
  let scanError = false;
  while (stack.length) {
    const current = stack.pop();
    let entries;
    try { entries = fs.readdirSync(current.dir, { withFileTypes: true }); }
    catch (e) { if (!e || e.code !== 'ENOENT') scanError = true; continue; }
    for (const entry of entries) {
      if (entry.isFile() && entry.name.toLowerCase().endsWith(suffix)) return { file: path.join(current.dir, entry.name), scanError };
      if (entry.isDirectory() && current.depth < maxDepth) stack.push({ dir: path.join(current.dir, entry.name), depth: current.depth + 1 });
    }
  }
  return { file: null, scanError };
}

function findCodexRollout(threadId, env = process.env) {
  const id = String(threadId || '').trim();
  if (!/^[0-9a-z][0-9a-z-]{0,99}$/i.test(id)) return { file: null, reason: 'thread_id_invalid' };
  const suffix = `-${id}.jsonl`.toLowerCase();
  const home = codexHome(env);
  const dateParts = codexThreadDateParts(id);
  const sessionRoot = dateParts ? path.join(home, 'sessions', ...dateParts) : path.join(home, 'sessions');
  let hadScanError = false;
  const roots = [{ root: sessionRoot, depth: dateParts ? 1 : 6 }];
  if (dateParts) roots.push({ root: path.join(home, 'sessions'), depth: 6 });
  roots.push({ root: path.join(home, 'archived_sessions'), depth: 6 });
  for (const item of roots) {
    const found = findRolloutInTree(item.root, suffix, item.depth);
    if (found.file) return { file: found.file, reason: 'ok' };
    hadScanError = hadScanError || found.scanError;
  }
  return { file: null, reason: hadScanError ? 'rollout_scan_error' : 'rollout_missing' };
}

function readCodexSessionMeta(file) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const buffer = Buffer.alloc(256 * 1024);
    const bytes = fs.readSync(fd, buffer, 0, buffer.length, 0);
    for (const line of buffer.toString('utf8', 0, bytes).split(/\r?\n/)) {
      const parsed = parseJson(line);
      if (parsed && parsed.type === 'session_meta' && parsed.payload && typeof parsed.payload === 'object') return parsed.payload;
    }
  } catch (e) { return null; }
  finally { if (fd !== undefined) try { fs.closeSync(fd); } catch (e) {} }
  return null;
}

function codexThreadAdmission(threadId, env = process.env) {
  const found = findCodexRollout(threadId, env);
  if (!found.file) return { allowed: false, reason: found.reason };
  const meta = readCodexSessionMeta(found.file);
  if (!meta) return { allowed: false, reason: 'rollout_meta_missing', file: found.file };
  const metaId = String(meta.id || meta.session_id || '');
  if (!metaId || metaId !== String(threadId)) return { allowed: false, reason: 'rollout_meta_mismatch', file: found.file };
  if (meta.parent_thread_id || meta.thread_source === 'subagent'
    || (meta.source && typeof meta.source === 'object' && meta.source.subagent)) {
    return { allowed: false, reason: 'subagent_thread', file: found.file };
  }
  return { allowed: true, reason: 'ok', file: found.file };
}

function codexThreadPersisted(threadId, env = process.env) {
  return codexThreadAdmission(threadId, env).allowed;
}

function admitCompletionEvent(event, env = process.env) {
  if (!event) return { event: null, reason: 'event_invalid' };
  const roots = Array.isArray(event.projectRoots) ? event.projectRoots.filter(Boolean) : [];
  if (!roots.length) return { event: null, reason: 'workspace_missing' };
  if (event.source !== 'codex') return { event, reason: 'ok' };
  if (!event.sessionId) return { event: null, reason: 'thread_id_missing' };
  if (!event.turnId) return { event: null, reason: 'turn_id_missing' };
  const thread = codexThreadAdmission(event.sessionId, env);
  if (!thread.allowed) return { event: null, reason: thread.reason };
  const meaningfulRoots = roots.filter(root => !isCodexProjectlessRoot(root, env));
  return meaningfulRoots.length
    ? { event: { ...event, projectRoots: meaningfulRoots }, reason: 'ok' }
    : { event: null, reason: 'projectless_workspace' };
}

function eventForQueue(event, env = process.env) {
  return admitCompletionEvent(event, env).event;
}

function localDateStamp(date = new Date()) {
  return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-');
}

function localTimestamp(date = new Date()) {
  return `${localDateStamp(date)} ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}:${String(date.getSeconds()).padStart(2, '0')}`;
}

function logCompletionDiagnostic(appDir, event, reason, error) {
  try {
    const now = new Date();
    const day = localDateStamp(now);
    const logs = path.join(appDir, 'logs');
    fs.mkdirSync(logs, { recursive: true });
    const session = String(event && event.sessionId || '').slice(0, 48);
    const code = error && error.code ? ` code=${String(error.code).slice(0, 32)}` : '';
    fs.appendFileSync(path.join(logs, `completion-notify-${day}.log`),
      `[${localTimestamp(now)}] source=${String(event && event.source || 'unknown')} reason=${reason} session=${session}${code}\n`, 'utf8');
  } catch (e) {}
}

function queueDir(appDir = DEFAULT_APP_DIR, env = process.env) {
  return env.AI_RESUME_COMPLETION_DIR || path.join(appDir, 'completion-events');
}

function writeEvent(event, dir = queueDir()) {
  fs.mkdirSync(dir, { recursive: true });
  const base = `${Date.now()}-${process.pid}-${crypto.randomBytes(5).toString('hex')}.json`;
  const target = path.join(dir, base);
  const temp = `${target}.tmp`;
  let fd;
  try {
    fd = fs.openSync(temp, 'wx');
    fs.writeFileSync(fd, JSON.stringify(event), 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    fs.renameSync(temp, target);
    return target;
  } finally {
    if (fd !== undefined && fd !== null) try { fs.closeSync(fd); } catch (e) {}
    try { fs.unlinkSync(temp); } catch (e) {}
  }
}

function previousNotify(args) {
  const index = args.indexOf('--previous-notify');
  if (index < 0 || !args[index + 1]) return null;
  try {
    const command = JSON.parse(args[index + 1]);
    return Array.isArray(command) && command.length ? command.map(String) : null;
  } catch (e) { return null; }
}

function forwardPrevious(command, rawPayload) {
  if (!command || !command.length) return false;
  try {
    const executable = command[0];
    if (/\.(?:cmd|bat)$/i.test(executable)) return false;
    const args = command.slice(1);
    if (rawPayload) args.push(rawPayload);
    const child = spawn(executable, args, {
      windowsHide: true,
      detached: true,
      stdio: 'ignore',
      shell: false,
    });
    child.once('error', () => {});
    child.unref();
    return true;
  } catch (e) { return false; }
}

function hookOutput(source) {
  if (source === 'cline') return JSON.stringify({ cancel: false });
  if (source === 'claude') return JSON.stringify({ continue: true, suppressOutput: true });
  return '';
}

function run(argv = process.argv.slice(2), env = process.env) {
  const source = String(argv[0] || '').toLowerCase();
  const args = argv.slice(1);
  let stdinText = '';
  if (source && source !== 'codex') {
    try { stdinText = fs.readFileSync(0, 'utf8'); } catch (e) {}
  }
  const input = payloadFrom(source, args, stdinText);
  const appDir = env.AI_RESUME_APP_DIR || DEFAULT_APP_DIR;
  const config = readConfig(appDir);
  if (config.completionNotifyEnabled !== false) {
    const context = readCompletionContext(appDir);
    const admissionEnv = Object.assign({}, env);
    if (!admissionEnv.AI_RESUME_CODEX_DOCUMENTS_ROOT && context.codexDocumentsRoot) {
      admissionEnv.AI_RESUME_CODEX_DOCUMENTS_ROOT = context.codexDocumentsRoot;
    }
    const normalized = normalizeEvent(source, input.payload, admissionEnv);
    const admission = admitCompletionEvent(normalized, admissionEnv);
    if (admission.event) {
      try { writeEvent(admission.event, queueDir(appDir, admissionEnv)); }
      catch (e) { logCompletionDiagnostic(appDir, normalized, 'queue_write_failed', e); }
    } else if (normalized) logCompletionDiagnostic(appDir, normalized, admission.reason);
  }
  if (source === 'codex') forwardPrevious(previousNotify(args), input.raw);
  const output = hookOutput(source);
  if (output) process.stdout.write(output);
  return input.payload;
}

if (require.main === module) run();

module.exports = {
  parseJson, payloadFrom, normalizeEvent, admitCompletionEvent, eventForQueue,
  findCodexRollout, codexThreadAdmission, codexThreadPersisted, isCodexProjectlessRoot,
  writeEvent, queueDir, previousNotify,
  forwardPrevious, hookOutput, run,
};
