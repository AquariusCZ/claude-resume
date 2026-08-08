// Regressions for the two user-reported failures:
//  A) 长跑没结果 —— a run that outlived the process (deploy/watchdog restart) left the user staring at
//     "进行中" forever. Now runs are tracked on disk and the next boot reports the interruption; and a
//     result whose CARD fails to send falls back to plain text instead of vanishing.
//  B) 发不了图片 —— an inbound Feishu image used to be dropped silently (message_type !== 'text').
//     Now it's downloaded, parked, and folded into the next text message for the selected AI.
// Also covers the heartbeat spam fix: one PATCHED progress card per run, not a message every 15s.
// Offline: mock Feishu client, runClaude stubbed (FEISHU_TEST_NO_CLAUDE). Run: node test/progress-image.js
'use strict';
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_NO_CLAUDE = '1';
process.env.FEISHU_TEST_API_TIMEOUT_MS = '50';
process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS = '100';
process.env.FEISHU_TEST_PROGRESS_INTERVAL_MS = '40';
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const testConfigHelper = require('./feishu-test-config');

const repoRoot = path.resolve(__dirname, '..');
const testConfig = testConfigHelper.prepareTestConfig({
  real: false,
  source: {
    enabled: true,
    feishuChatId: 'oc_progress_image_test',
    feishuAuthOpenIds: ['ou_progress_image_owner'],
    feishuAllowOpenIds: [],
    feishuChatProfile: 'openai-sol',
    customProjects: [{ name: 'AI Resume Migration', path: repoRoot }],
  },
});
process.once('exit', () => { try { testConfig.cleanup(); } catch (e) {} });
const INFLIGHT = path.join(testConfig.root, 'feishu-inflight.json');
const cfg = testConfig.config;
const CHAT = cfg.feishuChatId; assert(CHAT, 'need feishuChatId');
const OWNER = (cfg.feishuAuthOpenIds && cfg.feishuAuthOpenIds.filter(Boolean)[0]); assert(OWNER, 'need owner');

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;
const OWNER_QUEUE = A.imageQueueKey(CHAT, OWNER);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const waitFor = async (fn, timeout) => {
  const end = Date.now() + (timeout || 3000);
  while (Date.now() < end) { const value = fn(); if (value) return value; await sleep(25); }
  return null;
};
let n = 0;
const imgEv = (key, open, chat, chatType) => ({ message: { message_id: 'm_img_' + (++n) + '_' + Date.now(), chat_id: chat || CHAT, chat_type: chatType || 'p2p', message_type: 'image', content: JSON.stringify({ image_key: key || 'img_key_abcd1234' }) }, sender: { sender_id: { open_id: open || OWNER } } });
const txtEv = (t, open, chat, chatType) => ({ message: { message_id: 'm_txt_' + (++n) + '_' + Date.now(), chat_id: chat || CHAT, chat_type: chatType || 'p2p', message_type: 'text', content: JSON.stringify({ text: t }) }, sender: { sender_id: { open_id: open || OWNER } } });
// Mirrors Feishu im.message.receive_v1 for a picture + caption sent in ONE bubble. The locale wrapper,
// paragraph arrays, text elements and img.image_key fields are the production `post` shape.
const postEv = (content, open, chat, chatType) => ({ message: { message_id: 'm_post_' + (++n) + '_' + Date.now(), chat_id: chat || CHAT, chat_type: chatType || 'p2p', message_type: 'post', content: JSON.stringify(content) }, sender: { sender_id: { open_id: open || OWNER } } });
const fileEv = () => ({ message: { message_id: 'm_file_' + (++n) + '_' + Date.now(), chat_id: CHAT, chat_type: 'p2p', message_type: 'file', content: '{}' }, sender: { sender_id: { open_id: OWNER } } });
const texts = () => client.__calls.filter(c => c.op === 'create' && c.type === 'text').map(c => c.text || '');
const cards = () => client.__calls.filter(c => c.op === 'create' && c.type === 'interactive');

