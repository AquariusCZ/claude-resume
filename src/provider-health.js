'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { createAIRunner, classifyError } = require('./ai/runners');
const { profileById } = require('./ai/profiles');

function readRuntimeConfig() {
  const file = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume', 'config.json');
  try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { return {}; }
}

function providerResult(status, reason, ms, extra) {
  return Object.assign({ status, reason, ms: Math.max(0, Number(ms) || 0) }, extra || {});
}

function providerConfigFingerprint(cfg, provider) {
  const common = {
    aiProxy: String(cfg && cfg.aiProxy || '').trim(),
    aiNoProxy: String(cfg && cfg.aiNoProxy || '127.0.0.1,localhost,::1'),
  };
  const relevant = provider === 'openai'
    ? Object.assign(common, {
      openaiApiKey: String(cfg && cfg.openaiApiKey || ''),
      openaiBaseUrl: String(cfg && cfg.openaiBaseUrl || 'https://api.openai.com/v1').trim(),
      openaiReasoning: String(cfg && cfg.openaiReasoning || 'xhigh'),
    })
    : provider === 'deepseek'
      ? Object.assign(common, {
        deepseekApiKey: String(cfg && cfg.deepseekApiKey || ''),
        deepseekMillionContext: cfg && cfg.deepseekMillionContext !== false,
        deepseekEffort: String(cfg && cfg.deepseekEffort || ''),
      })
      : { provider: 'claude' };
  return crypto.createHash('sha256').update(JSON.stringify(relevant)).digest('hex').slice(0, 24);
}

