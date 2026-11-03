// AI Resume 子代理额度预检 mod 的适配层:只做 IO,判断全在 ../core(纯函数 + BudgetEngine)。
//
// 写给 AI Resume 控制面读的文件(数据目录默认 %LOCALAPPDATA%\AI Resume\state\budget-gate,只在 AI Resume 已建好 state 时才写):
//   logs\<启动时刻>-<启动时会话前 8 位>-<随机>-<段号>.jsonl   读数、逐次请求计量、派发与判断、结束复盘、软着陆、补派
//   sessions\<会话 id>.json                                   每会话心跳(也是其它会话读的 peer 记录)
//   calibration.json                                          价格样本与子代理历史(各会话合并写)
// 读控制面写的文件:config.json(模式与参数)、quota-snapshot.json(按模型族的周额度与 OAuth 5h/7d,不含任何凭据)、salt(哈希用的每机随机盐)。
//
// 红线:不读、不写、不刷新任何令牌(不调用 $.session.authorize),只用引擎给的读数;日志不记提示正文与项目私密内容;
// 任何一步出错一律放行(fail-open),事件原样交给 next。拦截只在 enforce 模式,且只能由用户在 AI Resume 界面上打开。

import type { EngineInterface, Register } from 'claude-code'

import { isPriceSample, type PriceSample } from '../core/calibrate.ts'
import { parseConfig, type Config, type Mode, type WindowKind } from '../core/config.ts'
import type { CompletedAgent } from '../core/cost.ts'
import { BudgetEngine, type ObservedWindow, type PeerView, type SpawnFacts } from '../core/engine.ts'
import { Journal } from '../core/journal.ts'
import { briefingText, deferMessage, landingMessage, redispatchText } from '../core/messages.ts'
import { normalizeReadings, readingsSignature, usageOf, eqOf, type Reading } from '../core/meter.ts'
import { familyOf } from '../core/model.ts'
import { clip, normalizeCwd, promptRow, readingsRow, spawnPostRow, spawnPreRow, stepRow } from '../core/rows.ts'

type Api = EngineInterface

const MOD_VERSION = '0.3.0'
const SCHEMA = 1
const FLUSH_MS = 3_000
const BEAT_MS = 60_000
const PEERS_MS = 20_000
const PEER_KEEP_MS = 24 * 3_600_000
const CALIBRATION_WRITE_MS = 30_000
/** 快照里按模型族的周额度超过这么久就不用(换账号、登出后旧快照不会自己消失)。 */
const SCOPED_FRESH_MS = 6 * 3_600_000
const DEFER_COUNT_CAP = 500
/** 没有 config.json 时的模式。V0 只记录;AI Resume install 会写 config.json(默认 shadow)。 */
const FALLBACK_MODE: Mode = 'observe'
/** 交卷与回报类工具不拦:带 schema 的工作流 agent 要靠 StructuredOutput 交回结果,队友靠 SendMessage 回报。 */
const HAND_IN_TOOLS = new Set(['StructuredOutput', 'SubagentHandback', 'SendMessage'])

let dataDir = ''
let sep = '\\'
let sessionId = ''
/** 启动时的会话 id:日志文件名用它冻结,/clear 换会话后不会把同一段换个名字再写一遍。 */
let bootSessionId = ''
let loadNonce = ''
let loadStamp = ''
/** 进程键:启动时刻 + 随机串。/clear 换会话也不变,用来在心跳里认出自己。 */
let processKey = ''
let startedAt = 0
let surface: string | null = null
let interactive = false
let engineVersion = ''
let projectKey = ''
let salt = ''
let config: Config = parseConfig(undefined, FALLBACK_MODE)
let configMtime = -1
let ended: { reason: string } | null = null
let isBooted = false
let flushChain: Promise<void> = Promise.resolve()
let mainTurnRunning = false
let spawnSeq = 0
let lastReadingsSig = ''
let lastReadings: Reading[] = []
/** 读数最后一次变化的时刻(不是轮询时刻):空闲会话缓存的旧读数不能冒充新的。 */
let readingsChangedAt = 0
let engine: BudgetEngine | null = null
let calibrationDirty = false
let lastCalibrationWrite = 0
let peersLoadedAt = 0
let lastError: string | null = null
let lastBriefSig = ''
const journal = new Journal()
const spawnFacts = new Map<number, SpawnFacts>()
const deferCounts = new Map<string, number>()
const landedAgents = new Map<string, number>()
const peerCache = new Map<string, { mtimeMs: number; view: PeerView | null }>()
const counters = {
  steps: 0, spawns: 0, measures: 0, prompts: 0, errors: 0,
  allow: 0, defer: 0, deniedApplied: 0, landings: 0, landingsApplied: 0, redispatches: 0, briefs: 0,
}

const iso = (ms: number): string => new Date(ms).toISOString()
const join = (...parts: string[]): string => parts.join(sep)
const judging = (): boolean => config.mode === 'shadow' || config.mode === 'enforce'

