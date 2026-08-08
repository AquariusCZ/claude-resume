'use strict';

// S1-F TaskOrchestrator —— 现役兼容编排层（不是目标 C# RunContract 的持久状态机）。
//
// 本层只拥有：
//   - 同步 runKey 预占/释放与 busy 判断（外部 running Map + 本层 reservation 集合）
//   - 现役 provider 候选/一次 attempt 链与 fallback（与 feishu-agent 历史 runForUser 语义逐字等价）
//   - OpenAI/DeepSeek 健康预检等待及可取消 token
//   - 活动 runner/preflight 取消决策
//   - legacy taskTimeoutMs 计算（query/chat 30 分钟、modify=0）
//   - terminal 结果原样交还
//
// 本层不拥有：飞书 SDK/消息/卡片/图片、权限与项目/session key 计算、配置持久化、进程登记
// 实现（running Map 与孤儿登记真身仍在 feishu-agent）、provider CLI/session 细节、目标
// queued/starting/running 持久状态、Start/Status API、重试或任何新的产品行为。
// D-001 的 cancel 侧在本层处理：running 中 orphan===true 的占位命中时返回 kind='orphan'，
// 绝不调用 agentAdapter.cancel，也不继续取消 preflight；三态分类/回收仍由 feishu-agent 持有。
// D-002 的 activity 分类由 ai/runners.js 持有；本层只消费其单调 sideEffectsStarted 标志，
// retryable 失败一旦已产生或无法排除副作用就不再 fallback。D-003 项目披露不属于本层。
//
// 兼容性红线：现役 query/chat 30 分钟 legacy deadline 与 modify=0 仅作为兼容实现保留，
// **不是目标 RunContract**（见 docs/adr/0002-run-lifecycle-contract.md 与 docs/RUN-CONTRACT.md）。
// 目标 chat/query/modify/resume/probe 全部不设客户端总时限，采用 Start/Status/Cancel；只有
// 结构化 HTTP 408/504 / gateway_timeout 才是 failed_provider 超时。本层不得新增静默超时、
// heartbeat 超时或任何额外总时限；无 deadline 时绝不注入计时器。

const REQUIRED_DEPS = [
  'agentAdapter', 'running', 'readConfig', 'getUserProfile', 'canUseOwnerOnlyProfile',
  'profileById', 'fallbackProfiles', 'defaultProfileId', 'ensureFreshProviderHealth',
  'providerHealth', 'providerReasonText', 'orphanBlocksRun', 'maxTimeoutMs', 'defaultAiTimeoutMs',
];

