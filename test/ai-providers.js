'use strict';
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { EventEmitter } = require('events');
const { PassThrough } = require('stream');
const testConfigHelper = require('./feishu-test-config');

const repoRoot = path.resolve(__dirname, '..');
const testConfig = testConfigHelper.prepareTestConfig({
  real: false,
  source: {
    enabled: true,
    armCycleId: 'ai-providers-test',
    feishuChatId: 'oc_ai_providers_test',
    feishuAuthOpenIds: ['ou_ai_providers_owner'],
    feishuChatProfile: 'openai-sol',
    feishuUserProfiles: {},
    customProjects: [{ name: 'AI Resume Migration', path: repoRoot }],
  },
});
process.once('exit', () => { try { testConfig.cleanup(); } catch (e) {} });
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_NO_AI = '1';

const { profilesFor, profileById, parseProfileInput } = require('../src/ai/profiles');
const { createAIRunner, findCodexCmd, resolveTimeoutMs, childEnv, clearProxyEnv, buildCodexArgs, classifyError, classifyClaudeStreamLine, classifyCodexStreamLine, MAX_TIMEOUT_MS } = require('../src/ai/runners');
const { probeProviders } = require('../src/provider-health');
const A = require('../src/feishu-agent');

let failed = 0;
const check = (name, ok, detail) => {
  console.log((ok ? '  ✓ ' : '  ✗ ') + name + (ok ? '' : ' — ' + detail));
  if (!ok) failed++;
};

