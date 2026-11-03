import assert from 'node:assert/strict'
import { test } from 'node:test'

// 适配层 hooks/register.ts 的测试:用假的 `$` 与 `on` 驱动钩子(它对 'claude-code' 只有类型导入,Node 去掉类型后可直接加载)。
// 每个用例加载一份全新的模块实例(模块里有进程级状态)。真实的事件形状见 fixtures 里 2.1.309 的录制。

type Hook = (api: unknown, e: unknown, next: (e: unknown) => Promise<unknown>) => unknown
type Registered = { event: string; matcher: Record<string, unknown> | null; hook: Hook }

const DATA = 'X:\\data'
let instance = 0

type FakeFile = { text: string; mtime: number }

function harness(opts: { config?: unknown; rateLimits?: () => unknown[]; usageThrows?: boolean; writeThrows?: boolean }) {
  const files = new Map<string, FakeFile>()
  let tick = 1_000
  let sessionId = 'sess-aaaaaaaa-1'
  const everies: (() => unknown)[] = []
  const afters: (() => unknown)[] = []
  const submitted: unknown[] = []
  if (opts.config !== undefined) files.set(`${DATA}\\config.json`, { text: JSON.stringify(opts.config), mtime: tick++ })
  const dirOf = (p: string) => p.slice(0, p.lastIndexOf('\\'))
  const api = {
    env: { get: async (name: string) => (name === 'AIRESUME_BUDGET_GATE_DIR' ? DATA : undefined) },
    fs: {
      exists: async (p: string) => files.has(p) || [...files.keys()].some(k => k.startsWith(p + '\\')),
      stat: async (p: string) => {
        const f = files.get(p)
        if (!f) throw new Error('ENOENT')
        return { mtimeMs: f.mtime, kind: 'file', size: f.text.length, isLink: false }
      },
      read: async (p: string) => {
        const f = files.get(p)
        if (!f) throw new Error('ENOENT')
        return f.text
      },
      write: async (p: string, text: string) => {
        if (opts.writeThrows) throw new Error('EACCES')
        files.set(p, { text, mtime: tick++ })
      },
      list: async (dir: string) => [...files.entries()].filter(([k]) => dirOf(k) === dir)
        .map(([k, v]) => ({ name: k.slice(dir.length + 1), kind: 'file', size: v.text.length, mtimeMs: Date.now(), isLink: false })),
    },
    session: {
      id: async () => sessionId,
      version: async () => ({ version: '2.1.309', base: '2.1.309' }),
      usage: async () => {
        if (opts.usageThrows) throw new Error('usage 不可用')
        return { rateLimits: opts.rateLimits ? opts.rateLimits() : [] }
      },
    },
    clock: {
      every: (_ms: number, fn: () => unknown) => (everies.push(fn), { cancel() {} }),
      after: (_ms: number, fn: () => unknown) => (afters.push(fn), { cancel() {} }),
    },
    prompt: { submit: async (args: unknown) => (submitted.push(args), { text: (args as { text: string }).text }) },
  }
  return {
    api, files, submitted,
    setSession: (id: string) => { sessionId = id },
    runTimers: async () => { for (const fn of [...afters, ...everies]) await fn() },
  }
}

async function load() {
  instance += 1
  const mod = await import(`../airesume-budget-gate/hooks/register.ts?case=${instance}`) as { register: (on: unknown) => void }
  const hooks: Registered[] = []
  const on = (event: string, a: unknown, b?: unknown) => {
    const matcher = typeof a === 'function' ? null : a as Record<string, unknown>
    const hook = (typeof a === 'function' ? a : b) as Hook
    hooks.push({ event, matcher, hook })
    return { catch: () => undefined }
  }
  mod.register(on)
  const fire = async (event: string, e: Record<string, unknown>, next: (e: unknown) => Promise<unknown> = async x => x) => {
    let result: unknown = undefined
    let called = 0
    for (const h of hooks.filter(x => x.event === event && (!x.matcher || Object.entries(x.matcher).every(([k, v]) => e[k] === v)))) {
      result = await h.hook(currentApi, e, async x => { called += 1; return next(x) })
    }
    return { result, called }
  }
  let currentApi: unknown = null
  return { fire, use: (api: unknown) => { currentApi = api } }
}

