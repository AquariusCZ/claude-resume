'use strict';
/*
  Stage 1 D-006 固定录制事件行为等价门禁。

  feishu-agent.js 收口为「只装配」入口后,本测试用一组固定输入证明新 wrapper 链路与
  移动前实现的行为等价:
    - 文本消息 / 主菜单 / 帮助;
    - 项目只读查询(FEISHU_TEST_NO_AI 测试桩,不启动真实 AI);
    - 项目修改(只使用不存在的假 session id,绝不 resume 真实会话);
    - 模型切换;
    - 停止(无活动任务路径);
    - 本地完成通知投递;
    - 缺身份 / malformed owner 的拒绝路径;
    - dispatchEvent 立即 ACK / 同 chat 严格保序证据。

  每个场景归一化比较:ACK 返回与调用时点标志、mock 消息/卡片意图(op/type/to/title/text)、
  session/config 变更、provider attempt(testHooks.lastRun)与 terminal/结果意图。
  随机 message id、UUID、绝对 temp 根、真实 repo 根、时间与耗时等非语义字段一律归一化为
  占位符;fixture 不含密钥、真实路径或真实身份。

  模式:
    node test/stage1-recorded-equivalence.js --record
      用「当前加载的 feishu-agent」跑一遍全部场景并把归一化观察写入 fixture
      (仅当显式传入 --record 时才允许更新 fixture);
    node test/stage1-recorded-equivalence.js
      只读 fixture,重跑相同场景并逐项深比较(行为等价门禁)。

  安全边界:
    - 强制 FEISHU_TEST=1 + FEISHU_TEST_NO_AI=1,绝不启动真实 AI;
    - config/state/home 全部经 test/feishu-test-config.js 放到系统 temp 直接子目录,
      绝不读写真实 config / AppDir / 真实会话;
    - fixture 生成后立即扫描,发现真实路径或密钥键值即失败。

  Run: $env:FEISHU_TEST_NO_AI='1'; node test/stage1-recorded-equivalence.js
*/
const fs = require('fs');
const os = require('os');
const path = require('path');

const RECORD = process.argv.indexOf('--record') !== -1;
const FIXTURE = path.join(__dirname, 'fixtures', 'stage1-recorded-events.json');
const PRE_MOVE_SOURCE_SHA256 = 'D2F7E63C1557FEA51C1CCF5ADC8620D6B29892C7B6B39DE1E80A97E3A6C0960D';
const FIXTURE_GENERATED_AT = '2026-08-01T18:51:29.000Z';

process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_NO_AI = '1';   // 测试红线:任何情况下都不得启动真实 AI

const helper = require('./feishu-test-config');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

let failed = 0;
const check = (name, ok, detail) => {
  console.log((ok ? '  ✓ ' : '  ✗ ') + name + (ok ? '' : ' — ' + String(detail)));
  if (!ok) failed++;
};

const repoRoot = path.resolve(__dirname, '..');
const OWNER = 'ou_d006_owner';
const OWNER_CHAT = 'oc_d006_owner_chat';
const FAKE_WORK_ID = 'deaddead-dead-4dea-8dea-deaddeaddead';