function noteError(where: string, error: unknown): void {
  counters.errors += 1
  const text = error instanceof Error ? `${error.name}: ${error.message}` : String(error)
  lastError = `${where}: ${clip(text, 200) ?? 'unknown'}`
  if (isBooted) record({ ev: 'error', where, msg: clip(text, 200) ?? null })
}

function record(row: Record<string, unknown>): void {
  if (config.mode === 'off' && row.ev !== 'load' && row.ev !== 'config' && row.ev !== 'end') return
  journal.append({ ts: iso(Date.now()), sid: sessionId, ...row })
}

/** 加盐哈希(盐来自 AI Resume 写的每机随机盐;读不到就用本进程随机盐,哈希不跨会话可比,但也无法被字典反推)。 */
async function shortHash(text: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(salt + '\u0000' + text))
  return Array.from(new Uint8Array(digest).slice(0, 8), b => b.toString(16).padStart(2, '0')).join('')
}

function randomHex(bytes: number): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(bytes)), b => b.toString(16).padStart(2, '0')).join('')
}

/**
 * 数据目录。默认只在 AI Resume 已经建好的 `%LOCALAPPDATA%\AI Resume\state` 下面写:
 * Desktop 是 MSIX 包,它拉起的 Claude Code 在 `%LOCALAPPDATA%` 下新建顶层目录会被重定向进包私有目录,
 * 包外的 AI Resume 看不到;写进已存在的真实目录则落在真实位置(2026-11-02 实测)。所以 mod 绝不自己创建 `AI Resume`。
 */
async function resolveDataDir($: Api): Promise<string> {
  const override = await $.env.get('AIRESUME_BUDGET_GATE_DIR')
  if (override !== undefined && override.trim() !== '') return override.trim().replace(/[\\/]+$/, '')
  const local = await $.env.get('LOCALAPPDATA')
  if (local === undefined || local.trim() === '') return ''
  const state = `${local.trim().replace(/[\\/]+$/, '')}\\AI Resume\\state`
  return (await $.fs.exists(state)) ? `${state}\\budget-gate` : ''
}

async function readJson($: Api, name: string): Promise<{ mtimeMs: number; value: unknown } | null> {
  const path = join(dataDir, name)
  let mtimeMs: number
  try {
    mtimeMs = (await $.fs.stat(path)).mtimeMs
  } catch {
    return null
  }
  return { mtimeMs, value: JSON.parse(await $.fs.read(path)) as unknown }
}

async function readSalt($: Api): Promise<void> {
  try {
    const text = dataDir ? (await $.fs.read(join(dataDir, 'salt'))).trim() : ''
    salt = /^[0-9a-f]{16,128}$/i.test(text) ? text : randomHex(16)
  } catch {
    salt = randomHex(16)
  }
}

/** 读控制面写的 config.json;文件不在或读不懂时按 FALLBACK_MODE。 */
async function readConfig($: Api): Promise<void> {
  let file: { mtimeMs: number; value: unknown } | null = null
  try {
    file = await readJson($, 'config.json')
  } catch (error) {
    noteError('config', error)
  }
  const mtime = file?.mtimeMs ?? 0
  if (mtime === configMtime) return
  configMtime = mtime
  const before = config
  config = parseConfig(file?.value, FALLBACK_MODE)
  if (engine) engine.params = config.params
  if (isBooted && (before.mode !== config.mode || before.version !== config.version)) {
    record({ ev: 'config', mode: config.mode, ver: config.version, params: config.params, problems: config.problems })
  }
}

/**
 * AI Resume 导出的只读快照(不含任何凭据):按模型族的周额度(mod 自己看不到),以及 OAuth 的 5h/7d 读数
 * (本会话还没有读数时兜底;承接值标 carried,引擎不用)。超过 6 小时的族周额度不用:换账号或登出后旧快照不会自己消失。
 */
async function readSnapshot($: Api): Promise<void> {
  if (!engine) return
  try {
    const file = await readJson($, 'quota-snapshot.json')
    const root = (file?.value ?? null) as { scoped?: unknown; windows?: unknown } | null
    const now = Date.now()
    const windows = Array.isArray(root?.windows) ? root!.windows : []
    engine.snapshotWindows = windows.flatMap((item): ObservedWindow[] => {
      const w = item as Record<string, unknown>
      if ((w.kind !== 'five_hour' && w.kind !== 'seven_day') || typeof w.percentUsed !== 'number' || !Number.isFinite(w.percentUsed)) return []
      const reset = typeof w.resetsAt === 'string' ? Date.parse(w.resetsAt) : NaN
      const observed = typeof w.observedAt === 'string' ? Date.parse(w.observedAt) : NaN
      if (!Number.isFinite(observed)) return []
      return [{ kind: w.kind as WindowKind, percentUsed: w.percentUsed, resetsAtMs: Number.isFinite(reset) ? reset : null, observedAtMs: observed, carried: w.carried === true }]
    })
    const list = Array.isArray(root?.scoped) ? root!.scoped : []
    engine.scoped = list.flatMap(item => {
      const s = item as Record<string, unknown>
      if (typeof s.family !== 'string') return []
      const observed = typeof s.observedAt === 'string' ? Date.parse(s.observedAt) : NaN
      if (!Number.isFinite(observed) || now - observed > SCOPED_FRESH_MS) return []
      const reset = typeof s.resetsAt === 'string' ? Date.parse(s.resetsAt) : NaN
      return [{
        family: s.family.toLowerCase(),
        percentUsed: typeof s.percentUsed === 'number' && Number.isFinite(s.percentUsed) ? s.percentUsed : null,
        resetsAtMs: Number.isFinite(reset) ? reset : null,
      }]
    })
  } catch (error) {
    noteError('quota-snapshot', error)
  }
}

