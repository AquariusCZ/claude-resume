// Stage 1 S1-A —— AuthorizationPolicy 纯决策单元测试(纯内存:不读 AppDir config、
// 不加载 feishu-agent、不启动任何 AI)。
// Run: node test/authorization-policy.js
'use strict';
const assert = require('assert');
const path = require('path');
const { createAuthorizationPolicy } = require(path.join(__dirname, '..', 'src', 'authorization-policy.js'));

let failed = 0;
const check = (name, cond, detail) => {
  console.log((cond ? '  ✓ ' : '  ✗ ') + name + (cond ? '' : (detail !== undefined ? ' — ' + detail : '')));
  if (!cond) failed++;
};
const policyFor = cfg => createAuthorizationPolicy({ readConfig: () => cfg });
const throwing = err => createAuthorizationPolicy({ readConfig: () => { throw err || new Error('read failed'); } });
const DECISIONS = ['level', 'isExplicitOwner', 'senderIsAllowed', 'canProject', 'canConfig',
  'canUsePrivilegedTools', 'canUseOwnerOnlyProfile', 'canBindOwnerChat'];

// ---------- 工厂与返回形状 ----------
check('S1-A 工厂缺失 readConfig 时抛错', (() => { try { createAuthorizationPolicy({}); return false; } catch (e) { return true; } })());
check('S1-A readConfig 非函数时抛错', (() => { try { createAuthorizationPolicy({ readConfig: 1 }); return false; } catch (e) { return true; } })());
{
  const p = policyFor({});
  check('S1-A 返回 8 个纯决策函数', DECISIONS.every(k => typeof p[k] === 'function'), JSON.stringify(Object.keys(p)));
  check('S1-A 返回对象不含多余可调用入口', Object.keys(p).length === DECISIONS.length, Object.keys(p).join(','));
}

// ---------- 冻结语义 #2:显式 [] = 未锁定 ----------
{
  const p = policyFor({ feishuAuthOpenIds: [] });
  check('解锁:有身份发送者 level=full', p.level('ou_anyone') === 'full', p.level('ou_anyone'));
  check('解锁:可进入/查询项目', p.canProject('ou_anyone') === true);
  check('解锁:可修改/配置', p.canConfig('ou_anyone') === true);
  check('解锁:bootstrap-full 陌生人不是显式 owner', p.isExplicitOwner('ou_anyone') === false);
  check('解锁:bootstrap-full 陌生人无特权工具', p.canUsePrivilegedTools('ou_anyone') === false);
  check('解锁:bootstrap-full 陌生人不能用 owner-only profile', p.canUseOwnerOnlyProfile('ou_anyone') === false);
  check('缺 open_id 始终 none', p.level(undefined) === 'none' && p.level(null) === 'none' && p.level('') === 'none' && p.level('  ') === 'none');
  check('缺 open_id 不可进入/配置', p.canProject(undefined) === false && p.canConfig('') === false);
  check('缺 open_id 无特权能力', p.canUsePrivilegedTools(null) === false && p.canUseOwnerOnlyProfile('') === false);
  check('非字符串 open_id 视为缺失', p.level(123) === 'none' && p.level({}) === 'none');
}

// ---------- 冻结语义 #3:非空 = 锁定 ----------
{
  const p = policyFor({ feishuAuthOpenIds: ['ou_owner', 'ou_mgr'] });
  check('锁定:显式成员 level=full', p.level('ou_owner') === 'full' && p.level('ou_mgr') === 'full');
  check('锁定:显式成员可配置/特权/owner-only', p.canConfig('ou_owner') && p.canUsePrivilegedTools('ou_owner') && p.canUseOwnerOnlyProfile('ou_mgr'));
  check('锁定:其余有身份发送者 viewer', p.level('ou_mate') === 'viewer', p.level('ou_mate'));
  check('锁定:viewer 可进入/查询项目', p.canProject('ou_mate') === true);
  check('锁定:viewer 不能修改/配置', p.canConfig('ou_mate') === false);
  check('锁定:viewer 无特权工具/owner-only profile', p.canUsePrivilegedTools('ou_mate') === false && p.canUseOwnerOnlyProfile('ou_mate') === false);
  check('锁定:精确匹配,不模糊匹配', p.level('ou_owner_extra') === 'viewer' && p.level('OU_OWNER') === 'viewer');
}