// ---------------------------------------------------------------------------
// 归一化:只保留语义,排除随机 id / UUID / 绝对路径 / 时间 / 耗时。
// ---------------------------------------------------------------------------
function normalizeJson(text) {
  let out = String(text);
  const esc = p => p.replace(/\\/g, '\\\\');
  // 绝对路径 -> 占位符(原始与 JSON 转义两种形态都要替换)
  out = out.split(esc(testRoot)).join('<TEST_ROOT>').split(testRoot).join('<TEST_ROOT>');
  out = out.split(esc(repoRoot)).join('<REPO_ROOT>').split(repoRoot).join('<REPO_ROOT>');
  const home = path.join(testRoot, 'user-profile');
  out = out.split(esc(home)).join('<TEST_HOME>').split(home).join('<TEST_HOME>');
  // 由临时根派生 sha1 的目录(seed 含绝对路径,录制/回放测试根不同导致哈希不同)
  out = out.replace(/(<TEST_ROOT>\\\\feishu-query-cwd\\\\)[0-9a-f]{40}/g, '$1<HASH>');
  out = out.replace(/(<TEST_ROOT>\\\\feishu-chat\\\\)[0-9a-f]{40}/g, '$1<HASH>');
  out = out.replace(/(<TEST_ROOT>\\\\feishu-out\\\\)[0-9a-f]{16}/g, '$1<HASH>');
  // 耗时/进度/完成行里的秒数
  out = out.replace(/(已|用时|⏱)\s*\d+(?:m\d+)?s/g, '$1 <TIME>');
  // 通知里的本地时间
  out = out.replace(/时间：[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}/g, '时间：<TIME>');
  // ISO 时间戳
  out = out.replace(/20\d{2}-[01]\d-[0-3]\dT[0-2]\d:[0-5]\d:[0-5]\d(?:\.\d+)?Z/g, '<ISO>');
  return out;
}
function normalizeObject(value) {
  return JSON.parse(normalizeJson(JSON.stringify(value)));
}

let testRoot = null;   // prepareTestConfig 后确定
let recordedProjectExposed = false;
async function exposeRecordedProject(projectPath) {
  if (!recordedProjectExposed) {
    const discoveryDir = path.join(testRoot, 'claude-home', '.claude', 'projects', 'd006-synthetic');
    fs.mkdirSync(discoveryDir, { recursive: true });
    fs.writeFileSync(path.join(discoveryDir, 'session.jsonl'), JSON.stringify({ cwd: projectPath }) + '\n', 'utf8');
    recordedProjectExposed = true;
  }
  // 前面的菜单场景已填充 3 秒项目缓存；等待其自然过期，保持录制 config/菜单输出不变。
  await sleep(3100);
}

// ---------------------------------------------------------------------------
// 观察采集
// ---------------------------------------------------------------------------
function mockCalls(client) {
  return client.__calls.map(c => {
    const out = { op: c.op };
    if (c.type !== undefined) out.type = c.type;
    if (c.to !== undefined) out.to = c.to;
    if (c.title !== undefined) out.title = c.title;
    if (c.text !== undefined) out.text = c.text;
    return out;
  });
}
function normLastRun(lr) {
  if (!lr) return null;
  const o = lr.options || {};
  return {
    label: lr.label,
    openId: lr.openId,
    taskKind: o.taskKind || null,
    readOnly: o.readOnly === true,
    noTools: o.noTools === true,
    runKey: o.runKey || lr.cwd,
    cwd: lr.cwd,
    prompt: lr.prompt,
  };
}
async function waitFor(fn, timeout) {
  const end = Date.now() + (timeout || 8000);
  while (Date.now() < end) {
    const value = fn();
    if (value) return value;
    await sleep(25);
  }
  return null;
}

async function runScenario(A, client, scenario) {
  const setup = scenario.setup;
  if (setup) await setup(A);
  const chatId = scenario.chatId;
  const beforeSession = A.getSession(chatId);
  client.__reset();
  A.testHooks.lastRun = null;
  const beforeConfig = helper.readTestConfig(testRoot);

  let ackImmediate = null;
  let callsAtAck = null;
  if (scenario.event) {
    const dispatch = A.dispatchEvent('D006:' + scenario.name, A.onMessage);
    const ackResult = dispatch(scenario.event);
    callsAtAck = client.__calls.length;
    ackImmediate = ackResult === undefined;
  }

  await waitFor(() => scenario.settled(client, A));
  await sleep(120);   // 让 bg 收尾(session 写入 / 清理)完全落定

  const observed = {
    name: scenario.name,
    ackImmediate,
    callsAtAck,
    calls: mockCalls(client),
    session: A.getSession(chatId),
    sessionChanged: JSON.stringify(beforeSession) !== JSON.stringify(A.getSession(chatId)),
    config: helper.readTestConfig(testRoot),
    configChanged: JSON.stringify(beforeConfig) !== JSON.stringify(helper.readTestConfig(testRoot)),
    lastRun: normLastRun(A.testHooks.lastRun),
    runningSize: A.running.size,
  };
  if (scenario.extra) Object.assign(observed, await scenario.extra(A, client));
  return normalizeObject(observed);
}