async function readCalibration($: Api): Promise<{ samples: PriceSample[]; history: CompletedAgent[] }> {
  const file = await readJson($, 'calibration.json')
  const root = (file?.value ?? {}) as { samples?: unknown; history?: unknown }
  const samples = Array.isArray(root.samples) ? root.samples.filter(isPriceSample) : []
  const history = Array.isArray(root.history)
    ? (root.history as CompletedAgent[]).filter(h => typeof h?.agentId === 'string' && typeof h.eq === 'number' && typeof h.endedAtMs === 'number')
    : []
  return { samples, history }
}

/** 合并写回 calibration.json:先读别的会话写过的,再并上自己的(同 key 去重)。写之前清脏标记,写盘期间的新样本留到下次。 */
async function writeCalibration($: Api, force = false): Promise<void> {
  if (!engine || !dataDir || !calibrationDirty) return
  const now = Date.now()
  if (!force && now - lastCalibrationWrite < CALIBRATION_WRITE_MS) return
  lastCalibrationWrite = now
  calibrationDirty = false
  try {
    const disk = await readCalibration($)
    engine.absorbSamples(disk.samples)
    engine.absorbHistory(disk.history)
    await $.fs.write(join(dataDir, 'calibration.json'), JSON.stringify({
      schema: SCHEMA, updatedAt: iso(now), samples: engine.samples, history: engine.history,
    }))
  } catch (error) {
    calibrationDirty = true
    noteError('calibration', error)
  }
}

function peerView(value: unknown): PeerView | null {
  const h = value as Record<string, unknown> | null
  if (!h || typeof h.sessionId !== 'string' || typeof h.nonce !== 'string') return null
  const beat = typeof h.beatAt === 'string' ? Date.parse(h.beatAt) : NaN
  if (!Number.isFinite(beat)) return null
  const started = typeof h.startedAt === 'string' ? Date.parse(h.startedAt) : NaN
  const key = typeof h.process === 'string' ? h.process : `${h.startedAt}|${h.nonce}`
  const observedAt = typeof h.readingsAt === 'string' ? Date.parse(h.readingsAt) : NaN
  const readings = normalizeReadings(h.readings).flatMap((r): ObservedWindow[] => {
    if (r.kind !== 'five_hour' && r.kind !== 'seven_day') return []
    const reset = r.resetsAt ? Date.parse(r.resetsAt) : NaN
    return [{ kind: r.kind as WindowKind, percentUsed: r.percentUsed, resetsAtMs: Number.isFinite(reset) ? reset : null, observedAtMs: Number.isFinite(observedAt) ? observedAt : beat }]
  })
  const numbers = (v: unknown): Record<string, number> => {
    const out: Record<string, number> = {}
    if (v && typeof v === 'object') for (const [k, x] of Object.entries(v)) if (typeof x === 'number' && Number.isFinite(x)) out[k] = x
    return out
  }
  const inflight = Array.isArray(h.inflight)
    ? (h.inflight as Record<string, unknown>[]).flatMap(a => typeof a?.agentId === 'string' && typeof a.model === 'string'
      ? [{ agentId: a.agentId, model: a.model, steps: Number(a.steps) || 0, spentEq: Number(a.spentEq) || 0, type: typeof a.type === 'string' ? a.type : 'unknown' }]
      : [])
    : []
  return {
    key,
    startedAtMs: Number.isFinite(started) ? started : beat,
    readings,
    spent: numbers(h.spent),
    inflight: h.ended === true ? [] : inflight,
    mainEqPerMinute: h.ended === true ? {} : numbers(h.mainEqPerMinute),
    beatAtMs: beat,
  }
}

/** 读其它会话的心跳:只重读 mtime 变了的;24 小时内的都进账本(校准要两端一致),新鲜与否由引擎判断。 */
async function refreshPeers($: Api, force = false): Promise<void> {
  if (!engine || !dataDir || config.mode === 'off') return
  const now = Date.now()
  if (!force && now - peersLoadedAt < PEERS_MS) return
  peersLoadedAt = now
  try {
    const dir = join(dataDir, 'sessions')
    if (!(await $.fs.exists(dir))) return
    const views: PeerView[] = []
    for (const entry of await $.fs.list(dir)) {
      if (entry.kind !== 'file' || !entry.name.endsWith('.json') || now - entry.mtimeMs > PEER_KEEP_MS) continue
      const cached = peerCache.get(entry.name)
      let view = cached?.view ?? null
      if (!cached || cached.mtimeMs !== entry.mtimeMs) {
        try {
          view = peerView(JSON.parse(await $.fs.read(join(dir, entry.name))))
        } catch {
          view = cached?.view ?? null // 半写的文件:沿用上一份,下一轮再读
        }
        peerCache.set(entry.name, { mtimeMs: entry.mtimeMs, view })
      }
      if (view) views.push(view)
    }
    engine.setPeers(views)
  } catch (error) {
    noteError('peers', error)
  }
}