function createTaskOrchestrator(options) {
  const opts = options || {};
  for (const name of REQUIRED_DEPS) {
    if (opts[name] === undefined || opts[name] === null) {
      throw new Error(`createTaskOrchestrator: 缺少必需依赖 ${name}`);
    }
  }
  for (const name of ['readConfig', 'getUserProfile', 'canUseOwnerOnlyProfile', 'profileById',
    'fallbackProfiles', 'ensureFreshProviderHealth', 'providerHealth', 'providerReasonText',
    'orphanBlocksRun']) {
    if (typeof opts[name] !== 'function') {
      throw new Error(`createTaskOrchestrator: 依赖 ${name} 必须是函数`);
    }
  }
  if (typeof opts.agentAdapter.run !== 'function' || typeof opts.agentAdapter.cancel !== 'function') {
    throw new Error('createTaskOrchestrator: agentAdapter 必须提供 run/cancel 能力');
  }
  for (const method of ['get', 'has', 'set', 'delete']) {
    if (typeof opts.running[method] !== 'function') {
      throw new Error(`createTaskOrchestrator: running 必须是 Map 兼容容器（缺少 ${method}）`);
    }
  }

  const agentAdapter = opts.agentAdapter;
  const running = opts.running;
  const readConfig = opts.readConfig;
  const getUserProfile = opts.getUserProfile;
  const canUseOwnerOnlyProfile = opts.canUseOwnerOnlyProfile;
  const profileById = opts.profileById;
  const fallbackProfiles = opts.fallbackProfiles;
  const defaultProfileId = opts.defaultProfileId;
  const ensureFreshProviderHealth = opts.ensureFreshProviderHealth;
  const providerHealth = opts.providerHealth;
  const providerReasonText = opts.providerReasonText;
  const orphanBlocksRun = opts.orphanBlocksRun;
  const maxTimeoutMs = opts.maxTimeoutMs;
  const defaultAiTimeoutMs = opts.defaultAiTimeoutMs;
  const logLine = typeof opts.logLine === 'function' ? opts.logLine : null;
  const onAttempt = typeof opts.onAttempt === 'function' ? opts.onAttempt : null;
  const isShuttingDown = typeof opts.isShuttingDown === 'function' ? opts.isShuttingDown : (() => false);

  // 观察/日志异常绝不影响任务本身（可选回调）。
  const safeLog = text => {
    if (!logLine) return;
    try { logLine(text); } catch (_) {}
  };
  const safeObserve = attempt => {
    if (!onAttempt) return;
    try { onAttempt(attempt); } catch (_) {}
  };

  // ---- runKey 同步预占/释放与 busy 判断 ----
  // 预占与 running 检查在同一个同步操作内完成，关闭 check->spawn 竞态；空 key 一律拒绝。
  // 预占只代表“本任务已接纳”，不替代 running Map（真身不变，仍由 agentAdapter/runner 维护）。
  const reservations = new Map();

  function tryReserve(runKey) {
    const key = String(runKey || '').toLowerCase();
    if (!key) return false;
    if (isShuttingDown() || running.has(key) || reservations.has(key)) return false;
    reservations.set(key, { cancelled: false });
    return true;
  }

  function isBusy(runKey) {
    const key = String(runKey || '').toLowerCase();
    return !!key && (running.has(key) || reservations.has(key));
  }

  function release(runKey) {
    const key = String(runKey || '').toLowerCase();
    if (key) reservations.delete(key);
  }

  function reservationCancelled(runKey) {
    const token = reservations.get(String(runKey || '').toLowerCase());
    return !!(token && token.cancelled);
  }

  // ---- 健康预检等待与可取消 token（现役语义原样迁移） ----
  const providerPreflightRuns = new Map();

  function cancelPreflight(keys) {
    for (const rawKey of keys || []) {
      const key = String(rawKey || '').toLowerCase();
      const token = providerPreflightRuns.get(key);
      if (!token || token.cancelled) continue;
      token.cancelled = true;
      token.resolveCancel();
      return key;
    }
    return null;
  }

  async function waitForProviderHealthForRun(reason, provider, runKey, deadline) {
    const key = String(runKey || '').toLowerCase();
    let resolveCancel;
    const token = { cancelled: false, resolveCancel: () => resolveCancel && resolveCancel() };
    const cancelled = new Promise(resolve => { resolveCancel = resolve; });
    providerPreflightRuns.set(key, token);
    let deadlineTimer = null;
    try {
      const health = ensureFreshProviderHealth(reason, provider).then(state => ({ state }));
      const races = [health, cancelled.then(() => ({ cancelled: true }))];
      if (deadline) {
        const remainingMs = deadline - Date.now();
        if (remainingMs <= 0) return { timedOut: true, lease: { key, token } };
        races.push(new Promise(resolve => { deadlineTimer = setTimeout(() => resolve({ timedOut: true }), remainingMs); }));
      }
      const outcome = await Promise.race(races);
      return Object.assign(outcome, { lease: { key, token } });
    } catch (e) {
      releaseProviderPreflight({ key, token });
      throw e;
    } finally {
      clearTimeout(deadlineTimer);
    }
  }

  function releaseProviderPreflight(lease) {
    if (lease && providerPreflightRuns.get(lease.key) === lease.token) providerPreflightRuns.delete(lease.key);
  }

  // ---- legacy 总时限计算（仅兼容现役行为，不是目标 RunContract） ----
  function taskTimeoutMs(taskKind, cfg) {
    if (taskKind === 'modify') return 0;
    const keys = { query: 'feishuQueryTimeoutMinutes', chat: 'feishuChatTimeoutMinutes' };
    const key = keys[taskKind];
    if (!key) return null;
    const configured = cfg && cfg[key];
    const minutes = typeof configured === 'number' && Number.isFinite(configured) && configured > 0 ? configured : 30;
    return Math.min(maxTimeoutMs, Math.max(1, Math.floor(minutes * 60000)));
  }

  // ---- 取消决策 ----
  // 按 key 顺序先找活动 runner（running Map）。命中 orphan===true 占位时返回 kind='orphan'，
  // 绝不调用 agentAdapter.cancel 也不继续取消 preflight（身份未核验，禁止按 PID 终止）；命中
  // 正常活动 child 只调用一次 agentAdapter.cancel；没有活动 child 时，先持久标记“已接纳但
  // 尚未 spawn”的 reservation 为 cancelled，并同时唤醒可能存在的健康预检；最后才处理
  // 纯 preflight。返回小型结构，由飞书层选择中文提示。
  function cancel(runKeys) {
    const keys = (runKeys || []).map(raw => String(raw || '').toLowerCase());
    for (const key of keys) {
      if (running.has(key)) {
        const child = running.get(key);
        if (child && child.orphan === true) return { kind: 'orphan', key };
        agentAdapter.cancel(key);
        return { kind: 'child', key };
      }
    }
    for (const key of keys) {
      const token = reservations.get(key);
      if (!token) continue;
      token.cancelled = true;
      cancelPreflight([key]);
      return { kind: 'reservation', key };
    }
    const preflightKey = cancelPreflight(keys);
    return preflightKey ? { kind: 'preflight', key: preflightKey } : { kind: 'none', key: null };
  }

  // ---- 现役 runForUser 语义原样迁移 ----
  async function run(cwd, label, prompt, openId, opts, cfg) {
    const options = opts || {};
    const c = cfg === undefined ? readConfig() : cfg;
    const owner = canUseOwnerOnlyProfile(openId, c);   // owner-only 后备模型只给显式 owner
    // 运行时防御：非显式 owner 绝不启动 owner-only profile——options.profile 或用户存储
    // 返回 ownerOnly 时，primary 一律降级到默认 profile。
    let primary = options.profile || getUserProfile(openId, c);
    if (primary && primary.ownerOnly && !owner) primary = profileById(defaultProfileId);
    const candidates = [primary];
    const taskTimeout = taskTimeoutMs(options.taskKind, c);
    const configuredTimeoutMs = Object.prototype.hasOwnProperty.call(options, 'timeoutMs')
      ? options.timeoutMs
      : (taskTimeout !== null ? taskTimeout : defaultAiTimeoutMs);
    const totalTimeoutMs = typeof configuredTimeoutMs === 'number' && Number.isFinite(configuredTimeoutMs) && configuredTimeoutMs >= 0
      ? Math.min(maxTimeoutMs, Math.floor(configuredTimeoutMs))
      : null;
    const deadline = totalTimeoutMs > 0 ? Date.now() + totalTimeoutMs : null;
    const preflightRunKey = String(options.runKey || cwd).toLowerCase();
    const terminalResult = (profile, errorCode, text, retryable, attemptedProfiles) => ({
      ok: false, limited: errorCode === 'rate_limit', retryable: !!retryable, errorCode, text,
      ms: 0, sessionId: null, usage: null, cost: null, sideEffectsStarted: false, childPending: false,
      profile, provider: profile.provider, model: profile.model, networkRoute: null, attemptedProfiles,
    });
    if (options.allowFallback !== false) {
      for (const p of fallbackProfiles(c, primary.id)) {
        if ((!p.ownerOnly || owner) && !candidates.some(x => x.id === p.id)) candidates.push(p);
      }
    }
    if (isShuttingDown()) {
      return terminalResult(primary, 'cancelled', '服务正在关闭，本次任务未启动。', false, []);
    }
    if (reservationCancelled(preflightRunKey)) {
      return terminalResult(primary, 'cancelled', '已按用户请求停止。', false, []);
    }
    const failures = [];
    for (let i = 0; i < candidates.length; i++) {
      const profile = candidates[i];
      const attemptedProfiles = candidates.slice(0, i + 1).map(x => x.id);
      if (isShuttingDown() || reservationCancelled(preflightRunKey)) {
        return terminalResult(profile, 'cancelled', isShuttingDown() ? '服务正在关闭，本次任务未启动。' : '已按用户请求停止。', false, attemptedProfiles);
      }
      if (deadline && Date.now() >= deadline) {
        return terminalResult(profile, 'transient', `${profile.fullLabel} 执行前等待已超过本次总时限。`, true, attemptedProfiles);
      }
      let preflightLease = null;
      const releasePreflight = () => {
        releaseProviderPreflight(preflightLease);
        preflightLease = null;
      };
      try {
        let selectedNetworkRoute = options.networkRoute;
        if (!selectedNetworkRoute && (profile.provider === 'openai' || profile.provider === 'deepseek')) {
          const preflight = await waitForProviderHealthForRun(`执行 ${label}`, profile.provider, preflightRunKey, deadline);
          preflightLease = preflight.lease;
          if (preflight.cancelled || reservationCancelled(preflightRunKey) || isShuttingDown()) {
            releasePreflight();
            return terminalResult(profile, 'cancelled', isShuttingDown() ? '服务正在关闭，本次任务未启动。' : '已按用户请求停止。', false, attemptedProfiles);
          }
          if (preflight.timedOut) {
            releasePreflight();
            return terminalResult(profile, 'transient', `${profile.fullLabel} 线路检测超过本次总时限，未启动正式任务。`, true, attemptedProfiles);
          }
          const health = providerHealth(profile.provider);
          if (health.status === 'available' && (health.route === 'direct' || health.route === 'proxy')) {
            selectedNetworkRoute = health.route;
          } else {
            const healthErrorCode = health.reason === 'proxy_unavailable' ? 'transient' : health.reason;
            const result = {
              ok: false, limited: healthErrorCode === 'rate_limit',
              retryable: ['auth', 'rate_limit', 'model_unavailable', 'command_missing', 'transient'].includes(healthErrorCode),
              errorCode: healthErrorCode || 'unknown',
              text: `${profile.fullLabel} 当前不可用（${providerReasonText(health)}）。`,
              ms: Math.max(0, Number(health.ms) || 0), sessionId: null, usage: null, cost: null,
              sideEffectsStarted: false, profile, provider: profile.provider, model: profile.model,
              networkRoute: null, attemptedProfiles,
            };
            releasePreflight();
            failures.push(result);
            if (!result.retryable || i === candidates.length - 1 || (deadline && Date.now() >= deadline)) return result;
            continue;
          }
        }
        let dynamic;
        try { dynamic = options.forProfile ? await options.forProfile(profile, i, failures) : {}; }
        catch (e) { releasePreflight(); throw e; }
        if (isShuttingDown() || reservationCancelled(preflightRunKey) || (preflightLease && preflightLease.token.cancelled)) {
          releasePreflight();
          return terminalResult(profile, 'cancelled', isShuttingDown() ? '服务正在关闭，本次任务未启动。' : '已按用户请求停止。', false, attemptedProfiles);
        }
        if (deadline && Date.now() >= deadline) {
          releasePreflight();
          return terminalResult(profile, 'transient', `${profile.fullLabel} 执行前等待已超过本次总时限。`, true, attemptedProfiles);
        }
        const runCwd = dynamic.cwd || cwd;
        let attemptPrompt = dynamic.prompt || prompt;
        if (i > 0) {
          const prev = failures[failures.length - 1];
          attemptPrompt = `[自动切换说明] 原 AI「${prev.profile.fullLabel}」因 ${prev.errorCode || '运行错误'} 未完成。本轮已切换到「${profile.fullLabel}」。请根据下面的原始请求从头完成,不要声称看过原 AI 未返回的内部过程。\n\n${attemptPrompt}`;
          safeLog(`自动切换 [${label}] ${prev.profile.fullLabel} -> ${profile.fullLabel} (${prev.errorCode})`);
        }
        const runOptions = Object.assign({}, options, dynamic, {
          profile,
          runKey: dynamic.runKey || options.runKey || runCwd,
          networkRoute: dynamic.networkRoute || selectedNetworkRoute,
        });
        if (orphanBlocksRun(runOptions.runKey, options.taskKind)) {
          releasePreflight();
          return {
            ok: false, limited: false, retryable: false, errorCode: 'orphan_pending',
            text: 'AI 子进程登记或上次异常退出的进程尚未安全恢复，本次运行已拒绝；请等待后台确认，登记损坏时需人工检查。',
            ms: 0, sessionId: null, usage: null, cost: null, sideEffectsStarted: false,
            profile, provider: profile.provider, model: profile.model, attemptedProfiles: [profile.id],
          };
        }
        if (totalTimeoutMs !== null) {
          const remainingMs = deadline ? deadline - Date.now() : 0;
          if (deadline && remainingMs <= 0) {
            releasePreflight();
            return terminalResult(profile, 'transient', `${profile.fullLabel} 执行前等待已超过本次总时限。`, true, attemptedProfiles);
          }
          runOptions.timeoutMs = deadline ? remainingMs : 0;
        }
        if (isShuttingDown() || reservationCancelled(preflightRunKey) || (preflightLease && preflightLease.token.cancelled)) {
          releasePreflight();
          return terminalResult(profile, 'cancelled', isShuttingDown() ? '服务正在关闭，本次任务未启动。' : '已按用户请求停止。', false, attemptedProfiles);
        }
        // 供调用方（现役 feishu-agent 的 TEST_MODE lastRun）观察 attempt 边界；异常不影响任务。
        safeObserve({ cwd: runCwd, label, prompt: attemptPrompt, openId, options: runOptions });
        let resultPromise;
        try { resultPromise = agentAdapter.run(runCwd, label, attemptPrompt, runOptions); }
        finally { releasePreflight(); }
        const result = await resultPromise;
        result.attemptedProfiles = candidates.slice(0, i + 1).map(x => x.id);
        if (result.ok) {
          if (i > 0) result.fallbackFrom = failures[0].profile;
          return result;
        }
        failures.push(result);
        if (!result.retryable || result.sideEffectsStarted || result.childPending || i === candidates.length - 1) return result;
        if (deadline && Date.now() >= deadline) return result;
      } finally {
        releasePreflight();
      }
    }
    return failures[failures.length - 1];
  }

  return {
    run,
    tryReserve,
    isBusy,
    release,
    cancel,
    cancelPreflight,
    taskTimeoutMs,
  };
}

module.exports = { createTaskOrchestrator };