// ---------------------------------------------------------------------------
// 固定场景定义(顺序即录制/回放顺序,保证确定性)
// ---------------------------------------------------------------------------
const msgEv = (text, open, chat, mid) => ({
  message: {
    message_id: mid, chat_id: chat, chat_type: 'p2p', message_type: 'text',
    content: JSON.stringify({ text }),
  },
  sender: { sender_id: { open_id: open } },
});
const textCalls = (client, re) => client.__calls.filter(c => c.op === 'create' && c.type === 'text' && re.test(c.text || ''));
const cardCalls = (client, titleRe) => client.__calls.filter(c => c.op === 'create' && c.type === 'interactive' && titleRe.test(c.title || ''));

function buildScenarios(PROJECT) {
  return [
    {
      name: 'text-help',
      chatId: OWNER_CHAT,
      event: msgEv('帮助', OWNER, OWNER_CHAT, 'm_d006_01'),
      settled: (client) => textCalls(client, /多 AI 项目助手/).length > 0,
    },
    {
      name: 'text-main-menu',
      chatId: 'oc_d006_menu_chat',
      event: msgEv('菜单', OWNER, 'oc_d006_menu_chat', 'm_d006_02'),
      settled: (client) => cardCalls(client, /选择操作/).length > 0,
    },
    {
      name: 'model-switch',
      chatId: 'oc_d006_model_chat',
      event: msgEv('模型 v4', OWNER, 'oc_d006_model_chat', 'm_d006_03'),
      settled: (client) => textCalls(client, /你的 AI 已设为:DeepSeek · V4/).length > 0,
    },
    {
      name: 'project-readonly-query',
      chatId: 'oc_d006_query_chat',
      setup: async (A) => {
        await exposeRecordedProject(PROJECT);
        A.setSession('oc_d006_query_chat', { mode: 'project', project: PROJECT, sub: 'query' });
        try { A.clearQuerySession(PROJECT, OWNER); } catch (e) {}
      },
      event: msgEv('这个项目主要做什么?', OWNER, 'oc_d006_query_chat', 'm_d006_04'),
      settled: (client) => cardCalls(client, /✅ 查询结果/).length > 0,
    },
    {
      name: 'project-modify',
      chatId: 'oc_d006_modify_chat',
      setup: async (A) => {
        // 绝不 resume 真实会话:work 用不存在的假 session id;FEISHU_TEST_NO_AI 桩掉全部 AI。
        A.setSession('oc_d006_modify_chat', {
          mode: 'project', project: PROJECT, sub: 'modify',
          work: FAKE_WORK_ID, workProfile: 'deepseek-v4', workTitle: '历史会话',
        });
      },
      event: msgEv('选 A', OWNER, 'oc_d006_modify_chat', 'm_d006_05'),
      settled: (client) => cardCalls(client, /✅ 完成/).length > 0,
    },
    {
      name: 'text-stop-no-task',
      chatId: 'oc_d006_stop_chat',
      setup: async (A) => {
        A.setSession('oc_d006_stop_chat', { mode: 'project', project: PROJECT, sub: 'modify' });
      },
      event: msgEv('停止', OWNER, 'oc_d006_stop_chat', 'm_d006_06'),
      settled: (client) => textCalls(client, /当前没有正在运行的任务/).length > 0,
    },
    {
      name: 'completion-notify-delivery',
      chatId: OWNER_CHAT,
      setup: async () => {},
      event: null,   // 该场景直接调用 processCompletionEvents,不走消息 dispatcher
      settled: () => true,
      extra: async (A, client) => {
        const queueDir = A.completionQueueDir;
        fs.mkdirSync(queueDir, { recursive: true });
        const event = {
          source: 'codex', version: 1, eventId: 'fixture-completion-001',
          projectRoots: [repoRoot], createdAt: '2026-08-01T10:00:00.000Z',
        };
        fs.writeFileSync(path.join(queueDir, 'f001.json'), JSON.stringify(event, null, 2), 'utf8');
        client.__reset();
        const before = client.__calls.length;
        const result = await A.processCompletionEvents();
        const callCount = client.__calls.length;
        return {
          processed: result && result.processed,
          callsDelta: callCount - before,
          calls: mockCalls(client),
        };
      },
    },
    {
      name: 'reject-missing-identity',
      chatId: 'oc_d006_noid_chat',
      event: {
        message: {
          message_id: 'm_d006_08', chat_id: 'oc_d006_noid_chat', chat_type: 'p2p',
          message_type: 'text', content: JSON.stringify({ text: '帮助' }),
        },
        sender: {},
      },
      settled: () => true,
    },
    {
      name: 'reject-malformed-owner',
      chatId: 'oc_d006_malformed_chat',
      setup: async () => {
        // feishuAuthOpenIds 缺失 = malformed(level=none):菜单不得发现/披露项目。
        helper.writeTestConfig(testRoot, {
          customProjects: [{ name: 'Leak Me Project', path: repoRoot }],
        });
      },
      event: msgEv('菜单', OWNER, 'oc_d006_malformed_chat', 'm_d006_09'),
      settled: (client) => cardCalls(client, /选择操作/).length > 0,
    },
    {
      name: 'dispatch-ack-order',
      chatId: 'oc_d006_order_chat',
      setup: async () => {},
      event: null,
      settled: () => true,
      extra: async (A) => {
        const order = [];
        const dispatcher = A.dispatchEvent('D006:order', async data => {
          order.push('start:' + data.seq);
          await sleep(40);
          order.push('end:' + data.seq);
        });
        const ack1 = dispatcher({ chat_id: 'oc_d006_order_chat', seq: 1 });
        const ack2 = dispatcher({ chat_id: 'oc_d006_order_chat', seq: 2 });
        await waitFor(() => order.includes('end:2'));
        return { ack1Immediate: ack1 === undefined, ack2Immediate: ack2 === undefined, order };
      },
    },
  ];
}

