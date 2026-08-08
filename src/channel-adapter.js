'use strict';
// Stage 1 S1-G ChannelAdapter:飞书传输边界。
//
// 本模块只拥有:
//   - Feishu SDK Client/WSClient/EventDispatcher 的创建与启动(测试模式用录制式 mock client);
//   - 单次 Feishu API 请求级 timeout 与现役"至多一次网络重试"规则(超时/上传不重试);
//   - chat_id / od:open_id 目标转换,以及 text/card/image message create、card patch、
//     image upload、message image resource get;
//   - 事件注册:立即返回 ACK、同 key 严格保序、跨 key 可并发、handler 同步前缀不得阻塞
//     ACK;v2 消息注册失败时保留 v1/card/menu/noop 回退。
//
// 本模块不拥有:权限、项目/会话状态、卡片 lastCard/epoch/hash/导航队列、业务文案、文本
// 分片、结果卡 fallback、图片目录/大小/TTL/暂存、进程登记、provider/task 编排、配置持久化
// 或目标 C# RunContract。
const crypto = require('crypto');
const fs = require('fs');

let sdkModule = null;
try { sdkModule = require('@larksuiteoapi/node-sdk'); }
catch (e) { sdkModule = null; }

// D-013:SDK 默认 logger 会在 tenant token 请求失败时把含明文 app_secret 的
// axios 错误对象整体打进 stdout 日志。所有真实 Client/WSClient 必须使用本脱敏
// logger:Error 只取 message,对象经防循环序列化,敏感键与已知机密值一律置换。
function makeSanitizedSdkLogger(secretValues, sink) {
  const secrets = (secretValues || []).filter(s => typeof s === 'string' && s.length >= 6);
  const out = typeof sink === 'function' ? sink : (...args) => { try { console.log(...args); } catch (e) {} };
  const scrub = (text) => secrets.reduce((acc, s) => acc.split(s).join('[REDACTED]'), String(text));
  const serialize = (value) => {
    if (value instanceof Error) return scrub(value.message);
    if (typeof value === 'string') return scrub(value);
    try {
      const seen = new WeakSet();
      return scrub(JSON.stringify(value, (key, val) => {
        if (/secret|password|token|authorization|cookie/i.test(key)) return '[REDACTED]';
        if (val && typeof val === 'object') {
          if (seen.has(val)) return '[circular]';
          seen.add(val);
        }
        return val;
      }));
    } catch (e) { return '[unserializable sdk log arg]'; }
  };
  const emit = (level) => (...args) => { try { out(`[sdk:${level}]`, ...args.map(serialize)); } catch (e) {} };
  return { error: emit('error'), warn: emit('warn'), info: emit('info'), debug: () => {}, trace: () => {} };
}

