'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const handler = require('../src/completion-notify');
const { childEnv } = require('../src/ai/runners');
const {
  createCompletionEvents,
  resolveCompletionProject,
  formatCompletionNotification,
  validCompletionEvent,
  completionSeen,
  stableMessageUuid,
} = require('../src/completion-events');

let failed = 0;
function check(name, condition, detail) {
  console.log((condition ? '  ✓ ' : '  ✗ ') + name + (condition ? '' : ' — ' + detail));
  if (!condition) failed++;
}

function event(overrides) {
  return Object.assign({
    version: 1,
    eventId: 'codex:thread-1:turn-1',
    source: 'codex',
    client: 'Codex',
    provider: 'openai',
    model: '',
    status: 'finished',
    createdAt: new Date().toISOString(),
    projectRoots: [process.cwd()],
    sessionId: 'thread-1',
    taskId: '',
    turnId: 'turn-1',
  }, overrides || {});
}

function writeQueue(queueDir, value, name) {
  fs.mkdirSync(queueDir, { recursive: true });
  fs.writeFileSync(path.join(queueDir, name || `${Date.now()}-${Math.random()}.json`), JSON.stringify(value));
}

function rolloutMeta(id, extra) {
  return JSON.stringify({ type: 'session_meta', payload: Object.assign({ id, session_id: id, source: 'vscode' }, extra || {}) }) + '\n';
}

