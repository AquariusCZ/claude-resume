/*
  authorization-policy.js  ---  Stage 1 工作包 S1-A:纯 AuthorizationPolicy

  createAuthorizationPolicy({ readConfig }) 返回一组「纯决策」函数:每个决策只根据注入的
  readConfig() 返回值(或调用方显式传入的 cfg 快照)做内存计算,不做任何 I/O、不落盘、
  不发消息。读取抛错、配置对象缺失或 owner/allow 配置 malformed 时一律 fail-closed
  (none / 拒绝)。

  可选 cfg 快照:所有决策签名都接受第二个参数 cfg。只有 cfg === undefined 时才调用注入的
  readConfig;调用方已有快照时直接传快照,避免再次读盘。显式传入的 cfg 缺失(null)、
  非对象或 malformed 一律 fail-closed,绝不回退到注入的 readConfig。

  冻结语义(见工作包 S1-A):
  1. feishuAuthOpenIds 显式 [] = 未锁定:有 open_id 的发送者 level=full,可进入/查询/
     修改/配置;缺 open_id 始终 none。
  2. feishuAuthOpenIds 非空:显式成员 full,其余有身份发送者 viewer。
  3. 配置对象缺失、读取抛错、feishuAuthOpenIds 缺失/非数组/含空白或非字符串项:level=none,
     project/config/特权能力均拒绝。
  4. feishuAllowOpenIds 缺失或显式空数组 = 允许所有有身份发送者;非空只允许成员;
     malformed 或配置读取失败拒绝。
  5. 文件/执行工具与 owner-only profile 只授予显式 owner;未锁定时的 bootstrap-full
     陌生人不能获得。
  6. owner 通知 chat 仅 p2p 可绑定:显式 owner 可绑定;仅当 owner 列表显式为空且当前没有
     feishuChatId 时,首个有身份发送者可 bootstrap;群聊不能绑定。
  7. feishuViewerOpenIds 不改变自动 viewer 规则。
*/
'use strict';

function createAuthorizationPolicy({ readConfig } = {}) {
  if (typeof readConfig !== 'function') {
    throw new TypeError('createAuthorizationPolicy requires a readConfig function');
  }

  // 读取一次配置;抛错、缺失、非对象一律视为读取失败(返回 null,fail-closed)。
  function load() {
    let cfg;
    try { cfg = readConfig(); }
    catch (e) { return null; }
    if (!cfg || typeof cfg !== 'object' || Array.isArray(cfg)) return null;
    return cfg;
  }

  // 决策的配置来源:显式快照优先;只有 cfg === undefined 时才走注入 readConfig。
  // 显式传入的 null / 非对象 / 数组视为「无配置」(返回 null),不得回退到注入配置。
  function resolveCfg(cfg) {
    if (cfg === undefined) return load();
    if (!cfg || typeof cfg !== 'object' || Array.isArray(cfg)) return null;
    return cfg;
  }

  // open_id 列表项必须是非空且不含任何空白的字符串;缺失/非数组/含非法项 = malformed。
  // 返回 { ok:true, list } 或 { ok:false }(显式空数组是合法「未锁定」)。
  function openIdList(value) {
    if (value === undefined || !Array.isArray(value)) return { ok: false };
    const list = [];
    for (const item of value) {
      if (typeof item !== 'string' || !item.trim() || /\s/.test(item)) return { ok: false };
      list.push(item);
    }
    return { ok: true, list };
  }

  function hasIdentity(openId) {
    return typeof openId === 'string' && openId.trim() !== '';
  }

  function isExplicitOwner(openId, cfg) {
    const c = resolveCfg(cfg);
    if (!c || !hasIdentity(openId)) return false;
    const owners = openIdList(c.feishuAuthOpenIds);
    return !!(owners.ok && owners.list.indexOf(openId) !== -1);
  }

  // 'full'(显式成员或未锁定 bootstrap)/ 'viewer'(锁定下的有身份非成员)/ 'none'。
  function level(openId, cfg) {
    const c = resolveCfg(cfg);
    if (!c || !hasIdentity(openId)) return 'none';        // 缺 open_id 始终 none
    const owners = openIdList(c.feishuAuthOpenIds);
    if (!owners.ok) return 'none';                        // 配置缺失/malformed
    if (owners.list.indexOf(openId) !== -1) return 'full';
    if (owners.list.length === 0) return 'full';          // 显式 [] = 未锁定
    return 'viewer';
  }

  // allowlist:缺失或显式 [] = 允许所有有身份发送者;非空只允许成员;malformed/读失败拒绝。
  function senderIsAllowed(openId, cfg) {
    const c = resolveCfg(cfg);
    if (!c || !hasIdentity(openId)) return false;
    const allow = c.feishuAllowOpenIds;
    if (allow === undefined) return true;
    if (!Array.isArray(allow)) return false;
    for (const item of allow) {
      if (typeof item !== 'string' || !item.trim() || /\s/.test(item)) return false;
    }
    if (allow.length === 0) return true;
    return allow.indexOf(openId) !== -1;
  }

  function canProject(openId, cfg) { return level(openId, cfg) !== 'none'; }
  function canConfig(openId, cfg) { return level(openId, cfg) === 'full'; }

  // 文件/执行工具、owner-only profile:只授予显式列出的 owner。
  function canUsePrivilegedTools(openId, cfg) { return isExplicitOwner(openId, cfg); }
  function canUseOwnerOnlyProfile(openId, cfg) { return isExplicitOwner(openId, cfg); }

  // owner 通知 chat 绑定:{ openId, chatId, isP2P, currentFeishuChatId }。
  function canBindOwnerChat(sender, cfg) {
    const s = sender || {};
    const c = resolveCfg(cfg);
    if (!c || !hasIdentity(s.openId)) return false;
    if (!s.chatId || s.isP2P !== true) return false;      // 群聊/无 chat 不能绑定
    if (s.currentFeishuChatId === s.chatId) return false; // 已绑定该 chat,无需再写
    const owners = openIdList(c.feishuAuthOpenIds);
    if (!owners.ok) return false;                         // malformed owner 配置不绑定
    if (owners.list.indexOf(s.openId) !== -1) return true; // 显式 owner 可(重新)绑定
    // bootstrap:未锁定(显式空列表)且当前无 feishuChatId 时,首个有身份发送者可绑定。
    return owners.list.length === 0 && !s.currentFeishuChatId;
  }

  return {
    level,
    isExplicitOwner,
    senderIsAllowed,
    canProject,
    canConfig,
    canUsePrivilegedTools,
    canUseOwnerOnlyProfile,
    canBindOwnerChat,
  };
}

module.exports = { createAuthorizationPolicy };
