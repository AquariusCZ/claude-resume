// Stage 1 D-003 integration check: malformed/missing owner config must never enumerate,
// resolve, or echo projects across every inbound entry (text message / card / bottom menu),
// and must not start any AI run. Valid config afterwards keeps owner/viewer behavior intact.
// S1-C1 迁移:经 prepareTestConfig 在 synthetic LOCALAPPDATA/临时 config 内运行,
// 不读取/备份/写回真实 config。
// Run: node test/menu-authorization.js
'use strict';
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const helper = require('./feishu-test-config');

const projectDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-resume-secret-project-'));
const projectDirLower = projectDir.toLowerCase();
const repoRoot = path.resolve(__dirname, '..');   // 合法可发现路径(非 temp/AppDir),与 routing.js 相同

let failed = 0;
const check = (name, condition, detail) => {
  console.log((condition ? '  OK ' : '  FAIL ') + name + (condition ? '' : ' - ' + detail));
  if (!condition) failed++;
};

const h = helper.prepareTestConfig({
  real: false,
  source: {
    feishuAuthOpenIds: ['ou_owner'],
    customProjects: [{ name: 'Secret Project', path: projectDir }],
  },
});
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_NO_AI = '1';   // 绝不启动真实 AI

// D-003 只允许用合成 temp 配置;两类 fail-closed 快照 + 恢复用的合法配置。
const VALID = { feishuAuthOpenIds: ['ou_owner'], customProjects: [{ name: 'Secret Project', path: repoRoot }] };
const MISSING = { customProjects: [{ name: 'Secret Project', path: projectDir }] };                       // owner 字段缺失
const MALFORMED = { feishuAuthOpenIds: 'ou_owner', customProjects: [{ name: 'Secret Project', path: projectDir }] };   // owner malformed

let agent = null;
let projectTouchCount = 0;      // fs.existsSync(projectDir) 调用计数,证明 level=none 未触碰项目发现
let patched = false;
const originalExistsSync = fs.existsSync;
const patchExistsSync = () => {
  if (patched) return;
  patched = true;
  fs.existsSync = function (...args) {
    if (String(args[0]).toLowerCase() === projectDirLower) projectTouchCount++;
    return originalExistsSync.apply(fs, args);
  };
};
const restoreExistsSync = () => {
  if (!patched) return;
  patched = false;
  fs.existsSync = originalExistsSync;
};

const writeCfg = cfg => fs.writeFileSync(h.configPath, JSON.stringify(cfg, null, 4), 'utf8');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const FORBIDDEN = ['Secret Project', projectDir];
// 解出 mock 每条 create/patch 的完整内容,递归检查任何字符串是否泄露项目名/绝对路径。
const leakedStrings = () => {
  const found = [];
  for (const call of agent.client.__calls) {
    let parsed;
    try { parsed = JSON.parse(call.content); } catch (e) { continue; }
    const walk = node => {
      if (typeof node === 'string') {
        if (FORBIDDEN.some(s => node.indexOf(s) !== -1)) found.push(node);
        return;
      }
      if (Array.isArray(node)) return node.forEach(walk);
      if (node && typeof node === 'object') Object.values(node).forEach(walk);
    };
    walk(parsed);
  }
  return found;
};

let eid = 0;
const msgEv = (t, open, chat) => ({ message: { message_id: 'm_d003_' + (++eid), chat_id: chat, chat_type: 'p2p', message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: open } } });
const cardEv = (val, open, chat, mid) => ({ action: { value: val }, context: { open_chat_id: chat, open_message_id: mid || ('c_d003_' + (++eid)) }, operator: { open_id: open } });
const menuEv = (key, open) => ({ event_key: key, operator: { operator_id: { open_id: open } }, header: { event_id: 'e_d003_' + (++eid), create_time: String(Date.now()) } });
const msgNoOpen = (t, chat) => ({ message: { message_id: 'm_d003_n_' + (++eid), chat_id: chat, chat_type: 'p2p', message_type: 'text', content: JSON.stringify({ text: t }) }, sender: {} });
const cardNoOpen = (val, chat, mid) => ({ action: { value: val }, context: { open_chat_id: chat, open_message_id: mid || ('c_d003_n_' + (++eid)) }, operator: {} });
const menuNoOpen = key => ({ event_key: key, operator: {}, header: { event_id: 'e_d003_n_' + (++eid), create_time: String(Date.now()) } });