// ---------- 冻结语义 #4:配置缺失/读取抛错/owner 列表 malformed => none ----------
{
  for (const cfg of [undefined, null, [], 'config', 42, {}]) {
    const p = policyFor(cfg);
    const label = JSON.stringify(cfg);
    check(`配置对象缺失(${label}):level=none`, p.level('ou_x') === 'none', p.level('ou_x'));
    check(`配置对象缺失(${label}):project/config 拒绝`, p.canProject('ou_x') === false && p.canConfig('ou_x') === false);
    check(`配置对象缺失(${label}):特权能力拒绝`, p.canUsePrivilegedTools('ou_x') === false && p.canUseOwnerOnlyProfile('ou_x') === false);
  }
  const p = throwing(new Error('boom'));
  check('readConfig 抛错:level=none', p.level('ou_x') === 'none');
  check('readConfig 抛错:project/config 拒绝', p.canProject('ou_x') === false && p.canConfig('ou_x') === false);
  check('readConfig 抛错:特权能力拒绝', p.canUsePrivilegedTools('ou_x') === false && p.canUseOwnerOnlyProfile('ou_x') === false);
  check('readConfig 抛错:sender 拒绝', p.senderIsAllowed('ou_x') === false);
  check('readConfig 抛错:不绑定 owner chat', p.canBindOwnerChat({ openId: 'ou_x', chatId: 'oc_a', isP2P: true }) === false);

  const malformedOwners = [
    {},                          // feishuAuthOpenIds 缺失
    { feishuAuthOpenIds: 'ou_owner' },        // 非数组
    { feishuAuthOpenIds: { 0: 'ou_owner' } }, // 非数组
    { feishuAuthOpenIds: [''] },              // 空白项
    { feishuAuthOpenIds: [' '] },             // 空白项
    { feishuAuthOpenIds: [' ou_owner'] },     // 含空白项
    { feishuAuthOpenIds: ['ou_owner', 7] },   // 非字符串项
    { feishuAuthOpenIds: ['ou_owner', undefined] }, // 非字符串项
    { feishuAuthOpenIds: [null] },
  ];
  for (const cfg of malformedOwners) {
    const p = policyFor(cfg);
    const label = JSON.stringify(cfg.feishuAuthOpenIds);
    check(`owner 列表 malformed(${label}):level=none`, p.level('ou_owner') === 'none', p.level('ou_owner'));
    check(`owner 列表 malformed(${label}):project/config 拒绝`, p.canProject('ou_owner') === false && p.canConfig('ou_owner') === false);
    check(`owner 列表 malformed(${label}):特权/owner-only 拒绝`, p.canUsePrivilegedTools('ou_owner') === false && p.canUseOwnerOnlyProfile('ou_owner') === false);
  }
}

// ---------- 冻结语义 #5:feishuAllowOpenIds ----------
{
  const withAllow = allow => policyFor({ feishuAuthOpenIds: ['ou_owner'], feishuAllowOpenIds: allow });
  check('allowlist 缺失 = 允许所有有身份发送者', policyFor({ feishuAuthOpenIds: ['ou_owner'] }).senderIsAllowed('ou_x') === true);
  check('allowlist 显式空数组 = 允许所有有身份发送者', withAllow([]).senderIsAllowed('ou_x') === true);
  check('allowlist 非空只允许成员', withAllow(['ou_a', 'ou_b']).senderIsAllowed('ou_a') === true && withAllow(['ou_a']).senderIsAllowed('ou_b') === false);
  check('allowlist 与 owner 无关:非 owner 但允许即放行', withAllow(['ou_mate']).senderIsAllowed('ou_mate') === true);
  for (const allow of ['ou_a', { 0: 'ou_a' }, ['ou_a', ''], ['ou_a', ' ou_b'], ['ou_a', 5], [undefined]]) {
    check(`allowlist malformed(${JSON.stringify(allow)}):拒绝`, withAllow(allow).senderIsAllowed('ou_a') === false);
  }
  check('allowlist 配置读取失败:拒绝', throwing().senderIsAllowed('ou_x') === false);
  check('allowlist 缺失 open_id:拒绝', policyFor({}).senderIsAllowed(undefined) === false);
}

