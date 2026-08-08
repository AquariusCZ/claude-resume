// Stage 1 S1-G ChannelAdapter 离线契约测试。
// 纯离线:不加载 feishu-agent、不连飞书、不启动 AI;用录制式 mock client 与注入的假 SDK
// 验证依赖 fail-fast、chat/open_id 目标、create/patch/upload/resource 形状、网络错误仅重试
// 一次、超时不重试、上传不重试、ACK 立即返回、同 key 保序、跨 key 并发、同步 handler 前缀
// 不阻塞 ACK、handler 异常隔离、v2 注册失败回退。
// Run: node test/channel-adapter.js
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const { createChannelAdapter, makeSanitizedSdkLogger } = require(path.join(__dirname, '..', 'src', 'channel-adapter.js'));

const sleep = ms => new Promise(r => setTimeout(r, ms));
const waitFor = async (fn, timeout) => {
  const end = Date.now() + (timeout || 3000);
  while (Date.now() < end) { const value = fn(); if (value) return value; await sleep(20); }
  return null;
};

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + x)); if (!c) failed++; };
const throwsSync = (fn, re) => {
  try { fn(); return false; }
  catch (e) { return re.test(String(e && e.message)); }
};
const rejects = async (p, matcher) => {
  try { await p; return false; }
  catch (e) { return typeof matcher === 'function' ? matcher(e) : matcher.test(String(e && e.message)); }
};