// 每条消息用唯一 chat/message_id,先清理 mock 记录与 lastRun 快照,避免 seen/dedupe 干扰。
async function runMessageCase(name, text, setup) {
  agent.client.__reset();
  const chat = 'oc_d003_' + (++eid);
  if (setup) setup(chat);
  const before = agent.testHooks.lastRun;
  await agent.onMessage(msgEv(text, 'ou_d003_user', chat));
  await sleep(40);
  const leaked = leakedStrings();
  check(name + ' 不泄露 Secret Project / projectDir', leaked.length === 0, JSON.stringify(leaked));
  check(name + ' 未启动 AI(testHooks.lastRun 未变化)', agent.testHooks.lastRun === before, String(agent.testHooks.lastRun));
}
async function runCardCase(name, val, setup) {
  agent.client.__reset();
  const chat = 'oc_d003_card_' + (++eid);
  if (setup) setup(chat);
  const before = agent.testHooks.lastRun;
  await agent.onCardAction(cardEv(val, 'ou_d003_user', chat));
  await sleep(50);
  const leaked = leakedStrings();
  check(name + ' 不泄露 Secret Project / projectDir', leaked.length === 0, JSON.stringify(leaked));
  check(name + ' 未启动 AI(testHooks.lastRun 未变化)', agent.testHooks.lastRun === before, String(agent.testHooks.lastRun));
}
async function runMenuCase(name, key) {
  agent.client.__reset();
  const before = agent.testHooks.lastRun;
  await agent.onBotMenu(menuEv(key, 'ou_d003_user'));
  await sleep(50);
  const leaked = leakedStrings();
  check(name + ' 不泄露 Secret Project / projectDir', leaked.length === 0, JSON.stringify(leaked));
  check(name + ' 未启动 AI(testHooks.lastRun 未变化)', agent.testHooks.lastRun === before, String(agent.testHooks.lastRun));
}

// 预置旧 project session 的公共 setup:让伪造卡片/忘记查询/普通输入都落在「看起来在项目里」。
const oldProjectSession = chat => agent.setSession(chat, { mode: 'project', project: projectDir, sub: 'modify', work: 'fake-session' });
const oldQuerySession = chat => agent.setSession(chat, { mode: 'project', project: projectDir, sub: 'query' });

async function runLevelNonePhase(label, cfg) {
  writeCfg(cfg);
  await runMessageCase(`[${label}] onMessage 帮助`, '帮助');
  await runMessageCase(`[${label}] onMessage 菜单`, '菜单');
  await runMessageCase(`[${label}] onMessage 状态`, '状态');
  await runMessageCase(`[${label}] onMessage 显式进入`, '进入 Secret Project');
  await runMessageCase(`[${label}] onMessage 裸 Secret Project`, 'Secret Project');
  await runMessageCase(`[${label}] onMessage Secret Project + 一次性命令`, 'Secret Project 帮我看看 README');
  await runMessageCase(`[${label}] onMessage 忘记查询(旧 project session)`, '忘记查询', oldQuerySession);
  await runMessageCase(`[${label}] onMessage 停止 Secret Project`, '停止 Secret Project');
  await runMessageCase(`[${label}] onMessage 旧 project session 普通输入`, '随便聊聊', oldProjectSession);
  // 伪造卡片:预置旧 session 使 pr 绑定本可匹配,证明 project action gate 先行拒绝。
  await runCardCase(`[${label}] 伪造 enter 卡`, { do: 'enter', p: projectDir }, oldProjectSession);
  await runCardCase(`[${label}] 伪造 submode 卡`, { do: 'submode', sm: 'modify', pr: agent.sessionProjectKey(projectDir) }, oldProjectSession);
  await runCardCase(`[${label}] 伪造 clearq 卡`, { do: 'clearq', pr: agent.sessionProjectKey(projectDir) }, oldProjectSession);
  await runMenuCase(`[${label}] 底部菜单 status`, 'status');
  await runMenuCase(`[${label}] 底部菜单 stop`, 'stop');
  await runMenuCase(`[${label}] 底部菜单 menu`, 'menu');
}