let failed = 0;
const check = (nm, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + nm + (c ? '' : ' — ' + x)); if (!c) failed++; };

async function main() {
  try {
    const PROJ = A.discoverProjects()[0]; assert(PROJ, 'need a project');

    // ---------- A. inbound images ----------
    client.__reset();
    await A.onMessage(imgEv());
    for (let i = 0; i < 20 && !texts().some(t => /收到图片/.test(t)); i++) await sleep(100);
    check('A1 发图片 → 被下载并回执(不再石沉大海)', texts().some(t => /收到图片/.test(t)), JSON.stringify(texts()));
    check('A2 确实调用了下载接口', client.__calls.some(c => c.op === 'downloadImage'), JSON.stringify(client.__calls.map(c => c.op)));
    const parked = A.pendingImages.get(OWNER_QUEUE) || [];
    check('A3 图片落到本地并挂起等下一条消息', parked.length === 1 && fs.existsSync(parked[0]), JSON.stringify(parked));

    // a second image stacks
    client.__reset();
    await A.onMessage(imgEv('img_key_second'));
    for (let i = 0; i < 20 && !texts().some(t => /共 2 张/.test(t)); i++) await sleep(100);
    check('A4 再发一张 → 累计 2 张', (A.pendingImages.get(OWNER_QUEUE) || []).length === 2, JSON.stringify(A.pendingImages.get(OWNER_QUEUE)));

    // the next TEXT message folds them into the prompt (owner path has Read)
    const before = (A.pendingImages.get(OWNER_QUEUE) || []).slice();
    const folded = A.withPendingImages(CHAT, OWNER, true, '这两张图什么问题?', []);
    check('A5 下一条文字 → 图片路径进入 prompt(claude 可 Read)',
      /view_image \/ Read/.test(folded.prompt) && before.every(f => folded.prompt.includes(f)) && folded.n === 2, folded.prompt.slice(0, 160));
    check('A6 取用后清空挂起(不会重复带入下一轮)', (A.pendingImages.get(OWNER_QUEUE) || []).length === 0);
    A.cleanupInboundImages(folded.files);

    // a viewer's run has no Read tool -> images are dropped, not silently pretended
    const tmpImg = path.join(A.imageInDir(CHAT, OWNER), 'viewer_case.png');
    fs.writeFileSync(tmpImg, Buffer.from('89504e470d0a1a0a', 'hex'));
    A.pendingImages.set(OWNER_QUEUE, [tmpImg]);
    const blocked = A.withPendingImages(CHAT, OWNER, false, '看看这个', []);
    check('A7 只读用户 → 图片被明确忽略(blocked)', blocked.blocked === true && blocked.prompt === '看看这个', JSON.stringify(blocked));
    check('A7b 被忽略的图片文件也被清理掉', !fs.existsSync(tmpImg), 'file still there');

    const filesBeforeSlowWrite = fs.readdirSync(A.imageInDir(CHAT, OWNER)).slice().sort();
    client.__reset(); client.__setBehavior({ downloadWriteDelayMs: 180 });
    await A.onMessage(imgEv('slow_write'));
    await sleep(250);
    check('A7c 下载落盘阶段超时会明确报错', texts().some(t => /下载失败/.test(t)), JSON.stringify(texts()));
    check('A7d 下载落盘超时不留部分文件或挂起图片',
      (A.pendingImages.get(OWNER_QUEUE) || []).length === 0 && JSON.stringify(fs.readdirSync(A.imageInDir(CHAT, OWNER)).slice().sort()) === JSON.stringify(filesBeforeSlowWrite),
      JSON.stringify(A.pendingImages.get(OWNER_QUEUE) || []));

    // other rich types answer instead of going silent
    client.__reset();
    await A.onMessage(fileEv());
    check('A8 发文件等其它类型 → 有明确提示,不静默', texts().some(t => /只支持文字和图片/.test(t)), JSON.stringify(texts()));

    // A real rich-text post: title + two image resources + caption text in localized paragraphs.
    const realPost = {
      zh_cn: {
        title: '',
        content: [
          [{ tag: 'img', image_key: 'img_post_left' }, { tag: 'img', image_key: 'img_post_right' }],
          [{ tag: 'text', text: '左右的平均值不一样吗，' }, { tag: 'text', text: '右边的是什么平均？' }],
        ],
      },
    };
    const parsed = A.parsePostContent(JSON.stringify(realPost));
    check('A9 真实 localized post → 同时提取完整问题和全部图片 key',
      parsed.ok && parsed.text === '左右的平均值不一样吗，右边的是什么平均？' &&
        JSON.stringify(parsed.imageKeys) === JSON.stringify(['img_post_left', 'img_post_right']), JSON.stringify(parsed));
    const flat = A.parsePostContent(JSON.stringify({ title: '补充', content: [[{ tag: 'a', text: '链接说明', href: 'https://example.test' }, { tag: 'img', image_key: 'img_flat' }]] }));
    check('A10 扁平 post 兼容 → 标题/链接文字/图片均保留',
      flat.ok && flat.text === '补充\n链接说明' && flat.imageKeys[0] === 'img_flat', JSON.stringify(flat));

    // End-to-end through the actual project-query route: post images must be downloaded and consumed
    // into this exact AI prompt, not parked for an unrelated future turn or rejected as unsupported.
    A.pendingImages.delete(OWNER_QUEUE); A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    A.testHooks.lastRun = null; client.__reset();
    await A.onMessage(postEv(realPost));
    for (let i = 0; i < 80 && !A.testHooks.lastRun; i++) await sleep(25);
    const postDownloads = client.__calls.filter(c => c.op === 'downloadImage');
    const postRun = A.testHooks.lastRun;
    check('A11 图文 post → 两张图片都按真实 message_id/file_key 下载',
      postDownloads.length === 2 && postDownloads.every(c => /^m_post_/.test(c.messageId)) &&
        JSON.stringify(postDownloads.map(c => c.fileKey)) === JSON.stringify(['img_post_left', 'img_post_right']), JSON.stringify(postDownloads));
    check('A12 图文 post → 文字和两张本地图片进入同一次项目查询 prompt',
      !!postRun && /左右的平均值不一样吗/.test(postRun.prompt) && /view_image \/ Read/.test(postRun.prompt) &&
        (postRun.prompt.match(/feishu-in/gi) || []).length >= 2, postRun && postRun.prompt.slice(-500));
    check('A13 图文 post → 查询已消费图片且不再回复“不支持 post”',
      (A.pendingImages.get(OWNER_QUEUE) || []).length === 0 && !texts().some(t => /收到的是 post|不支持.*post/.test(t)), JSON.stringify(texts()));
    const postPaths = (postRun && postRun.prompt || '').split(/\r?\n/).map(x => x.trim()).filter(x => /^[A-Za-z]:[\\/].*feishu-in/i.test(x));
    const postFilesCleaned = await waitFor(() => postPaths.length === 2 && postPaths.every(f => !fs.existsSync(f)), 2000);
    check('A14 查询结束 → 本轮入站图片从磁盘删除', !!postFilesCleaned, JSON.stringify(postPaths));

    const malformed = A.parsePostContent('{bad json');
    check('A15 损坏的 post JSON → 明确解析失败而非抛异常', !malformed.ok && malformed.error === 'invalid_json', JSON.stringify(malformed));
    const localeFallback = A.parsePostContent(JSON.stringify({
      zh_cn: { title: '', content: [] },
      en_us: { title: '', content: [[{ tag: 'img', image_key: 'img_en' }, { tag: 'text', text: 'English question' }]] },
    }));
    check('A16 首选语言为空壳 → 回退到下一种有内容的 locale',
      localeFallback.text === 'English question' && localeFallback.imageKeys[0] === 'img_en', JSON.stringify(localeFallback));
    const richTags = A.parsePostContent(JSON.stringify({
      zh_cn: { title: '富文本标题', content: [[
        { tag: 'md', text: '说明' }, { tag: 'at', user_id: 'ou_only_id' }, { tag: 'at', user_id: 'all' },
        { tag: 'hr' }, { tag: 'code_block', text: 'x = 1' },
        { tag: 'img', image_key: 'img_dup' }, { tag: 'img', image_key: 'img_dup' },
      ]] },
    }));
    check('A16b post 富文本标签 → title/md/at user_id/@all/hr/code_block 保留且图片去重',
      richTags.text === '富文本标题\n说明@ou_only_id@all---x = 1' && JSON.stringify(richTags.imageKeys) === JSON.stringify(['img_dup']),
      JSON.stringify(richTags));

    // The post owns its inline files. A later standalone image may be parked for a FUTURE turn but
    // must never join the already-dispatched question while its notification send is still pending.
    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    A.testHooks.lastRun = null; client.__reset(); client.__setBehavior({ createDelayMs: 30 });
    const racePost = { zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_race_a' }], [{ tag: 'text', text: '只看图片 A' }]] } };
    const dispatched = A.dispatchEvent('post-image-race', A.onMessage);
    dispatched(postEv(racePost));
    dispatched(imgEv('img_race_b'));
    const raceRun = await waitFor(() => A.testHooks.lastRun, 3000);
    await waitFor(() => (A.pendingImages.get(OWNER_QUEUE) || []).length === 1, 3000);
    check('A17 后到图片 B 不会串入正在派发的问题 A',
      !!raceRun && (raceRun.prompt.match(/feishu-in/gi) || []).length === 1 && (A.pendingImages.get(OWNER_QUEUE) || []).length === 1,
      raceRun && raceRun.prompt.slice(-400));
    const late = A.withPendingImages(CHAT, OWNER, true, 'cleanup', []); A.cleanupInboundImages(late.files);
    const racePaths = (raceRun && raceRun.prompt || '').split(/\r?\n/).map(x => x.trim()).filter(x => /^[A-Za-z]:[\\/].*feishu-in/i.test(x));
    await waitFor(() => racePaths.length === 1 && racePaths.every(f => !fs.existsSync(f)), 2000);

    // A busy query rejects the whole post atomically: its inline image is deleted, never attached to
    // the next unrelated turn.
    const BUSY_CHAT = 'oc_busy_image_test';
    A.setSession(BUSY_CHAT, { mode: 'project', project: PROJ.path, sub: 'query' });
    const qk = A.querySession(PROJ.path, OWNER).cwd.toLowerCase();
    A.running.set(qk, { pid: 999999 }); A.testHooks.lastRun = null; client.__reset();
    await A.onMessage(postEv({ zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_busy' }], [{ tag: 'text', text: '忙碌时的问题' }]] } }, OWNER, BUSY_CHAT));
    A.running.delete(qk);
    check('A18 查询忙碌 → 本条 post 图片回滚且不启动 AI',
      !A.testHooks.lastRun && (A.pendingImages.get(A.imageQueueKey(BUSY_CHAT, OWNER)) || []).length === 0 && fs.readdirSync(A.imageInDir(BUSY_CHAT, OWNER)).length === 0,
      JSON.stringify({ texts: texts(), files: fs.readdirSync(A.imageInDir(BUSY_CHAT, OWNER)) }));

    // Idle one-off MODIFY is a separate route from project mode and must carry the same post image.
    A.setSession(CHAT, { mode: 'idle' }); A.testHooks.lastRun = null; client.__reset();
    const oneOffPost = { zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_oneoff' }], [{ tag: 'text', text: `${PROJ.name} 根据图片做一次测试修改` }]] } };
    await A.onMessage(postEv(oneOffPost));
    const oneOffRun = await waitFor(() => A.testHooks.lastRun && A.testHooks.lastRun.options.taskKind === 'modify' && A.testHooks.lastRun, 3000);
    check('A19 idle 一次性修改 → 图文进入同一次 modify prompt',
      !!oneOffRun && /根据图片做一次测试修改/.test(oneOffRun.prompt) && (oneOffRun.prompt.match(/feishu-in/gi) || []).length === 1,
      oneOffRun && oneOffRun.prompt.slice(-400));
    const oneOffPaths = (oneOffRun && oneOffRun.prompt || '').split(/\r?\n/).map(x => x.trim()).filter(x => /^[A-Za-z]:[\\/].*feishu-in/i.test(x));
    check('A20 一次性修改结束 → 入站图片删除', !!(await waitFor(() => oneOffPaths.length === 1 && oneOffPaths.every(f => !fs.existsSync(f)), 2000)), JSON.stringify(oneOffPaths));

    // Group chats share chat_id, so sender identity must be part of both the in-memory queue and disk
    // directory. A viewer's command post must not inject an image into the owner's later modify run.
    const VIEWER = 'ou_image_viewer_test', GROUP = 'oc_image_group_test';
    client.__reset();
    await A.onMessage(postEv({ zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_viewer' }], [{ tag: 'text', text: '帮助' }]] } }, VIEWER, GROUP, 'group'));
    check('A21 viewer 的命令 post 未消费图片 → handler 结束即删除', fs.readdirSync(A.imageInDir(GROUP, VIEWER)).length === 0, 'viewer image remained');
    A.setSession(GROUP, { mode: 'project', project: PROJ.path, sub: 'modify', work: 'new' });
    A.testHooks.lastRun = null; client.__reset();
    await A.onMessage(txtEv('owner 后续修改', OWNER, GROUP, 'group'));
    const ownerGroupRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A22 群聊图片按发送者隔离 → viewer 不能注入 owner 修改 prompt',
      !!ownerGroupRun && !/view_image \/ Read/.test(ownerGroupRun.prompt) && !/feishu-in/i.test(ownerGroupRun.prompt), ownerGroupRun && ownerGroupRun.prompt.slice(-300));

    // Bound disk use even when the platform hands the SDK an unexpectedly large resource.
    A.setSession(CHAT, { mode: 'idle' }); client.__reset(); client.__setBehavior({ downloadSizeBytes: 10 * 1024 * 1024 + 1 });
    await A.onMessage(imgEv('img_oversize'));
    check('A23 入站图片超过 10MB → 拒绝并删除落盘文件',
      texts().some(t => /超过 10MB/.test(t)) && fs.readdirSync(A.imageInDir(CHAT, OWNER)).length === 0 && (A.pendingImages.get(OWNER_QUEUE) || []).length === 0,
      JSON.stringify(texts()));

    // An enabled sender allow-list is an authentication boundary. A malformed event without open_id
    // must fail closed before any image resource request.
    A.updateConfig(c => { c.feishuAllowOpenIds = [OWNER]; }); client.__reset();
    const missingSender = postEv(realPost); missingSender.sender = { sender_id: {} };
    await A.onMessage(missingSender);
    check('A24 allowlist 启用时缺失 open_id → 下载前拒绝', !client.__calls.some(c => c.op === 'downloadImage'), JSON.stringify(client.__calls));
    client.__reset();
    await A.onCardAction({ context: { open_chat_id: CHAT, open_message_id: 'msg_missing_sender' }, action: { value: { do: 'home' } }, operator: {} });
    await A.onBotMenu({ event_key: 'menu', operator: {} });
    check('A24b 卡片/底部菜单缺失 open_id → 同样拒绝且不写消息', !client.__calls.some(c => c.op === 'create' || c.op === 'patch'), JSON.stringify(client.__calls));
    client.__reset();
    await A.onMessage(postEv(realPost, 'ou_not_allowlisted_image_test'));
    check('A24c allowlist 启用时有效但未命中的 open_id → 下载前拒绝', !client.__calls.some(c => c.op === 'downloadImage'), JSON.stringify(client.__calls));
    A.updateConfig(c => { c.feishuAllowOpenIds = Array.isArray(cfg.feishuAllowOpenIds) ? cfg.feishuAllowOpenIds : []; });

    client.__reset(); A.setConfigReadFailureForTest(true);
    await A.onMessage(postEv(realPost));
    await A.onCardAction({ context: { open_chat_id: CHAT, open_message_id: 'msg_config_fail' }, action: { value: { do: 'home' } }, operator: { open_id: OWNER } });
    await A.onBotMenu({ event_key: 'menu', operator: { operator_id: { open_id: OWNER } } });
    A.setConfigReadFailureForTest(false);
    check('A24d config.json 读取失败 → 消息/卡片/菜单全部 fail-closed',
      !client.__calls.some(c => c.op === 'downloadImage' || c.op === 'create' || c.op === 'patch'), JSON.stringify(client.__calls));

    A.setSession(CHAT, { mode: 'project', project: PROJ.path, sub: 'query' }); A.testHooks.lastRun = null; client.__reset();
    const sevenImages = { zh_cn: { title: '', content: [[...Array.from({ length: 7 }, (_, i) => ({ tag: 'img', image_key: 'img_limit_' + i }))], [{ tag: 'text', text: '多图上限测试' }]] } };
    await A.onMessage(postEv(sevenImages));
    const limitRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A25 单条 post 超过 6 图 → 明示上限且只把前 6 图交给 AI',
      !!limitRun && (limitRun.prompt.match(/feishu-in/gi) || []).length === 6 && client.__calls.filter(c => c.op === 'downloadImage').length === 6 &&
        texts().some(t => /1 张超过单条最多 6 张/.test(t)), JSON.stringify(texts()));

    // A warning about omitted/failed resources is secondary. Even if that notification exhausts its
    // network retry, the valid caption and first six images must still reach the run.
    A.testHooks.lastRun = null; client.__reset(); client.__setBehavior({ createErrorsRemaining: 2 });
    await A.onMessage(postEv(sevenImages));
    const warningFailRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A26 图片告警发送失败 → 不阻断有效图文主请求',
      !!warningFailRun && /多图上限测试/.test(warningFailRun.prompt) && (warningFailRun.prompt.match(/feishu-in/gi) || []).length === 6,
      warningFailRun && warningFailRun.prompt.slice(-500));

    // The final AI turn, not just each source queue, is capped. Current post images take precedence
    // over older standalone images so the caption never loses its directly attached resources.
    const combinedDir = A.imageInDir(CHAT, OWNER);
    const combinedPending = Array.from({ length: 6 }, (_, i) => {
      const f = path.join(combinedDir, `combined_pending_${i}.png`); fs.writeFileSync(f, Buffer.from([i + 1])); return f;
    });
    A.pendingImages.set(OWNER_QUEUE, combinedPending);
    A.testHooks.lastRun = null; client.__reset();
    const combinedPost = { zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'inline_a' }, { tag: 'img', image_key: 'inline_b' }], [{ tag: 'text', text: '合并上限测试' }]] } };
    await A.onMessage(postEv(combinedPost));
    const combinedRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A27 暂存图 + post 图 → 最终单次请求仍最多 6 张且 post 图优先',
      !!combinedRun && (combinedRun.prompt.match(/feishu-in/gi) || []).length === 6 && /inline_a/.test(combinedRun.prompt) && /inline_b/.test(combinedRun.prompt) &&
        texts().some(t => /另有 2 张未交给 AI/.test(t)), combinedRun && combinedRun.prompt.slice(-700));
    check('A27b 合并超额文件和已消费文件最终都被清理',
      !!(await waitFor(() => fs.readdirSync(combinedDir).length === 0 && !A.pendingImages.has(OWNER_QUEUE), 2000)),
      JSON.stringify({ files: fs.readdirSync(combinedDir), pending: A.pendingImages.get(OWNER_QUEUE) }));

    const stale = path.join(combinedDir, 'stale_pending.png'); fs.writeFileSync(stale, Buffer.from([1]));
    fs.utimesSync(stale, new Date(Date.now() - 25 * 60 * 60 * 1000), new Date(Date.now() - 25 * 60 * 60 * 1000));
    A.pendingImages.set(OWNER_QUEUE, [stale]);
    const staleRemoved = A.cleanupOldInboundImages(Date.now());
    check('A28 24h 孤儿清理 → 同步删除磁盘文件和内存队列', staleRemoved === 1 && !fs.existsSync(stale) && !A.pendingImages.has(OWNER_QUEUE),
      JSON.stringify({ staleRemoved, pending: A.pendingImages.get(OWNER_QUEUE) }));

    A.testHooks.lastRun = null; client.__reset(); client.__setBehavior({ downloadFailKeys: ['img_partial_bad'] });
    const partialPost = { zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_partial_ok' }, { tag: 'img', image_key: 'img_partial_bad' }], [{ tag: 'text', text: '部分下载测试' }]] } };
    await A.onMessage(postEv(partialPost));
    const partialRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A29 post 部分图片下载失败 → 其余图片和文字仍进入同一请求',
      !!partialRun && /部分下载测试/.test(partialRun.prompt) && (partialRun.prompt.match(/feishu-in/gi) || []).length === 1 && texts().some(t => /1 张下载失败/.test(t)),
      JSON.stringify(texts()));

    A.testHooks.lastRun = null; client.__reset(); client.__setBehavior({ downloadSizeBytes: 10 * 1024 * 1024 + 1 });
    await A.onMessage(postEv({ zh_cn: { title: '', content: [[{ tag: 'img', image_key: 'img_post_oversize' }], [{ tag: 'text', text: '超大图仍处理文字' }]] } }));
    const oversizedPostRun = await waitFor(() => A.testHooks.lastRun, 3000);
    check('A30 post 图片超过 10MB → 拒绝图片但仍处理文字且不留文件',
      !!oversizedPostRun && /超大图仍处理文字/.test(oversizedPostRun.prompt) && !/feishu-in/i.test(oversizedPostRun.prompt) &&
        texts().some(t => /1 张下载失败/.test(t)) && fs.readdirSync(combinedDir).length === 0,
      JSON.stringify(texts()));

    // ---------- B. progress card: ONE message, patched ----------
    client.__reset(); A.lastCard.set(CHAT, 'control_mid');
    const prog = await A.startProgress(CHAT, '测试项目');
    const firstCards = cards().length;
    check('B1 开跑 → 只发 1 张进度卡', firstCards === 1 && /运行中/.test(cards()[0].title || ''), JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    await sleep(1100);
    check('B1b 进度 tick 只更新独立进度卡,不抢占控制卡', client.__calls.some(c => c.op === 'patch') && A.lastCard.get(CHAT) === 'control_mid', JSON.stringify(client.__calls));
    await prog.stop({ ok: true, ms: 42000 });
    check('B2 结束 → 原地 patch 成完成态(没有再发新消息)',
      cards().length === firstCards && client.__calls.some(c => c.op === 'patch'), JSON.stringify(client.__calls.map(c => c.op + ':' + (c.title || c.type))));
    check('B3 一次运行的进度开销 = 1 条消息(旧版每 15s 一条)', cards().length === 1, 'cards=' + cards().length);
    check('B4 进度 stop 后仍不改变当前控制卡', A.lastCard.get(CHAT) === 'control_mid', 'lastCard=' + A.lastCard.get(CHAT));

    // A tick already in flight must settle before the completion patch. Without a per-progress write
    // queue, the fast completion patch could finish first and then be overwritten by the old tick.
    client.__reset(); client.__setBehavior({ patchDelaysMs: [40, 5] });
    const raceProg = await A.startProgress(CHAT, '乱序测试');
    for (let i = 0; i < 40 && !client.__calls.some(c => c.op === 'patch' && c.state === 'started'); i++) await sleep(20);
    await raceProg.stop({ ok: true, ms: 1000 });
    await sleep(50);
    const racePatches = client.__calls.filter(c => c.op === 'patch' && c.state === 'settled');
    check('B5 慢 tick 与完成态并发时,最终写入仍是完成态',
      racePatches.length >= 2 && /已完成/.test(racePatches[racePatches.length - 1].content || ''),
      JSON.stringify(racePatches.map(c => ({ title: c.title, content: c.content }))));

    // If a tick exceeds the API timeout, its underlying SDK request may still land later. The final
    // state must move to a fresh message so that late completion can only mutate the older card above it.
    client.__reset(); client.__setBehavior({ patchDelaysMs: [100, 5] });
    const timeoutProg = await A.startProgress(CHAT, '超时乱序测试');
    for (let i = 0; i < 40 && !client.__calls.some(c => c.op === 'patch' && c.state === 'started'); i++) await sleep(20);
    await timeoutProg.stop({ ok: true, ms: 1000 });
    await sleep(120);
    const timeoutCards = cards();
    check('B6 tick 超时后用新完成卡兜底,旧请求晚到也覆盖不了最终可见状态',
      timeoutCards.length === 2 && /已完成/.test(timeoutCards[1].text || ''),
      JSON.stringify(client.__calls.map(c => ({ op: c.op, state: c.state, title: c.title, text: c.text }))));

    // ---------- C. interrupted runs are reported after a restart ----------
    try { fs.unlinkSync(INFLIGHT); } catch (e) {}
    const done = A.trackRun(CHAT, '某项目', '执行');
    check('C1 运行中会落盘(重启后可发现)', fs.existsSync(INFLIGHT), 'no inflight file');
    // simulate a crash: DON'T call done(); a new boot reads the file
    client.__reset();
    await A.reportInterruptedRuns();
    check('C2 重启后告知用户上次运行被打断', texts().some(t => /被打断|中断/.test(t) && /某项目/.test(t)), JSON.stringify(texts()));
    check('C3 汇报后清空记录(不会重复打扰)', !fs.existsSync(INFLIGHT), 'inflight file still there');
    done();
    // normal completion leaves nothing behind
    const done2 = A.trackRun(CHAT, '另一个', '查询'); done2();
    client.__reset();
    await A.reportInterruptedRuns();
    check('C4 正常跑完的运行不会被误报为中断', !texts().some(t => /被打断/.test(t)), JSON.stringify(texts()));

    // ---------- D. a result must never vanish ----------
    // make the card send fail (mock throws on 'gone'), assert the answer still arrives as text
    const realCreate = client.im.message.create;
    client.__reset();
    client.im.message.create = async (o) => { if (o.data.msg_type === 'interactive') throw new Error('mock: card rejected'); return realCreate(o); };
    try {
      await A.sendResult(CHAT, '✅ 完成 · 测试', '这是**结果**正文,不能丢。', { ok: true, ms: 1000 }, 'green');
    } finally { client.im.message.create = realCreate; }
    check('D1 结果卡片发送失败 → 自动回退纯文本,结果不丢',
      texts().some(t => /这是\*\*结果\*\*正文|结果.*正文/.test(t)), JSON.stringify(texts()));
  } finally {
    try { for (const f of (A.pendingImages.get(OWNER_QUEUE) || [])) { try { fs.unlinkSync(f); } catch (e) {} } } catch (e) {}
    A.pendingImages.delete(OWNER_QUEUE);
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