const START = { cwd: 'C:\\proj', surface: null, isInteractive: false }
const spawnEvent = (idx: number, over: Record<string, unknown> = {}) => ({
  tool_use_id: 'toolu_x', prompt: `do task ${idx}`, description: `task ${idx}`, subagentType: 'workflow-subagent',
  provider: { plugin: 'engine', tier: 'core' }, model: 'opus', parentModel: 'claude-opus-5-5', background: false, fork: false,
  workflow: { runId: 'wf_t', agentIndex: idx }, ...over,
})
const fiveHour = (p: number) => [{ kind: 'five_hour', percentUsed: p, resetsAt: new Date(Date.now() + 2 * 3_600_000).toISOString() }]
const logs = (files: Map<string, FakeFile>) => [...files.entries()].filter(([k]) => k.includes('\\logs\\'))
const rows = (files: Map<string, FakeFile>) => logs(files).flatMap(([, f]) => f.text.trim().split('\n').map(l => JSON.parse(l) as Record<string, unknown>))

test('observe:提示、工具调用、派发原样交给 next,不加 context、不拦、不推迟', async () => {
  const h = harness({ rateLimits: () => fiveHour(99) })
  const m = await load()
  m.use(h.api)
  await m.fire('session.start', START)
  const prompt = { text: 'I hit my usage limit while you were working, but it has reset now.', origin: { kind: 'sdk' }, wait: false }
  let seen: unknown = null
  await m.fire('prompt.submit', prompt, async x => (seen = x, x))
  assert.deepEqual(seen, prompt)
  const tool = await m.fire('tool.call', { tool: 'Read', agentId: 'a1', file_path: 'x' }, async () => ({ result: 'ok' }))
  assert.equal(tool.called, 1)
  const spawn = await m.fire('agent.spawn', spawnEvent(1), async () => ({ model: 'claude-opus-5-5', agentId: 'a1' }))
  assert.equal(spawn.called, 1)
  assert.deepEqual(spawn.result, { model: 'claude-opus-5-5', agentId: 'a1' })
  await h.runTimers()
  assert.equal(rows(h.files).filter(r => r.ev === 'decision').length, 0, 'observe 不做判断')
})

test('shadow:并行批次先判断再预留,后到的看得见前面的预留;next 抛错时撤销预留', async () => {
  const h = harness({ config: { mode: 'shadow', version: 1 }, rateLimits: () => fiveHour(80) })
  const m = await load()
  m.use(h.api)
  await m.fire('session.start', START)
  await m.fire('session.measure', { context: { window: 1_000_000 }, rateLimits: fiveHour(80), changed: ['rateLimits'] })
  let release: () => void = () => {}
  const gate = new Promise<void>(r => { release = r })
  const batch = [1, 2, 3].map(i => m.fire('agent.spawn', spawnEvent(i), async () => (await gate, { model: 'claude-opus-5-5', agentId: `a${i}` })))
  await new Promise(r => setTimeout(r, 20))
  release()
  const results = await Promise.all(batch)
  assert.ok(results.every(r => r.called === 1), '影子模式一律放行')
  await m.fire('agent.spawn', spawnEvent(9), async () => { throw new Error('用户按了 Esc') }).catch(() => undefined)
  await h.runTimers()
  const decisions = rows(h.files).filter(r => r.ev === 'decision')
  assert.equal(decisions.length, 4)
  const inflight = decisions.map(d => ((d.win as { inflight: number }[])[0] ?? { inflight: -1 }).inflight)
  assert.ok(inflight[1]! > inflight[0]! && inflight[2]! > inflight[1]!, `后到的候选看得见前面的预留:${inflight}`)
  const beat = JSON.parse(h.files.get(`${DATA}\\sessions\\sess-aaaaaaaa-1.json`)!.text) as { reserved: number; inflight: unknown[] }
  assert.equal(beat.reserved, 0, 'next 抛错的那次预留已撤销')
  assert.equal(beat.inflight.length, 3)
})

