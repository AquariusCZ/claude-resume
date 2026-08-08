'use strict';
// S1-F TaskOrchestrator 单元测试:全部使用 fake adapter/health/config,无网络、无真实配置、
// 无 AI、无 sleep/静默判超时。小 deadline 只验证现役显式 timeout 兼容(预算递减/透传)。
const { createTaskOrchestrator } = require('../src/task-orchestrator');

const OWNER = 'ou_owner';
const PROFILES = {
  'openai-sol': { id: 'openai-sol', provider: 'openai', engine: 'codex', fullLabel: 'OpenAI · GPT-5.6 Sol', model: 'gpt-5.6-sol' },
  'deepseek-v4': { id: 'deepseek-v4', provider: 'deepseek', engine: 'claude', fullLabel: 'DeepSeek · V4', model: 'deepseek-v4-flash' },
  'deepseek-v4-pro': { id: 'deepseek-v4-pro', provider: 'deepseek', engine: 'claude', fullLabel: 'DeepSeek · V4 Pro', model: 'deepseek-v4-pro' },
  'claude-default': { id: 'claude-default', provider: 'claude', engine: 'claude', fullLabel: 'Claude · 默认', model: '' },
  'claude-fable-5': { id: 'claude-fable-5', provider: 'claude', engine: 'claude', fullLabel: 'Claude · Fable 5', model: 'claude-fable-5', ownerOnly: true },
};
const okResult = { ok: true };
const retryableAuth = { ok: false, errorCode: 'auth', retryable: true };

let failed = 0;
const check = (name, cond, detail) => {
  console.log((cond ? '  ✓ ' : '  ✗ ') + name + (cond ? '' : ' — ' + (detail === undefined ? '' : String(detail))));
  if (!cond) failed++;
};

function baseOptions(overrides) {
  const state = {
    cfg: { fallback: ['deepseek-v4', 'openai-sol'] },
    running: new Map(),
    health: {
      openai: { status: 'available', reason: 'ok', route: 'direct' },
      deepseek: { status: 'available', reason: 'ok', route: 'direct' },
      claude: { status: 'available', reason: 'ok' },
    },
    results: null,
    orphan: () => false,
    shuttingDown: false,
  };
  Object.assign(state, overrides || {});
  const calls = { runs: [], cancels: [], health: [], logs: [], observes: [] };
  const adapter = {
    run: async (cwd, label, prompt, opts) => {
      const index = calls.runs.length;
      calls.runs.push({ cwd, label, prompt, opts });
      if (state.beforeRun) state.beforeRun(index, { cwd, label, prompt, opts });
      const stamp = result => Object.assign({}, result, { profile: opts.profile });
      if (typeof state.results === 'function') return stamp(state.results(index, { cwd, label, prompt, opts }));
      const entry = Array.isArray(state.results) ? state.results[index] : okResult;
      return typeof entry === 'function' ? stamp(await entry()) : stamp(entry);
    },
    cancel: key => { calls.cancels.push(key); return true; },
  };
  const orchestrator = createTaskOrchestrator({
    agentAdapter: adapter,
    running: state.running,
    readConfig: () => state.cfg,
    getUserProfile: () => PROFILES['openai-sol'],
    canUseOwnerOnlyProfile: openId => openId === OWNER,
    profileById: id => PROFILES[id] || null,
    fallbackProfiles: (cfg, primaryId) => {
      const list = Array.isArray(cfg && cfg.fallback) ? cfg.fallback : ['deepseek-v4', 'openai-sol'];
      return list.map(id => PROFILES[id]).filter(p => p && p.id !== primaryId);
    },
    defaultProfileId: 'openai-sol',
    ensureFreshProviderHealth: (reason, provider) => {
      calls.health.push(provider);
      return state.healthPromise ? state.healthPromise : Promise.resolve(state.health);
    },
    providerHealth: provider => state.providerHealth
      ? state.providerHealth(provider)
      : state.health[provider] || { status: 'unavailable', reason: 'unknown', ms: 0 },
    providerReasonText: item => (item && item.reason) || 'unknown',
    orphanBlocksRun: state.orphan,
    isShuttingDown: () => state.shuttingDown,
    maxTimeoutMs: 0x7fffffff,
    defaultAiTimeoutMs: 30 * 60000,
    logLine: text => { calls.logs.push(text); if (state.logThrows) throw new Error('log throws'); },
    onAttempt: attempt => { calls.observes.push(attempt); if (state.observeThrows) throw new Error('observe throws'); },
  });
  return { orchestrator, state, calls, adapter };
}