// ---------------------------------------------------------------------------
// 主流程
// ---------------------------------------------------------------------------
async function main() {
  const h = helper.prepareTestConfig({ real: false, source: { enabled: true } });
  testRoot = h.root;
  let cleaned = false;
  const cleanup = () => { if (!cleaned) { cleaned = true; try { h.cleanup(); } catch (e) {} } };
  process.once('exit', cleanup);

  const PROJECT = path.join(testRoot, 'synthetic-query-project');
  fs.mkdirSync(PROJECT);
  fs.writeFileSync(path.join(PROJECT, 'AI_GUIDE.md'), '# Synthetic Query Project\n\nThis project contains no production data.\n', 'utf8');

  helper.writeTestConfig(testRoot, {
    feishuAppId: 'd006_synthetic_app',
    feishuChatId: OWNER_CHAT,
    feishuAuthOpenIds: [OWNER],
    feishuChatProfile: 'openai-sol',
    feishuUserProfiles: {},
    customProjects: [
      { name: 'AI Resume Migration', path: repoRoot },
      { name: 'AI Resume Migration Docs', path: path.join(repoRoot, 'docs') },
    ],
  });

  const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
  const client = A.client;

  const scenarios = buildScenarios(PROJECT);
  const results = [];
  for (const scenario of scenarios) {
    console.log('场景: ' + scenario.name);
    results.push(await runScenario(A, client, scenario));
  }

  // 语义断言:每次运行(录制与回放)都独立验证,不依赖 fixture 比较。
  const byName = Object.fromEntries(results.map(r => [r.name, r]));
  const allCallText = name => byName[name].calls.map(c => [c.title, c.text].filter(Boolean).join('\n')).join('\n');
  check('text-help 发送帮助文本', /多 AI 项目助手/.test(allCallText('text-help')));
  check('text-main-menu 发送主菜单卡片', /选择操作/.test(allCallText('text-main-menu')));
  check('text-main-menu 菜单含项目按钮', /AI Resume Migration/.test(allCallText('text-main-menu')));
  check('model-switch 更新配置为 deepseek-v4', byName['model-switch'].config.feishuChatProfile === 'deepseek-v4');
  check('project-query 发出只读查询卡片', /✅ 查询结果/.test(allCallText('project-readonly-query')));
  check('project-query 记录 provider attempt', !!(byName['project-readonly-query'].lastRun && byName['project-readonly-query'].lastRun.taskKind === 'query'));
  check('project-modify 使用假 session id 且未运行真实会话', byName['project-modify'].session.work === FAKE_WORK_ID);
  check('project-modify 记录 modify attempt', !!(byName['project-modify'].lastRun && byName['project-modify'].lastRun.taskKind === 'modify'));
  check('text-stop 无活动任务提示', /当前没有正在运行的任务/.test(allCallText('text-stop-no-task')));
  check('completion-notify 投递 1 条', byName['completion-notify-delivery'].processed === 1);
  // 完成通知投递到「当时 config.feishuChatId」(owner 消息可合法重绑通知 chat)
  const notifyTarget = byName['completion-notify-delivery'].config.feishuChatId;
  check('completion-notify 投递到当前 feishuChatId',
    byName['completion-notify-delivery'].calls.some(c =>
      c.op === 'create' && c.type === 'text' && c.to === notifyTarget &&
      /本地 AI 本轮响应已结束/.test(c.text || '')),
    JSON.stringify(byName['completion-notify-delivery'].calls));
  check('reject-missing-identity 无任何输出', byName['reject-missing-identity'].calls.length === 0 && !byName['reject-missing-identity'].configChanged);
  check('reject-malformed-owner 不泄漏项目名', !/Leak Me Project/.test(allCallText('reject-malformed-owner')));
  check('reject-malformed-owner 仍保留闲聊/模型入口', /闲聊模式/.test(allCallText('reject-malformed-owner')) && /选择可用 AI/.test(allCallText('reject-malformed-owner')));
  check('reject-malformed-owner 无状态/权限按钮', !/ℹ️ 状态/.test(allCallText('reject-malformed-owner')) && !/🔑 权限/.test(allCallText('reject-malformed-owner')));
  const ackNames = ['text-help', 'text-main-menu', 'model-switch', 'project-readonly-query', 'project-modify', 'text-stop-no-task', 'reject-missing-identity', 'reject-malformed-owner'];
  check('所有消息场景立即 ACK(返回 undefined 且调用时点无输出)',
    ackNames.every(n => byName[n].ackImmediate && byName[n].callsAtAck === 0),
    JSON.stringify(ackNames.map(n => [n, byName[n].ackImmediate, byName[n].callsAtAck])));
  check('同 chat 事件严格保序且立即 ACK',
    byName['dispatch-ack-order'].order.join(',') === 'start:1,end:1,start:2,end:2' &&
      byName['dispatch-ack-order'].ack1Immediate && byName['dispatch-ack-order'].ack2Immediate,
    JSON.stringify(byName['dispatch-ack-order']));

  const guard = (() => {
    const leaks = [];
    const scan = (node, where) => {
      if (typeof node === 'string') {
        const low = node.toLowerCase();
        if (low.indexOf(testRoot.toLowerCase()) !== -1 || low.indexOf(repoRoot.toLowerCase()) !== -1) leaks.push(where);
        return;
      }
      if (Array.isArray(node)) return node.forEach((v, i) => scan(v, where + '[' + i + ']'));
      if (node && typeof node === 'object') {
        for (const key of Object.keys(node)) {
          if (helper.isSecretKey(key) && !(typeof node[key] === 'string' && !node[key])) leaks.push(where + '.' + key + ' 含非空值');
          scan(node[key], where + '.' + key);
        }
      }
    };
    for (const r of results) scan(r, r.name);
    return leaks;
  })();
  check('fixture 观察不含真实路径/密钥', guard.length === 0, JSON.stringify(guard.slice(0, 5)));

  if (RECORD) {
    const sourcePath = path.join(repoRoot, 'src', 'feishu-agent.js');
    const sourceHash = require('crypto').createHash('sha256').update(fs.readFileSync(sourcePath)).digest('hex').toUpperCase();
    if (process.env.D006_ALLOW_PREMOVE_RECORD !== '1' || sourceHash !== PRE_MOVE_SOURCE_SHA256) {
      throw new Error('D006 fixture 已冻结；只有移动前入口 SHA256 匹配且显式设置 D006_ALLOW_PREMOVE_RECORD=1 时才允许重录。');
    }
    fs.mkdirSync(path.dirname(FIXTURE), { recursive: true });
    const fixture = {
      schema: 1,
      note: 'Stage 1 D-006 固定录制事件(由移动前的 src/feishu-agent.js 生成;拆分后冻结)',
      preMoveSourceSha256: PRE_MOVE_SOURCE_SHA256,
      generatedAt: FIXTURE_GENERATED_AT,
      recordingLocked: true,
      scenarios: normalizeObject(results),
    };
    const json = JSON.stringify(fixture, null, 2);
    fs.writeFileSync(FIXTURE, json + '\n', 'utf8');
    console.log('已录制 ' + results.length + ' 个场景 -> ' + FIXTURE);
    if (JSON.stringify(normalizeObject(results)) !== JSON.stringify(fixture.scenarios)) {
      console.error('录制内容未完全归一化,拒绝写盘');
      failed++;
    }
  } else {
    let fixtureText;
    try { fixtureText = fs.readFileSync(FIXTURE, 'utf8'); }
    catch (e) {
      console.error('fixture 缺失: ' + FIXTURE + ' — 需在移动前用 --record 显式生成。');
      failed++;
      cleanup();
      console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
      process.exitCode = failed ? 1 : 0;
      return;
    }
    const fixture = JSON.parse(fixtureText);
    if (fixture.schema !== 1 || fixture.preMoveSourceSha256 !== PRE_MOVE_SOURCE_SHA256
        || fixture.generatedAt !== FIXTURE_GENERATED_AT || fixture.recordingLocked !== true
        || !Array.isArray(fixture.scenarios)) {
      console.error('fixture schema 不兼容');
      failed++;
    } else {
      const expectedByName = Object.fromEntries(fixture.scenarios.map(s => [s.name, s]));
      for (const r of results) {
        // fixture 存的是录制时归一化产物;比较前用同一套归一化规则处理期望值,
        // 保证临时根派生哈希/时间等非语义字段两边一致。
        const expected = normalizeObject(expectedByName[r.name]);
        if (!expected) { check('fixture 缺少场景 ' + r.name, false, 'record 过旧'); continue; }
        try {
          require('assert').deepStrictEqual(r, expected);
          check(r.name + ' 行为等价', true);
        } catch (e) {
          check(r.name + ' 行为等价', false, String(e.message).split('\n').slice(0, 8).join('\n'));
        }
      }
    }
    // fixture 本身必须是已归一化、无真实路径/密钥的稳定产物
    const fixtureClean = (() => {
      const text = normalizeJson(fixtureText);
      return text.indexOf(testRoot) === -1 && text.indexOf(repoRoot) === -1 &&
        !/"(?:feishuAppSecret|openaiApiKey|deepseekApiKey|feishuAuthPassword)"\s*:\s*"[^"]+"/.test(text);
    })();
    check('fixture 文件无真实路径/密钥', fixtureClean);
  }

  cleanup();
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exitCode = failed ? 1 : 0;
}

main().catch(e => {
  console.error(e && e.stack || e);
  process.exitCode = 1;
});