async function main() {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'channel-adapter-' + process.pid + '-'));
  try {
    // 1. SDK 缺失且非测试模式 → fail-fast;测试模式无需 SDK
    check('SDK 缺失且非测试模式 → fail-fast',
      throwsSync(() => createChannelAdapter({ testMode: false, sdk: null }), /缺少依赖 @larksuiteoapi\/node-sdk/),
      'no throw');
    const chMock = createChannelAdapter({ testMode: true });
    check('测试模式无需 SDK,创建录制式 mock client',
      !!chMock.client && Array.isArray(chMock.client.__calls) && typeof chMock.client.__reset === 'function',
      'missing mock surface');

    // 2. chat_id / od:open_id 目标转换
    await chMock.createMessage('oc_chat_x', 'text', JSON.stringify({ text: 'hi' }));
    const chatCall = chMock.client.__calls[0];
    check('chat_id 目标 → receive_id_type=chat_id 且 receive_id 原样',
      chatCall.toType === 'chat_id' && chatCall.to === 'oc_chat_x', JSON.stringify(chatCall));
    chMock.client.__reset();
    await chMock.createMessage('od:ou_user_x', 'text', JSON.stringify({ text: 'hi' }));
    const openCall = chMock.client.__calls[0];
    check('od:open_id 目标 → receive_id_type=open_id 且去掉 od: 前缀',
      openCall.toType === 'open_id' && openCall.to === 'ou_user_x', JSON.stringify(openCall));

    // 3. create/patch/upload/resource 真实 API 形状
    chMock.client.__reset();
    const createRes = await chMock.createMessage('oc_c', 'interactive', JSON.stringify({ elements: [] }));
    check('message.create 保持 {data:{message_id}} 形状',
      !!(createRes && createRes.data && /^msg_/.test(createRes.data.message_id)), JSON.stringify(createRes));
    const patchRes = await chMock.patchMessage('msg_p', JSON.stringify({ elements: [] }));
    check('message.patch 正常返回且记录目标 message_id',
      patchRes !== undefined && chMock.client.__calls.some(c => c.op === 'patch' && c.id === 'msg_p'),
      JSON.stringify(chMock.client.__calls));
    const tmpPng = path.join(tmpDir, 'a.png');
    fs.writeFileSync(tmpPng, Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
    const imageKey = await chMock.uploadImage(tmpPng);
    check('im.image.create 顶层 {image_key} 被正确取出',
      typeof imageKey === 'string' && /^imgkey_/.test(imageKey), 'key=' + imageKey);
    const resourceRes = await chMock.getMessageResource('m1', 'f1');
    check('messageResource.get 原样返回(writeFile helper 由 agent 落盘)',
      !!(resourceRes && typeof resourceRes.writeFile === 'function'), JSON.stringify(resourceRes));

    // 4. 网络错误仅重试一次,且 create 稳定 uuid 在重试中复用
    chMock.client.__reset(); chMock.client.__setBehavior({ createErrorsRemaining: 1 });
    await chMock.createMessage('oc_c', 'text', JSON.stringify({ text: 'r' }));
    const retried = chMock.client.__calls.filter(c => c.op === 'create');
    check('网络错误仅重试一次 → 恰好 2 次 create(1 失败 + 1 成功)',
      retried.length === 2 && retried[0].state === 'failed' && retried[1].state === 'settled',
      JSON.stringify(retried.map(c => c.op + ':' + c.state)));
    check('显式网络重试复用同一稳定 uuid',
      !!retried[0].uuid && retried[0].uuid === retried[1].uuid,
      JSON.stringify(retried.map(c => c.uuid)));
    chMock.client.__reset(); chMock.client.__setBehavior({ createErrorsRemaining: 2 });
    check('第二次失败不再继续重试(至多一次重试)',
      await rejects(chMock.createMessage('oc_c', 'text', JSON.stringify({ text: 'r2' })), /ECONNRESET/) &&
        chMock.client.__calls.filter(c => c.op === 'create').length === 2,
      JSON.stringify(chMock.client.__calls.map(c => c.op + ':' + c.state)));

    // 5. 超时不重试
    process.env.FEISHU_TEST_API_TIMEOUT_MS = '120';
    process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS = '300';
    const chTimeout = createChannelAdapter({ testMode: true });
    chTimeout.client.__setBehavior({ createHang: true });
    check('超时(AI_RESUME_FEISHU_TIMEOUT)不重试 → 恰好 1 次 create',
      await rejects(chTimeout.createMessage('oc_c', 'text', JSON.stringify({ text: 't' })),
        e => e && e.code === 'AI_RESUME_FEISHU_TIMEOUT') &&
        chTimeout.client.__calls.filter(c => c.op === 'create').length === 1,
      JSON.stringify(chTimeout.client.__calls.map(c => c.op + ':' + c.state)));

    // 6. 上传不重试:网络错误不重试,超时也不重试
    chMock.client.__reset();
    const realUpload = chMock.client.im.image.create;
    let uploadAttempts = 0;
    chMock.client.im.image.create = async o => { uploadAttempts++; if (uploadAttempts === 1) throw new Error('socket disconnected'); return realUpload(o); };
    check('上传遇网络错误不重试 → 恰好 1 次尝试',
      await rejects(chMock.uploadImage(tmpPng), /socket disconnected/) && uploadAttempts === 1,
      'attempts=' + uploadAttempts);
    chMock.client.im.image.create = realUpload;
    process.env.FEISHU_TEST_API_TIMEOUT_MS = '60';
    process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS = '120';
    const chUpload = createChannelAdapter({ testMode: true });
    chUpload.client.__setBehavior({ uploadDelayMs: 400 });
    check('上传超时也不重试 → 恰好 1 次 uploadImage',
      await rejects(chUpload.uploadImage(tmpPng), e => e && e.code === 'AI_RESUME_FEISHU_TIMEOUT') &&
        chUpload.client.__calls.filter(c => c.op === 'uploadImage').length === 1,
      JSON.stringify(chUpload.client.__calls.map(c => c.op + ':' + c.state)));
    delete process.env.FEISHU_TEST_API_TIMEOUT_MS;
    delete process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS;

    // 7. 资源下载请求保留现役单次网络重试
    chMock.client.__reset();
    const realResource = chMock.client.im.messageResource.get;
    let resourceAttempts = 0;
    chMock.client.im.messageResource.get = async o => { resourceAttempts++; if (resourceAttempts === 1) throw new Error('ECONNRESET'); return realResource(o); };
    const resourceRetried = await chMock.getMessageResource('m1', 'f1');
    chMock.client.im.messageResource.get = realResource;
    check('资源下载网络错误重试一次并成功',
      resourceAttempts === 2 && !!(resourceRetried && typeof resourceRetried.writeFile === 'function'),
      'attempts=' + resourceAttempts);

    // 8. 事件注册:ACK 立即返回(慢 handler 不阻塞)
    const logs = [];
    const chDispatch = createChannelAdapter({ testMode: true, onLog: m => logs.push(m) });
    const slowDispatch = chDispatch.dispatchEvent('慢 handler', async () => { await sleep(200); }, e => e.chat_id);
    const ackAt = Date.now();
    const ackResult = slowDispatch({ chat_id: 'c1' });
    check('dispatch 立即返回 ACK(慢 handler 不阻塞且不暴露后台 Promise)',
      Date.now() - ackAt < 50 && ackResult === undefined,
      'ackMs=' + (Date.now() - ackAt) + ' result=' + String(ackResult));

    // 9. 同 key 严格保序
    const order = [];
    const orderedDispatch = chDispatch.dispatchEvent('保序', async ev => {
      order.push('start' + ev.n);
      if (ev.n === 1) await sleep(80);
      order.push('end' + ev.n);
    }, e => e.chat_id);
    const orderedAt = Date.now();
    orderedDispatch({ chat_id: 'c1', n: 1 });
    orderedDispatch({ chat_id: 'c1', n: 2 });
    const orderedAckMs = Date.now() - orderedAt;
    await waitFor(() => order.length === 4, 500);
    check('同 key 严格按到达顺序执行且 dispatch 均秒回',
      orderedAckMs < 50 && order.join(',') === 'start1,end1,start2,end2',
      `ackMs=${orderedAckMs} order=${order.join(',')}`);

    // 10. 跨 key 可并发
    const conc = [];
    const concurrentDispatch = chDispatch.dispatchEvent('并发', async ev => {
      conc.push(ev.k + '-start');
      if (ev.k === 'a') await sleep(120);
      conc.push(ev.k + '-end');
    }, e => e.chat_id);
    concurrentDispatch({ chat_id: 'cA', k: 'a' });
    concurrentDispatch({ chat_id: 'cB', k: 'b' });
    await waitFor(() => conc.length === 4, 500);
    check('跨 key 并发执行(b 不等待 a 完成)',
      conc.indexOf('b-start') < conc.indexOf('a-end') && conc.indexOf('b-end') < conc.indexOf('a-end'),
      conc.join(','));

    // 11. handler 同步前缀不阻塞 ACK
    let syncHandlerFinished = false;
    const syncDispatch = chDispatch.dispatchEvent('同步前缀', () => {
      const until = Date.now() + 150;
      while (Date.now() < until) {}
      syncHandlerFinished = true;
    }, e => e.chat_id);
    const syncAt = Date.now();
    syncDispatch({ chat_id: 'c1' });
    check('同步 handler 前缀不阻塞 ACK',
      Date.now() - syncAt < 50 && !syncHandlerFinished,
      `ms=${Date.now() - syncAt} finished=${syncHandlerFinished}`);
    await waitFor(() => syncHandlerFinished, 500);

    // 12. handler 异常隔离:只记录,不中断同 key 后续事件
    const throwingDispatch = chDispatch.dispatchEvent('异常', () => { throw new Error('boom'); }, e => e.chat_id);
    throwingDispatch({ chat_id: 'c1' });
    await waitFor(() => logs.some(m => /异常/.test(m) && /boom/.test(m)), 500);
    let afterRun = false;
    const afterDispatch = chDispatch.dispatchEvent('异常后', () => { afterRun = true; }, e => e.chat_id);
    afterDispatch({ chat_id: 'c1' });
    await waitFor(() => afterRun, 500);
    check('handler 异常被隔离记录且队列不卡死',
      logs.some(m => /异常/.test(m) && /boom/.test(m)) && afterRun, JSON.stringify(logs));

    // 13. v2 注册失败 → 保留 v1/card/menu/noop 回退;正常注册保留 v2
    const failDispatchers = [];
    class FailDispatcher {
      constructor() { failDispatchers.push(this); this.registered = null; }
      register(h) {
        this.registered = Object.assign({}, h);
        if (h['im.message.receive_v2']) throw new Error('v2 register failed');
        return this;
      }
    }
    const wsClients = [];
    class FakeWSClient {
      constructor(o) { this.opts = o; wsClients.push(this); this.started = null; }
      start(o) { this.started = o; }
    }
    const sdkStub = { Client: class Client { constructor(o) { this.opts = o; } }, EventDispatcher: FailDispatcher, WSClient: FakeWSClient };
    const chFallback = createChannelAdapter({ testMode: false, sdk: sdkStub, appId: 'app_x', appSecret: 'sec_y', onLog: m => logs.push(m) });
    const handlers = {
      'im.message.receive_v1': () => {},
      'im.message.receive_v2': () => {},
      'card.action.trigger': () => {},
      'application.bot.menu_v6': () => {},
      'im.message.message_read_v1': async () => {},
      'im.message.reaction.created_v1': async () => {},
      'im.message.reaction.deleted_v1': async () => {},
      'im.message.recalled_v1': async () => {},
      'im.message.bot_muted_v1': async () => {},
    };
    let readyCallbacks = 0;
    chFallback.start({ handlers, onReady: () => { readyCallbacks++; } });
    const fallbackHandlers = failDispatchers[1] && failDispatchers[1].registered;
    check('v2 注册失败 → 回退注册保留 v1/card/menu/noop 并去掉 v2',
      failDispatchers.length === 2 &&
        fallbackHandlers && fallbackHandlers['im.message.receive_v1'] &&
        fallbackHandlers['card.action.trigger'] && fallbackHandlers['application.bot.menu_v6'] &&
        fallbackHandlers['im.message.message_read_v1'] &&
        !('im.message.receive_v2' in fallbackHandlers),
      JSON.stringify(failDispatchers.map(d => d.registered && Object.keys(d.registered))));
    check('回退后 WS 仍启动且使用回退 dispatcher',
      wsClients.length === 1 && wsClients[0].started && wsClients[0].started.eventDispatcher === failDispatchers[1],
      'wsClients=' + wsClients.length);
    if (wsClients[0] && typeof wsClients[0].opts.onReady === 'function') wsClients[0].opts.onReady();
    check('WSClient 原生 onReady 回调透传给 runtime 生成结构化就绪标记',
      readyCallbacks === 1, `readyCallbacks=${readyCallbacks}`);
    check('回退原因被记录',
      logs.some(m => /回退消息 v1/.test(m) && /v2 register failed/.test(m)), JSON.stringify(logs));
    check('Client 使用传入的 appId/appSecret',
      chFallback.client && chFallback.client.opts && chFallback.client.opts.appId === 'app_x' && chFallback.client.opts.appSecret === 'sec_y',
      JSON.stringify(chFallback.client && chFallback.client.opts));

    const okDispatchers = [];
    class OkDispatcher {
      constructor() { okDispatchers.push(this); this.registered = null; }
      register(h) { this.registered = Object.assign({}, h); return this; }
    }
    const wsClients2 = [];
    class FakeWSClient2 {
      constructor(o) { this.opts = o; wsClients2.push(this); this.started = null; }
      start(o) { this.started = o; }
    }
    const chOk = createChannelAdapter({
      testMode: false,
      sdk: { Client: class Client {}, EventDispatcher: OkDispatcher, WSClient: FakeWSClient2 },
      appId: 'app', appSecret: 'sec',
    });
    chOk.start({ handlers });
    check('v2 注册成功 → 保留 v2 且 WS 启动一次',
      okDispatchers.length === 1 && !!okDispatchers[0].registered['im.message.receive_v2'] &&
        wsClients2.length === 1 && !!wsClients2[0].started && !!wsClients2[0].started.eventDispatcher,
      'dispatchers=' + okDispatchers.length + ' ws=' + wsClients2.length);
    // 12. D-013:真实模式 Client/WSClient 必须收到脱敏 logger;token 失败的 axios
    // 错误对象(含明文 app_secret、循环引用)绝不能以明文进入日志输出。
    const secretVal = 'super_secret_value_9f8e7d';
    const builtClients = [];
    class LoggerClient { constructor(o) { builtClients.push(o); } }
    const loggerWsClients = [];
    class LoggerWSClient { constructor(o) { loggerWsClients.push(o); } start() {} }
    class LoggerDispatcher { register(h) { return this; } }
    const chLogger = createChannelAdapter({
      testMode: false,
      sdk: { Client: LoggerClient, EventDispatcher: LoggerDispatcher, WSClient: LoggerWSClient },
      appId: 'app_z', appSecret: secretVal,
    });
    chLogger.start({ handlers: { 'im.message.receive_v1': () => {} } });
    const clientLogger = builtClients[0] && builtClients[0].logger;
    const wsLogger = loggerWsClients[0] && loggerWsClients[0].logger;
    check('D-013: Client 与 WSClient 均收到同一脱敏 logger',
      !!clientLogger && typeof clientLogger.error === 'function' && wsLogger === clientLogger,
      'client=' + !!clientLogger + ' ws-same=' + (wsLogger === clientLogger));
    const sinkLines = [];
    const sanitized = makeSanitizedSdkLogger([secretVal], (...args) => sinkLines.push(args.join(' ')));
    const axiosLike = { message: 'Request failed with status code 400', config: { url: '/auth/v3/tenant_access_token/internal', data: { app_id: 'app_z', app_secret: secretVal } }, response: { status: 400 } };
    axiosLike.self = axiosLike;
    sanitized.error('token request failed', axiosLike, new Error('body: app_secret=' + secretVal));
    const joined = sinkLines.join('\n');
    check('D-013: 明文 secret 不进日志(值置换+敏感键置换+防循环)',
      sinkLines.length === 1 && !joined.includes(secretVal) && joined.includes('[REDACTED]') && joined.includes('[circular]'),
      'lines=' + sinkLines.length + ' leaked=' + joined.includes(secretVal));
    check('D-013: Error 实例只保留脱敏后的 message',
      joined.includes('body: app_secret=[REDACTED]') && !joined.includes('stack'),
      joined.slice(0, 120));
  } finally {
    delete process.env.FEISHU_TEST_API_TIMEOUT_MS;
    delete process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS;
    try { fs.rmSync(tmpDir, { recursive: true, force: true }); } catch (e) {}
  }
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}

main().catch(e => { console.error(e); process.exit(1); });
