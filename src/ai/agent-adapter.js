'use strict';

// S1-E AgentAdapter: a single-provider attempt boundary around the existing createAIRunner.
//
// Scope (owned here):
//   - wrapping one run/resume/cancel attempt of the underlying provider runner
//   - attempt lifecycle observation events (starting/running/terminal), never touching output
//   - terminal result hand-off back to the caller unchanged
//
// Explicitly NOT owned here (stays in callers / later stage packages):
//   - provider health probing, candidate selection, cross-provider fallback
//   - deadline / total-timeout computation, runKey concurrency policy, retry
//   - Feishu SDK/cards, configuration persistence, target C# RunContract state machine
//
// The underlying runner keeps its exact exception boundary: errors thrown by runner.run are
// re-thrown to the caller unchanged (the adapter only emits an observation event first).

const { createAIRunner } = require('./runners');

const READONLY_PROPS = ['claudeCmd', 'codexCmd', 'terminationGraceMs', 'killTree'];
const TERMINAL_META_KEYS = ['ok', 'errorCode', 'retryable', 'sideEffectsStarted', 'childPending', 'sessionId', 'ms'];

// sessionMode is observation metadata only; it never mutates opts and never changes CLI behavior.
// Codex resumes native threads by sessionId. Claude/DeepSeek resume an existing session when
// sessionExists=true, pin a fixed id otherwise; without a sessionId they continue the last
// session unless useContinue===false (a fresh run).
function computeSessionMode(opts) {
  const o = opts || {};
  const profile = o.profile || {};
  const sessionId = o.sessionId;
  if (profile.engine === 'codex') return sessionId ? 'resume' : 'new';
  if (sessionId) {
    if (o.sessionExists === true) return 'resume';
    return 'fixed';
  }
  if (o.useContinue !== false) return 'continue';
  return 'new';
}

// Events must never carry prompt, output text, keys, or full config. Only identity/lifecycle
// metadata plus the whitelisted small terminal fields are allowed.
function buildEventBase(cwd, opts) {
  const o = opts || {};
  const profile = o.profile || {};
  const base = {
    cwd: String(cwd || ''),
    sessionMode: computeSessionMode(o),
  };
  if (o.runKey !== undefined) base.runKey = String(o.runKey);
  if (o.taskKind !== undefined) base.taskKind = String(o.taskKind);
  if (profile.id !== undefined) base.profileId = String(profile.id);
  if (profile.provider !== undefined) base.provider = String(profile.provider);
  if (profile.engine !== undefined) base.engine = String(profile.engine);
  return base;
}

function terminalMeta(result) {
  const meta = {};
  for (const key of TERMINAL_META_KEYS) {
    if (result && result[key] !== undefined) meta[key] = result[key];
  }
  return meta;
}

function createAgentAdapter(options) {
  const opts = options || {};
  const logLine = typeof opts.logLine === 'function' ? opts.logLine : null;
  let runner = opts.runner || null;
  if (!runner) {
    const runnerOptions = Object.assign({}, opts);
    delete runnerOptions.runner;
    delete runnerOptions.onEvent;
    runner = createAIRunner(runnerOptions);
  }
  for (const method of ['run', 'cancel', 'waitForIdle']) {
    if (typeof runner[method] !== 'function') {
      throw new Error(`createAgentAdapter: 底层 runner 缺少 ${method} 能力,无法包装 provider attempt`);
    }
  }
  const onEvent = typeof opts.onEvent === 'function' ? opts.onEvent : null;

  // Safe observation callback: a throwing observer must never affect the attempt or its result.
  function emit(type, base, extra) {
    if (!onEvent) return;
    const event = Object.assign({ type, timestamp: new Date().toISOString() }, base, extra || {});
    try { onEvent(event); }
    catch (_) {
      try { if (logLine) logLine('agent-adapter onEvent 回调异常'); }
      catch (_) {}
    }
  }

  // Pass cwd/label/prompt/opts through untouched: no timeoutMs filling, no profile selection,
  // no fallback/retry, no networkRoute/tool/session rewriting.
  function run(cwd, label, prompt, runOpts) {
    const base = buildEventBase(cwd, runOpts);
    emit('starting', base);
    let attempt;
    try {
      attempt = runner.run(cwd, label, prompt, runOpts);
    } catch (e) {
      emit('terminal', base, { threw: true, ok: false });
      throw e;
    }
    emit('running', base);
    return Promise.resolve(attempt).then(
      result => {
        emit('terminal', base, terminalMeta(result));
        return result;
      },
      e => {
        emit('terminal', base, { threw: true, ok: false });
        throw e;
      }
    );
  }

  function cancel(runKey) { return runner.cancel(runKey); }
  function waitForIdle(runKey) { return runner.waitForIdle(runKey); }

  const api = { run, cancel, waitForIdle };
  for (const key of READONLY_PROPS) {
    Object.defineProperty(api, key, { enumerable: true, get: () => runner[key] });
  }
  return api;
}

module.exports = { createAgentAdapter };