function partPath(part: number): string {
  return join(dataDir, 'logs', `${loadStamp}-${bootSessionId.slice(0, 8)}-${loadNonce}-${part}.jsonl`)
}

async function writeJournal($: Api): Promise<void> {
  if (!dataDir || !journal.isDirty) return
  try {
    const snapshot = journal.snapshot()
    await $.fs.write(partPath(snapshot.part), snapshot.text)
    journal.written(snapshot)
  } catch (error) {
    counters.errors += 1
    lastError = `flush: ${clip(String(error), 200) ?? 'unknown'}`
  }
}

/** 日志写盘串行化:定时器与 session.end 都排进同一条链,结束时那一次一定写得进 end 行。 */
function flush($: Api): Promise<void> {
  flushChain = flushChain.then(() => writeJournal($), () => writeJournal($))
  return flushChain
}

/** 心跳 = 本会话的 peer 记录:AI Resume 用它判断 mod 是否真的在跑、在跑哪个模式;其它会话用它算在途与全机计量。 */
async function beat($: Api, forSession = sessionId, isEnded = ended !== null, endReason = ended?.reason ?? null): Promise<void> {
  if (!dataDir || !forSession || !engine) return
  const now = Date.now()
  const own = engine.ownInflight(now)
  const heartbeat = {
    schema: SCHEMA,
    mod: MOD_VERSION,
    engine: engineVersion,
    sessionId: forSession,
    process: processKey,
    nonce: loadNonce,
    surface,
    interactive,
    mode: config.mode,
    configVersion: config.version,
    configProblems: config.problems,
    projectKey,
    startedAt: iso(startedAt),
    beatAt: iso(now),
    ended: isEnded,
    endReason,
    readings: lastReadings,
    readingsAt: readingsChangedAt > 0 ? iso(readingsChangedAt) : null,
    inflight: own.map(a => ({ agentId: a.agentId, type: a.type, model: a.model, steps: a.steps, spentEq: a.eq, runId: a.runId, agentIndex: a.agentIndex, startedAt: iso(a.startedAtMs) })),
    reserved: engine.reserved.size,
    spent: { ...engine.spent },
    mainEqPerMinute: isEnded ? {} : engine.mainEqPerMinute(now),
    pending: engine.pending.filter(p => p.sessionId === forSession).slice(-50)
      .map(p => ({ ...p, at: iso(p.atMs), deferUntil: p.deferUntilMs === null ? null : iso(p.deferUntilMs) })),
    landing: judging() ? engine.landing(now) : [],
    prices: judging() ? engine.priceTable(now) : [],
    counters: { ...counters, dropped: journal.dropped },
    lastError,
    logFile: partPath(journal.part),
  }
  try {
    await $.fs.write(join(dataDir, 'sessions', `${forSession}.json`), JSON.stringify(heartbeat, null, 1))
  } catch (error) {
    counters.errors += 1
    lastError = `beat: ${clip(String(error), 200) ?? 'unknown'}`
  }
}

function noteReadings(raw: unknown, src: string): void {
  const readings = normalizeReadings(raw)
  const sig = readingsSignature(readings)
  if (sig === lastReadingsSig) return
  const now = Date.now()
  lastReadingsSig = sig
  lastReadings = readings
  readingsChangedAt = now
  record(readingsRow(readings, src))
  if (!engine || config.mode === 'off') return
  for (const sample of engine.observeReadings(readings, now)) {
    calibrationDirty = true
    record({ ev: 'price', ...sample })
  }
}

async function pollReadings($: Api, src: string): Promise<void> {
  const usage = await $.session.usage()
  noteReadings(usage.rateLimits, src)
}

/** 不在关键路径上等的取数:记录用,出错只记一笔。 */
function pollLater($: Api, src: string): void {
  pollReadings($, src).catch(error => noteError(`poll:${src}`, error))
}

/**
 * 启用模式:到期的推迟项(本对话、主循环派发)在会话空闲时以插件消息补派一次;先提交、成功再出队,失败最多重试 3 次。
 * 影子模式只记"本来会补派"。
 */