async function main() {
  // ---- completion-notify hook 准入(保留原有覆盖)----
  const codex = handler.normalizeEvent('codex', {
    type: 'agent-turn-complete', 'thread-id': 'thread-a', 'turn-id': 'turn-a', cwd: process.cwd(),
  }, {});
  check('Codex turn-ended 生成稳定事件 ID 和真实工作目录',
    codex && codex.eventId === 'codex:thread-a:turn-a' && codex.projectRoots[0] === process.cwd(), JSON.stringify(codex));

  const claudeDeepSeek = handler.normalizeEvent('claude', {
    session_id: 'session-a', cwd: process.cwd(), last_assistant_message: 'done',
  }, { ANTHROPIC_BASE_URL: 'https://api.deepseek.com/anthropic' });
  check('Claude Code 经 DeepSeek 兼容端点时保留客户端并标注 provider',
    claudeDeepSeek && claudeDeepSeek.client === 'Claude Code' && claudeDeepSeek.provider === 'deepseek', JSON.stringify(claudeDeepSeek));

  const cline1 = handler.normalizeEvent('cline', {
    taskId: 'task-a', timestamp: '2026-07-31T08:00:00.000Z',
    workspaceRoots: [{ path: process.cwd() }], model: { provider: 'openrouter', slug: 'deepseek/test' },
  }, {});
  const cline2 = handler.normalizeEvent('cline', {
    taskId: 'task-a', timestamp: '2026-07-31T09:00:00.000Z', workspaceRoots: [process.cwd()],
  }, {});
  check('Cline 同一 task 再次完成时按时间区分事件', cline1.eventId !== cline2.eventId, `${cline1.eventId} / ${cline2.eventId}`);
  check('旧 Codex 批处理通知器不会通过 shell 转发不可信 payload', handler.forwardPrevious(['C:\\Tools\\notify.cmd'], '{"x":"& calc"}') === false);
  check('失效的旧 Codex 通知器异步报错不会击穿当前 handler', handler.forwardPrevious(['Z:\\missing-ai-resume-notifier.exe'], '{}') === true);
  check('内部飞书/续跑 AI 被抑制', handler.normalizeEvent('codex', { cwd: process.cwd() }, { AI_RESUME_INTERNAL_RUN: '1' }) === null);
  check('Node AI runner 总是给子进程打内部标记', childEnv({}).AI_RESUME_INTERNAL_RUN === '1');

  const admissionRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-resume-completion-admission-'));
  try {
    const codexHome = path.join(admissionRoot, '.codex');
    const sessions = path.join(codexHome, 'sessions', '2026', '07', '31');
    fs.mkdirSync(sessions, { recursive: true });
    fs.writeFileSync(path.join(sessions, 'rollout-test-thread-a.jsonl'), rolloutMeta('thread-a'));
    const admitted = handler.eventForQueue(codex, { AI_RESUME_CODEX_HOME: codexHome });
    check('Codex 持久化顶层会话在真实项目目录中允许通知', admitted && admitted.sessionId === 'thread-a', JSON.stringify(admitted));

    const datedThreadId = '019fb709-abb5-7572-ad58-b0a9d5ad3f3d';
    const shiftedSessions = path.join(codexHome, 'sessions', '2026', '08', '01');
    fs.mkdirSync(shiftedSessions, { recursive: true });
    fs.writeFileSync(path.join(shiftedSessions, `rollout-test-${datedThreadId}.jsonl`), rolloutMeta(datedThreadId));
    const shiftedEvent = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': datedThreadId, 'turn-id': 'turn-shifted', cwd: process.cwd(),
    }, {});
    check('UUID 日期快速定位失配时仍回退完整 sessions 树',
      !!handler.eventForQueue(shiftedEvent, { AI_RESUME_CODEX_HOME: codexHome }));

    const ephemeral = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-ephemeral', 'turn-id': 'turn-e', cwd: process.cwd(),
    }, {});
    check('未持久化的 Codex 子任务或 ephemeral turn 不发完成通知',
      handler.eventForQueue(ephemeral, { AI_RESUME_CODEX_HOME: codexHome }) === null);

    fs.writeFileSync(path.join(sessions, 'rollout-test-thread-subagent.jsonl'), rolloutMeta('thread-subagent', {
      parent_thread_id: 'thread-a', forked_from_id: 'thread-a', source: { subagent: { thread_spawn: { parent_thread_id: 'thread-a' } } },
    }));
    const subagent = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-subagent', 'turn-id': 'turn-s', cwd: process.cwd(),
    }, {});
    const subagentAdmission = handler.admitCompletionEvent(subagent, { AI_RESUME_CODEX_HOME: codexHome });
    check('已持久化的 Codex 子代理 fork 仍不得发完成通知',
      !subagentAdmission.event && subagentAdmission.reason === 'subagent_thread', JSON.stringify(subagentAdmission));

    fs.writeFileSync(path.join(sessions, 'rollout-test-thread-user-fork.jsonl'), rolloutMeta('thread-user-fork', {
      forked_from_id: 'thread-a', thread_source: 'user',
    }));
    const userFork = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-user-fork', 'turn-id': 'turn-f', cwd: process.cwd(),
    }, {});
    check('用户主动 fork 的独立顶层 thread 仍允许通知',
      !!handler.eventForQueue(userFork, { AI_RESUME_CODEX_HOME: codexHome }));

    fs.writeFileSync(path.join(sessions, 'rollout-test-thread-missing-meta-id.jsonl'),
      JSON.stringify({ type: 'session_meta', payload: { source: 'vscode', thread_source: 'user' } }) + '\n');
    const missingMetaId = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-missing-meta-id', 'turn-id': 'turn-m', cwd: process.cwd(),
    }, {});
    check('rollout 元数据缺少 thread id 时 fail-closed',
      handler.admitCompletionEvent(missingMetaId, { AI_RESUME_CODEX_HOME: codexHome }).reason === 'rollout_meta_mismatch');

    const documentsRoot = path.join(admissionRoot, 'Documents', 'Codex');
    const projectless = path.join(documentsRoot, '2026-07-31', 'wo-x');
    fs.mkdirSync(projectless, { recursive: true });
    fs.writeFileSync(path.join(sessions, 'rollout-test-thread-projectless.jsonl'), rolloutMeta('thread-projectless'));
    const projectlessEvent = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-projectless', 'turn-id': 'turn-p', cwd: projectless,
    }, {});
    check('Codex projectless 生成目录不冒充真实项目发通知',
      handler.eventForQueue(projectlessEvent, { AI_RESUME_CODEX_HOME: codexHome, AI_RESUME_CODEX_DOCUMENTS_ROOT: documentsRoot }) === null);

    const realRepo = path.join(documentsRoot, '2026-07-31', 'real-repo');
    fs.mkdirSync(path.join(realRepo, '.git'), { recursive: true });
    const realRepoEvent = handler.normalizeEvent('codex', {
      type: 'agent-turn-complete', 'thread-id': 'thread-a', 'turn-id': 'turn-r', cwd: realRepo,
    }, {});
    check('生成目录下明确存在 Git 根时仍按真实项目通知',
      !!handler.eventForQueue(realRepoEvent, { AI_RESUME_CODEX_HOME: codexHome, AI_RESUME_CODEX_DOCUMENTS_ROOT: documentsRoot }));

    const appDir = path.join(admissionRoot, 'app');
    fs.mkdirSync(appDir, { recursive: true });
    fs.writeFileSync(path.join(appDir, 'config.json'), JSON.stringify({ completionNotifyEnabled: true }));
    fs.writeFileSync(path.join(appDir, 'completion-context.json'), JSON.stringify({ codexDocumentsRoot: documentsRoot }));
    const contextQueue = path.join(admissionRoot, 'context-queue');
    handler.run(['codex', JSON.stringify({
      type: 'agent-turn-complete', 'thread-id': 'thread-projectless', 'turn-id': 'turn-context', cwd: projectless,
    })], { AI_RESUME_APP_DIR: appDir, AI_RESUME_CODEX_HOME: codexHome, AI_RESUME_COMPLETION_DIR: contextQueue });
    check('运行时使用安装器固化的重定向 Documents 根拒绝 projectless 事件并记录原因',
      (!fs.existsSync(contextQueue) || fs.readdirSync(contextQueue).length === 0)
        && fs.readdirSync(path.join(appDir, 'logs')).some(name => name.startsWith('completion-notify-')));
    const now = new Date();
    const localDay = [now.getFullYear(), String(now.getMonth() + 1).padStart(2, '0'), String(now.getDate()).padStart(2, '0')].join('-');
    check('完成通知诊断日志按本地日期归档',
      fs.existsSync(path.join(appDir, 'logs', `completion-notify-${localDay}.log`)));

    const blockedQueue = path.join(admissionRoot, 'queue-is-a-file');
    fs.writeFileSync(blockedQueue, 'not a directory');
    handler.run(['codex', JSON.stringify({
      type: 'agent-turn-complete', 'thread-id': 'thread-a', 'turn-id': 'turn-write-failure', cwd: process.cwd(),
    })], { AI_RESUME_APP_DIR: appDir, AI_RESUME_CODEX_HOME: codexHome, AI_RESUME_COMPLETION_DIR: blockedQueue });
    const diagnostic = fs.readdirSync(path.join(appDir, 'logs'))
      .filter(name => name.startsWith('completion-notify-'))
      .map(name => fs.readFileSync(path.join(appDir, 'logs', name), 'utf8')).join('\n');
    check('有效事件写队列失败时记录本地诊断而不是静默丢失', diagnostic.includes('reason=queue_write_failed'));
  } finally {
    fs.rmSync(admissionRoot, { recursive: true, force: true });
  }

  // 队列处理直接使用 completion-events 模块;appDir/queueDir/seenPath/homeDir/desktopDir/documentsDir
  // 全部隔离在本测试独占的临时目录,不读取/写入/备份/恢复真实 %LOCALAPPDATA%\ClaudeResume\config.json。
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-resume-completion-events-'));
  try {
    const appDir = path.join(root, 'app');
    const queueDir = path.join(root, 'queue');
    const seenPath = path.join(root, 'seen.json');
    const homeDir = path.join(root, 'home');
    const desktopDir = path.join(homeDir, 'Desktop');
    const documentsDir = path.join(homeDir, 'Documents');
    fs.mkdirSync(appDir, { recursive: true });
    fs.mkdirSync(homeDir, { recursive: true });
    fs.mkdirSync(desktopDir, { recursive: true });
    fs.mkdirSync(documentsDir, { recursive: true });

    // 默认 target 不注入,由同一 cfg 快照派生(feishu-agent 装配同语义)
    const events = createCompletionEvents({
      appDir,
      queueDir,
      seenPath,
      homeDir,
      desktopDir,
      documentsDir,
      config: () => ({ completionNotifyEnabled: true, feishuChatId: 'oc_test' }),
      knownProjects: () => [{ name: '动态项目名', path: process.cwd() }],
      send: async () => true,
      log: () => {},
    });
    const exclusionDeps = { appDir, homeDir, desktopDir, documentsDir, tempDir: os.tmpdir() };

    const first = event();
    writeQueue(queueDir, first);
    const sent = [];
    const result = await events.processCompletionEvents({
      target: 'oc_test',
      knownProjects: [{ name: '动态项目名', path: process.cwd() }],
      send: async (target, text, options) => { sent.push({ target, text, options }); return true; },
    });
    check('队列事件发送后删除并记录去重状态', result.processed === 1 && fs.readdirSync(queueDir).length === 0 && fs.existsSync(seenPath), JSON.stringify(result));
    check('通知只包含项目、执行端和时间且 Codex 文案不冒充整个会话完成', sent[0] && /本轮响应已结束/.test(sent[0].text) && /项目：动态项目名/.test(sent[0].text) && /执行端：Codex/.test(sent[0].text) && !/projectRoots|sessionId/.test(sent[0].text), sent[0] && sent[0].text);

    writeQueue(queueDir, first, 'duplicate.json');
    const duplicate = await events.processCompletionEvents({
      target: 'oc_test',
      send: async () => { sent.push({ duplicate: true }); return true; },
    });
    check('同一事件重复入队不会重复发消息', duplicate.processed === 0 && sent.length === 1 && fs.readdirSync(queueDir).length === 0, JSON.stringify(duplicate));

    fs.writeFileSync(path.join(queueDir, '000-malformed.json'), '{broken-json', 'utf8');
    const afterMalformed = event({ eventId: 'codex:after-malformed' });
    writeQueue(queueDir, afterMalformed, '001-valid.json');
    let afterMalformedSent = 0;
    const malformedRound = await events.processCompletionEvents({
      target: 'oc_test', send: async () => { afterMalformedSent++; return true; },
    });
    check('malformed JSON 隔离丢弃后同轮继续处理后续合法事件',
      malformedRound.processed === 1 && afterMalformedSent === 1 && fs.readdirSync(queueDir).length === 0,
      JSON.stringify({ malformedRound, files: fs.readdirSync(queueDir) }));

    const retryEvent = event({ eventId: 'cline:retry', source: 'cline', client: 'Cline', provider: 'deepseek' });
    writeQueue(queueDir, retryEvent, 'retry.json');
    const failedSend = await events.processCompletionEvents({
      target: 'oc_test', send: async () => false,
    });
    check('发送失败保留事件供下次重试', failedSend.processed === 0 && fs.readdirSync(queueDir).includes('retry.json'), JSON.stringify(failedSend));
    let retryOptions;
    const retried = await events.processCompletionEvents({
      target: 'oc_test',
      send: async (target, text, options) => { retryOptions = options; return true; },
    });
    check('重试成功并使用稳定消息 UUID 种子', retried.processed === 1 && retryOptions.uuidSeed === 'completion:cline:retry', JSON.stringify(retryOptions));

    const generationEvent = event({ eventId: 'codex:generation-recovery' });
    writeQueue(queueDir, generationEvent, 'generation.json');
    const originalCopyFileSync = fs.copyFileSync;
    let generationProcessed;
    try {
      fs.copyFileSync = (source, destination, ...args) => {
        if (path.resolve(destination) === path.resolve(seenPath)) throw new Error('synthetic canonical replace failure');
        return originalCopyFileSync(source, destination, ...args);
      };
      generationProcessed = await events.processCompletionEvents({ target: 'oc_test', send: async () => true });
    } finally { fs.copyFileSync = originalCopyFileSync; }
    writeQueue(queueDir, generationEvent, 'generation-duplicate.json');
    let generationDuplicateSent = 0;
    const reloadedEvents = createCompletionEvents({
      appDir, queueDir, seenPath, homeDir, desktopDir, documentsDir,
      config: () => ({ completionNotifyEnabled: true, feishuChatId: 'oc_test' }),
      knownProjects: () => [{ name: '动态项目名', path: process.cwd() }],
      send: async () => { generationDuplicateSent++; return true; }, log: () => {},
    });
    const generationRecovered = await reloadedEvents.processCompletionEvents({ target: 'oc_test' });
    check('canonical seen 写失败后仍从 generation 恢复去重状态',
      generationProcessed.processed === 1 && generationRecovered.processed === 0 && generationDuplicateSent === 0,
      JSON.stringify({ generationProcessed, generationRecovered, generationDuplicateSent }));

    const concurrentIds = Array.from({ length: 4 }, (_, index) => `codex:concurrent-${index}`);
    const concurrentHandlers = concurrentIds.map((eventId, index) => {
      const concurrentQueue = path.join(root, `queue-concurrent-${index}`);
      writeQueue(concurrentQueue, event({ eventId }), `concurrent-${index}.json`);
      return createCompletionEvents({
        appDir, queueDir: concurrentQueue, seenPath, homeDir, desktopDir, documentsDir,
        config: () => ({ completionNotifyEnabled: true, feishuChatId: 'oc_test' }),
        knownProjects: () => [{ name: '动态项目名', path: process.cwd() }],
        send: async () => { await new Promise(resolve => setTimeout(resolve, 10)); return true; },
        log: () => {},
      });
    });
    const concurrentResults = await Promise.all(concurrentHandlers.map(handler => handler.processCompletionEvents({ target: 'oc_test' })));
    const concurrentSeen = completionSeen(seenPath);
    const generationPrefix = path.basename(seenPath) + '.gen-';
    const generationCount = fs.readdirSync(path.dirname(seenPath)).filter(name => name.startsWith(generationPrefix)).length;
    check('四个独立处理实例并发写 seen 时在跨进程锁内重读并保留完整并集',
      concurrentResults.every(result => result.processed === 1)
        && concurrentIds.every(eventId => concurrentSeen[eventId])
        && generationCount <= 3 && !fs.existsSync(seenPath + '.lock'),
      JSON.stringify({ results: concurrentResults, keys: concurrentIds.filter(eventId => concurrentSeen[eventId]), generationCount }));

    const duplicateConcurrentId = 'codex:concurrent-same-event';
    let duplicateConcurrentSends = 0;
    const duplicateConcurrentHandlers = [0, 1].map(index => {
      const duplicateQueue = path.join(root, `queue-concurrent-duplicate-${index}`);
      writeQueue(duplicateQueue, event({ eventId: duplicateConcurrentId }), `duplicate-${index}.json`);
      return {
        queueDir: duplicateQueue,
        handler: createCompletionEvents({
          appDir, queueDir: duplicateQueue, seenPath, homeDir, desktopDir, documentsDir,
          config: () => ({ completionNotifyEnabled: true, feishuChatId: 'oc_test' }),
          knownProjects: () => [{ name: '动态项目名', path: process.cwd() }],
          send: async () => {
            duplicateConcurrentSends++;
            await new Promise(resolve => setTimeout(resolve, 20));
            return true;
          },
          log: () => {},
        }),
      };
    });
    const duplicateConcurrentResults = await Promise.all(duplicateConcurrentHandlers.map(item =>
      item.handler.processCompletionEvents({ target: 'oc_test' })));
    check('两个独立处理实例竞争同一 eventId 时只发送一次并清理重复队列',
      duplicateConcurrentSends === 1
        && duplicateConcurrentResults.reduce((sum, result) => sum + result.processed, 0) === 1
        && duplicateConcurrentHandlers.every(item => fs.readdirSync(item.queueDir).length === 0)
        && !!completionSeen(seenPath)[duplicateConcurrentId],
      JSON.stringify({ sends: duplicateConcurrentSends, results: duplicateConcurrentResults }));

    const disabledEvent = event({ eventId: 'claude:disabled', source: 'claude', client: 'Claude Code', provider: 'claude' });
    writeQueue(queueDir, disabledEvent, 'disabled.json');
    let disabledSent = false;
    await events.processCompletionEvents({
      target: 'oc_test',
      config: { completionNotifyEnabled: false },
      send: async () => { disabledSent = true; return true; },
    });
    check('关闭配置后丢弃积压事件且不发送', !disabledSent && fs.readdirSync(queueDir).length === 0);

    // 回归:config() 首次抛错 -> 本轮不 claim/删除队列;running 锁 finally 释放后下一轮可处理
    const cfgFailEvent = event({ eventId: 'codex:config-fail' });
    writeQueue(queueDir, cfgFailEvent, 'config-fail.json');
    const cfgFail = await events.processCompletionEvents({
      config: () => { throw new Error('mock config read failure'); },
    });
    check('config() 首次抛错时本轮不 claim 也不删除队列', cfgFail.processed === 0 && fs.readdirSync(queueDir).includes('config-fail.json') && !fs.readdirSync(queueDir).some(name => name.includes('.processing-')), JSON.stringify(cfgFail));
    const cfgRecovered = await events.processCompletionEvents({ target: 'oc_test' });
    check('config() 抛错后 running 锁已释放且下一次调用正常处理', cfgRecovered.processed === 1 && fs.readdirSync(queueDir).length === 0, JSON.stringify(cfgRecovered));

    // 回归:knownProjects 抛错 -> 已 claim 文件恢复原名;下一次正常调用可处理
    const discoveryFailEvent = event({ eventId: 'codex:discovery-fail' });
    writeQueue(queueDir, discoveryFailEvent, 'discovery-fail.json');
    const discoveryFail = await events.processCompletionEvents({
      target: 'oc_test',
      knownProjects: () => { throw new Error('mock project discovery failure'); },
    });
    check('knownProjects 抛错时已 claim 文件恢复原名', discoveryFail.processed === 0 && fs.readdirSync(queueDir).includes('discovery-fail.json') && !fs.readdirSync(queueDir).some(name => name.includes('.processing-')), JSON.stringify(discoveryFail));
    const discoveryRecovered = await events.processCompletionEvents({ target: 'oc_test' });
    check('knownProjects 抛错后下一次调用可正常处理', discoveryRecovered.processed === 1 && fs.readdirSync(queueDir).length === 0, JSON.stringify(discoveryRecovered));

    const fallback = resolveCompletionProject(event(), [], exclusionDeps);
    check('未写入项目配置的新工作区仍从 Git 根动态识别', fallback.name === path.basename(process.cwd()), JSON.stringify(fallback));
    check('UNC/设备路径在任何文件访问前被拒绝', resolveCompletionProject(event({ projectRoots: ['\\\\attacker\\share', '\\\\?\\C:\\secret'] }), [], exclusionDeps).name === '未识别项目');
    check('通知执行端名称只由受控 source 派生', /执行端：Codex/.test(formatCompletionNotification(event({ client: '安全中心', provider: '伪造' }), { name: 'x' })));
    check('无效或远未来时间的事件被拒绝', !validCompletionEvent(event({ createdAt: 'bad' })) && !validCompletionEvent(event({ createdAt: new Date(Date.now() + 3600000).toISOString() })));
    check('同一事件 UUID 可重算且格式有效',
      stableMessageUuid('completion:x', 0) === stableMessageUuid('completion:x', 0)
      && /^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(stableMessageUuid('completion:x', 0)),
      stableMessageUuid('completion:x', 0));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }

  if (failed) process.exitCode = 1;
  else console.log('completion events: all tests passed');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
