// Session picker smoke test: ✏️修改项目 must let you choose WHICH conversation to continue
// (with a short history digest), or start a fresh one — and must never guess.
// Mocks the Feishu API; does NOT run claude (asserts it refuses to run until a session is picked).
// Run: node test/session-pick.js
'use strict';
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_API_TIMEOUT_MS = '120';
const assert = require('assert');
const path = require('path');
const testConfigHelper = require('./feishu-test-config');

const repoRoot = path.resolve(__dirname, '..');
const testConfig = testConfigHelper.prepareTestConfig({
  real: false,
  source: {
    enabled: true,
    feishuChatId: 'oc_session_pick_test',
    feishuAuthOpenIds: ['ou_session_pick_owner'],
    feishuChatProfile: 'openai-sol',
    customProjects: [
      { name: 'AI Resume Migration', path: repoRoot },
      { name: 'AI Resume Migration Docs', path: path.join(repoRoot, 'docs') },
    ],
  },
});
process.once('exit', () => { try { testConfig.cleanup(); } catch (e) {} });
const cfg = testConfig.config;
const CHAT = cfg.feishuChatId; assert(CHAT, 'need feishuChatId');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]); assert(OWNER, 'need owner');

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const waitFor = async (fn, timeout) => {
  const end = Date.now() + (timeout || 3000);
  while (Date.now() < end) { const value = fn(); if (value) return value; await sleep(25); }
  return null;
};
const cardEv = (val, mid) => ({ action: { value: val }, context: { open_chat_id: CHAT, open_message_id: mid }, operator: { open_id: OWNER } });
const msgEv = (t) => ({ message: { message_id: 'm_sp_' + Date.now() + Math.random(), chat_id: CHAT, message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: OWNER } } });
const last = () => client.__calls[client.__calls.length - 1];
const texts = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text').map(c => c.text || '');
const isSessionCard = t => /选择会话/.test(t || '');
const isLoadingCard = t => /正在读取会话/.test(t || '');
const isProjectCard = t => /项目操作/.test(t || '');
const projectAction = (projectPath, value) => Object.assign({ pr: A.sessionProjectKey(projectPath) }, value);
const settledCard = pred => client.__calls.find(c => c.state === 'settled' && pred(c.title));
const buttonValues = call => {
  const out = [];
  try {
    const card = JSON.parse(call.content);
    const walk = value => {
      if (!value) return;
      if (Array.isArray(value)) { value.forEach(walk); return; }
      if (value.value && value.value.do) out.push(value.value);
      if (value.elements) walk(value.elements);
      if (value.actions) walk(value.actions);
    };
    walk(card.elements);
  } catch (e) {}
  return out;
};
const actionValue = (call, name) => buttonValues(call).find(v => v.do === name);

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  const SYNTHETIC = [
    { id: 'synthetic-session-a', title: '合成历史会话 A', mtime: Date.now() - 60000, provider: 'test', engine: 'test', file: null },
    { id: 'synthetic-session-b', title: '合成历史会话 B', mtime: Date.now() - 120000, provider: 'test', engine: 'test', file: null },
  ];
  try {
    A.testHooks.sessionList = async () => ({ sessions: SYNTHETIC.slice(), error: null });
    A.testHooks.sessionPreview = async () => '· 你:合成问题\n  我:合成回答';
    const profile = A.getUserProfile(OWNER);
    const projects = A.discoverProjects();
    const PROJ = projects[0], OTHER = projects.find(p => p.path.toLowerCase() !== PROJ.path.toLowerCase());
    assert(PROJ && OTHER, 'need >=2 discovered projects');
    const list = SYNTHETIC;
    console.log(`项目: ${PROJ.name} · 使用 ${list.length} 条合成会话`);

    const MID = 'msg_sp_card';
    A.lastCard.clear(); A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: undefined }); client.__reset();

    // 1. slow provider enumeration must not block the card callback
    process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS = '600';
    const clickedAt = Date.now();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'modify' }), MID));
    const callbackMs = Date.now() - clickedAt;
    check('慢会话读取时卡片回调仍在 200ms 内返回', callbackMs < 200, 'callbackMs=' + callbackMs);
    const loadingCard = await waitFor(() => settledCard(isLoadingCard), 550);
    const firstPicker = await waitFor(() => settledCard(isSessionCard), 2500);
    delete process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS;
    check('慢会话读取超过 250ms 会显示可见加载态', !!loadingCard, JSON.stringify(client.__calls));
    check('点「✏️修改项目」→ 最终弹会话列表(不擅自续会话)', !!firstPicker, JSON.stringify(client.__calls));
    check('此时还没选会话(work 未定)', !A.getSession(CHAT).work);

    // 2. sending an instruction before picking must NOT run an AI
    client.__reset();
    await A.onMessage(msgEv('把 README 改一下'));
    const repicked = await waitFor(() => settledCard(isSessionCard), 2500);
    check('未选会话就发指令 → 不跑 AI,重新弹会话列表', !!repicked && !texts().some(t => /在「.*」执行/.test(t)), JSON.stringify(client.__calls));

    // 3. pick the token-bound synthetic session
    const pickA = actionValue(repicked, 'pick');
    check('会话按钮携带项目/AI 绑定令牌', !!(pickA && pickA.k && pickA.pr && pickA.p === profile.id), JSON.stringify(pickA));
    client.__reset(); client.__setBehavior({ patchDelayMs: 80 });
    await A.onCardAction(cardEv(pickA, MID));
    await waitFor(() => settledCard(isProjectCard), 1000);
    check('选中会话 → 卡片回到项目卡', !!settledCard(isProjectCard), JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    check('work 记录为所选会话', A.getSession(CHAT).work === list[0].id, 'work=' + A.getSession(CHAT).work);
    for (let i = 0; i < 20 && !texts().some(t => /已进入会话/.test(t)); i++) await sleep(150);
    const digest = texts().find(t => /已进入会话/.test(t)) || '';
    check('推送了合成会话摘要', /已进入会话/.test(digest) && /合成问题/.test(digest), JSON.stringify(texts()));
    await waitFor(() => A.controlCardWrites.size === 0, 1000);
    check('摘要文字先完成时,慢 patch 不会把已上移的旧卡重新登记为 live', !A.lastCard.has(CHAT), 'lastCard=' + A.lastCard.get(CHAT));

    // 4. sesslist -> back -> sesslist is a legal fast round trip (must not be swallowed for 4s)
    client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist' }), MID));
    const switched = await waitFor(() => settledCard(isSessionCard), 1000);
    check('点「🔀 切换会话」→ 再次弹列表', !!switched, JSON.stringify(client.__calls));
    const back = actionValue(switched, 'backproj');
    client.__reset(); await A.onCardAction(cardEv(back, MID));
    await waitFor(() => settledCard(isProjectCard), 1000);
    await sleep(400); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist' }), MID));
    const switchedAgain = await waitFor(() => settledCard(isSessionCard), 1000);
    check('会话列表→返回→立即再开列表不会被旧 4s 防抖吞掉', !!switchedAgain, JSON.stringify(client.__calls));

    // 5. 🆕 新开会话 -> Claude gets a UUID immediately; Codex gets its thread id after first run
    const newSession = actionValue(switchedAgain, 'newsess');
    client.__reset();
    await A.onCardAction(cardEv(newSession, MID));
    const fresh = A.getSession(CHAT);
    const w = fresh.work;
    const validFresh = w === 'new' || (!!w && w !== list[0].id && /^[0-9a-f-]{36}$/i.test(w));
    check('点「🆕 新开会话」→ 按当前 AI 建立新会话占位', validFresh, 'profile=' + fresh.workProfile + ' work=' + w);
    for (let i = 0; i < 20 && !texts().some(t => /全新会话/.test(t)); i++) await sleep(150);
    check('提示这是全新会话(不带历史)', texts().some(t => /全新会话/.test(t)), JSON.stringify(texts()));

    // 6. switching back to 👁只读 still works and doesn't need a session
    client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'query' }), MID));
    await waitFor(() => settledCard(isProjectCard), 1000);
    check('切回「👁只读查询」→ 项目卡(只读不需选会话)', !!settledCard(isProjectCard) && A.getSession(CHAT).sub === 'query', JSON.stringify(last()));

    // 7. an old picker from project A must never bind its session id to project B
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify' }); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist' }), 'msg_old_picker'));
    const oldPicker = await waitFor(() => settledCard(isSessionCard), 1000);
    const oldPick = actionValue(oldPicker, 'pick');
    client.__reset(); await A.onCardAction(cardEv({ do: 'enter', p: OTHER.path }, MID));
    await waitFor(() => settledCard(isProjectCard), 1000);
    const liveBeforeStale = A.lastCard.get(CHAT);
    client.__reset(); await A.onCardAction(cardEv(oldPick, 'msg_old_picker'));
    await waitFor(() => settledCard(t => /会话卡已失效/.test(t || '')), 1000);
    await waitFor(() => A.controlCardWrites.size === 0, 1000);
    const afterStale = A.getSession(CHAT);
    check('旧项目会话卡被拒绝,不会污染新项目 work', afterStale.project === OTHER.path && !afterStale.work, JSON.stringify(afterStale));
    check('旧会话卡的失效提示不会抢成当前控制卡', A.lastCard.get(CHAT) === liveBeforeStale, `before=${liveBeforeStale} after=${A.lastCard.get(CHAT)}`);

    // 7b. an old ordinary project card is also bound to its displayed project
    client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'modify' }), 'msg_old_project'));
    await waitFor(() => settledCard(t => /会话卡已失效/.test(t || '')), 1000);
    check('旧普通项目卡不能对当前新项目执行操作', A.getSession(CHAT).project === OTHER.path && !A.getSession(CHAT).sub, JSON.stringify(A.getSession(CHAT)));

    // 8. cancelling a slow enumeration prevents any stale session card write and releases the job
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: undefined }); client.__reset();
    process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS = '600';
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'modify', test: 'cancel' }), MID));
    await sleep(20);
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'query', test: 'cancel' }), MID));
    await waitFor(() => A.sessionCardLoads.size === 0, 600);
    delete process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS;
    await waitFor(() => A.controlCardWrites.size === 0, 1000);
    check('取消慢枚举后不再写入过期会话卡', !client.__calls.some(c => c.state === 'settled' && isSessionCard(c.title)) && A.getSession(CHAT).sub === 'query', JSON.stringify(client.__calls));

    // 9. per-chat card serialization makes the later query card win over a delayed picker patch
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: undefined }); client.__reset(); client.__setBehavior({ patchDelayMs: 80 });
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'modify', test: 'serial' }), MID));
    await sleep(10);
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'submode', sm: 'query', test: 'serial' }), MID));
    await waitFor(() => A.controlCardWrites.size === 0 && A.sessionCardLoads.size === 0, 1500);
    const settled = client.__calls.filter(c => c.state === 'settled' && (c.op === 'patch' || c.op === 'create'));
    check('延迟写入按聊天串行,最终页面与 query 状态一致', settled.length && isProjectCard(settled[settled.length - 1].title) && A.getSession(CHAT).sub === 'query', JSON.stringify(client.__calls));

    // 10. a Feishu patch that never resolves falls back to exactly one final picker card
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    client.__reset(); client.__setBehavior({ patchHang: true });
    const hungAt = Date.now();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'hung-patch' }), MID));
    const hungCallbackMs = Date.now() - hungAt;
    check('飞书 patch 永久挂起时回调仍在 200ms 内返回', hungCallbackMs < 200, 'callbackMs=' + hungCallbackMs);
    const fallbackPicker = await waitFor(() => client.__calls.find(c => c.op === 'create' && c.state === 'settled' && isSessionCard(c.title)), 2000);
    await waitFor(() => A.sessionCardLoads.size === 0, 1000);
    check('patch 超时后只补发一张最终会话卡', !!fallbackPicker && client.__calls.filter(c => c.op === 'create' && c.state === 'settled' && isSessionCard(c.title)).length === 1, JSON.stringify(client.__calls));
    check('超时任务已释放,不会吞掉后续点击', A.sessionCardLoads.size === 0, 'jobs=' + A.sessionCardLoads.size);

    // 11. all main control-card actions share the same visible fallback, not only the picker
    A.lastCard.set(CHAT, MID); A.setSession(CHAT, { mode: 'idle' }); client.__reset(); client.__setBehavior({ patchHang: true });
    await A.onCardAction(cardEv({ do: 'enter', p: OTHER.path, test: 'hung-enter' }, MID));
    const fallbackProject = await waitFor(() => client.__calls.find(c => c.op === 'create' && c.state === 'settled' && isProjectCard(c.title)), 1500);
    check('进入项目的 patch 挂起也会补发可见项目卡', !!fallbackProject, JSON.stringify(client.__calls));

    // 12. the actual WS dispatch wrapper must ACK immediately even when the inner patch hangs
    A.lastCard.set(CHAT, MID); A.setSession(CHAT, { mode: 'idle' }); client.__reset(); client.__setBehavior({ patchHang: true });
    const dispatch = A.dispatchEvent('测试卡片派发', A.onCardAction);
    const dispatchedAt = Date.now();
    dispatch(cardEv({ do: 'enter', p: PROJ.path, test: 'dispatcher' }, MID));
    const dispatchMs = Date.now() - dispatchedAt;
    check('WS 注册层在内部网络挂起时仍立即返回', dispatchMs < 50, 'dispatchMs=' + dispatchMs);
    await waitFor(() => client.__calls.some(c => c.op === 'create' && c.state === 'settled' && isProjectCard(c.title)), 1500);

    // 13. dispatch must also yield before a handler's synchronous prefix, not only before awaited I/O
    let syncHandlerFinished = false;
    const syncDispatch = A.dispatchEvent('测试同步前缀', () => {
      const until = Date.now() + 150;
      while (Date.now() < until) {}
      syncHandlerFinished = true;
    });
    const syncAt = Date.now();
    syncDispatch({});
    const syncDispatchMs = Date.now() - syncAt;
    check('WS 注册层也会让出 handler 的同步扫描前缀', syncDispatchMs < 50 && !syncHandlerFinished, `dispatchMs=${syncDispatchMs} finished=${syncHandlerFinished}`);
    await waitFor(() => syncHandlerFinished, 500);

    // 14. fast enumeration queued behind a slow write must still end on the final picker, never loading
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset(); client.__setBehavior({ patchDelayMs: 100 });
    process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS = '40';
    await A.onCardAction(cardEv({ do: 'pick', k: 'stale-loading', pr: 'stale', p: profile.id, s: 'stale' }, 'msg_loading_blocker'));
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'loading-order' }), MID));
    await waitFor(() => A.controlCardWrites.size === 0 && A.sessionCardLoads.size === 0, 1500);
    delete process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS;
    const loadingOrderWrites = client.__calls.filter(c => c.state === 'settled' && (c.op === 'patch' || c.op === 'create'));
    check('快速枚举即使被前置慢写阻塞,最终也是会话选择卡而非加载态',
      loadingOrderWrites.length && isSessionCard(loadingOrderWrites[loadingOrderWrites.length - 1].title) && !loadingOrderWrites.some(c => isLoadingCard(c.title)),
      JSON.stringify(client.__calls));

    // 15. if text invalidates lastCard while a later control write is still queued, it must create a
    // fresh bottom card rather than falling back to the old message id captured at enqueue time.
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'queued-epoch' }), MID));
    const queuedPicker = await waitFor(() => settledCard(isSessionCard), 1000);
    const queuedPick = actionValue(queuedPicker, 'pick');
    client.__reset(); client.__setBehavior({ patchDelayMs: 150 });
    await A.onCardAction(cardEv({ do: 'pick', k: 'stale', pr: 'stale', p: profile.id, s: 'stale' }, 'msg_queue_blocker'));
    await A.onCardAction(cardEv(queuedPick, MID));
    await waitFor(() => A.controlCardWrites.size === 0 && texts().some(t => /已进入会话/.test(t)), 2000);
    const queuedLive = A.lastCard.get(CHAT);
    check('排队期间被文字顶走后,最终控制卡在底部新建而非 patch 旧卡',
      !!queuedLive && queuedLive !== MID && client.__calls.some(c => c.op === 'create' && c.state === 'settled' && c.id === queuedLive && isProjectCard(c.title)),
      `lastCard=${queuedLive} calls=${JSON.stringify(client.__calls)}`);
    client.__reset();

    // 16. if the clicked control card was deleted, loading creates one replacement and the final picker
    // must follow that replacement instead of failing against the deleted message and creating a second card.
    const GONE = 'gone_session_loading';
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, GONE); client.__reset();
    process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS = '120';
    process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS = '20';
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'deleted-loading-card' }), GONE));
    await waitFor(() => A.controlCardWrites.size === 0 && A.sessionCardLoads.size === 0, 1500);
    delete process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS;
    delete process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS;
    const replacementLoading = client.__calls.filter(c => c.op === 'create' && c.state === 'settled' && isLoadingCard(c.title));
    const replacementPickers = client.__calls.filter(c => c.op === 'create' && c.state === 'settled' && isSessionCard(c.title));
    const finalPickerPatch = client.__calls.find(c => c.op === 'patch' && c.state === 'settled' && isSessionCard(c.title));
    check('加载态替换被删除的旧卡后,最终选择页复用同一张替代卡',
      replacementLoading.length === 1 && replacementPickers.length === 0 && finalPickerPatch && finalPickerPatch.id === replacementLoading[0].id,
      JSON.stringify(client.__calls));

    // 17. a digest belongs to the exact project/session/profile selection that requested it. Switching
    // projects while preview I/O is pending must suppress the stale text and preserve the new control card.
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'stale-digest-picker' }), MID));
    const digestPicker = await waitFor(() => settledCard(isSessionCard), 1000);
    const digestPick = actionValue(digestPicker, 'pick');
    let previewStartedResolve;
    const previewStarted = new Promise(resolve => { previewStartedResolve = resolve; });
    const normalPreview = A.testHooks.sessionPreview;
    A.testHooks.sessionPreview = async () => {
      previewStartedResolve();
      await sleep(120);
      return '· 你:延迟摘要\n  我:不应跨项目发送';
    };
    client.__reset();
    await A.onCardAction(cardEv(digestPick, MID));
    await previewStarted;
    await A.onCardAction(cardEv({ do: 'enter', p: OTHER.path, test: 'stale-digest-switch' }, MID));
    await sleep(200);
    A.testHooks.sessionPreview = normalPreview;
    check('摘要读取期间切换项目后,旧项目摘要不会发送到新项目页面',
      A.getSession(CHAT).project === OTHER.path && !texts().some(t => /已进入会话/.test(t)),
      `session=${JSON.stringify(A.getSession(CHAT))} texts=${JSON.stringify(texts())}`);

    // 18. the user's own text pushes the old control card upward. A picker load already in progress must
    // notice the visibility epoch change and create the final picker at the bottom instead of patching old.
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset();
    process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS = '180';
    process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS = '20';
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'input-epoch' }), MID));
    await waitFor(() => settledCard(isLoadingCard), 500);
    await A.onMessage(msgEv('继续选择会话'));
    await waitFor(() => A.controlCardWrites.size === 0 && A.sessionCardLoads.size === 0, 1500);
    delete process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS;
    delete process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS;
    const inputEpochPicker = client.__calls.find(c => c.op === 'create' && c.state === 'settled' && isSessionCard(c.title));
    check('用户输入推高旧卡后,最终会话选择页会在底部新建',
      inputEpochPicker && A.lastCard.get(CHAT) === inputEpochPicker.id,
      `lastCard=${A.lastCard.get(CHAT)} calls=${JSON.stringify(client.__calls)}`);

    // 19. workProfile intentionally remains the old provider for handoff, so digest validity must also
    // compare the user's current profile. Otherwise an OpenAI digest can arrive after switching DeepSeek.
    A.setUserProfileId(OWNER, profile.id);
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'stale-profile-picker' }), MID));
    const profilePicker = await waitFor(() => settledCard(isSessionCard), 1000);
    const profilePick = actionValue(profilePicker, 'pick');
    let profilePreviewStartedResolve;
    const profilePreviewStarted = new Promise(resolve => { profilePreviewStartedResolve = resolve; });
    const previewBeforeProfileSwitch = A.testHooks.sessionPreview;
    A.testHooks.sessionPreview = async () => {
      profilePreviewStartedResolve();
      await sleep(120);
      return '· 你:旧模型摘要\n  我:切模型后不应发送';
    };
    const alternateProfile = A.modelsFor(OWNER).map(x => x[1]).find(id => id !== profile.id);
    assert(alternateProfile, 'need an alternate profile');
    client.__reset();
    await A.onCardAction(cardEv(profilePick, MID));
    await profilePreviewStarted;
    await A.onCardAction(cardEv({ do: 'model', p: alternateProfile, from: 'm' }, 'msg_profile_switch'));
    await sleep(200);
    A.testHooks.sessionPreview = previewBeforeProfileSwitch;
    check('摘要读取期间切换 AI 后,旧 AI 摘要不会晚到',
      A.getUserProfile(OWNER).id === alternateProfile && !texts().some(t => /已进入会话/.test(t)),
      `profile=${A.getUserProfile(OWNER).id} texts=${JSON.stringify(texts())}`);
    A.setUserProfileId(OWNER, profile.id);

    // 20. same-chat dispatch returns immediately but preserves arrival order. This is load-bearing for
    // "忘记查询" followed immediately by a new question: deletion must finish before the query resumes.
    const dispatchOrder = [];
    const orderedDispatch = A.dispatchEvent('测试同聊天顺序', async ev => {
      dispatchOrder.push('start' + ev.n);
      if (ev.n === 1) await sleep(80);
      dispatchOrder.push('end' + ev.n);
    });
    const orderedAt = Date.now();
    orderedDispatch({ n: 1, message: { chat_id: CHAT } });
    orderedDispatch({ n: 2, message: { chat_id: CHAT } });
    const orderedAckMs = Date.now() - orderedAt;
    await waitFor(() => dispatchOrder.length === 4, 500);
    check('同聊天事件秒回 ACK 但 handler 严格按到达顺序执行',
      orderedAckMs < 50 && dispatchOrder.join(',') === 'start1,end1,start2,end2',
      `ack=${orderedAckMs} order=${dispatchOrder.join(',')}`);

    // 21. while a failed patch is creating its replacement, a second navigation click must follow that
    // replacement at execution time instead of retrying the deleted id and creating a duplicate card.
    const REPLACING = 'gone_control_replacing';
    A.setSession(CHAT, { mode: 'idle' }); A.lastCard.set(CHAT, REPLACING);
    client.__reset(); client.__setBehavior({ createDelayMs: 80 });
    await A.onCardAction(cardEv({ do: 'enter', p: PROJ.path, test: 'replacement-first' }, REPLACING));
    await sleep(10);
    await A.onCardAction(cardEv({ do: 'enter', p: OTHER.path, test: 'replacement-second' }, REPLACING));
    await waitFor(() => A.controlCardWrites.size === 0, 1000);
    const replacementCreates = client.__calls.filter(c => c.op === 'create' && c.state === 'settled' && isProjectCard(c.title));
    check('替代卡创建窗口内的后续导航复用同一张 live 卡',
      replacementCreates.length === 1 && A.getSession(CHAT).project === OTHER.path && A.lastCard.get(CHAT) === replacementCreates[0].id,
      `session=${JSON.stringify(A.getSession(CHAT))} last=${A.lastCard.get(CHAT)} calls=${JSON.stringify(client.__calls)}`);

    // 22. a real double click has different event_ids, so generic Feishu redelivery dedupe cannot catch
    // it. The accepted picker token is consumed once; the second click must not overwrite with expired UI.
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'modify', work: undefined, workProfile: undefined, workTitle: undefined });
    A.lastCard.set(CHAT, MID); client.__reset();
    await A.onCardAction(cardEv(projectAction(PROJ.path, { do: 'sesslist', test: 'double-pick-list' }), MID));
    const doublePicker = await waitFor(() => settledCard(isSessionCard), 1000);
    const doublePick = actionValue(doublePicker, 'pick');
    client.__reset();
    const doubleDispatch = A.dispatchEvent('测试会话双击', A.onCardAction);
    doubleDispatch(Object.assign(cardEv(doublePick, MID), { event_id: 'double-pick-1' }));
    doubleDispatch(Object.assign(cardEv(doublePick, MID), { event_id: 'double-pick-2' }));
    await waitFor(() => A.controlCardWrites.size === 0 && A.getSession(CHAT).work === doublePick.s, 1000);
    await sleep(100);
    check('同一会话按钮双击只消费一次,不会把项目卡覆盖成失效卡',
      !client.__calls.some(c => c.state === 'settled' && /会话卡已失效/.test(c.title || '')),
      JSON.stringify(client.__calls));

    // 23. provider/session enumeration failures are an explicit retry state, never "no history".
    const normalSessionList = A.testHooks.sessionList;
    A.testHooks.sessionList = async () => ({ sessions: [], error: new Error('synthetic session read failure') });
    const errorCard = await A.buildSessionCard(CHAT, OWNER, {
      projectPath: PROJ.path,
      projectName: PROJ.name,
      profile,
      picker: { token: 'error-token', projectKey: A.sessionProjectKey(PROJ.path), projectPath: PROJ.path, profileId: profile.id },
      session: { mode: 'project', project: PROJ.path, sub: 'modify' },
    });
    A.testHooks.sessionList = normalSessionList;
    const errorCardJson = JSON.stringify(errorCard);
    check('会话读取失败显示重试状态,不会冒充没有历史',
      /暂时无法读取/.test(errorCardJson) && /重新加载/.test(errorCardJson) && !/还没有历史会话/.test(errorCardJson),
      errorCardJson);
  } finally {
    delete process.env.FEISHU_TEST_SESSION_LIST_DELAY_MS;
    delete process.env.FEISHU_TEST_SESSION_LOADING_DELAY_MS;
    A.testHooks.sessionList = null; A.testHooks.sessionPreview = null;
    client.__reset();
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