async function main() {
  const viewerProfiles = profilesFor(false).map(p => p.id);
  check('普通用户可选 GPT-5.6 Sol', viewerProfiles.includes('openai-sol'), viewerProfiles.join(','));
  check('普通用户可选 DeepSeek V4 Pro', viewerProfiles.includes('deepseek-v4-pro'), viewerProfiles.join(','));
  check('普通用户不能选 Fable 5', !viewerProfiles.includes('claude-fable-5'), viewerProfiles.join(','));
  check('文字别名 v4pro 可解析', parseProfileInput('v4pro', false).id === 'deepseek-v4-pro');
  check('项目修改不设置总执行时限', A.taskTimeoutMs('modify', {}) === 0, String(A.taskTimeoutMs('modify', {})));
  check('只读查询默认 30 分钟', A.taskTimeoutMs('query', {}) === 30 * 60000, String(A.taskTimeoutMs('query', {})));
  check('闲聊默认 30 分钟且可独立配置', A.taskTimeoutMs('chat', { feishuChatTimeoutMinutes: 45 }) === 45 * 60000, String(A.taskTimeoutMs('chat', { feishuChatTimeoutMinutes: 45 })));
  check('runner 保留显式 timeoutMs=0,不会回退成旧的 30 分钟', resolveTimeoutMs({ timeoutMs: 0 }, { perProjectTimeoutMinutes: 30 }) === 0, String(resolveTimeoutMs({ timeoutMs: 0 }, { perProjectTimeoutMinutes: 30 })));
  check('超大 timeoutMs 被限制在 Node 安全上限', resolveTimeoutMs({ timeoutMs: 3000000000 }, {}) === MAX_TIMEOUT_MS, String(resolveTimeoutMs({ timeoutMs: 3000000000 }, {})));
  check('空值/负数 timeoutMs 不会意外关闭超时', resolveTimeoutMs({ timeoutMs: null }, {}) === 30 * 60000 && resolveTimeoutMs({ timeoutMs: -1 }, {}) === 30 * 60000, `${resolveTimeoutMs({ timeoutMs: null }, {})}/${resolveTimeoutMs({ timeoutMs: -1 }, {})}`);
  check('Node runner 不再读取后台续跑 perProjectTimeoutMinutes', resolveTimeoutMs({}, { perProjectTimeoutMinutes: 99 }) === 30 * 60000, String(resolveTimeoutMs({}, { perProjectTimeoutMinutes: 99 })));
  const directEnv = childEnv({ aiProxy: 'http://127.0.0.1:7897' }, 'direct');
  check('显式直连会清除继承和配置中的代理变量',
    !directEnv.http_proxy && !directEnv.https_proxy && !directEnv.HTTP_PROXY && !directEnv.HTTPS_PROXY && !directEnv.ALL_PROXY,
    JSON.stringify({ http_proxy: directEnv.http_proxy, HTTP_PROXY: directEnv.HTTP_PROXY, ALL_PROXY: directEnv.ALL_PROXY }));
  const proxyEnv = childEnv({ aiProxy: 'http://127.0.0.1:7897', aiNoProxy: 'localhost' }, 'proxy');
  check('显式代理只注入配置的子进程代理',
    proxyEnv.http_proxy === 'http://127.0.0.1:7897' && proxyEnv.HTTPS_PROXY === 'http://127.0.0.1:7897' && proxyEnv.NO_PROXY === 'localhost',
    JSON.stringify({ http_proxy: proxyEnv.http_proxy, HTTPS_PROXY: proxyEnv.HTTPS_PROXY, NO_PROXY: proxyEnv.NO_PROXY }));
  const mixedProxyEnv = clearProxyEnv({ Http_Proxy: 'http://mixed', hTTps_PrOxY: 'http://mixed', No_Proxy: 'localhost', KEEP_ME: 'yes' });
  check('直连会按大小写不敏感规则清除 Windows 代理变量',
    !mixedProxyEnv.Http_Proxy && !mixedProxyEnv.hTTps_PrOxY && !mixedProxyEnv.No_Proxy && mixedProxyEnv.KEEP_ME === 'yes',
    JSON.stringify(mixedProxyEnv));
  const defaultDirectEnv = childEnv({ aiProxy: 'http://127.0.0.1:7897' });
  check('未显式选择线路时默认直连而不是强制代理', !defaultDirectEnv.http_proxy && !defaultDirectEnv.HTTP_PROXY, JSON.stringify({ http_proxy: defaultDirectEnv.http_proxy, HTTP_PROXY: defaultDirectEnv.HTTP_PROXY }));
  const cwd = path.join(os.tmpdir(), 'claude-resume-provider-test');
  const codexCfg = { openaiBaseUrl: 'https://api.openai.com/v1', openaiReasoning: 'xhigh' };
  const codexProfile = profileById('openai-sol');
  const newCodexArgs = buildCodexArgs(cwd, { noTools: true, readOnly: true }, codexProfile, codexCfg);
  const resumeCodexArgs = buildCodexArgs(cwd, { noTools: true, readOnly: true, sessionId: 'thread-test' }, codexProfile, codexCfg);
  check('Codex 新建与续接共用非 Git 工作目录兼容参数',
    newCodexArgs.filter(x => x === '--skip-git-repo-check').length === 1
      && resumeCodexArgs.filter(x => x === '--skip-git-repo-check').length === 1,
    JSON.stringify({ newCodexArgs, resumeCodexArgs }));
  check('Codex resume 参数保持 resume 子命令、原生 thread id 和 stdin prompt',
    resumeCodexArgs[0] === 'exec' && resumeCodexArgs[1] === 'resume'
      && resumeCodexArgs.slice(-2).join('|') === 'thread-test|-'
      && !resumeCodexArgs.includes('-C'), JSON.stringify(resumeCodexArgs));
  check('Codex 新建参数仍固定 cwd 且不会误带 resume',
    newCodexArgs.includes('-C') && newCodexArgs[newCodexArgs.indexOf('-C') + 1] === cwd && newCodexArgs[1] !== 'resume', JSON.stringify(newCodexArgs));
  const ephemeralArgs = buildCodexArgs(cwd, { noTools: true, readOnly: true, ephemeral: true }, codexProfile, codexCfg);
  check('Codex 健康探测使用 ephemeral 且 resume 不携带该参数',
    ephemeralArgs.includes('--ephemeral') && !resumeCodexArgs.includes('--ephemeral'), JSON.stringify({ ephemeralArgs, resumeCodexArgs }));
  check('Git 信任与会话缺失错误不再退化成 unknown',
    classifyError('Not inside a trusted directory and --skip-git-repo-check was not specified.').errorCode === 'workspace_untrusted'
      && classifyError('Session id was not found').errorCode === 'session_missing'
      && classifyError('unexpected argument --skip-git-repo-check').errorCode === 'cli_config');
  const tempRoot = path.resolve(os.tmpdir()).toLowerCase() + path.sep;
  check('项目列表排除系统临时目录', !A.discoverProjects().some(p => path.resolve(p.path).toLowerCase().startsWith(tempRoot)));

  process.env.FEISHU_TEST_AI_FAIL_PROFILE = 'openai-sol';
  process.env.FEISHU_TEST_AI_FAIL_CODE = 'auth';
  delete process.env.FEISHU_TEST_AI_FAIL_SIDE_EFFECTS;
  const fallback = await A.runForUser(cwd, 'provider-fallback', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), readOnly: true, noTools: true,
  });
  check('OpenAI 可重试失败后自动切到 DeepSeek V4',
    fallback.ok && fallback.profile.id === 'deepseek-v4' && fallback.fallbackFrom.id === 'openai-sol', JSON.stringify(fallback));

  process.env.FEISHU_TEST_AI_FAIL_SIDE_EFFECTS = '1';
  const stopped = await A.runForUser(cwd, 'provider-side-effects', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'),
  });
  check('修改已产生副作用时禁止自动切换',
    !stopped.ok && stopped.profile.id === 'openai-sol' && stopped.attemptedProfiles.length === 1, JSON.stringify(stopped));

  delete process.env.FEISHU_TEST_AI_FAIL_SIDE_EFFECTS;
  process.env.FEISHU_TEST_AI_FAIL_CODE = 'cancelled';
  const cancelledFallback = await A.runForUser(cwd, 'provider-cancelled', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), timeoutMs: 0,
  });
  check('用户取消是终态,不会自动切换 provider',
    !cancelledFallback.ok && cancelledFallback.errorCode === 'cancelled' && cancelledFallback.attemptedProfiles.length === 1, JSON.stringify(cancelledFallback));

  process.env.FEISHU_TEST_AI_FAIL_CODE = 'auth';
  process.env.FEISHU_TEST_AI_DELAY_MS = '80';
  const budgetStart = Date.now();
  const budgeted = await A.runForUser(cwd, 'provider-total-budget', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), readOnly: true, noTools: true, timeoutMs: 120,
  });
  const budgetElapsed = Date.now() - budgetStart;
  check('provider fallback 共用一次总预算',
    !budgeted.ok && budgeted.attemptedProfiles.length === 2 && budgetElapsed < 190,
    `elapsed=${budgetElapsed} result=${JSON.stringify(budgeted)}`);
  delete process.env.FEISHU_TEST_AI_DELAY_MS;
  delete process.env.FEISHU_TEST_AI_FAIL_PROFILE;
  delete process.env.FEISHU_TEST_AI_FAIL_CODE;

  const fakeRunning = new Map();
  let fakeKilled = false;
  const fakeRunner = createAIRunner({
    readConfig: () => ({ openaiApiKey: 'test' }), running: fakeRunning, codexCmd: 'codex-test',
    spawnProcess: () => {
      const child = new EventEmitter();
      child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
      child.kill = () => { fakeKilled = true; setImmediate(() => child.emit('close', 1)); };
      return child;
    },
  });
  const cancelKey = 'timeout-cancel-test';
  const cancelPromise = fakeRunner.run(cwd, 'cancel-test', 'test', {
    profile: profileById('openai-sol'), runKey: cancelKey, timeoutMs: 0, useContinue: false,
  });
  await new Promise(resolve => setImmediate(resolve));
  const cancelFound = fakeRunner.cancel(cancelKey);
  const cancelResult = await cancelPromise;
  check('无限时限任务仍可手动停止且返回 cancelled',
    cancelFound && fakeKilled && cancelResult.errorCode === 'cancelled' && cancelResult.retryable === false,
    JSON.stringify(cancelResult));

  // ---- D-002:provider stdout 活动分类 fail-closed(fake child + 录制 JSONL) ----
  {
    const d002Config = () => ({ openaiApiKey: 'test', deepseekApiKey: 'test-deepseek' });
    const makeChild = (lines, closeCode, stderr) => {
      const child = new EventEmitter();
      child.pid = 45000 + Math.floor(Math.random() * 500);
      child.stdin = new PassThrough();
      child.stdout = new PassThrough();
      child.stderr = new PassThrough();
      child.kill = () => {};
      process.nextTick(() => {
        for (const line of stderr || []) child.stderr.write(line + '\n');
        for (const line of lines) child.stdout.write(line + '\n');
        child.stdout.end();
        child.stderr.end();
        setImmediate(() => child.emit('close', closeCode));
      });
      return child;
    };
    const runRecording = async (profile, lines, opts) => {
      const runner = createAIRunner({
        readConfig: d002Config,
        codexCmd: 'codex-test',
        running: new Map(),
        spawnProcess: () => makeChild(lines, (opts && opts.code !== undefined ? opts.code : 0), opts && opts.stderr),
      });
      const runKey = path.join(os.tmpdir(), `d002-${profile.id}-${Math.floor(Math.random() * 1e9)}`).toLowerCase();
      return runner.run(os.tmpdir(), 'd002-recorded', 'test', Object.assign({
        profile, timeoutMs: 0, useContinue: false, runKey,
      }, opts || {}));
    };
    const doneResult = '{"type":"result","result":"done","is_error":false}';
    const claudeReadOnlyTools = ['Read', 'Glob', 'Grep', 'WebSearch', 'WebFetch'];
    const claudeMutatingTools = ['Bash', 'Write', 'Edit', 'NotebookEdit'];
    const claudeCases = [
      ['文本+思考为只读不设副作用',
        [`{"type":"assistant","message":{"content":[{"type":"text","text":"hi"},{"type":"thinking","thinking":"t","signature":"s"}]}}`, doneResult],
        r => !r.sideEffectsStarted && r.ok],
      ['redacted_thinking data 字符串为只读不设副作用',
        [`{"type":"assistant","message":{"content":[{"type":"redacted_thinking","data":"redacted","signature":"s"}]}}`, doneResult],
        r => !r.sideEffectsStarted && r.ok],
      ['redacted_thinking 缺/非法 data fail-closed',
        [`{"type":"assistant","message":{"content":[{"type":"redacted_thinking","signature":"s"}]}}`, doneResult],
        r => r.sideEffectsStarted && r.ok],
      ['Read/Glob/Grep/WebSearch/WebFetch 只读',
        [`{"type":"assistant","message":{"content":[${claudeReadOnlyTools.map((name, i) => `{"type":"tool_use","id":"t${i}","name":"${name}","input":{}}`).join(',')}]}}`, doneResult],
        r => !r.sideEffectsStarted && r.ok],
      ['Bash/Write/Edit/NotebookEdit 有副作用',
        [`{"type":"assistant","message":{"content":[${claudeMutatingTools.map((name, i) => `{"type":"tool_use","id":"t${i}","name":"${name}","input":{}}`).join(',')}]}}`, doneResult],
        r => r.sideEffectsStarted && r.ok],
      ['非空行 malformed JSON fail-closed', ['{broken json', doneResult], r => r.sideEffectsStarted && r.ok],
      ['assistant content 非数组 fail-closed',
        ['{"type":"assistant","message":{"content":{"type":"text","text":"x"}}}', doneResult],
        r => r.sideEffectsStarted],
      ['未知 content part fail-closed',
        ['{"type":"assistant","message":{"content":[{"type":"image","source":{}}]}}', doneResult],
        r => r.sideEffectsStarted],
      ['tool_use 缺合法 name fail-closed',
        ['{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","input":{}}]}}', doneResult],
        r => r.sideEffectsStarted],
      ['未知 tool name fail-closed',
        ['{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"FutureTool","input":{}}]}}', doneResult],
        r => r.sideEffectsStarted],
      ['未知顶层事件不判副作用', ['{"type":"custom_event","x":1}', doneResult], r => !r.sideEffectsStarted && r.ok],
    ];
    for (const profileId of ['claude-default', 'deepseek-v4']) {
      for (const [name, lines, expect] of claudeCases) {
        const result = await runRecording(profileById(profileId), lines);
        check(`D-002 ${profileId} ${name}`, expect(result), `sideEffects=${result.sideEffectsStarted} ok=${result.ok} err=${result.errorCode}`);
      }
    }
    const codexAgentText = '{"type":"item.completed","item":{"id":"m1","type":"agent_message","text":"done"}}';
    const codexCases = [
      ['agent_message/reasoning/web_search 只读', [
        '{"type":"item.started","item":{"id":"m1","type":"agent_message","text":"done"}}',
        '{"type":"item.completed","item":{"id":"r1","type":"reasoning","summary":["x"]}}',
        '{"type":"item.completed","item":{"id":"w1","type":"web_search","searches":[]}}',
        codexAgentText,
        '{"type":"turn.completed","usage":{"total_tokens":1}}',
      ], r => !r.sideEffectsStarted && r.ok],
      ['file_change/command_execution/mcp_tool_call 有副作用', [
        '{"type":"item.started","item":{"id":"f1","type":"file_change","path":"x"}}',
        '{"type":"item.started","item":{"id":"c1","type":"command_execution","command":"echo hi"}}',
        '{"type":"item.started","item":{"id":"t1","type":"mcp_tool_call","tool":"x"}}',
        codexAgentText,
      ], r => r.sideEffectsStarted && r.ok],
      ['非空行 malformed JSON fail-closed', ['{broken json', codexAgentText], r => r.sideEffectsStarted && r.ok],
      ['item.started 缺 item fail-closed', ['{"type":"item.started","thread_id":"t1"}', codexAgentText], r => r.sideEffectsStarted && r.ok],
      ['未知 item.type fail-closed',
        ['{"type":"item.completed","item":{"id":"u1","type":"future_item"}}', codexAgentText],
        r => r.sideEffectsStarted && r.ok],
      ['user_message 无现役契约证据按 unknown fail-closed',
        ['{"type":"item.completed","item":{"id":"u2","type":"user_message","text":"hi"}}', codexAgentText],
        r => r.sideEffectsStarted && r.ok],
      ['未知顶层事件不判副作用',
        ['{"type":"thread.idle","thread_id":"t1"}', '{"type":"heartbeat","ts":1}', codexAgentText],
        r => !r.sideEffectsStarted && r.ok],
    ];
    for (const [name, lines, expect] of codexCases) {
      const result = await runRecording(profileById('openai-sol'), lines);
      check(`D-002 codex ${name}`, expect(result), `sideEffects=${result.sideEffectsStarted} ok=${result.ok} err=${result.errorCode}`);
    }

    const claudeLimited = await runRecording(profileById('claude-default'),
      ['not json at all', '{"type":"result","is_error":true,"result":"429 rate limit exceeded"}'], { code: 1 });
    check('D-002 录制 retryable 429 + malformed activity 返回 rate_limit 且 sideEffectsStarted=true',
      claudeLimited.errorCode === 'rate_limit' && claudeLimited.retryable && claudeLimited.sideEffectsStarted,
      JSON.stringify(claudeLimited));
    const codexLimited = await runRecording(profileById('openai-sol'), [
      '{"type":"item.started","item":{"type":"future_item"}}',
      '{"type":"turn.failed","error":{"code":"rate_limit","message":"429 rate limit"}}',
    ], { code: 1 });
    check('D-002 Codex 录制 retryable 429 + unknown item 返回 rate_limit 且 sideEffectsStarted=true',
      codexLimited.errorCode === 'rate_limit' && codexLimited.retryable && codexLimited.sideEffectsStarted,
      JSON.stringify(codexLimited));

    check('D-002 纯分类 helper:顶层非 activity 事件为 null、缺 item/未知 type 为 unknown',
      classifyClaudeStreamLine({ type: 'result', result: 'x' }) === null
        && classifyClaudeStreamLine({ type: 'custom', x: 1 }) === null
        && classifyClaudeStreamLine({ type: 'assistant', message: { content: [{}] } }) === 'unknown'
        && classifyClaudeStreamLine({ type: 'assistant', message: { content: [{ type: 'redacted_thinking', data: 'x' }] } }) === 'read-only'
        && classifyClaudeStreamLine({ type: 'assistant', message: { content: [{ type: 'redacted_thinking', signature: 's' }] } }) === 'unknown'
        && classifyCodexStreamLine({ type: 'thread.idle' }) === null
        && classifyCodexStreamLine({ type: 'item.started' }) === 'unknown'
        && classifyCodexStreamLine({ type: 'item.started', item: { type: 'future' } }) === 'unknown'
        && classifyCodexStreamLine({ type: 'item.started', item: { type: 'user_message' } }) === 'unknown',
      `claude=${classifyClaudeStreamLine({ type: 'custom', x: 1 })}/${classifyClaudeStreamLine({ type: 'assistant', message: { content: [{}] } })} codex=${classifyCodexStreamLine({ type: 'item.started' })}`);
  }

  let stuckChild, stuckStarted = 0, stuckEnded = 0;
  const stuckRunning = new Map();
  const stuckRunner = createAIRunner({
    readConfig: () => ({ openaiApiKey: 'test' }), running: stuckRunning, codexCmd: 'codex-test', terminationGraceMs: 20,
    onChildStart: () => { stuckStarted++; }, onChildEnd: () => { stuckEnded++; },
    spawnProcess: () => {
      stuckChild = new EventEmitter();
      stuckChild.stdin = new PassThrough(); stuckChild.stdout = new PassThrough(); stuckChild.stderr = new PassThrough();
      stuckChild.kill = () => false;
      return stuckChild;
    },
  });
  const stuckKey = 'stuck-timeout-test';
  const stuckResult = await stuckRunner.run(cwd, 'stuck-timeout-test', 'test', {
    profile: profileById('openai-sol'), runKey: stuckKey, timeoutMs: 10, useContinue: false,
  });
  check('taskkill 不确认时查询超时仍会在宽限期后返回',
    stuckResult.errorCode === 'transient' && stuckResult.retryable && stuckResult.childPending && stuckRunning.get(stuckKey) === stuckChild && stuckStarted === 1 && stuckEnded === 0,
    `result=${JSON.stringify(stuckResult)} running=${stuckRunning.has(stuckKey)} start=${stuckStarted} end=${stuckEnded}`);
  let stuckIdleResolved = false;
  const stuckIdle = stuckRunner.waitForIdle(stuckKey).then(() => { stuckIdleResolved = true; });
  await new Promise(resolve => setImmediate(resolve));
  check('未真实 close 前 waitForIdle 保持等待', !stuckIdleResolved, String(stuckIdleResolved));
  stuckChild.emit('close', 1);
  await stuckIdle;
  check('超时逻辑返回后仍等真实 close 才注销 PID 并释放清理等待', !stuckRunning.has(stuckKey) && stuckEnded === 1 && stuckIdleResolved, `running=${stuckRunning.has(stuckKey)} end=${stuckEnded} idle=${stuckIdleResolved}`);

  let stuckCancelChild;
  const stuckCancelRunner = createAIRunner({
    readConfig: () => ({ openaiApiKey: 'test' }), codexCmd: 'codex-test', terminationGraceMs: 20,
    spawnProcess: () => {
      stuckCancelChild = new EventEmitter();
      stuckCancelChild.stdin = new PassThrough(); stuckCancelChild.stdout = new PassThrough(); stuckCancelChild.stderr = new PassThrough();
      stuckCancelChild.kill = () => false;
      return stuckCancelChild;
    },
  });
  const stuckCancelPromise = stuckCancelRunner.run(cwd, 'stuck-cancel-test', 'test', {
    profile: profileById('openai-sol'), runKey: 'stuck-cancel-test', timeoutMs: 0, useContinue: false,
  });
  await new Promise(resolve => setImmediate(resolve));
  stuckCancelRunner.cancel('stuck-cancel-test');
  const stuckCancelResult = await stuckCancelPromise;
  check('taskkill 不确认时手动停止也会返回不可重试 cancelled', stuckCancelResult.errorCode === 'cancelled' && !stuckCancelResult.retryable, JSON.stringify(stuckCancelResult));
  stuckCancelChild.emit('close', 1);

  let rejectedChildKilled = false, rejectedChild;
  const rejectedRunning = new Map();
  const rejectedRunner = createAIRunner({
    readConfig: () => ({ openaiApiKey: 'test' }), running: rejectedRunning, codexCmd: 'codex-test', terminationGraceMs: 20,
    onChildStart: () => false,
    spawnProcess: () => {
      rejectedChild = new EventEmitter();
      rejectedChild.stdin = new PassThrough(); rejectedChild.stdout = new PassThrough(); rejectedChild.stderr = new PassThrough();
      rejectedChild.kill = () => { rejectedChildKilled = true; return true; };
      return rejectedChild;
    },
  });
  const rejectedResult = await rejectedRunner.run(cwd, 'registry-failure-test', 'test', {
    profile: profileById('openai-sol'), runKey: 'registry-failure-test', taskKind: 'modify', timeoutMs: 0, useContinue: false,
  });
  check('首次 PID 登记失败会终止并在未 close 时保留挂起状态', rejectedChildKilled && rejectedResult.errorCode === 'registry_unavailable' && !rejectedResult.retryable && rejectedResult.childPending && rejectedRunning.has('registry-failure-test'), JSON.stringify(rejectedResult));
  rejectedChild.emit('close', 1);
  await new Promise(resolve => setImmediate(resolve));
  check('登记失败的子进程也要等真实 close 才释放运行锁', !rejectedRunning.has('registry-failure-test'));

  const lateRunning = new Map();
  let lateChild;
  const lateRunner = createAIRunner({
    readConfig: () => ({ openaiApiKey: 'test' }), running: lateRunning, codexCmd: 'codex-test',
    spawnProcess: () => {
      lateChild = new EventEmitter();
      lateChild.stdin = new PassThrough(); lateChild.stdout = new PassThrough(); lateChild.stderr = new PassThrough();
      lateChild.kill = () => {};
      return lateChild;
    },
  });
  const lateKey = 'late-close-test';
  const latePromise = lateRunner.run(cwd, 'late-close-test', 'test', {
    profile: profileById('openai-sol'), runKey: lateKey, timeoutMs: 0, useContinue: false,
  });
  await new Promise(resolve => setImmediate(resolve));
  lateChild.emit('error', new Error('synthetic spawn failure'));
  await latePromise;
  const sentinel = {};
  lateRunning.set(lateKey, sentinel);
  lateChild.emit('close', 1);
  await new Promise(resolve => setImmediate(resolve));
  check('旧 attempt 的迟到 close 不会删除新任务登记', lateRunning.get(lateKey) === sentinel, String(lateRunning.has(lateKey)));

  const orphanRegistry = path.join(os.tmpdir(), `claude-resume-orphan-children-${process.pid}.json`);
  const orphanStartedAt = Date.now() - 1000;
  const orphanAgentPid = 39000;
  fs.writeFileSync(orphanRegistry, JSON.stringify({ agentPid: orphanAgentPid, children: [
    { pid: 41001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai' },
    { pid: 41002, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'claude' },
    { pid: 41003, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'deepseek' },
  ] }));
  const orphanKilled = [];
  const orphanCount = A.reapOrphanedAIChildren({
    registryPath: orphanRegistry,
    inspectProcess: pid => pid === 41001
      ? { state: 'found', process: { ProcessId: pid, ParentProcessId: orphanAgentPid, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: new Date(orphanStartedAt).toISOString() } }
      : pid === 41002
        ? { state: 'found', process: { ProcessId: pid, ParentProcessId: orphanAgentPid, Name: 'cmd.exe', CommandLine: 'cmd.exe /c claude.cmd', CreationDate: new Date(orphanStartedAt - 10 * 60000).toISOString() } }
        : { state: 'found', process: { ProcessId: pid, ParentProcessId: 99999, Name: 'cmd.exe', CommandLine: 'cmd.exe /c claude.cmd', CreationDate: new Date(orphanStartedAt).toISOString() } },
    killProcessTree: pid => { orphanKilled.push(pid); return true; },
  });
  check('异常重启只清理启动时间和命令都匹配的遗留 AI 进程', orphanCount === 1 && orphanKilled.join(',') === '41001' && !fs.existsSync(orphanRegistry), `count=${orphanCount} killed=${orphanKilled}`);

  const retainedRegistry = path.join(os.tmpdir(), `claude-resume-retained-children-${process.pid}.json`);
  fs.writeFileSync(retainedRegistry, JSON.stringify({ agentPid: orphanAgentPid, children: [
    { pid: 42001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai' },
    { pid: 42002, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'deepseek' },
  ] }));
  const matchingProcess = pid => ({ state: 'found', process: {
    ProcessId: pid, ParentProcessId: orphanAgentPid, Name: pid === 42001 ? 'codex.exe' : 'cmd.exe',
    CommandLine: pid === 42001 ? 'codex.exe exec' : 'cmd.exe /c claude.cmd', CreationDate: new Date(orphanStartedAt).toISOString(),
  } });
  const retainedCount = A.reapOrphanedAIChildren({
    registryPath: retainedRegistry,
    inspectProcess: pid => pid === 42001 ? matchingProcess(pid) : { state: 'failed', reason: 'synthetic-cim-timeout' },
    killProcessTree: () => false,
  });
  const retained = JSON.parse(fs.readFileSync(retainedRegistry, 'utf8'));
  check('taskkill 失败或 CIM 检查失败会保留登记供下次重试', retainedCount === 0 && retained.children.length === 2, `count=${retainedCount} children=${retained.children.length}`);
  for (const candidate of [retainedRegistry, retainedRegistry + '.bak']) try { fs.unlinkSync(candidate); } catch (e) {}

  const recoveredRegistry = path.join(os.tmpdir(), `claude-resume-recovered-children-${process.pid}.json`);
  fs.writeFileSync(recoveredRegistry, '{broken');
  fs.writeFileSync(recoveredRegistry + '.tmp-complete', JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid: 43001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai' },
  ] }));
  const recoveredOld = new Date(Date.now() - 2000), recoveredNew = new Date();
  fs.utimesSync(recoveredRegistry, recoveredOld, recoveredOld);
  fs.utimesSync(recoveredRegistry + '.tmp-complete', recoveredNew, recoveredNew);
  const recoveredCount = A.reapOrphanedAIChildren({ registryPath: recoveredRegistry, inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('主登记截断时会从完整临时文件恢复并确认已退出进程', recoveredCount === 0 && !fs.existsSync(recoveredRegistry) && !fs.existsSync(recoveredRegistry + '.tmp-complete'));

  const blockedFrontierRegistry = path.join(os.tmpdir(), `claude-resume-blocked-frontier-${process.pid}.json`);
  fs.writeFileSync(blockedFrontierRegistry, '{broken-main');
  fs.writeFileSync(blockedFrontierRegistry + '.tmp-valid', JSON.stringify({ agentPid: orphanAgentPid, children: [] }));
  fs.writeFileSync(blockedFrontierRegistry + '.tmp-corrupt', '{newer-broken-generation');
  const frontierT1 = new Date(Date.now() - 6000), frontierT2 = new Date(Date.now() - 4000), frontierT3 = new Date(Date.now() - 2000);
  fs.utimesSync(blockedFrontierRegistry, frontierT1, frontierT1);
  fs.utimesSync(blockedFrontierRegistry + '.tmp-valid', frontierT2, frontierT2);
  fs.utimesSync(blockedFrontierRegistry + '.tmp-corrupt', frontierT3, frontierT3);
  const blockedFrontierReap = A.reapOrphanedAIChildren({ registryPath: blockedFrontierRegistry, inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('有效 generation 早于任一损坏 generation 时拒绝恢复并保留全部现场',
    blockedFrontierReap === 0
      && fs.existsSync(blockedFrontierRegistry)
      && fs.existsSync(blockedFrontierRegistry + '.tmp-valid')
      && fs.existsSync(blockedFrontierRegistry + '.tmp-corrupt'),
    `reaped=${blockedFrontierReap}`);
  for (const candidate of [blockedFrontierRegistry, blockedFrontierRegistry + '.tmp-valid', blockedFrontierRegistry + '.tmp-corrupt']) {
    try { fs.unlinkSync(candidate); } catch (e) {}
  }

  const allowedFrontierRegistry = path.join(os.tmpdir(), `claude-resume-allowed-frontier-${process.pid}.json`);
  fs.writeFileSync(allowedFrontierRegistry, '{broken-main');
  fs.writeFileSync(allowedFrontierRegistry + '.tmp-corrupt', '{middle-broken-generation');
  fs.writeFileSync(allowedFrontierRegistry + '.tmp-valid', JSON.stringify({ agentPid: orphanAgentPid, children: [] }));
  fs.utimesSync(allowedFrontierRegistry, frontierT1, frontierT1);
  fs.utimesSync(allowedFrontierRegistry + '.tmp-corrupt', frontierT2, frontierT2);
  fs.utimesSync(allowedFrontierRegistry + '.tmp-valid', frontierT3, frontierT3);
  const allowedFrontierReap = A.reapOrphanedAIChildren({ registryPath: allowedFrontierRegistry, inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('完整 generation 严格晚于主文件和所有损坏 generation 时允许恢复',
    allowedFrontierReap === 0
      && !fs.existsSync(allowedFrontierRegistry)
      && !fs.existsSync(allowedFrontierRegistry + '.tmp-valid')
      && !fs.existsSync(allowedFrontierRegistry + '.tmp-corrupt'),
    `reaped=${allowedFrontierReap}`);

  const newerBackupRegistry = path.join(os.tmpdir(), `claude-resume-newer-backup-${process.pid}.json`);
  const newerBackupBytes = JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [] });
  fs.writeFileSync(newerBackupRegistry, '{older-broken-main');
  fs.writeFileSync(newerBackupRegistry + '.bak', newerBackupBytes);
  const corruptOldTime = new Date(Date.now() - 5000), backupNewTime = new Date();
  fs.utimesSync(newerBackupRegistry, corruptOldTime, corruptOldTime);
  fs.utimesSync(newerBackupRegistry + '.bak', backupNewTime, backupNewTime);
  const newerBackupReap = A.reapOrphanedAIChildren({
    registryPath: newerBackupRegistry,
    inspectProcess: () => ({ state: 'gone' }),
    killProcessTree: () => false,
  });
  check('损坏主登记存在时即使备份更新也不得恢复或覆盖现场',
    newerBackupReap === 0
      && fs.readFileSync(newerBackupRegistry, 'utf8') === '{older-broken-main'
      && fs.readFileSync(newerBackupRegistry + '.bak', 'utf8') === newerBackupBytes,
    `reaped=${newerBackupReap}`);
  try { fs.unlinkSync(newerBackupRegistry); } catch (e) {}
  try { fs.unlinkSync(newerBackupRegistry + '.bak'); } catch (e) {}

  const missingMainRegistry = path.join(os.tmpdir(), `claude-resume-missing-main-${process.pid}.json`);
  fs.writeFileSync(missingMainRegistry + '.bak', JSON.stringify({ agentPid: orphanAgentPid, children: [] }));
  const missingMainReap = A.reapOrphanedAIChildren({
    registryPath: missingMainRegistry,
    inspectProcess: () => ({ state: 'gone' }),
    killProcessTree: () => false,
  });
  check('主登记缺失时允许最后一次完整备份恢复',
    missingMainReap === 0 && !fs.existsSync(missingMainRegistry + '.bak'),
    `reaped=${missingMainReap}`);

  const lockRegistry = A.childRegistryPath;
  const lockRunKey = path.join(os.tmpdir(), 'orphan-locked-project').toLowerCase();
  fs.writeFileSync(lockRegistry, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid: 44001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', profileId: 'openai-sol', runKey: lockRunKey, taskKind: 'modify', cwd: lockRunKey },
    { pid: 44002, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'deepseek', profileId: 'deepseek-v4', runKey: '', taskKind: '', cwd: '' },
  ] }));
  A.reapOrphanedAIChildren({
    inspectProcess: () => ({ state: 'failed', reason: 'synthetic-cim-timeout' }),
    killProcessTree: () => false,
  });
  check('未确认孤儿会恢复同任务占位锁且旧格式全局阻止修改',
    A.running.has(lockRunKey) && A.orphanBlocksRun(lockRunKey, 'modify') && A.orphanBlocksRun('another-project', 'modify') && !A.orphanBlocksRun('another-query', 'query'),
    `running=${A.running.has(lockRunKey)}`);
  const blockedResult = await A.runForUser(lockRunKey, 'orphan-lock-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: lockRunKey, taskKind: 'modify', timeoutMs: 0,
  });
  check('孤儿锁存在时 runForUser fail-closed 不启动第二个修改进程', blockedResult.errorCode === 'orphan_pending' && !blockedResult.retryable, JSON.stringify(blockedResult));
  A.reapOrphanedAIChildren({ onlyOrphans: true, inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('后台重试确认进程消失后释放占位锁', !A.running.has(lockRunKey) && !A.orphanBlocksRun(lockRunKey, 'modify') && !A.orphanBlocksRun('another-project', 'modify') && !fs.existsSync(lockRegistry));

  // ---- D-001:三态身份分类 ----
  const baseEntry = { pid: 50001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai' };
  const baseInfo = { ProcessId: 50001, ParentProcessId: orphanAgentPid, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: new Date(orphanStartedAt).toISOString() };
  let cls = A.classifyRegisteredAIProcess(baseInfo, baseEntry);
  check('三态分类:全部元数据匹配返回 matched', cls.verdict === 'matched' && cls.reason === 'matched', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { ParentProcessId: 99999 }), baseEntry);
  check('三态分类:父 PID 不同返回 mismatched', cls.verdict === 'mismatched' && cls.reason === 'parent-mismatch', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { CreationDate: new Date(orphanStartedAt - 10 * 60000).toISOString() }), baseEntry);
  check('三态分类:启动时间超出 5 秒窗口返回 mismatched', cls.verdict === 'mismatched' && cls.reason === 'start-time-mismatch', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { Name: 'pwsh.exe', CommandLine: 'pwsh.exe -Command ls' }), baseEntry);
  check('三态分类:命令签名不含 provider 命令返回 mismatched', cls.verdict === 'mismatched' && cls.reason === 'command-signature-mismatch', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { CommandLine: 'node.exe feishu-agent.js' }), baseEntry);
  check('三态分类:feishu-agent 自身返回 mismatched', cls.verdict === 'mismatched' && cls.reason === 'agent-self', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(baseInfo, { pid: 50001, agentPid: orphanAgentPid, provider: 'openai' });
  check('三态分类:启动时间缺失返回 unverifiable', cls.verdict === 'unverifiable' && cls.reason === 'registry-metadata-invalid', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(baseInfo, { pid: 50001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'mystery' });
  check('三态分类:未知 provider 返回 unverifiable', cls.verdict === 'unverifiable' && cls.reason === 'provider-unknown', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { CreationDate: 'not-a-date' }), baseEntry);
  check('三态分类:CreationDate 无法解析返回 unverifiable', cls.verdict === 'unverifiable' && cls.reason === 'process-info-incomplete', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(null, baseEntry);
  check('三态分类:进程信息缺失返回 unverifiable', cls.verdict === 'unverifiable' && cls.reason === 'process-info-missing', JSON.stringify(cls));
  cls = A.classifyRegisteredAIProcess(Object.assign({}, baseInfo, { CommandLine: '' }), baseEntry);
  check('三态分类:CommandLine 缺失返回 unverifiable', cls.verdict === 'unverifiable' && cls.reason === 'process-info-incomplete', JSON.stringify(cls));
  check('三态分类 reason 不含敏感信息(PID/路径/命令名/数字)',
    !/[0-9]{2,}|codex|claude|feishu|\.exe|[\\/]/.test(cls.reason), JSON.stringify(cls));
  check('isRegisteredAIProcess 布尔兼容面复用三态分类',
    A.isRegisteredAIProcess(baseInfo, baseEntry) === true
      && A.isRegisteredAIProcess(Object.assign({}, baseInfo, { ParentProcessId: 99999 }), baseEntry) === false
      && A.isRegisteredAIProcess(null, baseEntry) === false,
    `${A.isRegisteredAIProcess(baseInfo, baseEntry)}/${A.isRegisteredAIProcess(null, baseEntry)}`);

  // ---- D-001:found 但 unverifiable 时登记与 runKey 锁保留、kill 不调用 ----
  const unverifiableKey = path.join(os.tmpdir(), 'orphan-unverifiable-project').toLowerCase();
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid: 51001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', runKey: unverifiableKey, taskKind: 'modify', cwd: unverifiableKey },
  ] }));
  const unverifiedKilled = [];
  const unverifiedCount = A.reapOrphanedAIChildren({
    inspectProcess: () => ({ state: 'found', process: { ProcessId: 51001, ParentProcessId: orphanAgentPid, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: 'garbage-date' } }),
    killProcessTree: pid => { unverifiedKilled.push(pid); return true; },
  });
  check('found 但 unverifiable 时保留登记与 runKey 锁且不 kill',
    unverifiedCount === 0 && unverifiedKilled.length === 0 && fs.existsSync(A.childRegistryPath)
      && A.running.has(unverifiableKey) && A.orphanBlocksRun(unverifiableKey, 'modify'),
    `count=${unverifiedCount} killed=${unverifiedKilled.length} kept=${fs.existsSync(A.childRegistryPath)} locked=${A.running.has(unverifiableKey)}`);

  // ---- D-001:mismatched 时登记/锁释放但 kill 不调用 ----
  const mismatchedKey = path.join(os.tmpdir(), 'orphan-mismatched-project').toLowerCase();
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid: 52001, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', runKey: mismatchedKey, taskKind: 'modify', cwd: mismatchedKey },
  ] }));
  const mismatchedKilled = [];
  const mismatchedCount = A.reapOrphanedAIChildren({
    inspectProcess: () => ({ state: 'found', process: { ProcessId: 52001, ParentProcessId: orphanAgentPid, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: new Date(orphanStartedAt - 10 * 60000).toISOString() } }),
    killProcessTree: pid => { mismatchedKilled.push(pid); return true; },
  });
  check('mismatched 时释放登记与 runKey 锁且不 kill',
    mismatchedCount === 0 && mismatchedKilled.length === 0 && !fs.existsSync(A.childRegistryPath)
      && !A.running.has(mismatchedKey) && !A.orphanBlocksRun(mismatchedKey, 'modify'),
    `count=${mismatchedCount} killed=${mismatchedKilled.length} kept=${fs.existsSync(A.childRegistryPath)} locked=${A.running.has(mismatchedKey)}`);

  // ---- D-001:matched 才 kill;kill 失败后的二次 inspect 按契约处理 ----
  const matchedKey = path.join(os.tmpdir(), 'orphan-matched-project').toLowerCase();
  const matchedProcess = pid => ({ state: 'found', process: { ProcessId: pid, ParentProcessId: orphanAgentPid, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: new Date(orphanStartedAt).toISOString() } });
  const writeMatchedRegistry = pid => fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', runKey: matchedKey, taskKind: 'modify', cwd: matchedKey },
  ] }));
  writeMatchedRegistry(53001);
  const matchedKilled = [];
  const matchedCount = A.reapOrphanedAIChildren({
    inspectProcess: matchedProcess,
    killProcessTree: pid => { matchedKilled.push(pid); return true; },
  });
  check('matched 才调用 kill 并移除登记与锁',
    matchedCount === 1 && matchedKilled.join(',') === '53001' && !fs.existsSync(A.childRegistryPath)
      && !A.running.has(matchedKey) && !A.orphanBlocksRun(matchedKey, 'modify'),
    `count=${matchedCount} killed=${matchedKilled.join(',')} kept=${fs.existsSync(A.childRegistryPath)}`);

  writeMatchedRegistry(53002);
  let goneAfterKill = false;
  const goneAfterCount = A.reapOrphanedAIChildren({
    inspectProcess: pid => goneAfterKill ? { state: 'gone' } : matchedProcess(pid),
    killProcessTree: () => { goneAfterKill = true; return false; },
  });
  check('kill 返回 false 后二次 inspect 仅 gone 可确认结束并移除计数',
    goneAfterCount === 1 && !fs.existsSync(A.childRegistryPath) && !A.running.has(matchedKey),
    `count=${goneAfterCount} kept=${fs.existsSync(A.childRegistryPath)}`);

  writeMatchedRegistry(53003);
  const killFailKept = [];
  const killFailCount = A.reapOrphanedAIChildren({
    inspectProcess: matchedProcess,
    killProcessTree: pid => { killFailKept.push(pid); return false; },
  });
  check('kill 返回 false 后二次 inspect 仍 matched 时保留孤儿与锁',
    killFailCount === 0 && killFailKept.join(',') === '53003' && fs.existsSync(A.childRegistryPath)
      && A.running.has(matchedKey) && A.orphanBlocksRun(matchedKey, 'modify'),
    `count=${killFailCount} kept=${fs.existsSync(A.childRegistryPath)} locked=${A.running.has(matchedKey)}`);

  writeMatchedRegistry(53004);
  let failAfterKill = false;
  const failedAfterCount = A.reapOrphanedAIChildren({
    inspectProcess: () => failAfterKill ? { state: 'failed', reason: 'synthetic-cim-after-kill' } : matchedProcess(53004),
    killProcessTree: () => { failAfterKill = true; return false; },
  });
  check('kill 返回 false 后二次 inspect failed 时保留孤儿(fail-closed)',
    failedAfterCount === 0 && fs.existsSync(A.childRegistryPath) && A.running.has(matchedKey),
    `count=${failedAfterCount} kept=${fs.existsSync(A.childRegistryPath)}`);

  writeMatchedRegistry(53005);
  let pidReused = false;
  const reusedAfterCount = A.reapOrphanedAIChildren({
    inspectProcess: () => pidReused
      ? { state: 'found', process: { ProcessId: 53005, ParentProcessId: 88888, Name: 'codex.exe', CommandLine: 'codex.exe exec', CreationDate: new Date(orphanStartedAt).toISOString() } }
      : matchedProcess(53005),
    killProcessTree: () => { pidReused = true; return false; },
  });
  check('kill 返回 false 后二次 inspect mismatched 时移除但不计 killed',
    reusedAfterCount === 0 && !fs.existsSync(A.childRegistryPath) && !A.running.has(matchedKey),
    `count=${reusedAfterCount} kept=${fs.existsSync(A.childRegistryPath)}`);

  // ---- D-001:A:pid 缺失/非法的登记进入三态 unverifiable,不 inspect、不 kill,登记与锁保留 ----
  const invalidPidKey = path.join(os.tmpdir(), 'orphan-invalid-pid-project').toLowerCase();
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', runKey: invalidPidKey, taskKind: 'modify', cwd: invalidPidKey },
    { pid: 'not-a-pid', agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'deepseek' },
  ] }));
  const invalidPidInspect = [];
  const invalidPidKilled = [];
  const invalidPidCount = A.reapOrphanedAIChildren({
    inspectProcess: pid => { invalidPidInspect.push(pid); return { state: 'gone' }; },
    killProcessTree: pid => { invalidPidKilled.push(pid); return true; },
  });
  const invalidPidFile = JSON.parse(fs.readFileSync(A.childRegistryPath, 'utf8'));
  check('pid 缺失/非法时 reap 不 inspect 也不 kill,登记保留且 runKey 锁保留',
    invalidPidCount === 0 && invalidPidInspect.length === 0 && invalidPidKilled.length === 0
      && invalidPidFile.children.length === 2 && invalidPidFile.children.every(entry => entry.orphan === true)
      && A.running.has(invalidPidKey) && A.orphanBlocksRun(invalidPidKey, 'modify'),
    `count=${invalidPidCount} inspect=${invalidPidInspect.length} killed=${invalidPidKilled.length} children=${invalidPidFile.children.length} locked=${A.running.has(invalidPidKey)}`);
  check('缺 runKey 的不可核验旧登记触发 legacy 全局修改锁',
    A.orphanBlocksRun('another-modify-project', 'modify') && !A.orphanBlocksRun('another-query', 'query'),
    `legacyModify=${A.orphanBlocksRun('another-modify-project', 'modify')} legacyQuery=${A.orphanBlocksRun('another-query', 'query')}`);
  const laterValidPid = 54999;
  A.registeredChildren.set(laterValidPid, {
    pid: laterValidPid, agentPid: process.pid, startedAt: Date.now(), provider: 'openai',
    runKey: 'later-valid-run', taskKind: 'query', cwd: 'later-valid-run',
  });
  const persistedWithInvalid = A.persistChildRegistryForTest();
  const combinedRegistry = JSON.parse(fs.readFileSync(A.childRegistryPath, 'utf8'));
  check('后续正常登记持久化不会覆盖无合法 PID 的不可核验旧登记',
    persistedWithInvalid && combinedRegistry.children.length === 3
      && combinedRegistry.children.filter(entry => !Number.isInteger(Number(entry.pid)) || Number(entry.pid) <= 0).length === 2
      && combinedRegistry.children.some(entry => Number(entry.pid) === laterValidPid),
    JSON.stringify(combinedRegistry.children));
  A.registeredChildren.delete(laterValidPid);

  // ---- D-001:B:写盘失败时旧登记/内存 registeredChildren 与 running/orphan 锁保留,同 runKey 不启动 ----
  const failKey = path.join(os.tmpdir(), 'orphan-write-fail-project').toLowerCase();
  const failPid = 55001;
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, updatedAt: new Date().toISOString(), children: [
    { pid: failPid, agentPid: orphanAgentPid, startedAt: orphanStartedAt, provider: 'openai', runKey: failKey, taskKind: 'modify', cwd: failKey },
  ] }));
  A.setChildRegistryWriteFailureForTest(true);
  const failInspectCalls = [];
  const failWriteCount = A.reapOrphanedAIChildren({
    inspectProcess: pid => { failInspectCalls.push(pid); return { state: 'failed', reason: 'synthetic-cim-timeout' }; },
    killProcessTree: () => false,
  });
  A.setChildRegistryWriteFailureForTest(false);
  const failDisk = JSON.parse(fs.readFileSync(A.childRegistryPath, 'utf8'));
  check('写盘失败时不按未落盘结果更新:旧登记与 registeredChildren/orphan 锁保留',
    failWriteCount === 0 && failInspectCalls.join(',') === String(failPid)
      && failDisk.children.length === 1 && failDisk.children[0].pid === failPid && failDisk.children[0].orphan !== true
      && A.registeredChildren.has(failPid) && A.registeredChildren.get(failPid).orphan === true
      && A.running.has(failKey) && A.orphanBlocksRun(failKey, 'modify'),
    `count=${failWriteCount} disk=${failDisk.children.length} inMem=${A.registeredChildren.has(failPid)} locked=${A.running.has(failKey)}`);
  const blockedAfterFail = await A.runForUser(failKey, 'orphan-write-fail-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: failKey, taskKind: 'modify', timeoutMs: 0,
  });
  check('写盘失败后同 runKey 的第二个 modify 仍被 fail-closed 拒绝', blockedAfterFail.errorCode === 'orphan_pending' && !blockedAfterFail.retryable, JSON.stringify(blockedAfterFail));
  const afterFailRecover = A.reapOrphanedAIChildren({ inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('恢复写盘后按盘上旧登记重新核验并释放锁',
    afterFailRecover === 0 && !fs.existsSync(A.childRegistryPath) && !A.running.has(failKey)
      && !A.orphanBlocksRun(failKey, 'modify') && !A.registeredChildren.has(failPid),
    `count=${afterFailRecover} kept=${fs.existsSync(A.childRegistryPath)} locked=${A.running.has(failKey)}`);

  // ---- D-001:临时文件创建后写盘失败必须清理自建 tmp,不得把完好主登记升级为损坏前沿 ----
  const residuePid = 55002;
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: process.pid, updatedAt: new Date().toISOString(), children: [] }));
  A.registeredChildren.set(residuePid, {
    pid: residuePid, agentPid: process.pid, startedAt: Date.now(), provider: 'openai',
    runKey: 'tmp-residue-run', taskKind: 'query', cwd: 'tmp-residue-run',
  });
  A.setChildRegistryWriteFailureForTest(true, 'after-tmp');
  const residueFailPersist = A.persistChildRegistryForTest();
  A.setChildRegistryWriteFailureForTest(false);
  const residueTmps = fs.readdirSync(path.dirname(A.childRegistryPath))
    .filter(name => name.startsWith(path.basename(A.childRegistryPath) + '.tmp-'));
  const residueRetryPersist = A.persistChildRegistryForTest();
  const residueDisk = JSON.parse(fs.readFileSync(A.childRegistryPath, 'utf8'));
  check('临时文件创建后写盘失败会清理自建 tmp 且不锁存损坏,恢复后可再次持久化',
    residueFailPersist === false && residueTmps.length === 0 && !A.childRegistryCorruptForTest()
      && residueRetryPersist === true && residueDisk.children.some(entry => Number(entry.pid) === residuePid),
    `persist=${residueFailPersist} tmps=${residueTmps.join(',')} corrupt=${A.childRegistryCorruptForTest()} retry=${residueRetryPersist}`);
  A.registeredChildren.delete(residuePid);
  A.persistChildRegistryForTest();
  try { fs.unlinkSync(A.childRegistryPath); } catch (e) {}

  // ---- D-001:terminateRunningChildren 只杀当前 agent 的非 orphan child ----
  let activeShutdownKilled = false;
  const orphanPlaceholder = { pid: 54001, orphan: true, runKey: 'orphan-terminate-key' };
  const activeShutdownChild = { kill: () => { activeShutdownKilled = true; } };
  A.running.set('orphan-terminate-key', orphanPlaceholder);
  A.running.set('active-terminate-key', activeShutdownChild);
  const terminatedMixed = A.terminateRunningChildren('d001-test-shutdown');
  check('terminateRunningChildren 混合场景只杀 active、在真实 close 前保留两类锁',
    terminatedMixed === 1 && activeShutdownKilled && A.running.has('orphan-terminate-key')
      && A.running.get('orphan-terminate-key') === orphanPlaceholder && A.running.get('active-terminate-key') === activeShutdownChild,
    `count=${terminatedMixed} running=${Array.from(A.running.keys()).join(',')}`);
  A.running.delete('orphan-terminate-key');
  A.running.delete('active-terminate-key');
  A.resetShuttingDownForTest();
  // 清空本组测试残留的孤儿状态(running/orphanPlaceholders/legacy 锁),避免污染后续用例。
  fs.writeFileSync(A.childRegistryPath, JSON.stringify({ agentPid: orphanAgentPid, children: [] }));
  A.reapOrphanedAIChildren({ onlyOrphans: true, inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });

  // ---- D-001:stopRuns 对 orphan 给出不泄露 PID/路径的中文提示 ----
  A.client.__reset();
  const stopChat = 'oc_orphan_stop_test';
  A.setSession(stopChat, { mode: 'project', project: repoRoot });
  const stopProfile = A.getUserProfile('ou_ai_providers_owner');
  const stopOrphanKey = A.querySession(repoRoot, 'ou_ai_providers_owner', stopProfile.id).cwd.toLowerCase();
  A.running.set(stopOrphanKey, { pid: 77777, orphan: true, runKey: stopOrphanKey });
  await A.onMessage({ message: { message_id: 'mid_orphan_stop', chat_id: stopChat, chat_type: 'p2p', message_type: 'text', content: JSON.stringify({ text: '停止' }) }, sender: { sender_id: { open_id: 'ou_ai_providers_owner' } } });
  const stopSent = A.client.__calls.filter(call => call.op === 'create' && call.type === 'text').map(call => call.text).join('\n');
  check('停止孤儿任务时提示身份未核验、不按 PID 终止、不泄露 PID/路径',
    /身份尚未安全核验/.test(stopSent) && /未按 PID 强制终止/.test(stopSent) && /保留任务锁/.test(stopSent)
      && /后台自动重试/.test(stopSent) && !stopSent.includes('77777') && !stopSent.includes(stopOrphanKey),
    stopSent.replace(/\r?\n/g, ' '));
  A.running.delete(stopOrphanKey);
  A.setSession(stopChat, { mode: 'idle' });

  assert(profileById('openai-sol').model === 'gpt-5.6-sol');
  check('GPT-5.6 Sol 默认使用 xhigh 推理', profileById('openai-sol').reasoning === 'xhigh', profileById('openai-sol').reasoning);
  const fakeCodex = path.join(os.tmpdir(), `codex-cli-path-${process.pid}.exe`);
  const oldCodexPath = process.env.CODEX_CLI_PATH;
  try {
    fs.writeFileSync(fakeCodex, 'test', 'utf8');
    process.env.CODEX_CLI_PATH = fakeCodex;
    check('Codex CLI 优先使用 CODEX_CLI_PATH', path.resolve(findCodexCmd()) === path.resolve(fakeCodex), findCodexCmd());
  } finally {
    if (oldCodexPath === undefined) delete process.env.CODEX_CLI_PATH; else process.env.CODEX_CLI_PATH = oldCodexPath;
    try { fs.unlinkSync(fakeCodex); } catch (e) {}
  }

  const calls = [];
  const health = await probeProviders({
    readConfig: () => ({ openaiApiKey: 'test-openai', deepseekApiKey: 'test-deepseek' }),
    includeClaude: true,
    runner: { run: async (dir, label, prompt, opts) => {
      calls.push(`${opts.profile.id}:${opts.networkRoute}`);
      return opts.profile.id === 'openai-sol'
        ? { ok: true, ms: 12 }
        : opts.profile.id === 'deepseek-v4'
          ? { ok: false, errorCode: 'auth', ms: 18 }
          : { ok: false, errorCode: 'rate_limit', ms: 9 };
    } },
  });
  check('健康探测以真实直连成功判定 OpenAI 可用', health.providers.openai.status === 'available' && health.providers.openai.reason === 'ok' && health.providers.openai.route === 'direct', JSON.stringify(health));
  check('健康探测保留 DeepSeek 认证失败原因', health.providers.deepseek.status === 'unavailable' && health.providers.deepseek.reason === 'auth', JSON.stringify(health));
  check('健康探测保留 Claude 额度失败原因', health.providers.claude.status === 'unavailable' && health.providers.claude.reason === 'rate_limit', JSON.stringify(health));
  check('飞书健康探测使用三个提供商的代表模型各一次', calls.join(',') === 'openai-sol:direct,deepseek-v4:direct,claude-default:direct', calls.join(','));

  const proxyCalls = [];
  const proxyBudgets = [];
  const proxyHealth = await probeProviders({
    readConfig: () => ({ openaiApiKey: 'test-openai', deepseekApiKey: 'test-deepseek', aiProxy: 'http://127.0.0.1:7897' }),
    runner: { terminationGraceMs: 5000, run: async (dir, label, prompt, opts) => {
      proxyCalls.push(`${opts.profile.provider}:${opts.networkRoute}`);
      proxyBudgets.push(`${opts.profile.provider}:${opts.networkRoute}:${opts.timeoutMs}`);
      if (opts.profile.provider === 'openai' && opts.networkRoute === 'direct') return { ok: false, errorCode: 'transient', ms: 4 };
      return { ok: true, ms: 5 };
    } },
  });
  check('直连网络失败后才尝试备用代理并缓存成功线路',
    proxyHealth.providers.openai.status === 'available' && proxyHealth.providers.openai.route === 'proxy'
      && proxyHealth.providers.openai.attemptedRoutes.join(',') === 'direct,proxy'
      && proxyCalls.join(',') === 'openai:direct,deepseek:direct,openai:proxy',
    `health=${JSON.stringify(proxyHealth)} calls=${proxyCalls.join(',')}`);
  check('健康探测为终止宽限预留总预算',
    proxyBudgets.includes('openai:direct:45000') && proxyBudgets.some(value => /^openai:proxy:1[0-5]\d{3}$/.test(value)),
    proxyBudgets.join(','));

  const authCalls = [];
  const authHealth = await probeProviders({
    readConfig: () => ({ openaiApiKey: 'test-openai', deepseekApiKey: 'test-deepseek', aiProxy: 'http://127.0.0.1:7897' }),
    runner: { run: async (dir, label, prompt, opts) => {
      authCalls.push(`${opts.profile.provider}:${opts.networkRoute}`);
      return opts.profile.provider === 'openai' ? { ok: false, errorCode: 'auth', ms: 2 } : { ok: true, ms: 2 };
    } },
  });
  check('认证失败不会切换代理重试',
    authHealth.providers.openai.reason === 'auth' && authHealth.providers.openai.attemptedRoutes.join(',') === 'direct'
      && authCalls.filter(call => call.startsWith('openai:')).join(',') === 'openai:direct',
    `health=${JSON.stringify(authHealth)} calls=${authCalls.join(',')}`);

  const pendingCalls = [];
  let pendingHealthCwd = '';
  const pendingHealth = await probeProviders({
    readConfig: () => ({ openaiApiKey: 'test-openai', deepseekApiKey: 'test-deepseek', aiProxy: 'http://127.0.0.1:7897' }),
    runner: { run: async (dir, label, prompt, opts) => {
      pendingCalls.push(`${opts.profile.provider}:${opts.networkRoute}`);
      if (opts.profile.provider === 'openai') pendingHealthCwd = dir;
      return opts.profile.provider === 'openai'
        ? { ok: false, errorCode: 'transient', childPending: true, ms: 10 }
        : { ok: true, ms: 2 };
    }, waitForIdle: async () => {} },
  });
  check('直连子进程未确认退出时不启动代理探测',
    pendingHealth.providers.openai.reason === 'transient' && pendingHealth.providers.openai.childPending
      && pendingCalls.filter(call => call.startsWith('openai:')).join(',') === 'openai:direct',
    `health=${JSON.stringify(pendingHealth)} calls=${pendingCalls.join(',')}`);
  await new Promise(resolve => setImmediate(resolve));
  check('挂起探测在真实 close 通知后清理临时目录', pendingHealthCwd && !fs.existsSync(pendingHealthCwd), pendingHealthCwd);
  check('挂起探测暴露非 JSON 的真实 close 等待句柄', pendingHealth.pendingSettlements.length === 1, String(pendingHealth.pendingSettlements.length));

  const deadRoutes = await probeProviders({
    readConfig: () => ({ openaiApiKey: 'test-openai', deepseekApiKey: 'test-deepseek', aiProxy: 'http://127.0.0.1:7897' }),
    runner: { run: async () => ({ ok: false, errorCode: 'transient', ms: 3 }) },
  });
  check('直连和备用代理都失败时返回明确的代理异常原因',
    deadRoutes.providers.openai.reason === 'proxy_unavailable' && deadRoutes.providers.deepseek.reason === 'proxy_unavailable',
    JSON.stringify(deadRoutes));

  A.setProviderHealthForTest({
    openai: { status: 'unavailable', reason: 'transient' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'unavailable', reason: 'auth' },
  });
  A.ageProviderHealthForTest(31000);
  check('失败健康结果只做 30 秒负缓存', A.providerHealthIsStale('openai') && A.providerHealthIsStale(), `${A.providerHealthIsStale('openai')}/${A.providerHealthIsStale()}`);
  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'unavailable', reason: 'auth' },
  });
  A.ageProviderHealthForTest(31000);
  check('成功线路在 30 秒后仍命中 5 分钟缓存', !A.providerHealthIsStale('openai'), String(A.providerHealthIsStale('openai')));
  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct', configFingerprint: 'stale-config' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'unavailable', reason: 'auth' },
  });
  check('代理、密钥或端点配置变化会立即作废线路缓存', A.providerHealthIsStale('openai'), String(A.providerHealthIsStale('openai')));

  A.setProviderHealthForTest({
    openai: { status: 'unavailable', reason: 'transient', childPending: true, configFingerprint: 'pending-config' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'unavailable', reason: 'auth' },
  });
  A.ageProviderHealthForTest(6 * 60 * 1000);
  check('挂起子进程未 close 时即使负缓存过期也禁止重探', !A.providerHealthIsStale('openai') && !A.providerHealthIsStale(), `${A.providerHealthIsStale('openai')}/${A.providerHealthIsStale()}`);
  const stalePendingIgnored = A.settlePendingProviderHealthForTest('openai', 'other-config');
  const pendingSettled = A.settlePendingProviderHealthForTest('openai', 'pending-config');
  check('只有匹配快照的真实 close 才解除挂起并使线路立即过期', !stalePendingIgnored && pendingSettled && A.providerHealthIsStale('openai'), `${stalePendingIgnored}/${pendingSettled}/${A.providerHealthIsStale('openai')}`);

  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'available', reason: 'ok' },
  });
  A.ageProviderHealthForTest(6 * 60 * 1000);
  process.env.FEISHU_TEST_AI_DELAY_MS = '80';
  const cancelPreflightKey = path.join(os.tmpdir(), 'provider-preflight-cancel-test').toLowerCase();
  const lastRunBeforeCancel = A.testHooks.lastRun;
  const cancelledPreflightPromise = A.runForUser(cancelPreflightKey, 'preflight-cancel-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: cancelPreflightKey, readOnly: true, noTools: true, allowFallback: false, timeoutMs: 1000,
  });
  await new Promise(resolve => setTimeout(resolve, 10));
  const cancelledPreflightKey = A.cancelProviderPreflightForTest([cancelPreflightKey]);
  const cancelledPreflight = await cancelledPreflightPromise;
  check('等待线路探测时停止会阻止正式任务启动',
    cancelledPreflightKey === cancelPreflightKey && cancelledPreflight.errorCode === 'cancelled' && A.testHooks.lastRun === lastRunBeforeCancel,
    JSON.stringify(cancelledPreflight));
  await new Promise(resolve => setTimeout(resolve, 100));

  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'available', reason: 'ok' },
  });
  const cancelGapKey = path.join(os.tmpdir(), 'provider-preflight-gap-test').toLowerCase();
  const lastRunBeforeGapCancel = A.testHooks.lastRun;
  const cancelledGapPromise = A.runForUser(cancelGapKey, 'preflight-gap-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: cancelGapKey, readOnly: true, noTools: true, allowFallback: false, timeoutMs: 1000,
    forProfile: async () => { await new Promise(resolve => setTimeout(resolve, 50)); return {}; },
  });
  await new Promise(resolve => setTimeout(resolve, 10));
  const cancelledGapKey = A.cancelProviderPreflightForTest([cancelGapKey]);
  const cancelledGap = await cancelledGapPromise;
  check('健康缓存命中后到正式 child 登记前仍可停止',
    cancelledGapKey === cancelGapKey && cancelledGap.errorCode === 'cancelled' && A.testHooks.lastRun === lastRunBeforeGapCancel,
    JSON.stringify(cancelledGap));

  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'available', reason: 'ok' },
  });
  A.ageProviderHealthForTest(6 * 60 * 1000);
  const deadlineKey = path.join(os.tmpdir(), 'provider-preflight-deadline-test').toLowerCase();
  const lastRunBeforeDeadline = A.testHooks.lastRun;
  const preflightDeadline = await A.runForUser(deadlineKey, 'preflight-deadline-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: deadlineKey, readOnly: true, noTools: true, allowFallback: false, timeoutMs: 20,
  });
  check('线路探测耗尽总预算后不会再启动 1ms 正式请求',
    preflightDeadline.errorCode === 'transient' && A.testHooks.lastRun === lastRunBeforeDeadline,
    JSON.stringify(preflightDeadline));
  await new Promise(resolve => setTimeout(resolve, 100));
  delete process.env.FEISHU_TEST_AI_DELAY_MS;

  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'unavailable', reason: 'auth' },
  });
  const rootCard = A.buildModelCard('oc_health_test', 'ou_health_test', { origin: 'm' });
  const rootActions = rootCard.elements.flatMap(el => el.actions || []).map(action => action.value || {});
  check('模型根菜单只显示实测可用的 OpenAI / DeepSeek 服务',
    rootActions.some(v => v.do === 'modelprovider' && v.provider === 'openai')
      && rootActions.some(v => v.do === 'modelprovider' && v.provider === 'deepseek')
      && !rootActions.some(v => v.do === 'modelprovider' && v.provider === 'claude'), JSON.stringify(rootActions));
  const deepseekCard = A.buildModelCard('oc_health_test', 'ou_health_test', { origin: 'm', provider: 'deepseek' });
  const deepseekModels = deepseekCard.elements.flatMap(el => el.actions || []).map(action => action.value || {}).filter(v => v.do === 'model').map(v => v.p);
  check('DeepSeek 父级下只出现 V4 / V4 Pro 两个子模型', deepseekModels.join(',') === 'deepseek-v4,deepseek-v4-pro', deepseekModels.join(','));
  const unavailableClaudeCard = A.buildModelCard('oc_health_test', 'ou_health_test', { origin: 'm', provider: 'claude' });
  check('Claude 不可用时不能通过指定 provider 强行打开 Claude 子模型', !JSON.stringify(unavailableClaudeCard).includes('claude-opus'), JSON.stringify(unavailableClaudeCard));
  A.setProviderHealthForTest({
    openai: { status: 'available', reason: 'ok', route: 'direct' },
    deepseek: { status: 'available', reason: 'ok', route: 'direct' },
    claude: { status: 'available', reason: 'ok' },
  });
  const routedRun = await A.runForUser(cwd, 'cached-route-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), readOnly: true, noTools: true, allowFallback: false,
  });
  check('正式任务固定使用健康缓存中的成功线路且只执行一次',
    routedRun.ok && routedRun.networkRoute === 'direct' && routedRun.attemptedProfiles.join(',') === 'openai-sol'
      && A.testHooks.lastRun.options.networkRoute === 'direct',
    JSON.stringify({ result: routedRun, lastRun: A.testHooks.lastRun }));
  A.setProviderHealthForTest(Object.fromEntries(['openai', 'deepseek', 'claude'].map(name => [name, { status: 'available', reason: 'ok', route: name === 'claude' ? null : 'direct' }])));

  let missingKeyCalls = 0;
  const missing = await probeProviders({
    readConfig: () => ({}),
    runner: { run: async () => { missingKeyCalls++; return { ok: true }; } },
  });
  check('未配置密钥时不发起网络探测', missingKeyCalls === 0 && missing.providers.openai.status === 'unconfigured' && missing.providers.deepseek.status === 'unconfigured', JSON.stringify(missing));

  const inaccessibleMainBytes = '{physically-present-but-unreadable-main';
  const inaccessibleBackupBytes = JSON.stringify({ agentPid: process.pid, children: [] });
  fs.writeFileSync(A.childRegistryPath, inaccessibleMainBytes, 'utf8');
  fs.writeFileSync(A.childRegistryPath + '.bak', inaccessibleBackupBytes, 'utf8');
  const originalLstatSync = fs.lstatSync;
  let inaccessibleMainReap;
  try {
    fs.lstatSync = target => {
      if (path.resolve(String(target)) === path.resolve(A.childRegistryPath)) {
        const error = new Error('synthetic registry access failure');
        error.code = 'EACCES';
        throw error;
      }
      return originalLstatSync(target);
    };
    inaccessibleMainReap = A.reapOrphanedAIChildren({ inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  } finally { fs.lstatSync = originalLstatSync; }
  check('主登记检查失败不得冒充缺失并接受备份,且必须锁存全局启动阻断',
    inaccessibleMainReap === 0 && A.childRegistryCorruptForTest() && A.orphanBlocksRun('inaccessible-main', 'modify')
      && fs.readFileSync(A.childRegistryPath, 'utf8') === inaccessibleMainBytes
      && fs.readFileSync(A.childRegistryPath + '.bak', 'utf8') === inaccessibleBackupBytes,
    `reaped=${inaccessibleMainReap} corrupt=${A.childRegistryCorruptForTest()}`);
  try { fs.unlinkSync(A.childRegistryPath); } catch (e) {}
  try { fs.unlinkSync(A.childRegistryPath + '.bak'); } catch (e) {}

  const olderBackup = JSON.stringify({ agentPid: process.pid, updatedAt: new Date(Date.now() - 60000).toISOString(), children: [] });
  fs.writeFileSync(A.childRegistryPath + '.bak', olderBackup, 'utf8');
  fs.writeFileSync(A.childRegistryPath, '{newer-broken-json', 'utf8');
  const backupOldTime = new Date(Date.now() - 5000), corruptNewTime = new Date();
  fs.utimesSync(A.childRegistryPath + '.bak', backupOldTime, backupOldTime);
  fs.utimesSync(A.childRegistryPath, corruptNewTime, corruptNewTime);
  const newerCorruptReap = A.reapOrphanedAIChildren({ inspectProcess: () => ({ state: 'gone' }), killProcessTree: () => false });
  check('较新损坏主登记不会被较旧有效备份覆盖并解除全局阻断',
    newerCorruptReap === 0 && A.childRegistryCorruptForTest() && A.orphanBlocksRun('newer-corrupt', 'modify')
      && fs.readFileSync(A.childRegistryPath, 'utf8') === '{newer-broken-json'
      && fs.readFileSync(A.childRegistryPath + '.bak', 'utf8') === olderBackup,
    `reaped=${newerCorruptReap} corrupt=${A.childRegistryCorruptForTest()}`);
  try { fs.unlinkSync(A.childRegistryPath + '.bak'); } catch (e) {}

  const corruptBytes = '{"children":"corrupt"}';
  fs.writeFileSync(A.childRegistryPath, corruptBytes, 'utf8');
  const lastRunBeforeCorrupt = A.testHooks.lastRun;
  const firstPersistWhileCorrupt = A.persistChildRegistryForTest();
  const corruptBlocked = await A.runForUser(cwd, 'corrupt-registry-test', 'test', 'ou_provider_test', {
    profile: profileById('openai-sol'), runKey: 'corrupt-registry-test', taskKind: 'modify', timeoutMs: 0, allowFallback: false,
  });
  const persistWhileCorrupt = A.persistChildRegistryForTest();
  check('损坏子进程登记锁存后全局阻止新 AI attempt',
    A.childRegistryCorruptForTest() && A.orphanBlocksRun('any-key', 'modify')
      && firstPersistWhileCorrupt === false && corruptBlocked.errorCode === 'orphan_pending' && A.testHooks.lastRun === lastRunBeforeCorrupt,
    JSON.stringify(corruptBlocked));
  check('损坏登记期间禁止持久化覆盖且原字节不变',
    persistWhileCorrupt === false && fs.readFileSync(A.childRegistryPath, 'utf8') === corruptBytes,
    `persist=${persistWhileCorrupt}`);
  try { fs.unlinkSync(A.childRegistryPath); } catch (e) {}

  let shutdownKilled = false;
  const shutdownChild = { kill: () => { shutdownKilled = true; } };
  A.running.set('shutdown-test', shutdownChild);
  const terminated = A.terminateRunningChildren('test-shutdown');
  check('agent 关停请求终止 child 但在真实 close/error 前保留 running 锁',
    terminated === 1 && shutdownKilled && A.running.get('shutdown-test') === shutdownChild && A.shuttingDownForTest(),
    `terminated=${terminated} killed=${shutdownKilled} retained=${A.running.has('shutdown-test')}`);
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(1); });
