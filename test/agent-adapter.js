'use strict';

// S1-E AgentAdapter contract tests: pure fake runner, no network, no real config, no AI.
const assert = require('assert');
const { createAgentAdapter } = require('../src/ai/agent-adapter');

let failed = 0;
const check = (name, ok, detail) => {
  console.log((ok ? '  ✓ ' : '  ✗ ') + name + (ok ? '' : ' — ' + detail));
  if (!ok) failed++;
};

function fakeRunner(overrides) {
  const calls = { run: [], cancel: [], waitForIdle: [] };
  const runner = {
    calls,
    claudeCmd: 'claude-test.cmd',
    codexCmd: 'codex-test.exe',
    terminationGraceMs: 4321,
    killTree: async () => true,
    run(cwd, label, prompt, opts) {
      calls.run.push({ cwd, label, prompt, opts });
      return Promise.resolve({ ok: true, ms: 1 });
    },
    cancel(runKey) { calls.cancel.push(runKey); return 'cancel-ok'; },
    waitForIdle(runKey) { calls.waitForIdle.push(runKey); return Promise.resolve('idle-ok'); },
  };
  return Object.assign(runner, overrides || {});
}

const EVENT_ALLOWED = new Set([
  'type', 'timestamp', 'cwd', 'sessionMode', 'runKey', 'taskKind', 'profileId', 'provider', 'engine',
]);
const TERMINAL_ALLOWED = new Set([
  ...EVENT_ALLOWED, 'ok', 'errorCode', 'retryable', 'sideEffectsStarted', 'childPending', 'sessionId', 'ms', 'threw',
]);

async function collectRun(adapter, cwd, label, prompt, opts, events) {
  const result = await adapter.run(cwd, label, prompt, opts);
  return result;
}

