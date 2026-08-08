'use strict';
/*
  S1-C1 / D-004 契约测试:FEISHU_TEST 下 config/lock/state/Claude home 全部隔离在
  系统 temp 的进程独占测试根;真实 %LOCALAPPDATA%\ClaudeResume\config.json 只做只读
  SHA256 前后比对,绝不写入/备份/恢复/打印;任何测试输出不得包含真实配置内容。
  fail-closed 分支(missing/real/outside/malformed/junction)全部用真实子进程验证,
  仅 spawnSync.error.code === EPERM 时允许明确跳过;24h 陈旧目录自动清扫由
  prepareTestConfig 正常创建流程触发。
  运行:node test/config-isolation.js
*/
const assert = require('assert');
const cp = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const helper = require('./feishu-test-config');
const AGENT = path.join(__dirname, '..', 'src', 'feishu-agent.js');
const tmp = path.resolve(os.tmpdir());

const hasReal = (() => { try { fs.lstatSync(helper.realConfigPath()); return true; } catch (e) { return false; } })();
const realSha = hasReal ? helper.realConfigSha256() : null;
const createdRoots = [];
let failed = 0;
let epermSkipped = 0;
const check = (name, ok, detail) => {
  console.log((ok ? '  ✓ ' : '  ✗ ') + name + (ok ? '' : ' — ' + String(detail)));
  if (!ok) failed++;
};

// 在真实子进程中构造 fixture(目录名用子进程自身 PID)并加载 agent,验证 [S1-C1] fail-closed。
// 子进程内先 prepareTestConfig 自建合法测试根,再按分支改写 env/config,保证校验走到
// 目标分支而不是命名/归属检查;只有 spawnSync.error.code === EPERM 才允许跳过。
function spawnFailChild(label, branch, extraEnv = {}) {
  const script = [
    "'use strict';",
    "const fs = require('fs');",
    "const os = require('os');",
    "const path = require('path');",
    'const helper = require(' + JSON.stringify(require.resolve('./feishu-test-config')) + ');',
    'const AGENT = ' + JSON.stringify(AGENT) + ';',
    'const branch = process.env.CI_BRANCH;',
    'const realConfig = process.env.CI_REAL_CONFIG;',
    'const canary = process.env.CI_CANARY;',
    'const removable = [];',
    "const h = helper.prepareTestConfig({ real: false, source: { enabled: true, armCycleId: 's1c1-child' } });",
    'try {',
    "  if (branch === 'env-cfg-missing') delete process.env.FEISHU_TEST_CONFIG_PATH;",
    "  else if (branch === 'env-state-missing') delete process.env.FEISHU_TEST_STATE_DIR;",
    "  else if (branch === 'cfg-missing') fs.unlinkSync(path.join(h.root, 'config.json'));",
    "  else if (branch === 'cfg-malformed') fs.writeFileSync(path.join(h.root, 'config.json'), '{broken-json');",
    '  else if (branch === \'cfg-real\') process.env.FEISHU_TEST_CONFIG_PATH = realConfig;',
    "  else if (branch === 'cfg-outside') { const d = path.join(os.tmpdir(), 'claude-resume-cfg-outside-' + process.pid); fs.mkdirSync(d, { recursive: true }); fs.writeFileSync(path.join(d, 'config.json'), JSON.stringify({ enabled: true })); removable.push(d); process.env.FEISHU_TEST_CONFIG_PATH = path.join(d, 'config.json'); }",
    "  else if (branch === 'state-real') { process.env.FEISHU_TEST_STATE_DIR = path.dirname(realConfig); process.env.FEISHU_TEST_CONFIG_PATH = realConfig; }",
    "  else if (branch === 'state-outside') { const d = path.join(os.tmpdir(), 'claude-resume-outside-' + process.pid); fs.mkdirSync(d, { recursive: true }); removable.push(d); process.env.FEISHU_TEST_STATE_DIR = path.join(d, 'state'); process.env.FEISHU_TEST_CONFIG_PATH = path.join(d, 'state', 'config.json'); }",
    "  else if (branch === 'state-junction') { helper.removeFixtureSafe(h.root); fs.symlinkSync(canary, h.root, 'junction'); }",
    "  else if (branch === 'cfg-junction') { fs.unlinkSync(path.join(h.root, 'config.json')); fs.symlinkSync(canary, path.join(h.root, 'config.json'), 'junction'); }",
    '  require(AGENT);',
    "  console.log('UNEXPECTED-LOAD-OK');",
    "} catch (e) { console.error('AGENT-FAIL:' + (e && e.message || e)); process.exitCode = 7; }",
    'finally {',
    "  try { if (branch === 'state-junction') fs.unlinkSync(h.root); } catch (e) {}",
    "  try { if (branch === 'cfg-junction') fs.unlinkSync(path.join(h.root, 'config.json')); } catch (e) {}",
    '  for (const d of removable) { try { helper.removeFixtureSafe(d); } catch (e) {} }',
    '  try { h.cleanup(); } catch (e) {}',
    '}',
  ].join('\n');
  const env = Object.assign({}, process.env, {
    FEISHU_TEST: '1', FEISHU_TEST_NO_AI: '1',
    CI_BRANCH: branch, CI_REAL_CONFIG: helper.realConfigPath(), CI_CANARY: extraEnv.CI_CANARY || '',
  });
  const res = cp.spawnSync(process.execPath, ['-e', script], { env, encoding: 'utf8', timeout: 30000, windowsHide: true });
  if (res.error) {
    if (res.error && res.error.code === 'EPERM') {
      console.log(`   ⚠ ${label}:子进程创建被 EPERM 拒绝,按契约明确跳过该分支(${res.error.message})`);
      epermSkipped++;
      return;
    }
    throw res.error;
  }
  const stderr = String(res.stderr || '');
  const stdout = String(res.stdout || '');
  check(label + ' fail-closed(真实子进程退出非零 + [S1-C1])',
    res.status !== 0 && /\[S1-C1\]/.test(stderr) && !/UNEXPECTED-LOAD-OK/.test(stdout),
    `status=${res.status} stderr=${stderr.slice(0, 400)}`);
}

