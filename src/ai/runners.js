'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { spawn } = require('child_process');

const MAX_TIMEOUT_MS = 0x7fffffff;
const DEFAULT_AI_TIMEOUT_MS = 30 * 60000;

function findClaudeCmd() {
  const cands = [
    path.join(process.env.APPDATA || '', 'npm', 'claude.cmd'),
    path.join(process.env.ProgramFiles || '', 'nodejs', 'claude.cmd'),
  ];
  for (const c of cands) { try { if (c && fs.existsSync(c)) return c; } catch (e) {} }
  return 'claude.cmd';
}

function findCodexCmd() {
  const cands = [
    process.env.CODEX_CLI_PATH || '',
    path.join(process.env.LOCALAPPDATA || '', 'Programs', 'Codex', 'codex.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'OpenAI', 'Codex', 'bin', 'codex.exe'),
    path.join(process.env.APPDATA || '', 'npm', 'codex.cmd'),
  ];
  for (const c of cands) { try { if (c && fs.existsSync(c)) return c; } catch (e) {} }
  // Codex Desktop also installs versioned runtimes under bin/<build>/codex.exe. Startup-launched
  // agents may not inherit the desktop app's temporary PATH, so resolve the newest local build.
  try {
    const root = path.join(process.env.LOCALAPPDATA || '', 'OpenAI', 'Codex', 'bin');
    const builds = fs.readdirSync(root, { withFileTypes: true })
      .filter(x => x.isDirectory())
      .map(x => path.join(root, x.name, 'codex.exe'))
      .filter(x => fs.existsSync(x))
      .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
    if (builds.length) return builds[0];
  } catch (e) {}
  return process.platform === 'win32' ? 'codex.exe' : 'codex';
}

function killTree(child) {
  return new Promise(resolve => {
    try {
      if (process.platform === 'win32' && child && child.pid) {
        const killer = spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
        let done = false;
        const finish = ok => { if (!done) { done = true; clearTimeout(guard); resolve(!!ok); } };
        const guard = setTimeout(() => { try { killer.kill(); } catch (e) {} finish(false); }, 10000);
        if (guard.unref) guard.unref();
        killer.once('error', () => finish(false));
        killer.once('close', code => finish(code === 0));
      } else if (child) {
        const ok = child.kill();
        resolve(ok !== false);
      } else resolve(false);
    } catch (e) {
      try { resolve(child && child.kill() !== false); } catch (e2) { resolve(false); }
    }
  });
}

function classifyError(text, limited) {
  const s = String(text || '').toLowerCase();
  if (limited || /rate.?limit|usage limit|quota|too many requests|429|额度|限流/.test(s)) return { errorCode: 'rate_limit', retryable: true, limited: true };
  if (/unauthori[sz]ed|invalid api key|authentication|not logged in|login required|401|403|api key.*missing|密钥.*未配置|未登录/.test(s)) return { errorCode: 'auth', retryable: true, limited: false };
  if (/model.*(not found|unavailable|unsupported)|unknown model|404.*model|模型.*不可用/.test(s)) return { errorCode: 'model_unavailable', retryable: true, limited: false };
  if (/unexpected argument|unrecognized option|invalid value.*(?:config|argument)|failed to parse.*config/.test(s)) return { errorCode: 'cli_config', retryable: false, limited: false };
  if (/not inside a trusted directory|not a trusted directory|skip-git-repo-check was not specified/.test(s)) return { errorCode: 'workspace_untrusted', retryable: false, limited: false };
  if (/session (?:id )?.*(?:not found|does not exist)|thread .*(?:not found|does not exist)|no rollout found/.test(s)) return { errorCode: 'session_missing', retryable: false, limited: false };
  if (/enoent|not recognized|command not found|系统找不到指定的文件|启动.*失败/.test(s)) return { errorCode: 'command_missing', retryable: true, limited: false };
  if (/timed? ?out|timeout|econn|socket|tls|dns|network|connection|502|503|504|server overloaded|temporar/.test(s)) return { errorCode: 'transient', retryable: true, limited: false };
  return { errorCode: 'unknown', retryable: false, limited: false };
}

function friendlyError(profile, info) {
  const name = profile && profile.fullLabel || 'AI';
  if (info.errorCode === 'auth') return `${name} 认证失败或凭据未配置。请在本机更新对应登录/API Key。`;
  if (info.errorCode === 'rate_limit') return `${name} 当前限流或额度不足。`;
  if (info.errorCode === 'model_unavailable') return `${name} 当前模型不可用。`;
  if (info.errorCode === 'workspace_untrusted') return `${name} 拒绝了当前工作目录的 Git 信任检查。`;
  if (info.errorCode === 'session_missing') return `${name} 找不到要续接的原生会话,请清空该会话后重试。`;
  if (info.errorCode === 'cli_config') return `${name} 的本地命令参数或配置不兼容。`;
  if (info.errorCode === 'command_missing') return `${name} 的本地命令未安装或无法启动。`;
  if (info.errorCode === 'transient') return `${name} 网络或服务暂时不可用,请稍后重试。`;
  return `${name} 执行失败,请查看本机运行日志。`;
}

function normalizeResult(base, profile) {
  const r = Object.assign({
    ok: false, limited: false, retryable: false, errorCode: 'unknown', text: '', ms: 0,
    sessionId: null, usage: null, cost: null, sideEffectsStarted: false, childPending: false,
  }, base || {});
  r.profile = profile;
  r.provider = profile.provider;
  r.model = profile.model;
  return r;
}

// =====================================================================
// D-002:provider stdout 活动分类(fail-closed)。
// stream-json/JSONL 中无法可信分类的 activity/content/item 一律视为已产生
// 副作用,retryable 失败时禁止 fallback 重放;已知只读活动不得误标。顶层非
// activity 事件(thread/result/system/error 等)返回 null,不直接判副作用。
// 这些是纯函数:不读配置/进程/日志,也不抛异常。
// =====================================================================
const CLAUDE_READ_ONLY_TOOLS = new Set(['Read', 'Glob', 'Grep', 'WebSearch', 'WebFetch']);
const CLAUDE_MUTATING_TOOLS = new Set(['Bash', 'Write', 'Edit', 'NotebookEdit']);
const CLAUDE_READ_ONLY_PARTS = new Set(['text', 'thinking', 'redacted_thinking']);
// agent_message/reasoning/web_search 是任务枚举的只读 item;user_message 没有现役
// JSONL 录制/冻结契约证据,按 unknown fail-closed,不得猜测扩大只读白名单。
const CODEX_READ_ONLY_ITEMS = new Set(['agent_message', 'reasoning', 'web_search']);
const CODEX_MUTATING_ITEMS = new Set(['file_change', 'command_execution', 'mcp_tool_call']);

// 返回 'side-effect' | 'read-only' | 'unknown'(unknown 由调用方转 fail-closed)。
function classifyClaudeContentPart(part) {
  if (!part || typeof part !== 'object' || typeof part.type !== 'string') return 'unknown';
  if (CLAUDE_READ_ONLY_PARTS.has(part.type)) {
    // 文本/思考类内容必须有对应字符串字段才可信为只读,否则视为 malformed。
    if (part.type === 'text') return typeof part.text === 'string' ? 'read-only' : 'unknown';
    if (part.type === 'thinking') return typeof part.thinking === 'string' ? 'read-only' : 'unknown';
    // redacted_thinking 合法内容块使用 data 字符串字段承载内容,不是同名 redacted_thinking 字段。
    return typeof part.data === 'string' ? 'read-only' : 'unknown';
  }
  if (part.type !== 'tool_use') return 'unknown'; // 未知 content part fail-closed
  if (typeof part.name !== 'string' || !part.name.trim()) return 'unknown'; // tool_use 缺合法 name fail-closed
  if (CLAUDE_MUTATING_TOOLS.has(part.name)) return 'side-effect';
  if (CLAUDE_READ_ONLY_TOOLS.has(part.name)) return 'read-only';
  return 'unknown'; // 未知 tool name fail-closed
}

// 返回 'side-effect' | 'read-only' | null | 'unknown'。assistant content 非数组 fail-closed。
function classifyClaudeStreamLine(json) {
  if (!json || typeof json !== 'object') return 'unknown';
  if (json.type !== 'assistant') return null;
  const content = json.message && json.message.content;
  if (!Array.isArray(content)) return 'unknown';
  for (const part of content) {
    const cls = classifyClaudeContentPart(part);
    if (cls === 'side-effect' || cls === 'unknown') return cls;
  }
  return 'read-only';
}

// 返回 'side-effect' | 'read-only' | null | 'unknown'。
function classifyCodexStreamLine(json) {
  if (!json || typeof json !== 'object') return 'unknown';
  if (json.type !== 'item.started' && json.type !== 'item.completed') return null;
  const item = json.item;
  if (!item || typeof item !== 'object' || typeof item.type !== 'string' || !item.type.trim()) return 'unknown';
  if (CODEX_MUTATING_ITEMS.has(item.type)) return 'side-effect';
  if (CODEX_READ_ONLY_ITEMS.has(item.type)) return 'read-only';
  return 'unknown'; // 未知 item.type fail-closed
}

const PROXY_ENV_KEYS = new Set(['http_proxy', 'https_proxy', 'all_proxy', 'no_proxy']);

function configuredProxy(cfg) {
  return String(cfg && cfg.aiProxy || '').trim();
}

function resolveNetworkRoute(cfg, requestedRoute) {
  const requested = String(requestedRoute || '').trim().toLowerCase();
  if (requested === 'proxy') return 'proxy';
  return 'direct';
}

function clearProxyEnv(env) {
  for (const key of Object.keys(env || {})) {
    if (PROXY_ENV_KEYS.has(key.toLowerCase())) delete env[key];
  }
  return env;
}

function childEnv(cfg, requestedRoute) {
  const env = Object.assign({}, process.env);
  env.AI_RESUME_INTERNAL_RUN = '1';
  const route = resolveNetworkRoute(cfg, requestedRoute);
  clearProxyEnv(env);
  const proxy = configuredProxy(cfg);
  if (route === 'proxy' && proxy) {
    env.http_proxy = proxy;
    env.https_proxy = proxy;
    env.no_proxy = String(cfg.aiNoProxy || '127.0.0.1,localhost,::1');
    env.HTTP_PROXY = proxy;
    env.HTTPS_PROXY = proxy;
    env.NO_PROXY = env.no_proxy;
  }
  return env;
}

function codexEnv(cfg, requestedRoute) {
  const env = childEnv(cfg, requestedRoute);
  const key = String(cfg && cfg.openaiApiKey || process.env.CLAUDE_RESUME_OPENAI_API_KEY || process.env.OPENAI_API_KEY || '').trim();
  if (key) env.CLAUDE_RESUME_OPENAI_API_KEY = key;
  return env;
}

function codexProviderArgs(cfg, profile) {
  if (!profile || profile.provider !== 'openai') return [];
  const providerId = 'claude_resume_openai';
  const baseUrl = String(cfg && cfg.openaiBaseUrl || 'https://api.openai.com/v1').trim().replace(/\/+$/, '');
  return [
    '-c', `model_provider="${providerId}"`,
    '-c', `model_providers.${providerId}.name="OpenAI via AI Resume"`,
    '-c', `model_providers.${providerId}.base_url=${JSON.stringify(baseUrl)}`,
    '-c', `model_providers.${providerId}.wire_api="responses"`,
    '-c', `model_providers.${providerId}.env_key="CLAUDE_RESUME_OPENAI_API_KEY"`,
    '-c', `model_providers.${providerId}.http_headers={ "x-openai-actor-authorization" = "local-image-extension" }`,
  ];
}

function codexToolArgs(opts) {
  const a = [];
  if (opts.noTools) {
    a.push('--ignore-user-config', '--ignore-rules', '--disable', 'shell_tool', '--disable', 'apps', '--disable', 'multi_agent');
    a.push('-c', 'tools.view_image=false', '-c', 'web_search="disabled"', '-c', 'memories.use_memories=false');
    a.push('-c', 'sandbox_mode="read-only"', '-c', 'approval_policy="never"');
  } else if (opts.readOnly) {
    a.push('-c', 'sandbox_mode="read-only"', '-c', 'approval_policy="never"', '-c', 'web_search="disabled"');
  } else if (opts.skipPermissions !== false) {
    a.push('--dangerously-bypass-approvals-and-sandbox');
  } else {
    a.push('-c', 'sandbox_mode="workspace-write"', '-c', 'approval_policy="never"');
  }
  return a;
}

function buildCodexArgs(cwd, opts, profile, cfg) {
  const common = ['--json', '-m', profile.model];
  common.push(...codexProviderArgs(cfg, profile));
  const reasoning = String(cfg.openaiReasoning || profile.reasoning || '').trim();
  if (reasoning) common.push('-c', `model_reasoning_effort="${reasoning}"`);
  common.push(...codexToolArgs(opts));
  // Both new and resumed runs execute from isolated non-Git scratch directories. Keep this
  // invariant in one shared argument list so the two modes cannot drift again.
  common.push('--skip-git-repo-check');

  if (opts.sessionId) return ['exec', 'resume', ...common, opts.sessionId, '-'];
  const args = ['exec', ...common, '--color', 'never', '-C', cwd];
  if (opts.ephemeral) args.push('--ephemeral');
  if (opts.addDir && !opts.noTools) args.push('--add-dir', opts.addDir);
  args.push('-');
  return args;
}

function normalizeTimeoutMs(value, fallbackMs) {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return fallbackMs;
  if (value === 0) return 0;
  return Math.min(MAX_TIMEOUT_MS, Math.max(1, Math.floor(value)));
}

function resolveTimeoutMs(opts, cfg) {
  const fallbackMs = DEFAULT_AI_TIMEOUT_MS;
  return opts && Object.prototype.hasOwnProperty.call(opts, 'timeoutMs')
    ? normalizeTimeoutMs(opts.timeoutMs, fallbackMs)
    : fallbackMs;
}

function createAIRunner(options) {
  const readConfig = options.readConfig;
  const logLine = options.logLine || (() => {});
  const running = options.running || new Map();
  const testMode = !!options.testMode;
  const claudeCmd = options.claudeCmd || findClaudeCmd();
  const codexCmd = options.codexCmd || findCodexCmd();
  const spawnProcess = options.spawnProcess || spawn;
  const onChildStart = options.onChildStart || (() => {});
  const onChildEnd = options.onChildEnd || (() => {});
  const terminationGraceMs = typeof options.terminationGraceMs === 'number' && Number.isFinite(options.terminationGraceMs)
    ? Math.max(0, Math.floor(options.terminationGraceMs)) : 5000;
  const cancelledChildren = new WeakSet();
  const terminationHandlers = new WeakMap();
  const idleWaiters = new Map();

  function resolveIdleWaiters(key) {
    const waiters = idleWaiters.get(key);
    if (!waiters) return;
    idleWaiters.delete(key);
    for (const resolve of waiters) resolve();
  }

  function registerChild(key, child, profile, opts, cwd) {
    running.set(key, child);
    let accepted = true;
    try {
      accepted = onChildStart(child, {
        runKey: key, taskKind: String(opts && opts.taskKind || ''), cwd: String(cwd || ''),
        provider: profile.provider, profileId: profile.id,
      }) !== false;
    } catch (e) { accepted = false; }
    return accepted;
  }

  function unregisterChild(key, child, profile) {
    if (running.get(key) === child) {
      running.delete(key);
      resolveIdleWaiters(key);
    }
    try { onChildEnd(child, { runKey: key, provider: profile.provider, profileId: profile.id }); } catch (e) {}
  }

  function waitForIdle(runKey) {
    const key = String(runKey || '').toLowerCase();
    if (!running.has(key)) return Promise.resolve();
    return new Promise(resolve => {
      const waiters = idleWaiters.get(key) || new Set();
      waiters.add(resolve);
      idleWaiters.set(key, waiters);
    });
  }

  function rejectUnregisteredChild(key, child, profile, resolve) {
    let ended = false, settled = false, timer = null;
    const finish = childPending => {
      if (settled) return;
      settled = true;
      resolve(normalizeResult({
        text: '无法安全登记 AI 子进程,已中止本次运行。请检查运行目录权限或磁盘空间。',
        errorCode: 'registry_unavailable', retryable: false, childPending,
      }, profile));
    };
    const cleanup = () => {
      if (ended) return;
      ended = true;
      clearTimeout(timer);
      unregisterChild(key, child, profile);
      finish(false);
    };
    try { child.once('close', cleanup); child.once('error', cleanup); } catch (e) {}
    killTree(child).then(ok => { if (!ok) logLine(`登记失败后的进程树终止未确认 pid=${child.pid || '?'} provider=${profile.provider}`); });
    if (!settled) timer = setTimeout(() => finish(true), terminationGraceMs);
  }

  function cancel(runKey) {
    const key = String(runKey || '').toLowerCase();
    const child = running.get(key);
    if (!child) return false;
    cancelledChildren.add(child);
    const terminate = terminationHandlers.get(child);
    if (terminate) terminate('cancelled');
    else killTree(child);
    return true;
  }

  function testStub(profile, opts) {
    if (!testMode || (!process.env.FEISHU_TEST_NO_AI && !process.env.FEISHU_TEST_NO_CLAUDE)) return null;
    const delayed = result => {
      const delay = Math.max(0, parseInt(process.env.FEISHU_TEST_AI_DELAY_MS, 10) || 0);
      if (!delay) return result;
      const timeoutMs = normalizeTimeoutMs(opts.timeoutMs, 0);
      if (timeoutMs > 0 && delay > timeoutMs) {
        return new Promise(resolve => setTimeout(() => resolve(normalizeResult({
          text: `执行超时(> ${Math.max(1, Math.round(timeoutMs / 60000))} 分钟),已终止。`,
          ms: timeoutMs, sessionId: opts.sessionId || null,
          errorCode: 'transient', retryable: true,
        }, profile)), timeoutMs));
      }
      return new Promise(resolve => setTimeout(() => resolve(result), delay));
    };
    if (String(process.env.FEISHU_TEST_AI_FAIL_PROFILE || '') === profile.id) {
      const errorCode = String(process.env.FEISHU_TEST_AI_FAIL_CODE || 'transient');
      return delayed(normalizeResult({
        ok: false, text: '(测试桩:模拟失败)', errorCode,
        retryable: errorCode !== 'unknown' && errorCode !== 'cancelled', limited: errorCode === 'rate_limit',
        sideEffectsStarted: process.env.FEISHU_TEST_AI_FAIL_SIDE_EFFECTS === '1', ms: 1,
      }, profile));
    }
    const result = normalizeResult({
      ok: true, text: '(测试桩:未真正执行)', ms: 1,
      sessionId: opts.sessionId || crypto.randomUUID(),
    }, profile);
    return delayed(result);
  }

  function runClaude(cwd, label, prompt, opts, profile) {
    const stub = testStub(profile, opts); if (stub) return Promise.resolve(stub);
    const cfg = readConfig();
    if (profile.provider === 'deepseek') {
      const key = String(cfg.deepseekApiKey || process.env.DEEPSEEK_API_KEY || '');
      if (!key) return Promise.resolve(normalizeResult({ text: 'DeepSeek API Key 未配置。请在 AI 配置中填写 deepseekApiKey,或设置 DEEPSEEK_API_KEY。', errorCode: 'auth', retryable: true }, profile));
    }
    return new Promise((resolve) => {
      try { fs.mkdirSync(cwd, { recursive: true }); } catch (e) {}
      const args = ['/d', '/s', '/c', claudeCmd];
      if (opts.sessionId) args.push(opts.sessionExists ? '--resume' : '--session-id', opts.sessionId);
      else if (opts.useContinue !== false) args.push('--continue');
      if (opts.addDir) args.push('--add-dir', opts.addDir);
      if (Array.isArray(opts.disallowedTools) && opts.disallowedTools.length) args.push('--disallowedTools', ...opts.disallowedTools);
      args.push('-p', '--output-format', 'stream-json', '--verbose');
      if (profile.provider === 'claude' && profile.model) args.push('--model', profile.model);
      if (opts.readOnly) args.push('--permission-mode', 'plan');
      else if (opts.skipPermissions !== false && cfg.skipPermissions !== false) args.push('--dangerously-skip-permissions');

      const env = childEnv(cfg, opts.networkRoute);
      if (profile.provider === 'deepseek') {
        const selected = profile.model + (cfg.deepseekMillionContext === false ? '' : '[1m]');
        const flash = 'deepseek-v4-flash' + (cfg.deepseekMillionContext === false ? '' : '[1m]');
        env.ANTHROPIC_BASE_URL = 'https://api.deepseek.com/anthropic';
        env.ANTHROPIC_AUTH_TOKEN = String(cfg.deepseekApiKey || process.env.DEEPSEEK_API_KEY || '');
        env.ANTHROPIC_MODEL = selected;
        env.ANTHROPIC_DEFAULT_OPUS_MODEL = selected;
        env.ANTHROPIC_DEFAULT_SONNET_MODEL = selected;
        env.ANTHROPIC_DEFAULT_HAIKU_MODEL = flash;
        env.CLAUDE_CODE_SUBAGENT_MODEL = flash;
        env.CLAUDE_CODE_EFFORT_LEVEL = String(cfg.deepseekEffort || (profile.id === 'deepseek-v4-pro' ? 'max' : 'high'));
      }

      const timeoutMs = resolveTimeoutMs(opts, cfg);
      const key = String(opts.runKey || cwd).toLowerCase();
      let child;
      try { child = spawnProcess(process.env.ComSpec || 'cmd.exe', args, { cwd, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); }
      catch (e) {
        const c = classifyError(e.message, false);
        resolve(normalizeResult({ text: '启动 ' + profile.fullLabel + ' 失败: ' + e.message, ...c }, profile)); return;
      }
      if (!registerChild(key, child, profile, opts, cwd)) {
        rejectUnregisteredChild(key, child, profile, resolve);
        return;
      }
      try { child.stdin.on('error', () => {}); child.stdin.write(prompt, 'utf8'); child.stdin.end(); } catch (e) {}

      let buf = '', errBuf = '', resultText = null, lastAssistant = null, isError = null;
      let limited = false, usage = null, cost = null, sessionId = opts.sessionId || null, sideEffectsStarted = false, killedForTimeout = false;
      let settled = false, childEnded = false, terminationReason = null, terminationTimer = null;
      const t0 = Date.now();
      let to = null;
      const finish = base => {
        if (settled) return;
        settled = true;
        resolve(normalizeResult(base, profile));
      };
      const terminalBase = reason => reason === 'cancelled'
        ? { text: '已按用户请求停止。', ms: Date.now() - t0, sessionId, sideEffectsStarted, childPending: true, errorCode: 'cancelled', retryable: false }
        : { text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已请求终止。`, ms: Date.now() - t0, sessionId, sideEffectsStarted, childPending: true, errorCode: 'transient', retryable: true };
      const requestTermination = reason => {
        if (!terminationReason) terminationReason = reason;
        if (terminationReason === 'timeout') killedForTimeout = true;
        clearTimeout(to);
        killTree(child).then(ok => { if (!ok) logLine(`终止进程树未确认 [${label}] pid=${child.pid || '?'} reason=${terminationReason}`); });
        if (!terminationTimer) terminationTimer = setTimeout(() => finish(terminalBase(terminationReason)), terminationGraceMs);
      };
      const cleanupChild = () => {
        if (childEnded) return;
        childEnded = true;
        clearTimeout(to); clearTimeout(terminationTimer);
        terminationHandlers.delete(child);
        unregisterChild(key, child, profile);
      };
      terminationHandlers.set(child, requestTermination);
      to = timeoutMs > 0 ? setTimeout(() => requestTermination('timeout'), timeoutMs) : null;
      function scanLine(ln) {
        if (!ln || !ln.trim()) return;
        if (/"status"\s*:\s*"(blocked|rejected|limited|exceeded)"/.test(ln) || /usage limit|rate limit|limit reached|weekly limit/i.test(ln)) limited = true;
        let j;
        try {
          j = JSON.parse(ln);
        } catch (e) {
          // D-002:非空行 malformed JSON 无法可信分类,fail-closed。
          sideEffectsStarted = true;
          return;
        }
        if (!j || typeof j !== 'object' || Array.isArray(j)) {
          sideEffectsStarted = true;
          return;
        }
        try {
          if (j.session_id) sessionId = String(j.session_id);
          if (j.type === 'result') {
            if (typeof j.result === 'string') resultText = j.result;
            if (typeof j.is_error === 'boolean') isError = j.is_error;
            if (j.usage) usage = j.usage;
            if (typeof j.total_cost_usd === 'number') cost = j.total_cost_usd;
          } else if (j.type === 'assistant') {
            const cls = classifyClaudeStreamLine(j);
            if (cls === 'side-effect' || cls === 'unknown') sideEffectsStarted = true;
            const content = j && j.message && j.message.content;
            if (Array.isArray(content)) {
              for (const part of content) {
                if (part && part.type === 'text' && typeof part.text === 'string' && part.text.trim()) lastAssistant = part.text;
              }
            }
          }
        } catch (e) {
          // 分类/观察异常不得破坏 child 生命周期,但 fail-closed 标志必须可靠。
          sideEffectsStarted = true;
        }
      }
      child.stdout.on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\n')) >= 0) { scanLine(buf.slice(0, i)); buf = buf.slice(i + 1); } });
      child.stderr.on('data', d => { errBuf += d.toString('utf8'); if (errBuf.length > 8000) errBuf = errBuf.slice(-8000); });
      child.on('close', code => {
        const wasCancelled = cancelledChildren.has(child) || terminationReason === 'cancelled';
        cancelledChildren.delete(child); cleanupChild();
        if (settled) return;
        if (buf) scanLine(buf);
        const ms = Date.now() - t0;
        if (wasCancelled) {
          finish({ text: '已按用户请求停止。', ms, sessionId, sideEffectsStarted, errorCode: 'cancelled', retryable: false }); return;
        }
        if (killedForTimeout) {
          finish({ text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已终止。`, ms, sessionId, sideEffectsStarted, errorCode: 'transient', retryable: true }); return;
        }
        if (resultText !== null && isError !== true) {
          logLine(`完成 [${label}] ${profile.fullLabel} ${Math.round(ms / 1000)}s`);
          finish({ ok: true, limited, text: resultText, usage, cost, ms, sessionId, sideEffectsStarted }); return;
        }
        if (isError !== true && lastAssistant && lastAssistant.trim()) {
          finish({ ok: true, limited, text: lastAssistant, usage, cost, ms, sessionId, sideEffectsStarted }); return;
        }
        const raw = [resultText, lastAssistant, errBuf, 'exit=' + code].filter(Boolean).join('\n');
        const c = classifyError(raw, limited);
        logLine(`AI 未成功 [${label}] ${profile.fullLabel} exit=${code} ${c.errorCode}`);
        finish({ limited: c.limited, text: resultText || lastAssistant || friendlyError(profile, c), usage, cost, ms, sessionId, sideEffectsStarted, ...c });
      });
      child.on('error', e => {
        const wasCancelled = cancelledChildren.has(child) || terminationReason === 'cancelled';
        cancelledChildren.delete(child); cleanupChild();
        if (settled) return;
        if (wasCancelled) {
          finish({ text: '已按用户请求停止。', ms: Date.now() - t0, sessionId, sideEffectsStarted, errorCode: 'cancelled', retryable: false }); return;
        }
        if (killedForTimeout) {
          finish({ text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已请求终止。`, ms: Date.now() - t0, sessionId, sideEffectsStarted, errorCode: 'transient', retryable: true }); return;
        }
        const c = classifyError(e.message, false);
        finish({ text: '启动 ' + profile.fullLabel + ' 失败: ' + e.message, ms: Date.now() - t0, ...c });
      });
    });
  }

  function runCodex(cwd, label, prompt, opts, profile) {
    const stub = testStub(profile, opts); if (stub) return Promise.resolve(stub);
    const cfg = readConfig();
    const apiKey = String(cfg.openaiApiKey || process.env.CLAUDE_RESUME_OPENAI_API_KEY || process.env.OPENAI_API_KEY || '').trim();
    if (profile.provider === 'openai' && !apiKey) {
      return Promise.resolve(normalizeResult({
        text: 'OpenAI API Key 未配置。请在 AI 配置中填写 openaiApiKey。',
        errorCode: 'auth', retryable: true,
      }, profile));
    }
    return new Promise(resolve => {
      try { fs.mkdirSync(cwd, { recursive: true }); } catch (e) {}
      const args = buildCodexArgs(cwd, opts, profile, cfg);
      const timeoutMs = resolveTimeoutMs(opts, cfg);
      const key = String(opts.runKey || cwd).toLowerCase();
      let child;
      try { child = spawnProcess(codexCmd, args, { cwd, env: codexEnv(cfg, opts.networkRoute), windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); }
      catch (e) {
        const c = classifyError(e.message, false);
        resolve(normalizeResult({ text: '启动 Codex 失败: ' + e.message, ...c }, profile)); return;
      }
      if (!registerChild(key, child, profile, opts, cwd)) {
        rejectUnregisteredChild(key, child, profile, resolve);
        return;
      }
      try { child.stdin.on('error', () => {}); child.stdin.write(prompt, 'utf8'); child.stdin.end(); } catch (e) {}

      let buf = '', errBuf = '', finalText = '', sessionId = opts.sessionId || null, usage = null;
      let failedText = '', sideEffectsStarted = false, killedForTimeout = false;
      let settled = false, childEnded = false, terminationReason = null, terminationTimer = null;
      const t0 = Date.now();
      let to = null;
      const finish = base => {
        if (settled) return;
        settled = true;
        resolve(normalizeResult(base, profile));
      };
      const terminalBase = reason => reason === 'cancelled'
        ? { text: '已按用户请求停止。', ms: Date.now() - t0, sessionId, usage, sideEffectsStarted, childPending: true, errorCode: 'cancelled', retryable: false }
        : { text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已请求终止。`, ms: Date.now() - t0, sessionId, usage, sideEffectsStarted, childPending: true, errorCode: 'transient', retryable: true };
      const requestTermination = reason => {
        if (!terminationReason) terminationReason = reason;
        if (terminationReason === 'timeout') killedForTimeout = true;
        clearTimeout(to);
        killTree(child).then(ok => { if (!ok) logLine(`终止进程树未确认 [${label}] pid=${child.pid || '?'} reason=${terminationReason}`); });
        if (!terminationTimer) terminationTimer = setTimeout(() => finish(terminalBase(terminationReason)), terminationGraceMs);
      };
      const cleanupChild = () => {
        if (childEnded) return;
        childEnded = true;
        clearTimeout(to); clearTimeout(terminationTimer);
        terminationHandlers.delete(child);
        unregisterChild(key, child, profile);
      };
      terminationHandlers.set(child, requestTermination);
      to = timeoutMs > 0 ? setTimeout(() => requestTermination('timeout'), timeoutMs) : null;
      function scanLine(ln) {
        if (!ln || !ln.trim()) return;
        let j;
        try {
          j = JSON.parse(ln);
        } catch (e) {
          // D-002:非空行 malformed JSON 无法可信分类,fail-closed。
          sideEffectsStarted = true;
          return;
        }
        if (!j || typeof j !== 'object' || Array.isArray(j)) {
          sideEffectsStarted = true;
          return;
        }
        try {
          if (j.type === 'thread.started' && j.thread_id) sessionId = String(j.thread_id);
          if (j.type === 'item.started' || j.type === 'item.completed') {
            const cls = classifyCodexStreamLine(j);
            if (cls === 'side-effect' || cls === 'unknown') sideEffectsStarted = true;
            if (j.item && j.item.type === 'agent_message' && typeof j.item.text === 'string') finalText = j.item.text;
          }
          if (j.type === 'turn.completed' && j.usage) usage = j.usage;
          if (j.type === 'turn.failed' || j.type === 'error') failedText += '\n' + JSON.stringify(j.error || j);
        } catch (e) {
          // 分类/观察异常不得破坏 child 生命周期,但 fail-closed 标志必须可靠。
          sideEffectsStarted = true;
        }
      }
      child.stdout.on('data', d => { buf += d.toString('utf8'); let i; while ((i = buf.indexOf('\n')) >= 0) { scanLine(buf.slice(0, i)); buf = buf.slice(i + 1); } });
      child.stderr.on('data', d => { errBuf += d.toString('utf8'); if (errBuf.length > 8000) errBuf = errBuf.slice(-8000); });
      child.on('close', code => {
        const wasCancelled = cancelledChildren.has(child) || terminationReason === 'cancelled';
        cancelledChildren.delete(child); cleanupChild();
        if (settled) return;
        if (buf) scanLine(buf);
        const ms = Date.now() - t0;
        if (wasCancelled) {
          finish({ text: '已按用户请求停止。', ms, sessionId, usage, sideEffectsStarted, errorCode: 'cancelled', retryable: false }); return;
        }
        if (killedForTimeout) {
          finish({ text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已终止。`, ms, sessionId, usage, sideEffectsStarted, errorCode: 'transient', retryable: true }); return;
        }
        if (code === 0 && finalText.trim()) {
          logLine(`完成 [${label}] ${profile.fullLabel} ${Math.round(ms / 1000)}s`);
          finish({ ok: true, text: finalText, ms, sessionId, usage, sideEffectsStarted }); return;
        }
        const raw = [failedText, errBuf, finalText, 'exit=' + code].filter(Boolean).join('\n');
        const c = classifyError(raw, false);
        logLine(`AI 未成功 [${label}] ${profile.fullLabel} exit=${code} ${c.errorCode}`);
        finish({ text: finalText || friendlyError(profile, c), ms, sessionId, usage, sideEffectsStarted, ...c });
      });
      child.on('error', e => {
        const wasCancelled = cancelledChildren.has(child) || terminationReason === 'cancelled';
        cancelledChildren.delete(child); cleanupChild();
        if (settled) return;
        if (wasCancelled) {
          finish({ text: '已按用户请求停止。', ms: Date.now() - t0, sessionId, usage, sideEffectsStarted, errorCode: 'cancelled', retryable: false }); return;
        }
        if (killedForTimeout) {
          finish({ text: `执行超时(> ${Math.round(timeoutMs / 60000)} 分钟),已请求终止。`, ms: Date.now() - t0, sessionId, usage, sideEffectsStarted, errorCode: 'transient', retryable: true }); return;
        }
        const c = classifyError(e.message, false);
        finish({ text: '启动 Codex 失败: ' + e.message, ms: Date.now() - t0, ...c });
      });
    });
  }

  function run(cwd, label, prompt, opts) {
    const profile = opts.profile;
    if (!profile) return Promise.resolve(normalizeResult({ text: '未选择有效的 AI 配置。' }, { id: 'invalid', provider: 'unknown', engine: 'unknown', model: '', fullLabel: '未知 AI' }));
    const cfg = readConfig();
    const networkRoute = resolveNetworkRoute(cfg, opts.networkRoute);
    if (networkRoute === 'proxy' && !configuredProxy(cfg)) {
      return Promise.resolve(normalizeResult({
        text: `${profile.fullLabel} 的备用代理未配置。`,
        errorCode: 'proxy_unavailable', retryable: true, networkRoute,
      }, profile));
    }
    const effectiveOpts = Object.assign({}, opts, { networkRoute });
    const result = profile.engine === 'codex'
      ? runCodex(cwd, label, prompt, effectiveOpts, profile)
      : runClaude(cwd, label, prompt, effectiveOpts, profile);
    return Promise.resolve(result).then(value => {
      value.networkRoute = networkRoute;
      return value;
    });
  }

  return { run, cancel, waitForIdle, killTree, claudeCmd, codexCmd, classifyError, buildCodexArgs, terminationGraceMs };
}

module.exports = {
  createAIRunner, findClaudeCmd, findCodexCmd, killTree, classifyError,
  classifyClaudeContentPart, classifyClaudeStreamLine, classifyCodexStreamLine,
  resolveTimeoutMs, normalizeTimeoutMs, childEnv, clearProxyEnv, resolveNetworkRoute,
  buildCodexArgs, MAX_TIMEOUT_MS, DEFAULT_AI_TIMEOUT_MS,
};