// ---------- 冻结语义 #6:文件/执行工具与 owner-only profile 只给显式 owner ----------
{
  const unlocked = policyFor({ feishuAuthOpenIds: [] });
  check('未锁定:bootstrap-full 陌生人拿不到特权工具', unlocked.canUsePrivilegedTools('ou_stranger') === false);
  check('未锁定:bootstrap-full 陌生人拿不到 owner-only profile', unlocked.canUseOwnerOnlyProfile('ou_stranger') === false);
  const locked = policyFor({ feishuAuthOpenIds: ['ou_owner'] });
  check('锁定:显式 owner 有特权工具', locked.canUsePrivilegedTools('ou_owner') === true);
  check('锁定:显式 owner 可用 owner-only profile', locked.canUseOwnerOnlyProfile('ou_owner') === true);
  check('锁定:viewer 无特权工具/owner-only', locked.canUsePrivilegedTools('ou_mate') === false && locked.canUseOwnerOnlyProfile('ou_mate') === false);
}

// ---------- 冻结语义 #7:owner 通知 chat 绑定 ----------
{
  const locked = policyFor({ feishuAuthOpenIds: ['ou_owner'] });
  check('绑定:显式 owner 可绑定(尚无 chat)', locked.canBindOwnerChat({ openId: 'ou_owner', chatId: 'oc_a', isP2P: true }) === true);
  check('绑定:显式 owner 可重新绑定其他 chat', locked.canBindOwnerChat({ openId: 'ou_owner', chatId: 'oc_b', isP2P: true, currentFeishuChatId: 'oc_a' }) === true);
  check('绑定:已是同一 chat 不再写', locked.canBindOwnerChat({ openId: 'ou_owner', chatId: 'oc_a', isP2P: true, currentFeishuChatId: 'oc_a' }) === false);
  check('绑定:锁定后非成员不能 bootstrap', locked.canBindOwnerChat({ openId: 'ou_mate', chatId: 'oc_a', isP2P: true }) === false);
  check('绑定:群聊不能绑定(即便 owner)', locked.canBindOwnerChat({ openId: 'ou_owner', chatId: 'oc_g', isP2P: false }) === false);
  check('绑定:缺 chatId 不绑定', locked.canBindOwnerChat({ openId: 'ou_owner', chatId: '', isP2P: true }) === false);
  check('绑定:缺 open_id 不绑定', locked.canBindOwnerChat({ openId: '', chatId: 'oc_a', isP2P: true }) === false);

  const unlocked = policyFor({ feishuAuthOpenIds: [] });
  check('绑定(解锁):首个有身份发送者可 bootstrap', unlocked.canBindOwnerChat({ openId: 'ou_first', chatId: 'oc_a', isP2P: true }) === true);
  check('绑定(解锁):已有 feishuChatId 不再 bootstrap', unlocked.canBindOwnerChat({ openId: 'ou_second', chatId: 'oc_b', isP2P: true, currentFeishuChatId: 'oc_a' }) === false);
  check('绑定(解锁):群聊不能 bootstrap', unlocked.canBindOwnerChat({ openId: 'ou_first', chatId: 'oc_g', isP2P: false }) === false);

  check('绑定:owner 列表缺失不绑定', policyFor({}).canBindOwnerChat({ openId: 'ou_x', chatId: 'oc_a', isP2P: true }) === false);
  check('绑定:owner 列表 malformed 不绑定', policyFor({ feishuAuthOpenIds: [''] }).canBindOwnerChat({ openId: 'ou_x', chatId: 'oc_a', isP2P: true }) === false);
  check('绑定:读取抛错不绑定', throwing().canBindOwnerChat({ openId: 'ou_x', chatId: 'oc_a', isP2P: true }) === false);
}