async function probeProviders(options = {}) {
  const readConfig = options.readConfig || readRuntimeConfig;
  const cfg = readConfig() || {};
  const runner = options.runner || createAIRunner({
    readConfig: () => cfg,
    logLine: () => {},
    running: options.running,
    onChildStart: options.onChildStart,
    onChildEnd: options.onChildEnd,
  });
  const tempRoot = path.resolve(os.tmpdir()).toLowerCase() + path.sep;
  const pendingSettlements = [];
  const specs = [
    { provider: 'openai', key: 'openaiApiKey', profile: 'openai-sol' },
    { provider: 'deepseek', key: 'deepseekApiKey', profile: 'deepseek-v4' },
  ];
  if (options.includeClaude) specs.push({ provider: 'claude', profile: 'claude-default' });

  async function probe(spec) {
    const configFingerprint = providerConfigFingerprint(cfg, spec.provider);
    if (spec.key && !String(cfg[spec.key] || '').trim()) {
      return providerResult('unconfigured', 'unconfigured', 0, { configFingerprint });
    }
    const cwd = fs.mkdtempSync(path.join(os.tmpdir(), `ai-resume-health-${spec.provider}-`));
    const startedAt = Date.now();
    const totalTimeoutMs = typeof options.timeoutMs === 'number' && Number.isFinite(options.timeoutMs) && options.timeoutMs > 0
      ? Math.max(1, Math.floor(options.timeoutMs)) : 60000;
    const hasProxyFallback = spec.provider !== 'claude' && !!String(cfg.aiProxy || '').trim();
    const directTimeoutMs = hasProxyFallback ? Math.max(1, Math.floor(totalTimeoutMs * 0.75)) : totalTimeoutMs;
    const proxyTimeoutMs = hasProxyFallback ? Math.max(1, totalTimeoutMs - directTimeoutMs) : totalTimeoutMs;
    const deadline = startedAt + totalTimeoutMs;
    const terminationGraceMs = Math.max(0, Number(runner.terminationGraceMs) || 0);
    const attemptedRoutes = [];
    let childPending = false;
    const pendingRunKeys = [];
    const safeRemoveCwd = () => {
      try {
        const resolved = path.resolve(cwd).toLowerCase();
        if (resolved.startsWith(tempRoot) && path.basename(resolved).startsWith(`ai-resume-health-${spec.provider}-`)) {
          fs.rmSync(cwd, { recursive: true, force: true });
        }
      } catch (e) {}
    };
    const runAttempt = async (route, timeoutMs) => {
      attemptedRoutes.push(route);
      const runKey = `${cwd}:${route}`;
      const result = await runner.run(cwd, `health-${spec.provider}-${route}`, '只回答 OK，不要调用任何工具。', {
        profile: profileById(spec.profile), readOnly: true, noTools: true,
        disallowedTools: ['Task', 'Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit', 'WebFetch', 'WebSearch'],
        useContinue: false, skipPermissions: false, timeoutMs,
        ephemeral: spec.provider === 'openai',
        runKey, taskKind: 'provider-health', networkRoute: route,
      });
      if (result && result.childPending) {
        childPending = true;
        pendingRunKeys.push(runKey);
      }
      return result;
    };
    try {
      if (spec.provider === 'claude') {
        const route = 'direct';
        const result = await runAttempt(route, Math.max(1, totalTimeoutMs - terminationGraceMs));
        if (result && result.ok) return providerResult('available', 'ok', Date.now() - startedAt, { route, attemptedRoutes, configFingerprint });
        return providerResult('unavailable', result && result.errorCode || 'unknown', Date.now() - startedAt, { attemptedRoutes, childPending, configFingerprint });
      }

      const directBudgetMs = Math.min(directTimeoutMs, Math.max(1, deadline - Date.now() - terminationGraceMs));
      const direct = await runAttempt('direct', directBudgetMs);
      if (direct && direct.ok) {
        return providerResult('available', 'ok', Date.now() - startedAt, { route: 'direct', attemptedRoutes, configFingerprint });
      }
      const directReason = direct && direct.errorCode || 'unknown';
      if (directReason !== 'transient' || !hasProxyFallback || childPending) {
        return providerResult('unavailable', directReason, Date.now() - startedAt, { attemptedRoutes, childPending, configFingerprint });
      }

      const remainingMs = Math.min(proxyTimeoutMs, deadline - Date.now() - terminationGraceMs);
      if (remainingMs <= 0) {
        return providerResult('unavailable', directReason, Date.now() - startedAt, { attemptedRoutes, configFingerprint });
      }
      const proxied = await runAttempt('proxy', remainingMs);
      if (proxied && proxied.ok) {
        return providerResult('available', 'ok', Date.now() - startedAt, {
          route: 'proxy', attemptedRoutes, directReason, configFingerprint,
        });
      }
      const proxyReason = proxied && proxied.errorCode || 'unknown';
      return providerResult('unavailable', proxyReason === 'transient' ? 'proxy_unavailable' : proxyReason, Date.now() - startedAt, {
        attemptedRoutes, directReason, proxyReason, childPending, configFingerprint,
      });
    } catch (e) {
      const info = classifyError(e && e.message, false);
      return providerResult('unavailable', info.errorCode || 'unknown', Date.now() - startedAt, { attemptedRoutes, configFingerprint });
    } finally {
      if (!childPending) safeRemoveCwd();
      else if (typeof runner.waitForIdle === 'function') {
        const promise = Promise.all(pendingRunKeys.map(runKey => runner.waitForIdle(runKey))).then(() => {
          safeRemoveCwd();
          return { provider: spec.provider, configFingerprint };
        });
        pendingSettlements.push({ provider: spec.provider, configFingerprint, promise });
      }
    }
  }

  const entries = await Promise.all(specs.map(async spec => [spec.provider, await probe(spec)]));
  const output = { ok: true, probedAt: new Date().toISOString(), providers: Object.fromEntries(entries) };
  Object.defineProperty(output, 'pendingSettlements', { value: pendingSettlements, enumerable: false });
  return output;
}

if (require.main === module) {
  probeProviders()
    .then(result => process.stdout.write(JSON.stringify(result)))
    .catch(() => {
      process.stdout.write(JSON.stringify({ ok: false, error: 'probe_failed', providers: {} }));
      process.exitCode = 1;
    });
}

module.exports = { probeProviders, readRuntimeConfig, providerConfigFingerprint };