// a recording mock client so the card-flow logic can be tested without touching the network.
// tests read client.__calls (each {op:'create'|'patch', type, title, id}) and client.__reset().
function makeMockClient() {
  let seq = 0; const calls = [];
  const behavior = { patchDelayMs: 0, patchDelaysMs: [], patchHang: false, createDelayMs: 0, createHang: false, createErrorsRemaining: 0, uploadDelayMs: 0, downloadDelayMs: 0, downloadWriteDelayMs: 0, downloadSizeBytes: 0, downloadFailKeys: [] };
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  const titleOf = c => { try { const j = JSON.parse(c); return (j.header && j.header.title && j.header.title.content) || null; } catch (e) { return null; } };
  // text of a message: plain-text content, OR (for interactive cards) all lark_md/plain_text bodies
  // concatenated — so tests can scan a result card's body just like a text message.
  const textOf = c => {
    try {
      const j = JSON.parse(c);
      if (j.text) return j.text;
      if (j.elements) {
        const out = [];
        const walk = (el) => {
          if (!el) return;
          if (Array.isArray(el)) return el.forEach(walk);
          if (el.text && typeof el.text.content === 'string') out.push(el.text.content);
          if (typeof el.content === 'string') out.push(el.content);
          if (el.elements) walk(el.elements);
          if (el.actions) walk(el.actions);
        };
        walk(j.elements);
        return out.join('\n') || null;
      }
      return null;
    } catch (e) { return null; }
  };
  return {
    __calls: calls,
    __reset() { calls.length = 0; Object.assign(behavior, { patchDelayMs: 0, patchDelaysMs: [], patchHang: false, createDelayMs: 0, createHang: false, createErrorsRemaining: 0, uploadDelayMs: 0, downloadDelayMs: 0, downloadWriteDelayMs: 0, downloadSizeBytes: 0, downloadFailKeys: [] }); },
    __setBehavior(next) { Object.assign(behavior, next || {}); },
    im: {
      message: {
        create: async o => {
          const id = 'msg_' + (++seq); let imageKey = null;
          if (o.data.msg_type === 'image') { try { imageKey = JSON.parse(o.data.content).image_key; } catch (e) {} }
          const call = { op: 'create', state: 'started', type: o.data.msg_type, to: o.data.receive_id, toType: o.params && o.params.receive_id_type, title: titleOf(o.data.content), text: textOf(o.data.content), imageKey, uuid: o.data.uuid, content: o.data.content, id };
          calls.push(call);
          if (behavior.createHang) return await new Promise(() => {});
          if (behavior.createErrorsRemaining > 0) {
            behavior.createErrorsRemaining--;
            call.state = 'failed';
            throw new Error('mock ECONNRESET');
          }
          if (behavior.createDelayMs) await wait(behavior.createDelayMs);
          call.state = 'settled';
          return { data: { message_id: id } };
        },
        patch: async o => {
          if (String(o.path.message_id).indexOf('gone') !== -1) throw new Error('mock: message not found (deleted)');
          const call = { op: 'patch', state: 'started', id: o.path.message_id, title: titleOf(o.data.content), text: textOf(o.data.content), content: o.data.content };
          calls.push(call);
          if (behavior.patchHang) return await new Promise(() => {});
          const patchDelayMs = Array.isArray(behavior.patchDelaysMs) && behavior.patchDelaysMs.length
            ? behavior.patchDelaysMs.shift()
            : behavior.patchDelayMs;
          if (patchDelayMs) await wait(patchDelayMs);
          call.state = 'settled';
          return {};
        },
      },
      image: {
        // MIRROR THE LIVE API: image.create resolves to {image_key} at the TOP level (no code/data
        // wrapper) — the old mock wrapped it in {data:{}}, which hid a real bug where every outbound
        // image was silently dropped. Keep this shape identical to production.
        create: async o => {
          const call = { op: 'uploadImage', state: 'started', imageType: o.data && o.data.image_type };
          calls.push(call);
          const input = o.data && o.data.image;
          if (input && typeof input.once === 'function') {
            await new Promise((resolve, reject) => {
              input.once('open', () => { input.destroy(); resolve(); });
              input.once('error', reject);
            });
          }
          if (behavior.uploadDelayMs) await wait(behavior.uploadDelayMs);
          call.state = 'settled';
          return { image_key: 'imgkey_' + (++seq) };
        },
      },
      // download an inbound image (real path needs im:resource). Tests get a 1x1 PNG written out.
      messageResource: {
        get: async o => {
          calls.push({ op: 'downloadImage', messageId: o.path && o.path.message_id, fileKey: o.path && o.path.file_key });
          if (behavior.downloadDelayMs) await wait(behavior.downloadDelayMs);
          if (Array.isArray(behavior.downloadFailKeys) && behavior.downloadFailKeys.indexOf(o.path && o.path.file_key) !== -1) {
            throw new Error('mock image resource unavailable');
          }
          const png = behavior.downloadSizeBytes > 0
            ? Buffer.alloc(behavior.downloadSizeBytes, 1)
            : Buffer.from('89504e470d0a1a0a0000000d494844520000000100000001080600000' + '01f15c4890000000a49444154789c6300010000050001' + '0d0a2db40000000049454e44ae426082', 'hex');
          return { writeFile: async (p) => { if (behavior.downloadWriteDelayMs) await wait(behavior.downloadWriteDelayMs); fs.writeFileSync(p, png); } };
        },
      },
    },
  };
}