async function main() {
  // 1. 依赖验证:run/cancel/waitForIdle 缺一不可;未注入 runner 时用 createAIRunner 自建。
  assert.throws(() => createAgentAdapter({ runner: {} }), /run/);
  assert.throws(() => createAgentAdapter({ runner: { run: () => {} } }), /cancel/);
  assert.throws(() => createAgentAdapter({ runner: { run: () => {}, cancel: () => {} } }), /waitForIdle/);
  const minimal = fakeRunner();
  const okAdapter = createAgentAdapter({ runner: minimal });
  check('底层 runner 必须具备 run/cancel/waitForIdle 才可包装',
    ['run', 'cancel', 'waitForIdle'].every(m => typeof okAdapter[m] === 'function'));
  const auto = createAgentAdapter({ readConfig: () => ({}) });
  check('未注入 runner 时用现有 createAIRunner 创建',
    ['run', 'cancel', 'waitForIdle', 'killTree'].every(m => typeof auto[m] === 'function')
      && typeof auto.claudeCmd === 'string' && typeof auto.codexCmd === 'string'
      && Number.isFinite(auto.terminationGraceMs));

  // 2. 参数对象不克隆不修改且精确透传;结果也原样转交。
  const opts = {
    profile: { id: 'openai-sol', provider: 'openai', engine: 'codex', model: 'gpt-5.6-sol', fullLabel: 'OpenAI · GPT-5.6 Sol' },
    taskKind: 'modify', runKey: 'rk-passthrough', readOnly: true, noTools: true,
    nested: { list: [1, 2, 3] },
  };
  const snapshot = JSON.parse(JSON.stringify(opts));
  const passthrough = fakeRunner();
  const passthroughResult = { ok: true, ms: 7, text: 'RESULT' };
  passthrough.run = (cwd, label, prompt, o) => {
    passthrough.calls.run.push({ cwd, label, prompt, opts: o });
    return Promise.resolve(passthroughResult);
  };
  const passthroughAdapter = createAgentAdapter({ runner: passthrough });
  const returned = await passthroughAdapter.run('/work', 'label', 'prompt', opts);
  check('run 精确透传 cwd/label/prompt 且不克隆不修改 opts',
    passthrough.calls.run.length === 1
      && passthrough.calls.run[0].cwd === '/work'
      && passthrough.calls.run[0].label === 'label'
      && passthrough.calls.run[0].prompt === 'prompt'
      && passthrough.calls.run[0].opts === opts
      && JSON.stringify(opts) === JSON.stringify(snapshot),
    JSON.stringify(passthrough.calls.run[0] && passthrough.calls.run[0].opts));
  check('成功结果对象原样转交,不做克隆', returned === passthroughResult);

  // 3. 五种 sessionMode 纯观察元数据,且不改 opts。
  const modeCases = [
    { name: 'Codex sessionId=resume', opts: { profile: { id: 'openai-sol', provider: 'openai', engine: 'codex' }, sessionId: 'thread-1' }, expect: 'resume' },
    { name: 'Codex 无 sessionId=new', opts: { profile: { id: 'openai-sol', provider: 'openai', engine: 'codex' } }, expect: 'new' },
    { name: 'Claude/DeepSeek sessionExists=true=resume', opts: { profile: { id: 'deepseek-v4', provider: 'deepseek', engine: 'claude' }, sessionId: 'sess-1', sessionExists: true }, expect: 'resume' },
    { name: 'sessionId + sessionExists=false=fixed', opts: { profile: { id: 'claude-default', provider: 'claude', engine: 'claude' }, sessionId: 'sess-2', sessionExists: false }, expect: 'fixed' },
    { name: '无 sessionId 且 useContinue 未关闭=continue', opts: { profile: { id: 'claude-default', provider: 'claude', engine: 'claude' } }, expect: 'continue' },
    { name: '无 sessionId 且 useContinue=false=new', opts: { profile: { id: 'claude-default', provider: 'claude', engine: 'claude' }, useContinue: false }, expect: 'new' },
  ];
  for (const modeCase of modeCases) {
    const events = [];
    const runner = fakeRunner();
    const adapter = createAgentAdapter({ runner, onEvent: e => events.push(e) });
    const before = JSON.parse(JSON.stringify(modeCase.opts));
    await adapter.run('/w', 'mode-test', 'prompt', modeCase.opts);
    const terminal = events[events.length - 1];
    check(modeCase.name, terminal.sessionMode === modeCase.expect, `got=${terminal.sessionMode}`);
    check(modeCase.name + ' 不反向修改 opts', JSON.stringify(modeCase.opts) === JSON.stringify(before));
  }

  // 4. starting/running/terminal 顺序与无敏感字段。
  const events = [];
  const secretOpts = {
    profile: { id: 'openai-sol', provider: 'openai', engine: 'codex' },
    runKey: 'rk-events', taskKind: 'query',
    openaiApiKey: 'sk-secret', secretToken: 'hunter2', extra: 'dirty',
  };
  const sensitive = fakeRunner();
  const sensitiveAdapter = createAgentAdapter({ runner: sensitive, onEvent: e => events.push(e) });
  await sensitiveAdapter.run('/work', 'label', 'THIS IS THE PROMPT BODY', secretOpts);
  check('attempt 事件顺序为 starting/running/terminal',
    events.map(e => e.type).join(',') === 'starting,running,terminal',
    events.map(e => e.type).join(','));
  check('事件不含 prompt/输出正文/密钥/完整配置等敏感字段',
    events.every(e => {
      for (const key of Object.keys(e)) {
        if (!EVENT_ALLOWED.has(key) && !TERMINAL_ALLOWED.has(key)) return false;
      }
      return !JSON.stringify(e).includes('sk-secret')
        && !JSON.stringify(e).includes('hunter2')
        && !JSON.stringify(e).includes('THIS IS THE PROMPT BODY');
    }),
    JSON.stringify(events));
  check('starting/running 事件字段是只读身份元数据子集',
    events.slice(0, 2).every(e => Object.keys(e).every(k => EVENT_ALLOWED.has(k))));
  check('事件携带 runKey/taskKind/profileId/provider/engine/cwd/sessionMode/timestamp',
    events.every(e => e.timestamp && e.cwd === '/work' && e.runKey === 'rk-events' && e.taskKind === 'query'
      && e.profileId === 'openai-sol' && e.provider === 'openai' && e.engine === 'codex' && e.sessionMode === 'new'));

  // 5. terminal 只带白名单小元数据,正文/usage 等一律不进入事件。
  const terminalEvents = [];
  const terminalRunner = fakeRunner();
  terminalRunner.run = () => Promise.resolve({
    ok: false, errorCode: 'auth', retryable: true, sideEffectsStarted: false,
    childPending: false, sessionId: 'sess-x', ms: 123, text: 'SECRET BODY', usage: { in: 1 },
  });
  const terminalAdapter = createAgentAdapter({ runner: terminalRunner, onEvent: e => terminalEvents.push(e) });
  const terminalResult = await terminalAdapter.run('/w', 'l', 'p', { profile: { id: 'x', provider: 'y', engine: 'claude' }, runKey: 'rk-t' });
  const terminalEvent = terminalEvents[terminalEvents.length - 1];
  check('terminal 事件保留 ok/errorCode/retryable/sideEffectsStarted/childPending/sessionId/ms',
    terminalEvent.ok === false && terminalEvent.errorCode === 'auth' && terminalEvent.retryable === true
      && terminalEvent.sideEffectsStarted === false && terminalEvent.childPending === false
      && terminalEvent.sessionId === 'sess-x' && terminalEvent.ms === 123, JSON.stringify(terminalEvent));
  check('terminal 事件不泄露正文/usage 等非白名单字段',
    !Object.prototype.hasOwnProperty.call(terminalEvent, 'text') && !Object.prototype.hasOwnProperty.call(terminalEvent, 'usage')
      && !JSON.stringify(terminalEvent).includes('SECRET BODY'));
  check('terminal 结果仍完整返回给调用方', terminalResult.text === 'SECRET BODY' && terminalResult.usage.in === 1);

  // 6. onEvent 抛错不得影响运行。
  let onEventAttempts = 0;
  const throwingObserver = fakeRunner();
  const throwingAdapter = createAgentAdapter({
    runner: throwingObserver,
    onEvent: () => { onEventAttempts++; throw new Error('observer boom'); },
  });
  const observedResult = await throwingAdapter.run('/w', 'l', 'p', { profile: { id: 'p', provider: 'q', engine: 'claude' } });
  check('onEvent 抛错不影响 run 结果', observedResult.ok === true && onEventAttempts === 3, `attempts=${onEventAttempts}`);
  const throwingLoggerAdapter = createAgentAdapter({
    runner: fakeRunner(),
    onEvent: () => { throw new Error('observer boom'); },
    logLine: () => { throw new Error('logger boom'); },
  });
  const loggerResult = await throwingLoggerAdapter.run('/w', 'l', 'p', { profile: { id: 'p', provider: 'q', engine: 'claude' } });
  check('onEvent 与日志回调同时抛错也不影响 run 结果', loggerResult.ok === true);
  const observedCancel = throwingAdapter.cancel('k');
  const observedIdle = await throwingAdapter.waitForIdle('k');
  check('onEvent 抛错不影响 cancel/waitForIdle', observedCancel === 'cancel-ok' && observedIdle === 'idle-ok');

  // 7. runner 抛错继续抛给调用方,且先发 terminal threw 观察事件。
  const syncError = new Error('sync runner boom');
  const syncEvents = [];
  const syncRunner = fakeRunner();
  syncRunner.run = () => { throw syncError; };
  const syncAdapter = createAgentAdapter({ runner: syncRunner, onEvent: e => syncEvents.push(e) });
  let syncCaught = null;
  try { await syncAdapter.run('/w', 'l', 'p', { profile: { id: 'p', provider: 'q', engine: 'claude' } }); }
  catch (e) { syncCaught = e; }
  check('runner 同步抛错原样继续抛出', syncCaught === syncError, String(syncCaught));
  check('同步抛错事件序列为 starting/terminal 且 terminal.threw=true',
    syncEvents.map(e => e.type).join(',') === 'starting,terminal'
      && syncEvents[1].threw === true && syncEvents[1].ok === false,
    JSON.stringify(syncEvents.map(e => e.type)));

  const asyncError = new Error('async runner boom');
  const asyncEvents = [];
  const asyncRunner = fakeRunner();
  asyncRunner.run = () => Promise.reject(asyncError);
  const asyncAdapter = createAgentAdapter({ runner: asyncRunner, onEvent: e => asyncEvents.push(e) });
  let asyncCaught = null;
  try { await asyncAdapter.run('/w', 'l', 'p', { profile: { id: 'p', provider: 'q', engine: 'claude' } }); }
  catch (e) { asyncCaught = e; }
  check('runner 异步拒绝原样继续抛出', asyncCaught === asyncError, String(asyncCaught));
  check('异步抛错事件序列为 starting/running/terminal 且 terminal.threw=true',
    asyncEvents.map(e => e.type).join(',') === 'starting,running,terminal' && asyncEvents[2].threw === true,
    JSON.stringify(asyncEvents.map(e => e.type)));

  // 8. cancel/waitForIdle 与只读属性精确委托。
  const delegate = fakeRunner();
  const delegateAdapter = createAgentAdapter({ runner: delegate });
  const cancelValue = delegateAdapter.cancel('cancel-key');
  const idlePromise = delegateAdapter.waitForIdle('idle-key');
  const idleValue = await idlePromise;
  check('cancel/waitForIdle 精确委托底层 runner',
    cancelValue === 'cancel-ok' && idlePromise instanceof Promise && idleValue === 'idle-ok'
      && delegate.calls.cancel.join(',') === 'cancel-key' && delegate.calls.waitForIdle.join(',') === 'idle-key');
  check('claudeCmd/codexCmd/terminationGraceMs/killTree 只读暴露且与底层一致',
    delegateAdapter.claudeCmd === 'claude-test.cmd' && delegateAdapter.codexCmd === 'codex-test.exe'
      && delegateAdapter.terminationGraceMs === 4321 && delegateAdapter.killTree === delegate.killTree);
  assert.throws(() => { delegateAdapter.claudeCmd = 'overwrite'; }, TypeError);
  assert.throws(() => { delegateAdapter.killTree = () => {}; }, TypeError);
  check('只读属性禁止覆盖', delegateAdapter.claudeCmd === 'claude-test.cmd');

  // 9. adapter 本身不增加 deadline/fallback/retry。
  const noPolicyOpts = { profile: { id: 'openai-sol', provider: 'openai', engine: 'codex' }, runKey: 'rk-nopolicy' };
  const noPolicySnapshot = JSON.parse(JSON.stringify(noPolicyOpts));
  const noPolicyRunner = fakeRunner();
  noPolicyRunner.run = (cwd, label, prompt, o) => {
    noPolicyRunner.calls.run.push({ cwd, label, prompt, opts: o });
    return Promise.resolve({ ok: false, errorCode: 'auth', retryable: true, ms: 5 });
  };
  const noPolicyAdapter = createAgentAdapter({ runner: noPolicyRunner });
  const noPolicyResult = await noPolicyAdapter.run('/w', 'l', 'p', noPolicyOpts);
  check('adapter 不填 timeoutMs、不做 profile 选择/fallback/重试,失败结果原样转交',
    noPolicyRunner.calls.run.length === 1
      && !Object.prototype.hasOwnProperty.call(noPolicyRunner.calls.run[0].opts, 'timeoutMs')
      && JSON.stringify(noPolicyRunner.calls.run[0].opts) === JSON.stringify(noPolicySnapshot)
      && noPolicyResult.ok === false && noPolicyResult.errorCode === 'auth' && noPolicyResult.retryable === true,
    JSON.stringify(noPolicyRunner.calls.run[0].opts));

  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(1); });