async function main() {
  try {
    agent = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));

    // 保留原有 buildMenuCard 断言:malformed 快照直接渲染也不泄露、仍保留闲聊/模型入口。
    const malformed = JSON.stringify(agent.buildMenuCard('oc_test', 'ou_owner', MALFORMED));
    check('malformed owner config hides project name', !malformed.includes('Secret Project'), malformed);
    check('malformed owner config hides project path', !malformed.includes(projectDir), malformed);
    check('malformed owner config hides status and permission actions',
      !malformed.includes('"do":"status"') && !malformed.includes('"do":"perm"'), malformed);
    check('malformed owner config keeps chat and model entry points',
      malformed.includes('"do":"chat"') && malformed.includes('"do":"modelmenu"'), malformed);

    // ---- level=none 全入口:monkeypatch fs.existsSync 只对 projectDir 计数 ----
    patchExistsSync();
    await runLevelNonePhase('owner 缺失', MISSING);
    await runLevelNonePhase('owner malformed', MALFORMED);

    // 缺 open_id:消息/卡片/菜单一律不输出、不发现项目。
    agent.client.__reset();
    await agent.onMessage(msgNoOpen('进入 Secret Project', 'oc_d003_noid_msg'));
    await agent.onCardAction(cardNoOpen({ do: 'enter', p: projectDir }, 'oc_d003_noid_card'));
    await agent.onBotMenu(menuNoOpen('status'));
    await sleep(50);
    check('缺 open_id:消息/卡片/菜单均无输出', agent.client.__calls.length === 0, JSON.stringify(agent.client.__calls));
    check('level=none + 缺 open_id:未触碰项目发现(fs.existsSync(projectDir)=0)', projectTouchCount === 0, 'touches=' + projectTouchCount);
    restoreExistsSync();

    // ---- 恢复合法配置:owner/viewer 现有行为保持 ----
    writeCfg(VALID);
    const repoRootEscaped = JSON.stringify(repoRoot).slice(1, -1);   // JSON 内容里反斜杠是转义形式
    const ownerMenuJson = JSON.stringify(agent.buildMenuCard('oc_d003_owner_menu', 'ou_owner', VALID));
    check('恢复合法配置:owner 菜单仍含项目', ownerMenuJson.includes('Secret Project') && ownerMenuJson.includes(repoRootEscaped), ownerMenuJson);
    const viewerMenuJson = JSON.stringify(agent.buildMenuCard('oc_d003_viewer_menu', 'ou_viewer', VALID));
    check('恢复合法配置:viewer 菜单仍含项目', viewerMenuJson.includes('Secret Project') && viewerMenuJson.includes(repoRootEscaped), viewerMenuJson);

    agent.client.__reset();
    const ownerChat = 'oc_d003_owner_enter';
    await agent.onMessage(msgEv('进入 Secret Project', 'ou_owner', ownerChat));
    await sleep(50);
    const osess = agent.getSession(ownerChat);
    check('恢复合法配置:owner 能进入项目', osess.mode === 'project' && osess.project === repoRoot, JSON.stringify(osess));

    agent.client.__reset();
    const viewerChat = 'oc_d003_viewer_enter';
    await agent.onMessage(msgEv('进入 Secret Project', 'ou_viewer', viewerChat));
    await sleep(50);
    const vsess = agent.getSession(viewerChat);
    check('恢复合法配置:viewer 进入项目且为只读查询',
      vsess.mode === 'project' && vsess.project === repoRoot && vsess.sub === 'query', JSON.stringify(vsess));

    // ---- D-003 确定性 TOCTOU:事件入口快照必须全程约束授权与项目渲染 ----
    // 构造方法:先以另一配置快照(CHANGED,不同 customProjects 显示名)填充项目发现缓存;
    // 再拦截 readFileSync 让事件入口首次读到合法 VALID,同一事件内任何二次 config 读取都会
    // 命中 CHANGED。修复后的显式快照路径绝无项目发现二次读取;非 owner 消息也不会进入
    // owner 通知 chat 写锁。反向用例还把发送者在后续磁盘配置中升级成 owner,证明入口
    // level=none 的同一事件仍不发现/显示/进入项目,也不会获得 owner chat 绑定副作用。
    const CHANGED = { feishuAuthOpenIds: ['ou_owner'], customProjects: [{ name: 'Changed Project', path: repoRoot }] };
    const ESCALATED = { feishuAuthOpenIds: ['ou_d003_user'], customProjects: [{ name: 'Changed Project', path: repoRoot }] };
    const allOutput = () => agent.client.__calls.map(c => c.content).join('\n');
    const seedDiscoveryCache = () => { agent.discoverProjects(CHANGED); };
    const withScriptedConfigReads = async (firstCfg, laterCfg, fn) => {
      const reads = { count: 0, viaDiscovery: 0 };
      const origRead = fs.readFileSync;
      fs.readFileSync = function (...args) {
        const p = String(args[0] || '');
        if (path.resolve(p).toLowerCase() === path.resolve(h.configPath).toLowerCase()) {
          reads.count++;
          // 只有 discoverProjects() 无参路径才会从 readConfig 二次读配置;显式快照路径绝不。
          if (/discoverProjects/.test(new Error('x').stack || '')) reads.viaDiscovery++;
          return JSON.stringify(reads.count === 1 ? firstCfg : laterCfg, null, 4);
        }
        return origRead.apply(fs, args);
      };
      try { await fn(); } finally { fs.readFileSync = origRead; }
      return reads;
    };
    const snapshotName = (label, out) => {
      check(label + ' 渲染严格使用入口快照(Secret Project,无 Changed Project)',
        out.includes('Secret Project') && !out.includes('Changed Project'), out.slice(0, 400));
    };

    // 1) 卡片主菜单(home):入口 VALID,磁盘中途变 CHANGED,主菜单仍显示入口项目。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_menu_' + (++eid);
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onCardAction(cardEv({ do: 'home' }, 'ou_d003_user', chat));
        await sleep(60);
      });
      snapshotName('TOCTOU:卡片主菜单', allOutput());
      check('TOCTOU:卡片主菜单显式快照路径无项目发现二次读取且仅入口 1 次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 2) 卡片 enter:入口 VALID,磁盘中途变 CHANGED,进入并渲染入口快照项目。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_card_enter_' + (++eid);
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onCardAction(cardEv({ do: 'enter', p: repoRoot }, 'ou_d003_user', chat));
        await sleep(60);
      });
      const sess = agent.getSession(chat);
      snapshotName('TOCTOU:卡片 enter', allOutput());
      check('TOCTOU:卡片 enter 进入入口快照项目', sess.mode === 'project' && sess.project === repoRoot, JSON.stringify(sess));
      check('TOCTOU:卡片 enter 显式快照路径无项目发现二次读取且仅入口 1 次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 3) 文字进入:入口 VALID,磁盘中途变 CHANGED,仍解析并进入入口快照项目。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_text_enter_' + (++eid);
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onMessage(msgEv('进入 Secret Project', 'ou_d003_user', chat));
        await sleep(60);
      });
      const sess = agent.getSession(chat);
      snapshotName('TOCTOU:文字进入', allOutput());
      check('TOCTOU:文字进入解析入口快照项目', sess.mode === 'project' && sess.project === repoRoot, JSON.stringify(sess));
      check('TOCTOU:文字进入仅入口读取且无项目发现二次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 4) status(active project 路径):入口 VALID,磁盘中途变 CHANGED,当前项目用入口快照名。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_status_' + (++eid);
      agent.setSession(chat, { mode: 'project', project: repoRoot, sub: 'query' });
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onMessage(msgEv('状态', 'ou_d003_user', chat));
        await sleep(60);
      });
      snapshotName('TOCTOU:status', allOutput());
      check('TOCTOU:status 仅入口读取且无项目发现二次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 5) help:入口 VALID,磁盘中途变 CHANGED,当前项目用入口快照名。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_help_' + (++eid);
      agent.setSession(chat, { mode: 'project', project: repoRoot, sub: 'query' });
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onMessage(msgEv('帮助', 'ou_d003_user', chat));
        await sleep(60);
      });
      snapshotName('TOCTOU:help', allOutput());
      check('TOCTOU:help 仅入口读取且无项目发现二次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 6) 忘记查询:入口 VALID,磁盘中途变 CHANGED,清空的项目名用入口快照。
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_forgetq_' + (++eid);
      agent.setSession(chat, { mode: 'project', project: repoRoot, sub: 'query' });
      seedDiscoveryCache();
      const reads = await withScriptedConfigReads(VALID, CHANGED, async () => {
        await agent.onMessage(msgEv('忘记查询', 'ou_d003_user', chat));
        await sleep(60);
      });
      snapshotName('TOCTOU:忘记查询', allOutput());
      check('TOCTOU:忘记查询仅入口读取且无项目发现二次读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
    }

    // 反向用例:入口快照 level=none,事件中途磁盘变合法,仍不发现/显示/进入项目。
    patchExistsSync();
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_none_menu_' + (++eid);
      const beforeCfg = JSON.parse(fs.readFileSync(h.configPath, 'utf8'));
      const reads = await withScriptedConfigReads(MISSING, ESCALATED, async () => {
        await agent.onMessage(msgEv('菜单', 'ou_d003_user', chat));
        await sleep(60);
      });
      const afterCfg = JSON.parse(fs.readFileSync(h.configPath, 'utf8'));
      const leaked = leakedStrings();
      check('TOCTOU 反向:入口 none 菜单不显示项目', leaked.length === 0, JSON.stringify(leaked));
      check('TOCTOU 反向:入口 none 菜单未触碰项目发现', projectTouchCount === 0, 'touches=' + projectTouchCount);
      check('TOCTOU 反向:后续升级为 owner 仍仅入口读取', reads.count === 1 && reads.viaDiscovery === 0, JSON.stringify(reads));
      check('TOCTOU 反向:后续升级为 owner 不绑定通知 chat',
        afterCfg.feishuChatId === beforeCfg.feishuChatId, JSON.stringify({ before: beforeCfg.feishuChatId, after: afterCfg.feishuChatId }));
    }
    agent.client.__reset();
    {
      const chat = 'oc_d003_toctou_none_enter_' + (++eid);
      await withScriptedConfigReads(MISSING, VALID, async () => {
        await agent.onMessage(msgEv('进入 Secret Project', 'ou_d003_user', chat));
        await sleep(60);
      });
      const sess = agent.getSession(chat);
      const leaked = leakedStrings();
      check('TOCTOU 反向:入口 none 文字进入被拒且不进入项目', sess.mode !== 'project', JSON.stringify(sess));
      check('TOCTOU 反向:入口 none 文字进入不泄露项目', leaked.length === 0, JSON.stringify(leaked));
      check('TOCTOU 反向:入口 none 文字进入未触碰项目发现', projectTouchCount === 0, 'touches=' + projectTouchCount);
    }
    restoreExistsSync();
  } finally {
    restoreExistsSync();
  }
}

main()
  .then(() => {
    console.log(failed ? `\n${failed} assertions failed` : '\nall passed');
    h.cleanup();
    try { fs.rmSync(projectDir, { recursive: true, force: true }); } catch (e) {}
    assert.strictEqual(failed, 0);
  })
  .catch(e => {
    console.error(e && (e.stack || e));
    try { h.cleanup(); } catch (e2) {}
    try { fs.rmSync(projectDir, { recursive: true, force: true }); } catch (e2) {}
    process.exit(1);
  });