// 工厂:testMode=true 时创建录制式 mock client;否则创建真实 SDK Client 并负责 WS 启动。
// options:
//   testMode    - 离线测试模式(必须显式传入,不读取 FEISHU_TEST);
//   appId/appSecret - 生产 Client/WSClient 凭据;
//   sdk         - 可注入 SDK 模块(测试用;缺省为 @larksuiteoapi/node-sdk);
//   onLog       - 适配器内部诊断回调(默认空)。
function createChannelAdapter(options) {
  const opts = options || {};
  const testMode = !!opts.testMode;
  const sdk = opts.sdk !== undefined ? opts.sdk : sdkModule;
  if (!testMode && !sdk) {
    throw new Error('缺少依赖 @larksuiteoapi/node-sdk,请在本目录运行: npm install');
  }
  const appId = opts.appId || '';
  const appSecret = opts.appSecret || '';
  const log = typeof opts.onLog === 'function' ? opts.onLog : () => {};
  const sdkLogger = makeSanitizedSdkLogger([appSecret]);
  const client = testMode ? makeMockClient() : new sdk.Client({ appId, appSecret, logger: sdkLogger });

  // ---- 单次请求级超时与现役一次网络重试 ----
  // 超时(AI_RESUME_FEISHU_TIMEOUT)永不重试;上传显式 opt-out;其余仅在可识别的瞬时
  // socket/TLS/DNS/network 错误时重试一次,message create 的稳定 uuid 在重试中复用。
  const apiTimeoutMs = Math.max(50, Number(process.env.FEISHU_TEST_API_TIMEOUT_MS || 7000));
  const resourceTimeoutMs = Math.max(apiTimeoutMs, Number(process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS || 60000));
  function withTimeout(promise, timeoutMs, label) {
    let timer;
    const timeout = new Promise((resolve, reject) => {
      timer = setTimeout(() => {
        const error = new Error(`${label || '飞书 API'} 超时(${timeoutMs}ms)`);
        error.code = 'AI_RESUME_FEISHU_TIMEOUT';
        reject(error);
      }, timeoutMs);
    });
    return Promise.race([Promise.resolve(promise), timeout]).finally(() => clearTimeout(timer));
  }
  async function apiRetry(fn, label, retryOptions) {
    const rOpts = retryOptions || {};
    const call = () => withTimeout(Promise.resolve().then(fn), rOpts.timeoutMs || apiTimeoutMs, label);
    try { return await call(); }
    catch (e) {
      if (e && e.code === 'AI_RESUME_FEISHU_TIMEOUT') throw e;
      if (rOpts.retry !== false && /socket disconnected|handshake|TLS|ETIMEDOUT|ECONNRESET|ENOTFOUND|EAI_AGAIN|network/i.test(String(e && e.message))) {
        await new Promise(r => setTimeout(r, 250));   // transient blip — retry fast (was 700ms, felt sluggish)
        return await call();
      }
      throw e;
    }
  }

  // ---- chat_id / od:open_id 目标转换 ----
  // a target is a chat_id, or 'od:ou_xxx' to deliver into a user's p2p chat by open_id
  function sendParams(target) {
    return String(target).startsWith('od:')
      ? { type: 'open_id', id: String(target).slice(3) }
      : { type: 'chat_id', id: target };
  }

  // ---- 消息/卡片/图片发送 ----
  // message.create 保持真实 API 形状:message_id 在返回的 data 里;uuid 在第一次尝试前确定,
  // 一次显式网络重试中复用同一个 uuid,保证幂等。
  async function createMessage(target, msgType, content, messageOptions) {
    const mo = messageOptions || {};
    const tgt = sendParams(target);
    const uuid = mo.uuid || crypto.randomUUID();
    const res = await apiRetry(() => client.im.message.create({
      params: { receive_id_type: tgt.type },
      data: { receive_id: tgt.id, msg_type: msgType, content, uuid },
    }), mo.label || '发送消息', mo);
    return res;
  }
  async function patchMessage(messageId, content, messageOptions) {
    const mo = messageOptions || {};
    return apiRetry(() => client.im.message.patch({ path: { message_id: messageId }, data: { content } }),
      mo.label || '更新卡片', mo);
  }
  // 上传不可安全重试(源码流已被消费),使用更长的资源超时并显式 retry:false;
  // 超时后销毁本地读流,让底层请求尽快停止读取文件。
  async function uploadImage(filePath, messageOptions) {
    const mo = messageOptions || {};
    const stream = fs.createReadStream(filePath);
    let res;
    try {
      res = await apiRetry(() => client.im.image.create({ data: { image_type: 'message', image: stream } }),
        mo.label || '上传图片', { timeoutMs: resourceTimeoutMs, retry: false });
    } finally {
      if (!stream.destroyed) stream.destroy();
    }
    // SHAPE TRAP (verified against the live API): im.image.create resolves to `{image_key}` at the TOP
    // level, while im.message.create resolves to `{code,msg,data:{...}}`. Reading only res.data.image_key
    // silently returned undefined, so every outbound image was dropped without a word. Accept both.
    return res && (res.image_key || (res.data && res.data.image_key));
  }
  // 下载结果原样返回(含 SDK 的 writeFile/stream/buffer 形状),落盘与大小/TTL 边界由调用方负责。
  async function getMessageResource(messageId, fileKey, messageOptions) {
    const mo = messageOptions || {};
    return apiRetry(() => client.im.messageResource.get({
      path: { message_id: messageId, file_key: fileKey }, params: { type: 'image' },
    }), mo.label || '下载图片', { timeoutMs: resourceTimeoutMs, retry: true });
  }

  // ---- 事件注册:立即 ACK、同 key 保序、跨 key 并发、同步前缀不阻塞 ACK ----
  // 注册返回的 dispatch(data) 同步完成链式排队,handler 一律经 setImmediate 边界执行;
  // 同一 key 的队列严格保持到达顺序,不同 key 互不等待;handler 异常只记录不中断队列。
  // keyOf 由调用方提供(如按 chat_id),适配器不持有会话/权限状态。
  const eventDispatchQueues = new Map();
  function dispatchEvent(label, handler, keyOf) {
    const keyFn = typeof keyOf === 'function' ? keyOf : () => '__global__';
    return data => {
      const key = keyFn(data);
      const previous = eventDispatchQueues.get(key) || Promise.resolve();
      const current = previous.catch(() => {})
        .then(() => new Promise(resolve => setImmediate(resolve)))
        .then(() => handler(data))
        .catch(e => log(`后台任务异常 [${label}]: ` + (e && (e.stack || e))));
      eventDispatchQueues.set(key, current);
      current.finally(() => { if (eventDispatchQueues.get(key) === current) eventDispatchQueues.delete(key); });
      // Do not return the background Promise. EventDispatcher may await a returned thenable before
      // acknowledging the event; the transport boundary must ACK immediately while work continues.
    };
  }

  // ---- WS 启动:EventDispatcher 注册 + v2 失败回退 + WSClient.start ----
  // v2 消息事件注册失败时保留 v1/card/menu/noop 回退,生产可继续消费其余事件。
  function start(startOptions) {
    const so = startOptions || {};
    const handlers = so.handlers || {};
    if (!sdk) throw new Error('缺少依赖 @larksuiteoapi/node-sdk,请在本目录运行: npm install');
    let eventDispatcher;
    try {
      eventDispatcher = new sdk.EventDispatcher({}).register(handlers);
    } catch (e) {
      log('注册 v2 消息事件失败,保留卡片/菜单并回退消息 v1: ' + (e && e.message));
      const fallbackHandlers = Object.assign({}, handlers);
      delete fallbackHandlers['im.message.receive_v2'];
      eventDispatcher = new sdk.EventDispatcher({}).register(fallbackHandlers);
    }
    const wsOptions = { appId, appSecret, logger: sdkLogger };
    if (typeof so.onReady === 'function') wsOptions.onReady = so.onReady;
    const wsClient = new sdk.WSClient(wsOptions);
    wsClient.start({ eventDispatcher });
    return { eventDispatcher, wsClient };
  }

  return {
    client,
    apiTimeoutMs,
    resourceTimeoutMs,
    withTimeout,
    createMessage,
    patchMessage,
    uploadImage,
    getMessageResource,
    dispatchEvent,
    start,
  };
}

module.exports = { createChannelAdapter, makeMockClient, makeSanitizedSdkLogger };