async function redispatch($: Api): Promise<void> {
  if (!engine || !judging()) return
  const now = Date.now()
  const shadowDue = engine.pending.filter(p => !p.applied && p.deferUntilMs !== null && p.deferUntilMs <= now)
  if (shadowDue.length > 0) {
    record({ ev: 'redispatch', applied: false, items: shadowDue.map(p => ({ seq: p.seq, run: p.runId, idx: p.agentIndex })) })
    engine.dropPending(shadowDue)
  }
  // 换过对话(/clear)或嵌套派发的真推迟项不提醒到当前主会话,到期后只记一笔丢弃。
  const stranded = engine.pending.filter(p => p.applied && p.deferUntilMs !== null && p.deferUntilMs <= now && (p.nested || p.sessionId !== sessionId))
  if (stranded.length > 0) {
    record({ ev: 'redispatch', applied: false, dropped: 'other-conversation-or-nested', items: stranded.map(p => ({ seq: p.seq, run: p.runId, idx: p.agentIndex })) })
    engine.dropPending(stranded)
  }
  if (config.mode !== 'enforce' || !config.params.redispatch || mainTurnRunning) return
  const due = engine.dueRedispatch(now, sessionId)
  if (due.length === 0) return
  const resetAt = Math.max(...due.map(d => d.deferUntilMs ?? 0))
  for (const d of due) d.attempts += 1
  try {
    const result = await $.prompt.submit({ text: redispatchText(due, resetAt) })
    const dropped = (result as { drop?: string }).drop
    if (dropped !== undefined) throw new Error(`被丢弃:${clip(dropped, 80) ?? ''}`)
    engine.dropPending(due)
    counters.redispatches += 1
    record({ ev: 'redispatch', applied: true, ok: true, items: due.map(p => ({ seq: p.seq, run: p.runId, idx: p.agentIndex })) })
  } catch (error) {
    const giveUp = due.filter(d => d.attempts >= 3)
    if (giveUp.length > 0) engine.dropPending(giveUp)
    record({ ev: 'redispatch', applied: true, ok: false, gaveUp: giveUp.length, msg: clip(String(error), 160) ?? null })
  }
}

async function boot($: Api, cwd: string, startSurface: string | null, isInteractive: boolean): Promise<void> {
  dataDir = await resolveDataDir($)
  sep = dataDir.includes('\\') ? '\\' : '/'
  sessionId = await $.session.id()
  bootSessionId = sessionId
  loadNonce = randomHex(3)
  startedAt = Date.now()
  loadStamp = iso(startedAt).replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z')
  processKey = `${loadStamp}-${loadNonce}`
  surface = startSurface
  interactive = isInteractive
  const version = await $.session.version()
  engineVersion = version.version
  await readSalt($)
  projectKey = (await shortHash(normalizeCwd(cwd))).slice(0, 12)
  if (dataDir) await readConfig($)
  engine = new BudgetEngine(processKey, config.params, startedAt)
  if (dataDir && config.mode !== 'off') {
    try {
      const disk = await readCalibration($)
      engine.absorbSamples(disk.samples)
      engine.absorbHistory(disk.history)
    } catch (error) {
      noteError('calibration', error)
    }
    await readSnapshot($)
  }
  isBooted = true
  record({
    ev: 'load', schema: SCHEMA, mod: MOD_VERSION, engine: engineVersion, base: version.base ?? null,
    surface, interactive, mode: config.mode, cfg: config.version, params: config.params, project: projectKey,
    dir: dataDir ? 'ok' : 'none', samples: engine.samples.length, history: engine.history.length,
  })
  pollLater($, 'start')
  // 其它会话的心跳不在启动路径上读(24 小时内可能有上百个文件,会拖慢第一个提示);账本缺席的进程按基线规则处理。
  $.clock.after(0, () => refreshPeers($, true))
  $.clock.every(FLUSH_MS, () => flush($))
  $.clock.every(PEERS_MS, () => refreshPeers($))
  $.clock.every(BEAT_MS, async () => {
    try {
      await readConfig($)
      await readSnapshot($)
      await rebindIfChanged($, 'beat')
      await pollReadings($, 'beat')
      await redispatch($)
      await writeCalibration($)
    } catch (error) {
      noteError('beat', error)
    }
    await beat($)
  })
  await beat($)
  await flush($)
}

/** 会话 id 变了(/clear、会话内 /resume):旧会话的心跳标结束,之后按新 id 写。进程键不变,账本与在途照常延续。 */
async function rebindIfChanged($: Api, why: string): Promise<void> {
  const id = await $.session.id()
  if (id === sessionId) return
  await beat($, sessionId, true, why)
  sessionId = id
  ended = null
  record({ ev: 'rebind', why })
}