function main() {
  // ---- 0) 24h 陈旧目录自动清扫:由 prepareTestConfig 正常创建流程触发(先于其它 prepare) ----
  {
    const oldTime = new Date(Date.now() - 25 * 3600 * 1000);
    const stalePid = 2147483647;   // 合法但实际不可能活跃的 PID
    const make = (name, markerPid, markerNonce, extra) => {
      const dir = path.join(tmp, name);
      fs.mkdirSync(dir);
      fs.writeFileSync(path.join(dir, '.feishu-test-owner'), JSON.stringify({ pid: markerPid, nonce: markerNonce || 'sweepnonce012345', createdAtUtc: new Date().toISOString() }));
      fs.writeFileSync(path.join(dir, 'state.json'), '{}');
      fs.utimesSync(dir, oldTime, oldTime);
      fs.utimesSync(path.join(dir, 'state.json'), oldTime, oldTime);
      if (extra) extra(dir);
      return dir;
    };
    const positive = make('claude-resume-test-' + stalePid + '-pos', stalePid);
    const negPidMismatch = make('claude-resume-test-' + stalePid + '-negPid', stalePid + 1);
    const negFresh = make('claude-resume-test-' + stalePid + '-negFresh', stalePid);
    fs.utimesSync(negFresh, new Date(), new Date());
    const negBadName = make('claude-resume-unrelated-' + stalePid + '-negName', stalePid);
    const nestedParent = path.join(tmp, 'claude-resume-nested-' + stalePid);
    fs.mkdirSync(nestedParent);
    const negNested = make('claude-resume-test-' + stalePid + '-negNested', stalePid);
    fs.renameSync(negNested, path.join(nestedParent, path.basename(negNested)));
    const negActive = make('claude-resume-test-' + process.pid + '-negActive', process.pid);
    let negJunction = null, junctionTarget = null;
    try {
      junctionTarget = fs.mkdtempSync(path.join(tmp, 'claude-resume-jt-'));
      negJunction = make('claude-resume-test-' + stalePid + '-negJunction', stalePid, null, dir => {
        fs.symlinkSync(junctionTarget, path.join(dir, 'link'), 'junction');
      });
    } catch (e) { console.log('   ⚠ 本机不允许创建 junction,跳过 junction 清扫反例: ' + e.message); }
    const sweepRoot = helper.prepareTestConfig({ real: false, source: { enabled: true } });
    createdRoots.push(sweepRoot);
    check('prepareTestConfig 自动清扫陈旧目录(正例删除)', !fs.existsSync(positive));
    check('marker PID 与目录名 PID 不一致不清扫', fs.existsSync(negPidMismatch));
    check('24h 以内不清扫', fs.existsSync(negFresh));
    check('命名不合法不清扫', fs.existsSync(negBadName));
    check('非系统 temp 直接子目录不清扫', fs.existsSync(path.join(nestedParent, path.basename(negNested))));
    check('活跃 PID 不清扫', fs.existsSync(negActive));
    if (negJunction) check('树内含 junction 不清扫', fs.existsSync(negJunction));
    check('每进程最多清扫一次', helper.sweepStaleTestDirs().ran === false);
    for (const p of [positive, negPidMismatch, negFresh, negBadName, nestedParent, negActive, negJunction, junctionTarget]) {
      if (p) helper.removeFixtureSafe(p);
    }
  }

  // ---- 1) helper 根创建 / marker / 严格 cleanup 反例 ----
  {
    const h = helper.createTestRoot(); createdRoots.push(h);
    check('测试根是系统 temp 直接子目录', path.dirname(h.root).toLowerCase() === tmp.toLowerCase());
    check('测试根名称含当前 PID', h.root.includes(String(process.pid)));
    check('marker 含 PID+nonce', h.marker.pid === process.pid && typeof h.marker.nonce === 'string' && h.marker.nonce.length >= 8);
    fs.writeFileSync(h.markerPath, JSON.stringify(Object.assign({}, h.marker, { nonce: h.marker.nonce + '-tampered' })));
    const r1 = h.cleanup();
    check('marker nonce 不匹配拒绝清理', r1.removed === false && fs.existsSync(h.root), r1.reason);
    fs.writeFileSync(h.markerPath, JSON.stringify(h.marker));
    fs.writeFileSync(h.markerPath, JSON.stringify(Object.assign({}, h.marker, { pid: process.pid + 1 })));
    const r2 = h.cleanup();
    check('marker PID 不匹配拒绝清理', r2.removed === false && fs.existsSync(h.root), r2.reason);
    fs.writeFileSync(h.markerPath, JSON.stringify(h.marker));
    const r3 = h.cleanup();
    check('marker 恢复后清理成功', r3.removed === true && !fs.existsSync(h.root), r3.reason);
    createdRoots.pop();

    const reparseRoot = helper.createTestRoot(); createdRoots.push(reparseRoot);
    const canary = fs.mkdtempSync(path.join(tmp, 'claude-resume-cleanup-canary-'));
    fs.writeFileSync(path.join(canary, 'canary.txt'), 'unchanged');
    let linked = false;
    try {
      fs.symlinkSync(canary, path.join(reparseRoot.root, 'link'), 'junction');
      linked = true;
      const blocked = reparseRoot.cleanup();
      check('owner cleanup 遇到 junction 拒绝删除且不修改树', blocked.removed === false && fs.existsSync(path.join(reparseRoot.root, 'link')), blocked.reason);
      check('owner cleanup 不穿透 junction canary', fs.readFileSync(path.join(canary, 'canary.txt'), 'utf8') === 'unchanged');
    } catch (e) {
      console.log('   ⚠ 本机不允许创建 junction,跳过 owner cleanup junction 反例: ' + e.message);
    } finally {
      if (linked) fs.unlinkSync(path.join(reparseRoot.root, 'link'));
      const cleaned = reparseRoot.cleanup();
      check('移除受控 junction 后 owner cleanup 成功', cleaned.removed === true && !fs.existsSync(reparseRoot.root), cleaned.reason);
      createdRoots.pop();
      helper.removeFixtureSafe(canary);
    }
  }

  // ---- 2) 递归脱敏(合成样例,不涉及真实密钥) ----
  {
    const src = {
      feishuAppSecret: 'x', openaiApiKey: 'sk-x', deepseekApiKey: 'ds-x', Authorization: 'Bearer x',
      aiProxy: 'http://user:password@proxy.invalid:7890/path?token=proxy-token',
      nested: { apiKey: 'k', webhookUrl: 'https://x', credential: { user: 'u', pass: 'p' }, ai_proxy: 'https://proxy.invalid/?key=nested-token' },
      arr: [{ token: 't' }], safe: { name: 'ok' },
    };
    const b = helper.buildTestBaseline({ real: false, source: src });
    check('顶层凭据键被清空', b.config.feishuAppSecret === '' && b.config.openaiApiKey === '' && b.config.deepseekApiKey === '' && b.config.Authorization === '');
    check('嵌套凭据键递归清空', b.config.nested.apiKey === '' && b.config.nested.webhookUrl === '' && typeof b.config.nested.credential === 'object');
    check('aiProxy 顶层与嵌套字段一律清空，不把认证代理复制到临时 JSON', b.config.aiProxy === '' && b.config.nested.ai_proxy === '');
    check('数组内凭据清空、非凭据保留', b.config.arr[0].token === '' && b.config.safe.name === 'ok');
  }

  // ---- 3) 真实 config 脱敏基线 + 主进程加载 agent + updateConfig 只改临时 config ----
  const agentHandle = helper.prepareTestConfig({
    real: hasReal,
    source: hasReal ? null : { enabled: true, armCycleId: 's1c1-synthetic', feishuAuthOpenIds: ['ou_owner'], feishuChatProfile: 'openai-sol', feishuChatId: 'oc_s1c1' },
  });
  createdRoots.push(agentHandle);
  if (hasReal) {
    check('真实 config 脱敏基线 SHA 与只读快照一致', agentHandle.sha256 === realSha);
    check('真实 config 脱敏后 openaiApiKey/deepseekApiKey 为空', agentHandle.config.openaiApiKey === '' && agentHandle.config.deepseekApiKey === '');
  }
  process.env.FEISHU_TEST = '1';
  process.env.FEISHU_TEST_NO_AI = '1';
  const A = require(AGENT);
  check('agent 测试根导出正确', path.resolve(A.testRoot) === path.resolve(agentHandle.root));
  check('config/lock/state/Claude 路径均在测试根',
    [A.testConfigPath, A.testConfigLockPath, A.claudeProjectsDir, A.completionQueueDir, A.completionSeenPath, A.childRegistryPath]
      .every(p => String(p).startsWith(agentHandle.root + path.sep)));
  check('测试 config.json 位于 STATE_DIR', A.testConfigPath === path.join(agentHandle.root, 'config.json'));
  check('测试写锁位于 STATE_DIR', A.testConfigLockPath === path.join(agentHandle.root, 'config.json.write.lock'));
  check('Claude projects 重定向到测试根', A.claudeProjectsDir === path.join(agentHandle.root, 'claude-home', '.claude', 'projects'));
  check('未从真实 AppDir 复制 sessions/userchats', !fs.existsSync(path.join(agentHandle.root, 'feishu-sessions.json')) && !fs.existsSync(path.join(agentHandle.root, 'feishu-userchats.json')));
  A.setSession('oc_s1c1_chat', { mode: 'idle' });
  check('mutable state 写入落在测试根', fs.existsSync(path.join(agentHandle.root, 'feishu-sessions.json')));
  A.updateConfig(cfg => { cfg.s1c1Canary = 'temp-only'; return true; });
  const tempCfg = JSON.parse(fs.readFileSync(A.testConfigPath, 'utf8'));
  check('updateConfig 只改临时 config', tempCfg.s1c1Canary === 'temp-only' && tempCfg.armCycleId === (agentHandle.config.armCycleId || 's1c1-synthetic'));
  if (hasReal) check('真实 config SHA 在 updateConfig 后不变', helper.realConfigSha256() === realSha);

  // ---- 4) fail-closed:缺失 env / 缺失 / 损坏 / 真实目录 / STATE_DIR 外(真实子进程) ----
  {
    const h1 = helper.prepareTestConfig({ real: false, source: { enabled: true } });
    createdRoots.push(h1);
    spawnFailChild('FEISHU_TEST_CONFIG_PATH 缺失', 'env-cfg-missing');
    spawnFailChild('FEISHU_TEST_STATE_DIR 缺失', 'env-state-missing');
    spawnFailChild('config.json 缺失', 'cfg-missing');
    spawnFailChild('config.json 损坏', 'cfg-malformed');
    spawnFailChild('FEISHU_TEST_CONFIG_PATH 显式指向真实 config', 'cfg-real');
    spawnFailChild('config 位于 STATE_DIR 外', 'cfg-outside');
    spawnFailChild('STATE_DIR 指向真实 AppDir', 'state-real');
    spawnFailChild('STATE_DIR 在系统 temp 外', 'state-outside');
  }

  // ---- 5) symlink/junction 反例:外部 canary 不变 ----
  {
    const canary = fs.mkdtempSync(path.join(tmp, 'claude-resume-canary-'));
    fs.writeFileSync(path.join(canary, 'canary.txt'), 'unchanged');
    spawnFailChild('STATE_DIR 为 junction', 'state-junction', { CI_CANARY: canary });
    check('junction 目标 canary 不变(根)', fs.readFileSync(path.join(canary, 'canary.txt'), 'utf8') === 'unchanged');
    spawnFailChild('config.json 为 junction', 'cfg-junction', { CI_CANARY: canary });
    check('junction 目标 canary 不变(config)', fs.readFileSync(path.join(canary, 'canary.txt'), 'utf8') === 'unchanged');
    helper.removeFixtureSafe(canary);
  }

  // ---- 6) provider 密钥仅环境注入,不入临时 JSON;cleanup 恢复环境与真实 config SHA ----
  if (hasReal) {
    const beforeOpenai = process.env.CLAUDE_RESUME_OPENAI_API_KEY;
    const beforeDeepseek = process.env.DEEPSEEK_API_KEY;
    const beforeEnv = {
      state: process.env.FEISHU_TEST_STATE_DIR,
      cfg: process.env.FEISHU_TEST_CONFIG_PATH,
      user: process.env.USERPROFILE,
      claude: process.env.CLAUDE_CONFIG_DIR,
      codex: process.env.CODEX_HOME,
      local: process.env.LOCALAPPDATA,
    };
    const h = helper.prepareTestConfig({ real: true, keepSecrets: true });
    createdRoots.push(h);
    const openaiVal = process.env.CLAUDE_RESUME_OPENAI_API_KEY;
    const deepseekVal = process.env.DEEPSEEK_API_KEY;
    const injected = [openaiVal, deepseekVal].filter(v => typeof v === 'string' && v.length > 0);
    check('keepSecrets 注入环境变量', injected.length >= 1);
    const raw = fs.readFileSync(h.configPath, 'utf8');
    check('临时 JSON 不含注入密钥值', injected.every(v => !raw.includes(v)));
    const tempCfg = JSON.parse(raw);
    check('临时 JSON 密钥字段为空', tempCfg.openaiApiKey === '' && tempCfg.deepseekApiKey === '');
    const res = h.cleanup();
    createdRoots.pop();
    check('cleanup 恢复原环境(密钥 + FEISHU_TEST_*/USERPROFILE/CLAUDE_CONFIG_DIR/CODEX_HOME/LOCALAPPDATA)',
      process.env.CLAUDE_RESUME_OPENAI_API_KEY === beforeOpenai && process.env.DEEPSEEK_API_KEY === beforeDeepseek &&
      process.env.FEISHU_TEST_STATE_DIR === beforeEnv.state && process.env.FEISHU_TEST_CONFIG_PATH === beforeEnv.cfg &&
      process.env.USERPROFILE === beforeEnv.user && process.env.CLAUDE_CONFIG_DIR === beforeEnv.claude &&
      process.env.CODEX_HOME === beforeEnv.codex && process.env.LOCALAPPDATA === beforeEnv.local);
    check('cleanup 删除测试根', res.removed === true, res.reason);
  }

  for (const h of createdRoots.splice(0)) {
    const res = h.cleanup();
    if (!res.removed) console.log('   ⚠ 残留测试根(拒绝清理): ' + h.root + ' — ' + res.reason);
  }
}

main();
if (hasReal) check('全部测试结束后真实 config SHA 不变', helper.realConfigSha256() === realSha);
console.log(failed ? `config-isolation: ${failed} 项失败` : 'config-isolation: 全部通过' + (epermSkipped ? `(${epermSkipped} 个 EPERM 跳过)` : ''));
process.exitCode = failed ? 1 : 0;