test('enforce:放不下就返回 deny、不调用 next,理由带标签且不含祈使句', async () => {
  const h = harness({ config: { mode: 'enforce', version: 2, params: { costSeedMedianEq: 900_000_000 } }, rateLimits: () => fiveHour(83) })
  const m = await load()
  m.use(h.api)
  await m.fire('session.start', START)
  await m.fire('session.measure', { context: { window: 1_000_000 }, rateLimits: fiveHour(83), changed: ['rateLimits'] })
  const r = await m.fire('agent.spawn', spawnEvent(1), async () => ({ model: 'x', agentId: 'never' }))
  assert.equal(r.called, 0)
  const deny = (r.result as { deny?: string }).deny ?? ''
  assert.match(deny, /^\[AI Resume 预算闸门\]/)
  assert.doesNotMatch(deny, /不要|请|立即/)
  const resumed = await m.fire('agent.spawn', spawnEvent(2, { workflow: undefined, resume: { agentId: 'a0' } }), async () => ({ model: 'x', agentId: 'a0' }))
  assert.equal(resumed.called, 1, '续派一律放行')
})

test('出错放行:取读数和写盘都失败时,钩子照样把事件交给 next', async () => {
  const h = harness({ config: { mode: 'shadow', version: 1 }, usageThrows: true, writeThrows: true })
  const m = await load()
  m.use(h.api)
  await m.fire('session.start', START)
  const spawn = await m.fire('agent.spawn', spawnEvent(1), async () => ({ model: 'claude-opus-5-5', agentId: 'a1' }))
  assert.equal(spawn.called, 1)
  const prompt = await m.fire('prompt.submit', { text: 'hi', origin: { kind: 'composer' }, wait: false })
  assert.equal(prompt.called, 1)
  await h.runTimers()
})

test('/clear:日志文件名冻结在启动时(不重复写一份)、进程键不变、旧会话心跳标结束、进程不标结束', async () => {
  const h = harness({ config: { mode: 'shadow', version: 1 }, rateLimits: () => fiveHour(30) })
  const m = await load()
  m.use(h.api)
  await m.fire('session.start', START)
  await h.runTimers()
  const before = logs(h.files).map(([k]) => k)
  await m.fire('session.end', { reason: 'clear', sessionId: 'sess-aaaaaaaa-1', resume: {} })
  h.setSession('sess-bbbbbbbb-2')
  await m.fire('classic.SessionStart', { source: 'clear' })
  await m.fire('prompt.submit', { text: 'after clear', origin: { kind: 'composer' }, wait: false })
  await h.runTimers()
  assert.deepEqual(logs(h.files).map(([k]) => k), before, '同一进程只有一个日志文件名')
  const oldBeat = JSON.parse(h.files.get(`${DATA}\\sessions\\sess-aaaaaaaa-1.json`)!.text) as { ended: boolean; process: string }
  const newBeat = JSON.parse(h.files.get(`${DATA}\\sessions\\sess-bbbbbbbb-2.json`)!.text) as { ended: boolean; process: string }
  assert.equal(oldBeat.ended, true)
  assert.equal(newBeat.ended, false)
  assert.equal(newBeat.process, oldBeat.process)
  const sids = new Set(rows(h.files).map(r => r.sid))
  assert.deepEqual([...sids].sort(), ['sess-aaaaaaaa-1', 'sess-bbbbbbbb-2'])
  const promptRows = rows(h.files).filter(r => r.ev === 'prompt')
  assert.equal(promptRows.length, 1, '没有重复行')
})