function assertThrows(fn, pattern) {
  try { fn(); return null; }
  catch (e) { return String(e && e.message || e); }
}

function depOptions(overrides) {
  return Object.assign({
    agentAdapter: { run: async () => okResult, cancel: () => {} },
    running: new Map(),
    readConfig: () => ({}),
    getUserProfile: () => PROFILES['openai-sol'],
    canUseOwnerOnlyProfile: () => false,
    profileById: id => PROFILES[id],
    fallbackProfiles: () => [],
    defaultProfileId: 'openai-sol',
    ensureFreshProviderHealth: async () => ({}),
    providerHealth: () => ({}),
    providerReasonText: () => '',
    orphanBlocksRun: () => false,
    maxTimeoutMs: 100,
    defaultAiTimeoutMs: 100,
  }, overrides || {});
}

async function main() {
  // ---- 依赖校验 fail-fast ----
  {
    const missing = assertThrows(() => createTaskOrchestrator({}), '缺少必需依赖 agentAdapter');
    check('缺全部依赖时 fail-fast', !!missing, missing);
    const noRunning = assertThrows(() => createTaskOrchestrator(depOptions({ running: undefined })), '缺少必需依赖 running');
    check('缺 running 时 fail-fast', !!noRunning && /running/.test(noRunning), noRunning);
    const badAdapter = assertThrows(() => createTaskOrchestrator(depOptions({
      agentAdapter: { run: async () => okResult },
    })), 'agentAdapter 必须提供 run/cancel');
    check('agentAdapter 缺 cancel 时 fail-fast', !!badAdapter && /run\/cancel/.test(badAdapter), badAdapter);
    const badRunning = assertThrows(() => createTaskOrchestrator(depOptions({ running: {} })), 'running 必须是 Map 兼容容器');
    check('running 不是 Map 时 fail-fast', !!badRunning && /Map 兼容/.test(badRunning), badRunning);
  }

  // ---- tryReserve:同步占用/active running/释放 ----
  {
    const { orchestrator, state } = baseOptions({});
    check('空 key 拒绝预占', orchestrator.tryReserve('') === false && orchestrator.tryReserve(null) === false);
    check('首次预占成功且 isBusy 成立', orchestrator.tryReserve('Project-A') === true && orchestrator.isBusy('Project-A') === true);
    check('同 key 重复预占被拒绝', orchestrator.tryReserve('project-a') === false, 'case-insensitive');
    orchestrator.release('PROJECT-A');
    check('释放后 key 可再次预占', orchestrator.isBusy('project-a') === false && orchestrator.tryReserve('project-a') === true);
    state.running.set('running-key', {});
    check('活动 running child 占用后 tryReserve 拒绝且 isBusy 成立',
      orchestrator.tryReserve('RUNNING-KEY') === false && orchestrator.isBusy('running-key') === true);
  }

  // ---- 成功仅一次 attempt ----
  {
    const { orchestrator, calls } = baseOptions({});
    const result = await orchestrator.run('/proj', '成功一次', 'hello', OWNER, { profile: PROFILES['openai-sol'] });
    check('成功仅一次 attempt', result.ok && calls.runs.length === 1, JSON.stringify(calls.runs));
    check('成功结果带 attemptedProfiles', result.attemptedProfiles.join(',') === 'openai-sol', result.attemptedProfiles);
    check('openai 成功路径做了一次健康预检', calls.health.join(',') === 'openai', calls.health.join(','));
  }

  // ---- 已接纳但尚未 spawn 的 reservation 可被停止 ----
  {
    const { orchestrator, calls } = baseOptions({});
    const key = 'accepted-before-spawn';
    check('pre-spawn 场景先完成同步预占', orchestrator.tryReserve(key) === true);
    const outcome = orchestrator.cancel([key]);
    const result = await orchestrator.run('/proj', '接纳后立即停止', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: key, taskKind: 'modify', timeoutMs: 0,
    });
    check('停止命中 reservation 并在 provider/adapter 前返回 cancelled',
      outcome.kind === 'reservation' && outcome.key === key && result.errorCode === 'cancelled'
        && result.retryable === false && calls.health.length === 0 && calls.runs.length === 0,
      JSON.stringify({ outcome, result, health: calls.health, runs: calls.runs.length }));
    check('已取消 reservation 在显式 release 前仍保持 busy', orchestrator.isBusy(key) === true);
    orchestrator.release(key);
    check('release 后同 key 可重新接纳', orchestrator.tryReserve(key) === true);
  }

  // ---- shutdown gate:拒绝新预占与直接 run ----
  {
    const { orchestrator, state, calls } = baseOptions({ shuttingDown: true });
    check('shutdown 中拒绝新 reservation', orchestrator.tryReserve('shutdown-key') === false);
    const result = await orchestrator.run('/proj', '关停中', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: 'shutdown-key', taskKind: 'modify', timeoutMs: 0,
    });
    check('shutdown 中直接 run 也在 spawn 前 cancelled',
      result.errorCode === 'cancelled' && !result.retryable && calls.health.length === 0 && calls.runs.length === 0,
      JSON.stringify(result));
    state.shuttingDown = false;
  }

  // ---- owner-only 降级 ----
  {
    const { orchestrator, calls } = baseOptions({
      cfg: { fallback: ['claude-fable-5', 'deepseek-v4', 'openai-sol'] },
    });
    const result = await orchestrator.run('/proj', 'owner-only 降级', 'hello', 'ou_visitor', {
      profile: PROFILES['claude-fable-5'], allowFallback: false,
    });
    check('非 owner 的 owner-only primary 降级到默认 profile',
      result.profile.id === 'openai-sol' && calls.runs[0].opts.profile.id === 'openai-sol',
      calls.runs[0] && calls.runs[0].opts.profile.id);
  }
  {
    const { orchestrator, calls } = baseOptions({
      cfg: { fallback: ['claude-fable-5', 'deepseek-v4', 'openai-sol'] },
      results: [retryableAuth, retryableAuth, okResult],
    });
    const fail = await orchestrator.run('/proj', 'owner 保留 owner-only 后备', 'hello', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check('owner 的候选含 owner-only profile 且按其顺序 fallback',
      fail.ok && fail.fallbackFrom.id === 'openai-sol' && fail.profile.id === 'deepseek-v4'
        && calls.runs.map(r => r.opts.profile.id).join(',') === 'openai-sol,claude-fable-5,deepseek-v4',
      calls.runs.map(r => r.opts.profile.id).join(','));
  }

  // ---- fallback 顺序/提示/attemptedProfiles/fallbackFrom ----
  {
    const { orchestrator, calls } = baseOptions({ results: [retryableAuth, okResult] });
    const result = await orchestrator.run('/proj', '切换测试', '原始请求', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check('可重试失败后按 fallback 顺序切到 DeepSeek',
      result.ok && result.profile.id === 'deepseek-v4' && result.fallbackFrom.id === 'openai-sol'
        && result.attemptedProfiles.join(',') === 'openai-sol,deepseek-v4',
      JSON.stringify(result));
    check('fallback 提示注入原失败 provider 与目标 provider',
      calls.runs[1].prompt.includes('[自动切换说明]') && calls.runs[1].prompt.includes('OpenAI · GPT-5.6 Sol')
        && calls.runs[1].prompt.includes('DeepSeek · V4') && calls.runs[1].prompt.endsWith('原始请求'),
      calls.runs[1].prompt);
    check('fallback 切换记录日志', calls.logs.some(t => /自动切换 \[切换测试\] OpenAI · GPT-5.6 Sol -> DeepSeek · V4 \(auth\)/.test(t)), calls.logs.join('|'));
  }

  // ---- allowFallback=false ----
  {
    const { orchestrator, calls } = baseOptions({ results: [retryableAuth, okResult] });
    const result = await orchestrator.run('/proj', '禁后备', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false,
    });
    check('allowFallback=false 只尝试 primary', !result.ok && calls.runs.length === 1 && result.attemptedProfiles.length === 1,
      JSON.stringify({ result, runs: calls.runs.length }));
  }

  // ---- 不可重试/sideEffectsStarted/childPending/cancelled 禁止 fallback ----
  for (const [label, failure] of [
    ['不可重试', { ok: false, errorCode: 'unknown', retryable: false }],
    ['已产生副作用', { ok: false, errorCode: 'auth', retryable: true, sideEffectsStarted: true }],
    ['子进程挂起', { ok: false, errorCode: 'auth', retryable: true, childPending: true }],
    ['用户取消', { ok: false, errorCode: 'cancelled', retryable: false }],
  ]) {
    const { orchestrator, calls } = baseOptions({ results: [failure, okResult] });
    const result = await orchestrator.run('/proj', label, 'hello', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check(`${label} 时禁止 fallback`, !result.ok && calls.runs.length === 1 && result.attemptedProfiles.length === 1,
      `runs=${calls.runs.length} result=${JSON.stringify(result)}`);
  }

  // ---- D-002:retryable rate_limit + sideEffectsStarted 禁止 fallback;正向对照保留 ----
  {
    const { orchestrator, calls } = baseOptions({ results: [
      { ok: false, errorCode: 'rate_limit', retryable: true, sideEffectsStarted: true },
      okResult,
    ] });
    const result = await orchestrator.run('/proj', '限流已产生副作用', 'hello', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check('retryable rate_limit + sideEffectsStarted=true 只调用一次 provider、不启动 fallback',
      !result.ok && result.errorCode === 'rate_limit' && result.sideEffectsStarted === true
        && result.attemptedProfiles.join(',') === 'openai-sol' && calls.runs.length === 1,
      `runs=${calls.runs.length} result=${JSON.stringify(result)}`);
  }
  {
    const { orchestrator, calls } = baseOptions({ results: [
      { ok: false, errorCode: 'rate_limit', retryable: true, sideEffectsStarted: false },
      okResult,
    ] });
    const result = await orchestrator.run('/proj', '限流无副作用对照', 'hello', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check('retryable rate_limit + sideEffectsStarted=false 仍按现役规则 fallback',
      result.ok && result.fallbackFrom.id === 'openai-sol' && result.profile.id === 'deepseek-v4'
        && result.attemptedProfiles.join(',') === 'openai-sol,deepseek-v4' && calls.runs.length === 2,
      `runs=${calls.runs.length} result=${JSON.stringify(result)}`);
  }

  // ---- 健康 route ----
  {
    const { orchestrator, calls } = baseOptions({});
    const direct = await orchestrator.run('/proj', '健康线路', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false,
    });
    check('正式任务固定使用健康缓存的直连线路', direct.ok && calls.runs[0].opts.networkRoute === 'direct',
      calls.runs[0] && calls.runs[0].opts.networkRoute);
    const proxyRun = await orchestrator.run('/proj', '显式线路', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false, networkRoute: 'proxy',
    });
    check('显式 networkRoute 透传且不做健康预检', proxyRun.ok && calls.runs[1].opts.networkRoute === 'proxy' && calls.health.length === 1,
      `route=${calls.runs[1] && calls.runs[1].opts.networkRoute} health=${calls.health.length}`);
    const claudeRun = await orchestrator.run('/proj', 'claude 无预检', 'hello', OWNER, {
      profile: PROFILES['claude-default'], allowFallback: false,
    });
    check('claude 不进入健康预检且无线路', claudeRun.ok && calls.health.length === 1 && calls.runs[2].opts.networkRoute == null,
      `health=${calls.health.length}`);
  }

  // ---- 健康不可用的 fallback / 非可重试健康失败 ----
  {
    const { orchestrator, calls } = baseOptions({
      health: {
        openai: { status: 'unavailable', reason: 'auth', ms: 12 },
        deepseek: { status: 'available', reason: 'ok', route: 'direct' },
        claude: { status: 'available', reason: 'ok' },
      },
      results: [okResult],
    });
    const result = await orchestrator.run('/proj', '健康降级', 'hello', OWNER, { profile: PROFILES['openai-sol'] });
    check('健康不可用(可重试)后回退到下一个可用 provider',
      result.ok && result.profile.id === 'deepseek-v4' && result.fallbackFrom.id === 'openai-sol',
      JSON.stringify(result));
    check('健康失败作为 fallback 前置失败参与提示', calls.runs[0].prompt.includes('[自动切换说明]')
        && calls.runs[0].prompt.includes('原 AI「OpenAI · GPT-5.6 Sol」因 auth 未完成')
        && calls.runs[0].prompt.includes('DeepSeek · V4'),
      calls.runs[0].prompt);
  }
  {
    const { orchestrator, calls } = baseOptions({
      health: {
        openai: { status: 'unavailable', reason: 'unknown', ms: 5 },
        deepseek: { status: 'available', reason: 'ok', route: 'direct' },
        claude: { status: 'available', reason: 'ok' },
      },
      results: [okResult],
    });
    const result = await orchestrator.run('/proj', '健康硬失败', 'hello', OWNER, { profile: PROFILES['openai-sol'] });
    check('非可重试健康失败立即返回不 fallback', !result.ok && result.errorCode === 'unknown' && calls.runs.length === 0,
      `runs=${calls.runs.length} result=${JSON.stringify(result)}`);
  }

  // ---- 等待健康时 cancel 不启动正式 attempt ----
  {
    let resolveHealth;
    const healthPromise = new Promise(resolve => { resolveHealth = resolve; });
    const { orchestrator, calls } = baseOptions({ healthPromise });
    const key = 'preflight-cancel-key';
    const runPromise = orchestrator.run('/proj', '预检取消', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
    });
    const outcome = orchestrator.cancel([key, 'other-key']);
    const result = await runPromise;
    check('等待健康时 cancel 命中 preflight 且不启动正式 attempt',
      outcome.kind === 'preflight' && outcome.key === key && result.errorCode === 'cancelled'
        && result.retryable === false && calls.runs.length === 0 && calls.cancels.length === 0,
      JSON.stringify({ outcome, result }));
    resolveHealth({});
    check('取消后 preflight token 已释放(再次取消返回 none)',
      orchestrator.cancel([key]).kind === 'none', JSON.stringify(orchestrator.cancel([key])));
  }

  // ---- 健康缓存命中后到正式 child 登记前仍可停止(gap) ----
  {
    let resolveProfile;
    const profileGate = new Promise(resolve => { resolveProfile = resolve; });
    const { orchestrator, calls } = baseOptions({});
    const key = 'preflight-gap-key';
    const runPromise = orchestrator.run('/proj', '预检后间隙取消', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
      forProfile: async () => { await profileGate; return {}; },
    });
    const outcome = orchestrator.cancel([key]);
    resolveProfile({});
    const result = await runPromise;
    check('健康已缓存但 forProfile 未完成时仍可取消', outcome.kind === 'preflight' && result.errorCode === 'cancelled' && calls.runs.length === 0,
      JSON.stringify({ outcome, result }));
  }

  // ---- 活动 child cancel 优先 ----
  {
    const { orchestrator, state, calls } = baseOptions({});
    state.running.set('child-key', {});
    let resolvePreflight;
    const preflightPromise = new Promise(resolve => { resolvePreflight = resolve; });
    const withToken = baseOptions({ healthPromise: preflightPromise });
    withToken.state.running.set('child-key', {});
    withToken.orchestrator.cancel(['child-key', 'child-key']);
    check('活动 child 命中时只调用一次 cancel', withToken.calls.cancels.length === 1 && withToken.calls.cancels[0] === 'child-key');
    const childOutcome = orchestrator.cancel(['child-key', 'other']);
    check('cancel 返回 child 结构', childOutcome.kind === 'child' && childOutcome.key === 'child-key');
    const noneOutcome = orchestrator.cancel(['no-such-key']);
    check('无活动 child 且无 preflight 时返回 none', noneOutcome.kind === 'none', JSON.stringify(noneOutcome));
  }

  // ---- D-001:孤儿占位 cancel fail-closed ----
  {
    const { orchestrator, state, calls } = baseOptions({});
    const orphanKey = 'orphan-cancel-key';
    const orphanPlaceholder = { pid: 601, orphan: true, runKey: orphanKey };
    state.running.set(orphanKey, orphanPlaceholder);
    const outcome = orchestrator.cancel([orphanKey]);
    check('cancel 命中孤儿占位返回 orphan 且不调用 adapter.cancel',
      outcome.kind === 'orphan' && outcome.key === orphanKey && calls.cancels.length === 0,
      JSON.stringify({ outcome, cancels: calls.cancels }));
    const repeat = orchestrator.cancel([orphanKey]);
    check('重复 cancel 孤儿仍返回 orphan 且不调用 adapter.cancel',
      repeat.kind === 'orphan' && calls.cancels.length === 0,
      JSON.stringify({ repeat, cancels: calls.cancels }));
  }
  {
    // orphan 命中时绝不继续取消同批 preflight;preflight 保持可用,正式 attempt 正常完成。
    const { orchestrator, state, calls } = baseOptions({});
    const orphanKey = 'orphan-batch-key';
    state.running.set(orphanKey, { pid: 602, orphan: true, runKey: orphanKey });
    const preflightKey = 'orphan-batch-preflight-key';
    const runPromise = orchestrator.run('/proj', '预检挂起(孤儿批次)', 'x', OWNER, {
      profile: PROFILES['openai-sol'], runKey: preflightKey, allowFallback: false,
    });
    const outcome = orchestrator.cancel([orphanKey, preflightKey]);
    const result = await runPromise;
    check('cancel 命中 orphan 后不继续取消同批 preflight 且不调用 adapter.cancel',
      outcome.kind === 'orphan' && calls.cancels.length === 0 && result.ok && calls.runs.length === 1,
      JSON.stringify({ outcome, result, runs: calls.runs.length, cancels: calls.cancels }));
  }
  {
    // 正常活动 child 不回归:orphan 之外的 active child 仍只取消一次。
    const { orchestrator, state, calls } = baseOptions({});
    state.running.set('active-key', {});
    const outcome = orchestrator.cancel(['active-key']);
    check('正常活动 child 语义不回归(child 优先、只取消一次)',
      outcome.kind === 'child' && outcome.key === 'active-key' && calls.cancels.join(',') === 'active-key',
      JSON.stringify({ outcome, cancels: calls.cancels }));
  }

  // ---- legacy 0 / 30 分钟 / 显式 timeout / 总预算 remaining ----
  {
    const { orchestrator, calls: timeoutCalls } = baseOptions({});
    check('modify 无总时限', orchestrator.taskTimeoutMs('modify', {}) === 0);
    check('query 默认 30 分钟', orchestrator.taskTimeoutMs('query', {}) === 30 * 60000);
    check('chat 默认 30 分钟且可独立配置', orchestrator.taskTimeoutMs('chat', { feishuChatTimeoutMinutes: 45 }) === 45 * 60000);
    check('未知 taskKind 返回 null', orchestrator.taskTimeoutMs('resume', {}) === null);
    check('超大配置被限制在 Node 安全上限', orchestrator.taskTimeoutMs('query', { feishuQueryTimeoutMinutes: 99999999 }) === 0x7fffffff);

    const zero = await orchestrator.run('/proj', '显式 0', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false, timeoutMs: 0,
    });
    check('显式 timeoutMs=0 原样透传且不注入计时器', zero.ok && zero.attemptedProfiles.length === 1);

    const legacyRun = await orchestrator.run('/proj', 'legacy query', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false, taskKind: 'query',
    });
    check('legacy query 预算按剩余时间传给 attempt',
      legacyRun.ok && timeoutCalls.runs[1].opts.timeoutMs > 0 && timeoutCalls.runs[1].opts.timeoutMs <= 30 * 60000,
      String(timeoutCalls.runs[1] && timeoutCalls.runs[1].opts.timeoutMs));

    let markFirst, resolveFirst;
    const started = new Promise(resolve => { markFirst = resolve; });
    const firstGate = new Promise(resolve => { resolveFirst = resolve; });
    const budget = baseOptions({
      beforeRun(index) { if (index === 0) markFirst(); },
      results: [
        async () => { await firstGate; return retryableAuth; },
        okResult,
      ],
    });
    // 用受控时钟确定性验证预算递减,不用 sleep/静默判超时。
    const realNow = Date.now;
    let fakeNow = realNow();
    Date.now = () => fakeNow;
    try {
      const budgetPromise = budget.orchestrator.run('/proj', '总预算', 'hello', OWNER, {
        profile: PROFILES['openai-sol'], timeoutMs: 120,
      });
      await started;
      const firstTimeout = budget.calls.runs[0].opts.timeoutMs;
      fakeNow += 40;
      resolveFirst();
      const budgeted = await budgetPromise;
      const secondTimeout = budget.calls.runs[1].opts.timeoutMs;
      check('fallback 共用一次总预算且剩余递减',
        budgeted.ok && budget.calls.runs.length === 2 && firstTimeout === 120 && secondTimeout === 80,
        `first=${firstTimeout} second=${secondTimeout}`);
    } finally {
      Date.now = realNow;
    }
  }

  // ---- forProfile 抛错 / adapter 抛错后 preflight token 释放 ----
  {
    const { orchestrator, calls } = baseOptions({});
    const key = 'forprofile-throw-key';
    let rejected = null;
    try {
      await orchestrator.run('/proj', 'forProfile 抛错', 'hello', OWNER, {
        profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
        forProfile: async () => { throw new Error('forProfile boom'); },
      });
    } catch (e) { rejected = e; }
    check('forProfile 抛错原样上抛且未启动 attempt', rejected && rejected.message === 'forProfile boom' && calls.runs.length === 0,
      rejected && rejected.message);
    check('forProfile 抛错后 preflight token 释放', orchestrator.cancelPreflight([key]) === null);
    const after = await orchestrator.run('/proj', 'forProfile 后恢复', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
    });
    check('token 释放后同 key 可继续正常跑', after.ok && calls.runs.length === 1);
  }
  {
    const { orchestrator, calls } = baseOptions({
      results: () => { throw new Error('adapter sync boom'); },
    });
    const key = 'adapter-throw-key';
    let rejected = null;
    try {
      await orchestrator.run('/proj', 'adapter 抛错', 'hello', OWNER, {
        profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
      });
    } catch (e) { rejected = e; }
    check('adapter 同步抛错原样上抛', rejected && rejected.message === 'adapter sync boom');
    check('adapter 抛错后 preflight token 释放', orchestrator.cancelPreflight([key]) === null);
  }
  {
    const healthPromise = Promise.reject(new Error('health preflight boom'));
    const { orchestrator, calls } = baseOptions({ healthPromise });
    const key = 'health-reject-key';
    let rejected = null;
    try {
      await orchestrator.run('/proj', '健康预检拒绝', 'hello', OWNER, {
        profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
      });
    } catch (e) { rejected = e; }
    check('健康预检 Promise 拒绝原样上抛且未启动 attempt',
      rejected && rejected.message === 'health preflight boom' && calls.runs.length === 0,
      rejected && rejected.message);
    check('健康预检 Promise 拒绝后 token 释放', orchestrator.cancelPreflight([key]) === null);
  }
  {
    const { orchestrator, calls } = baseOptions({
      providerHealth: () => { throw new Error('health snapshot boom'); },
    });
    const key = 'health-snapshot-throw-key';
    let rejected = null;
    try {
      await orchestrator.run('/proj', '健康快照抛错', 'hello', OWNER, {
        profile: PROFILES['openai-sol'], runKey: key, allowFallback: false,
      });
    } catch (e) { rejected = e; }
    check('健康快照抛错原样上抛且未启动 attempt',
      rejected && rejected.message === 'health snapshot boom' && calls.runs.length === 0,
      rejected && rejected.message);
    check('健康快照抛错后 token 释放', orchestrator.cancelPreflight([key]) === null);
  }

  // ---- 观察回调/日志异常不影响任务 ----
  {
    const { orchestrator, calls } = baseOptions({ observeThrows: true, logThrows: true, results: [retryableAuth, okResult] });
    const result = await orchestrator.run('/proj', '回调异常', 'hello', OWNER, {
      profile: PROFILES['openai-sol'],
    });
    check('onAttempt/logLine 抛错不影响任务结果', result.ok && result.profile.id === 'deepseek-v4',
      JSON.stringify(result));
    check('观察回调仍被调用(异常被吞)', calls.observes.length === 2, String(calls.observes.length));
    check('日志仍被调用(异常被吞)', calls.logs.length >= 1, String(calls.logs.length));
  }

  // ---- 孤儿占位 fail-closed 语义保持 ----
  {
    const { orchestrator, calls } = baseOptions({ orphan: key => key === 'orphan-key' });
    const result = await orchestrator.run('/proj', '孤儿锁', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], runKey: 'orphan-key', taskKind: 'modify', allowFallback: false,
    });
    check('孤儿占位阻止同一 runKey 启动且不可重试',
      result.errorCode === 'orphan_pending' && !result.retryable && result.attemptedProfiles.join(',') === 'openai-sol' && calls.runs.length === 0,
      JSON.stringify(result));
  }

  // ---- 显式 cfg 参数优先于 readConfig ----
  {
    const { orchestrator, calls } = baseOptions({});
    const result = await orchestrator.run('/proj', '显式 cfg', 'hello', OWNER, {
      profile: PROFILES['openai-sol'], allowFallback: false, taskKind: 'chat',
    }, { feishuChatTimeoutMinutes: 45 });
    check('显式 cfg 参与 legacy 超时计算(45 分钟预算)',
      result.ok && calls.runs[0].opts.timeoutMs > 0 && calls.runs[0].opts.timeoutMs <= 45 * 60000,
      String(calls.runs[0] && calls.runs[0].opts.timeoutMs));
  }

  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(1); });