// ---------- 冻结语义 #8:feishuViewerOpenIds 不改变自动 viewer 规则 ----------
{
  const p = policyFor({ feishuAuthOpenIds: ['ou_owner'], feishuViewerOpenIds: ['ou_v'] });
  check('viewer 列表:列出的仍是 viewer', p.level('ou_v') === 'viewer', p.level('ou_v'));
  check('viewer 列表:未列出的有身份发送者也是 viewer', p.level('ou_other') === 'viewer');
  check('viewer 列表:两者都无特权能力', p.canUsePrivilegedTools('ou_v') === false && p.canUseOwnerOnlyProfile('ou_other') === false);
  const unlocked = policyFor({ feishuAuthOpenIds: [], feishuViewerOpenIds: ['ou_v'] });
  check('viewer 列表:未锁定时不影响 full', unlocked.level('ou_any') === 'full' && unlocked.level('ou_v') === 'full');
}

// ---------- readConfig 注入:每次决策调用读取一次、抛错被内部捕获 ----------
{
  let calls = 0;
  const spy = createAuthorizationPolicy({ readConfig: () => { calls++; return { feishuAuthOpenIds: ['ou_owner'] }; } });
  spy.level('ou_owner'); spy.senderIsAllowed('ou_owner'); spy.canBindOwnerChat({ openId: 'ou_owner', chatId: 'oc_a', isP2P: true });
  check('readConfig 注入:每个决策调用各读取一次配置', calls === 3, 'calls=' + calls);

  let threw = 0;
  const boom = createAuthorizationPolicy({ readConfig: () => { threw++; throw new Error('io'); } });
  boom.level('ou_x'); boom.senderIsAllowed('ou_x'); boom.canBindOwnerChat({ openId: 'ou_x', chatId: 'oc_a', isP2P: true });
  check('readConfig 抛错:抛错被捕获且每个决策各尝试一次', threw === 3, 'threw=' + threw);
}

// ---------- 显式 cfg 快照:不读注入配置,无效快照不回退 ----------
{
  let calls = 0;
  const p = createAuthorizationPolicy({ readConfig: () => {
    calls++;
    return { feishuAuthOpenIds: ['ou_injected'], feishuAllowOpenIds: ['ou_injected'] };
  } });
  const snapshot = { feishuAuthOpenIds: ['ou_snapshot'], feishuAllowOpenIds: ['ou_snapshot'] };
  check('显式快照:owner/allow 决策只使用快照',
    p.level('ou_snapshot', snapshot) === 'full'
      && p.level('ou_injected', snapshot) === 'viewer'
      && p.senderIsAllowed('ou_snapshot', snapshot) === true
      && p.senderIsAllowed('ou_injected', snapshot) === false);
  check('显式快照:owner-only 与 chat 绑定只使用快照',
    p.canUseOwnerOnlyProfile('ou_snapshot', snapshot) === true
      && p.canUsePrivilegedTools('ou_injected', snapshot) === false
      && p.canBindOwnerChat({ openId: 'ou_snapshot', chatId: 'oc_snapshot', isP2P: true }, snapshot) === true);
  check('显式快照:全部决策均未调用 readConfig', calls === 0, 'calls=' + calls);

  check('显式 null 快照:fail-closed 且不回退',
    p.level('ou_injected', null) === 'none'
      && p.senderIsAllowed('ou_injected', null) === false
      && p.canBindOwnerChat({ openId: 'ou_injected', chatId: 'oc_a', isP2P: true }, null) === false);
  check('显式 malformed 快照:fail-closed 且不回退',
    p.level('ou_injected', { feishuAuthOpenIds: 'ou_injected' }) === 'none'
      && p.canUsePrivilegedTools('ou_injected', { feishuAuthOpenIds: [''] }) === false);
  check('无效显式快照仍未调用 readConfig', calls === 0, 'calls=' + calls);
}

console.log(failed ? `\n${failed} 项断言失败` : '\n全部通过');
assert.strictEqual(failed, 0);