/** 软着陆(启用模式):读数进入收尾线后,在途子代理的工具调用不执行,让它带着已有结论收尾。影子模式每个 agent 只记一次。 */
function landingVerdict(agentId: string | undefined, tool: string): string | null {
  if (!engine || agentId === undefined || !judging() || HAND_IN_TOOLS.has(tool)) return null
  const agent = engine.agents.get(agentId)
  if (!agent || agent.ended) return null
  const now = Date.now()
  const windows = engine.landing(now)
  if (windows.length === 0) return null
  const count = (landedAgents.get(agentId) ?? 0) + 1
  const applied = config.mode === 'enforce' && config.params.softLanding
  if (!applied && count > 1) return null
  landedAgents.set(agentId, count)
  counters.landings += 1
  if (applied) {
    counters.landingsApplied += 1
    engine.markLanded(agentId)
  }
  record({ ev: 'landing', agentId, windows, applied, tool, count })
  return applied ? landingMessage(engine.effectiveWindows(now).filter(w => windows.includes(w.kind)), count) : null
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    try {
      await boot($, e.cwd, e.surface, e.isInteractive)
    } catch (error) {
      noteError('session.start', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.SessionStart', async ($, e, next) => {
    try {
      if (isBooted) await rebindIfChanged($, String(e.source))
    } catch (error) {
      noteError('classic.SessionStart', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('session.measure', async ($, e, next) => {
    try {
      if (isBooted) {
        counters.measures += 1
        record({
          ev: 'measure',
          changed: e.changed,
          ctx: { tokens: e.context.tokens ?? null, window: e.context.window, pct: e.context.percent ?? null },
          usd: e.cost?.usd ?? null,
        })
        noteReadings(e.rateLimits, 'measure')
      }
    } catch (error) {
      noteError('session.measure', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('turn.start', async ($, e, next) => {
    if (e.agentId === undefined) mainTurnRunning = true
    return next(e)
  }).catch(($, e, next) => next(e))

  on('turn.step', async function* ($, e, next) {
    const t0 = Date.now()
    const result = yield* next(e)
    // 流式钩子要手动兜底:记录失败绝不能让请求失败。取读数不等(只为记录,不在请求的关键路径上)。
    try {
      if (isBooted && config.mode !== 'off') {
        counters.steps += 1
        const usage = result.usage ? usageOf(result.usage) : undefined
        const answered = result.usage?.model
        engine?.observeStep(e.agentId, answered, usage, Date.now())
        record(stepRow({
          agentId: e.agentId, turnId: e.turnId, index: e.index, requestedModel: e.model,
          answeredModel: answered, usage, stopReason: result.stopReason, ms: Date.now() - t0,
        }))
        pollLater($, 'step')
      }
    } catch (error) {
      noteError('turn.step', error)
    }
    return result
  })

  on('turn.complete', async ($, e, next) => {
    try {
      if (isBooted) {
        const usage = e.usage ? usageOf(e.usage) : undefined
        record({
          ev: 'turn_end', agent: e.agentId ?? null, turn: e.turnId, reason: e.reason, aborted: e.isAborted,
          ms: e.durationMs, model: e.usage?.model ?? null, u: usage ?? null, eq: usage ? eqOf(usage) : 0,
        })
        if (e.agentId === undefined) {
          mainTurnRunning = false
        } else if (engine) {
          const outcome = engine.agentEnded(e.agentId, Date.now(), e.reason)
          landedAgents.delete(e.agentId)
          if (outcome) {
            if (outcome.complete) calibrationDirty = true
            record({ ev: 'agent_done', ...outcome, reason: e.reason })
          }
        }
      }
    } catch (error) {
      noteError('turn.complete', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('agent.spawn', async ($, e, next) => {
    const t0 = Date.now()
    const seq = ++spawnSeq
    let deny: string | null = null
    let facts: SpawnFacts | null = null
    // 判断与预留必须在同一步、中间不能有 await:同一 parallel() 批次的 spawn 几乎同时到达,
    // 判断前若先 await,恢复次序不保证先来先判,"只推迟后缀"就可能被打破。读数用已有的(含快照兜底),取数放到判断之后。
    try {
      if (isBooted && engine && judging()) {
        facts = {
          seq,
          type: e.subagentType,
          requestedModel: e.model,
          parentModel: e.parentModel,
          runId: e.workflow?.runId ?? null,
          agentIndex: e.workflow?.agentIndex ?? null,
          fork: e.fork,
          isResume: e.resume !== undefined,
          sessionId,
          nested: e.parentAgentId !== undefined,
          nowMs: t0,
        }
        spawnFacts.set(seq, facts)
        const judgement = engine.judgeSpawn(facts)
        const d = judgement.decision
        const enforce = config.mode === 'enforce'
        if (d.verdict === 'defer') {
          counters.defer += 1
          engine.noteDeferral(facts, judgement, enforce)
          if (enforce) {
            counters.deniedApplied += 1
            engine.forget(seq)
            const key = e.workflow ? `${e.workflow.runId}#${e.workflow.agentIndex}` : await shortHash(e.description)
            const attempt = (deferCounts.get(key) ?? 0) + 1
            deferCounts.set(key, attempt)
            if (deferCounts.size > DEFER_COUNT_CAP) deferCounts.delete(deferCounts.keys().next().value as string)
            deny = deferMessage(d, attempt, config.params.redispatch && !facts.nested)
          } else {
            engine.beginSpawn(seq, facts)
          }
        } else {
          counters.allow += 1
          engine.beginSpawn(seq, facts)
        }
        record({
          ev: 'decision', seq, mode: config.mode, verdict: d.verdict, applied: enforce && d.verdict === 'defer', rule: d.rule,
          model: judgement.model, type: e.subagentType, nested: facts.nested, resume: facts.isResume,
          wf: e.workflow ? { run: e.workflow.runId, idx: e.workflow.agentIndex } : null,
          cost: { med: Math.round(judgement.cost.medianEq), p80: Math.round(judgement.cost.p80Eq), basis: judgement.cost.basis, sib: judgement.cost.siblings },
          rate: Math.round(judgement.rateP80),
          win: d.windows,
          until: d.deferUntilMs === null ? null : iso(d.deferUntilMs),
          binding: d.binding,
        })
      }
    } catch (error) {
      deny = null
      if (engine) engine.forget(seq)
      noteError('agent.spawn:judge', error)
    }
    if (isBooted && judging()) pollLater($, 'spawn')
    try {
      if (isBooted && config.mode !== 'off') {
        counters.spawns += 1
        record(spawnPreRow({
          seq,
          toolUseId: e.tool_use_id,
          subagentType: e.subagentType,
          provider: { plugin: e.provider?.plugin, tier: e.provider?.tier },
          requestedModel: e.model,
          parentModel: e.parentModel,
          parentAgentId: e.parentAgentId,
          background: e.background,
          fork: e.fork,
          isTeammate: e.isTeammate === true,
          workflow: e.workflow,
          resumeAgentId: e.resume?.agentId,
          isNamed: e.name !== undefined,
          promptLength: e.prompt.length,
          promptHash: await shortHash(e.prompt),
          descriptionHash: await shortHash(e.description),
        }))
      }
    } catch (error) {
      noteError('agent.spawn:pre', error)
    }
    if (deny !== null) {
      spawnFacts.delete(seq)
      record(spawnPostRow(seq, { deny }, Date.now() - t0))
      return { deny }
    }
    let result: Awaited<ReturnType<typeof next>> | undefined
    try {
      result = await next(e)
      return result
    } finally {
      // next 抛错(被打断、下游出错)也要撤销预留,否则这份预留永远算在途。
      try {
        if (isBooted) {
          if (result) record(spawnPostRow(seq, result, Date.now() - t0))
          const settled = spawnFacts.get(seq)
          spawnFacts.delete(seq)
          if (engine && settled) {
            const agentId = result && result.deny === undefined ? result.agentId : undefined
            engine.spawnSettled(seq, agentId, result?.model, settled)
          } else if (engine) {
            engine.forget(seq)
          }
        }
      } catch (error) {
        noteError('agent.spawn:post', error)
      }
    }
  }).catch(($, e, next) => next(e))

  on('prompt.submit', async ($, e, next) => {
    let context: string | null = null
    try {
      if (isBooted) {
        counters.prompts += 1
        const row = promptRow({
          originKind: e.origin.kind,
          originPlugin: e.origin.kind === 'plugin' ? e.origin.name : undefined,
          isMeta: e.isMeta === true,
          wait: e.wait,
          hasCommand: e.command !== undefined,
          text: e.text,
          turnRunning: e.turnId !== undefined,
          contextBlocks: e.context?.length ?? 0,
        })
        record(row)
        // 续跑(Desktop 的自动继续原文)与工作流回报时附预算简报:放本轮 context,不动系统提示,不冲掉主会话缓存。
        // 同一批待补派与收尾状态只简报一次;用户自己键入的提示(composer)不附。
        const isResumeLike = (row.limitLike && e.origin.kind !== 'composer') ||
          e.origin.kind === 'auto-continuation' || e.origin.kind === 'task-notification'
        if (engine && judging() && isResumeLike && e.command === undefined) {
          const now = Date.now()
          const pending = engine.pending.filter(p => p.sessionId === sessionId && !p.nested && (p.applied || config.mode === 'shadow'))
          const landing = engine.landing(now)
          const sig = `${pending.map(p => p.seq).join(',')}|${landing.join(',')}`
          if ((pending.length > 0 || landing.length > 0) && sig !== lastBriefSig) {
            lastBriefSig = sig
            const applied = config.mode === 'enforce' && config.params.briefing
            counters.briefs += 1
            record({ ev: 'brief', applied, origin: e.origin.kind, pending: pending.length, landing })
            if (applied) context = briefingText(engine.effectiveWindows(now), pending, landing)
          }
        }
      }
    } catch (error) {
      context = null
      noteError('prompt.submit', error)
    }
    return context === null ? next(e) : next({ ...e, context: [...(e.context ?? []), context] })
  }).catch(($, e, next) => next(e))

  on('tool.call', async ($, e, next) => {
    let deny: string | null = null
    try {
      if (isBooted) deny = landingVerdict(e.agentId, String(e.tool))
    } catch (error) {
      deny = null
      noteError('tool.call:landing', error)
    }
    return deny === null ? next(e) : { deny }
  }).catch(($, e, next) => next(e))

  on('tool.call', { tool: 'Agent' }, async ($, e, next) => {
    const t0 = Date.now()
    const result = await next(e)
    try {
      if (isBooted && config.mode !== 'off') {
        const rec = (result as { result?: Record<string, unknown> }).result ?? {}
        record({
          ev: 'tool', tool: 'Agent', agent: e.agentId ?? null, ms: Date.now() - t0,
          deny: result.deny !== undefined, err: (result as { isError?: boolean }).isError === true,
          status: typeof rec.status === 'string' ? rec.status : null,
          agentId: typeof rec.agentId === 'string' ? rec.agentId : null,
          model: typeof rec.resolvedModel === 'string' ? rec.resolvedModel : null,
          totalTokens: typeof rec.totalTokens === 'number' ? rec.totalTokens : null,
          tools: typeof rec.totalToolUseCount === 'number' ? rec.totalToolUseCount : null,
          durMs: typeof rec.totalDurationMs === 'number' ? rec.totalDurationMs : null,
          reqModel: e.model ?? null,
        })
      }
    } catch (error) {
      noteError('tool.call:Agent', error)
    }
    return result
  }).catch(($, e, next) => next(e))

  on('tool.call', { tool: 'Workflow' }, async ($, e, next) => {
    try {
      // 续跑:前缀从缓存返回,agentIndex 从头数,该 run 的前缀闭合与推迟项随之解除。
      if (isBooted && engine && typeof e.resumeFromRunId === 'string') engine.workflowResumed(e.resumeFromRunId)
    } catch (error) {
      noteError('tool.call:Workflow:resume', error)
    }
    const t0 = Date.now()
    const result = await next(e)
    try {
      if (isBooted && config.mode !== 'off') {
        const rec = (result as { result?: Record<string, unknown> }).result ?? {}
        record({
          ev: 'tool', tool: 'Workflow', agent: e.agentId ?? null, ms: Date.now() - t0,
          deny: result.deny !== undefined, err: (result as { isError?: boolean }).isError === true,
          status: typeof rec.status === 'string' ? rec.status : null,
          runId: typeof rec.runId === 'string' ? rec.runId : null,
          taskType: typeof rec.taskType === 'string' ? rec.taskType : null,
          resumeFrom: typeof e.resumeFromRunId === 'string' ? e.resumeFromRunId : null,
          named: typeof e.name === 'string',
          syntaxError: typeof rec.error === 'string',
        })
      }
    } catch (error) {
      noteError('tool.call:Workflow', error)
    }
    return result
  }).catch(($, e, next) => next(e))

  on('tool.call', { tool: 'SendMessage' }, async ($, e, next) => {
    const t0 = Date.now()
    const result = await next(e)
    try {
      if (isBooted && config.mode !== 'off') {
        record({
          ev: 'tool', tool: 'SendMessage', agent: e.agentId ?? null, ms: Date.now() - t0,
          deny: result.deny !== undefined, err: (result as { isError?: boolean }).isError === true,
        })
      }
    } catch (error) {
      noteError('tool.call:SendMessage', error)
    }
    return result
  }).catch(($, e, next) => next(e))

  on('classic.SubagentStart', async ($, e, next) => {
    try {
      if (isBooted) record({ ev: 'sub_start', agentId: e.agent_id ?? null, type: e.agent_type ?? null })
    } catch (error) {
      noteError('classic.SubagentStart', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.SubagentStop', async ($, e, next) => {
    try {
      if (isBooted) record({ ev: 'sub_stop', agentId: e.agent_id ?? null, type: e.agent_type ?? null })
    } catch (error) {
      noteError('classic.SubagentStop', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.StopFailure', async ($, e, next) => {
    try {
      if (isBooted) {
        record({ ev: 'stop_failure', error: e.error, agentId: e.agent_id ?? null, details: clip(e.error_details, 200) ?? null })
        pollLater($, 'stop_failure')
        // 被动发现按模型族的限额:子代理首个请求就被限额拒绝(几乎不花钱)、而 5 小时与周窗口都还没到收尾线,
        // 多半是这个族的周额度满了:把它的模型标成受限至该族周额度重置(快照里没有就先按 1 小时复判)。
        if (engine && e.error === 'rate_limit' && e.agent_id !== undefined) {
          const agent = engine.agents.get(e.agent_id)
          if (agent && agent.steps <= 1) {
            const now = Date.now()
            const windows = engine.effectiveWindows(now)
            const mainOk = windows.every(w => w.percentUsed < 100 - config.params.landingDelta[w.kind])
            const scoped = engine.scopedFor(agent.model, now)
            if (mainOk) {
              const until = scoped?.resetsAtMs ?? now + 3_600_000
              engine.markLimited(agent.model, until)
              record({ ev: 'limited', model: agent.model, family: familyOf(agent.model), until: iso(until), source: scoped ? 'snapshot' : 'guess' })
            }
          }
        }
      }
    } catch (error) {
      noteError('classic.StopFailure', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.Notification', async ($, e, next) => {
    try {
      if (isBooted) record({ ev: 'notification', type: e.notification_type })
    } catch (error) {
      noteError('classic.Notification', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('session.end', async ($, e, next) => {
    try {
      if (isBooted) {
        record({ ev: 'end', reason: e.reason })
        // /clear 与会话内 /resume:进程还在,只是这个会话结束了——给它的心跳标结束,进程状态不动,之后按新 id 继续。
        const processContinues = e.reason === 'clear' || e.reason === 'resume'
        if (!processContinues) ended = { reason: e.reason }
        // 三件事并行写,挤进 session.end 的短时限里:日志(含 end 行)、心跳、校准。
        await Promise.all([
          flush($),
          beat($, e.sessionId || sessionId, true, e.reason),
          writeCalibration($, true),
        ])
      }
    } catch (error) {
      noteError('session.end', error)
    }
    return next(e)
  }).catch(($, e, next) => next(e))
}
