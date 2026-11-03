// 预算闸门的状态机:吃事件(读数、逐次请求计量、派发、结束、其它会话的心跳),吐判断与证据。
// 纯逻辑,时间由调用方给(nowMs),不碰 `$`;线上由适配层驱动,离线回放(V1)把录下来的 JSONL 按时间喂给同一个它。

import { periodOf } from './clock.ts'
import { WINDOWS, type Params, type WindowKind } from './config.ts'
import {
  advanceSegment,
  mergeSamples,
  priceOf,
  type EqLedger,
  type Price,
  type PriceSample,
  type Segment,
} from './calibrate.ts'
import { estimateCost, rateQuantile, remainingP80, type CompletedAgent, type CostEstimate } from './cost.ts'
import { closeAfter, decide, landingWindows, type Closure, type Decision, type Inflight, type ScopedState, type WindowState } from './decide.ts'
import { eqOf, type Reading, type Usage4 } from './meter.ts'
import { familyOf, modelKey, resolveModelKey } from './model.ts'

const MAIN_WINDOW_MS = 15 * 60_000
const HISTORY_CAP = 300
const PENDING_CAP = 1000
const PEER_FRESH_MS = 3 * 60_000
const SNAPSHOT_FRESH_MS = 30 * 60_000
/** 两小时没有任何请求的 agent 不再算在途:漏收结束事件(重试、远端 agent)时不至于永远占着余量。 */
const INFLIGHT_STALE_MS = 2 * 3_600_000
const RESET_TOLERANCE_MS = 5 * 60_000

export type ObservedWindow = WindowState & { observedAtMs: number; carried?: boolean }

/** 同一窗口的候选读数:丢掉重置已过的,在重置时刻最晚的周期里取百分比最大的(同值取观测更晚的)。 */
function pickCycleMax(candidates: readonly ObservedWindow[], nowMs: number): ObservedWindow | null {
  const live = candidates.filter(c => c.resetsAtMs === null || c.resetsAtMs > nowMs)
  if (live.length === 0) return null
  const resets = live.map(c => c.resetsAtMs).filter((r): r is number => r !== null)
  const latest = resets.length > 0 ? Math.max(...resets) : null
  const cycle = latest === null ? live : live.filter(c => c.resetsAtMs !== null && latest - c.resetsAtMs <= RESET_TOLERANCE_MS)
  let best = cycle[0]!
  for (const c of cycle) {
    if (c.percentUsed > best.percentUsed || (c.percentUsed === best.percentUsed && c.observedAtMs > best.observedAtMs)) best = c
  }
  return best
}

/** 其它会话心跳里本引擎关心的部分。`key` 是进程键(启动时刻 + 随机串),/clear 换会话也不变。 */
export type PeerView = {
  key: string
  startedAtMs: number
  readings: readonly ObservedWindow[]
  /** 该进程累计计量:模型键 → 等价 token。 */
  spent: Readonly<Record<string, number>>
  /** 该进程在途的子代理。 */
  inflight: readonly { agentId: string; model: string; steps: number; spentEq: number; type: string }[]
  /** 该进程主循环近 15 分钟的消耗:模型键 → 等价 token / 分钟。 */
  mainEqPerMinute: Readonly<Record<string, number>>
  beatAtMs: number
}

type TrackedAgent = {
  agentId: string
  type: string
  model: string
  runId: string | null
  agentIndex: number | null
  startedAtMs: number
  lastStepAtMs: number
  steps: number
  eq: number
  ended: boolean
  /** 软着陆过:它的结束是被截断的,成本不进历史。 */
  landed: boolean
  /** 派发时的成本预测(复盘 p80 覆盖率)。 */
  predicted: CostEstimate | null
  seq: number | null
}

export type SpawnFacts = {
  seq: number
  type: string
  requestedModel?: string
  parentModel: string
  runId: string | null
  agentIndex: number | null
  fork: boolean
  isResume: boolean
  /** 派发时所在的对话(/clear 之后补派不能发到新对话里)。 */
  sessionId: string
  /** 子代理里嵌套派发(父不是主循环):拒绝理由已交给那个子代理,补派不提醒到主会话。 */
  nested: boolean
  nowMs: number
}

export type SpawnJudgement = {
  seq: number
  model: string
  cost: CostEstimate
  rateP80: number
  decision: Decision
}

export type PendingItem = {
  seq: number
  runId: string | null
  agentIndex: number | null
  type: string
  model: string
  rule: Decision['rule']
  deferUntilMs: number | null
  atMs: number
  sessionId: string
  nested: boolean
  /** 影子模式下只是"本来会推迟";启用模式下是真推迟了。 */
  applied: boolean
  /** 补派提醒已尝试的次数(提交失败时保留重试,最多 3 次)。 */
  attempts: number
}

export type AgentOutcome = {
  agentId: string
  seq: number | null
  type: string
  model: string
  runId: string | null
  eq: number
  steps: number
  durationMs: number
  /** 正常作答结束且没有被软着陆截断:只有这种才进成本历史、才算 p80 覆盖率。 */
  complete: boolean
  predictedMedianEq: number | null
  predictedP80Eq: number | null
  covered: boolean | null
}

export class BudgetEngine {
  params: Params
  samples: PriceSample[] = []
  history: CompletedAgent[] = []
  readonly windows = new Map<WindowKind, ObservedWindow>()
  readonly segments = new Map<WindowKind, Segment>()
  readonly spent: Record<string, number> = {}
  readonly agents = new Map<string, TrackedAgent>()
  readonly closures = new Map<string, Closure>()
  readonly limited = new Map<string, number>()
  readonly judged = new Map<number, SpawnJudgement>()
  /**
   * 判断之后、启动之前的派发(按 seq)。实测同一 parallel() 批次的 agent.spawn 在 1 毫秒内全部先触发、之后才逐个启动,
   * 所以闸门与预留必须在同一步完成:判断放行后立即同步预留,后面的候选才看得见它。
   */
  readonly reserved = new Map<number, { model: string; type: string; runId: string | null; costP80Eq: number }>()
  pending: PendingItem[] = []
  peers: PeerView[] = []
  scoped: ScopedState[] = []
  /**
   * AI Resume 快照里的 OAuth 5h/7d 读数:只在没有更新的读数时兜底(实测全新会话第一次派发时 $.session.usage() 还是空的)。
   * 承接值(carried)不用:它是同一周期里更早的服务端读数,只会偏低。
   */
  snapshotWindows: ObservedWindow[] = []
  private mainSteps: { atMs: number; model: string; eq: number }[] = []
  readonly selfKey: string
  readonly startedAtMs: number

  constructor(selfKey: string, params: Params, startedAtMs = 0) {
    this.selfKey = selfKey
    this.params = params
    this.startedAtMs = startedAtMs
  }

  /** 本机见过的模型键(本会话计量 + 价格样本 + 其它会话计量)。 */
  knownModels(): string[] {
    const seen: string[] = []
    const add = (m: string): void => {
      if (!seen.includes(m)) seen.push(m)
    }
    for (const s of this.samples) add(s.model)
    for (const p of this.peers) for (const m of Object.keys(p.spent)) add(m)
    for (const m of Object.keys(this.spent)) add(m)
    return seen
  }

  price(window: WindowKind, model: string, nowMs: number): Price {
    return priceOf(this.samples, window, model, nowMs, this.params, this.knownModels())
  }

  /** 全机账本:本进程 + 24 小时内见过的其它进程(新鲜与否不论:段两端都要有它,增量才算得对)。 */
  ledger(): EqLedger {
    const ledger: EqLedger = { [this.selfKey]: { startedAtMs: this.startedAtMs, models: { ...this.spent } } }
    for (const p of this.peers) if (p.key !== this.selfKey) ledger[p.key] = { startedAtMs: p.startedAtMs, models: { ...p.spent } }
    return ledger
  }

  /**
   * 读数是账号级的。本会话与 3 分钟内的其它会话心跳合并,规则不看"谁观测得晚"(空闲会话的心跳每分钟刷新,但它缓存的是
   * 几小时前的读数),而是:先丢掉重置时刻已过的;在重置时刻最晚的那个周期里读数只增不减,取最大的那条。
   * 两者都没有时才用 30 分钟内的 AI Resume 快照兜底(它来自 OAuth,换账号时可能不是同一个账号,不参与取最大)。
   */
  effectiveWindows(nowMs: number): ObservedWindow[] {
    const out: ObservedWindow[] = []
    for (const kind of WINDOWS) {
      const candidates: ObservedWindow[] = []
      const own = this.windows.get(kind)
      if (own) candidates.push({ ...own, source: 'self' })
      for (const p of this.peers) {
        if (p.key === this.selfKey || nowMs - p.beatAtMs > PEER_FRESH_MS) continue
        for (const w of p.readings) if (w.kind === kind) candidates.push({ ...w, source: 'peer' })
      }
      let best = pickCycleMax(candidates, nowMs)
      if (!best) {
        const fallback = this.snapshotWindows.filter(w => w.kind === kind && !w.carried && nowMs - w.observedAtMs <= SNAPSHOT_FRESH_MS)
        best = pickCycleMax(fallback.map(w => ({ ...w, source: 'snapshot' as const })), nowMs)
      }
      if (best) out.push(best)
    }
    return out
  }

  setPeers(peers: readonly PeerView[]): void {
    this.peers = peers.filter(p => p.key !== this.selfKey)
  }

  absorbSamples(samples: readonly PriceSample[]): void {
    this.samples = mergeSamples(this.samples, samples)
  }

  /** 并入子代理历史:同一 agent 以后来的为准(队友被多次唤醒时,后一次的累计才完整)。 */
  absorbHistory(history: readonly CompletedAgent[]): void {
    const byId = new Map<string, CompletedAgent>()
    for (const h of [...this.history, ...history]) {
      const held = byId.get(h.agentId)
      if (!held || h.endedAtMs >= held.endedAtMs) byId.set(h.agentId, h)
    }
    this.history = [...byId.values()].sort((a, b) => a.endedAtMs - b.endedAtMs).slice(-HISTORY_CAP)
  }

  /** 新读数:更新窗口并推进校准切段;返回新产生的价格样本(调用方记日志并落盘)。 */
  observeReadings(readings: readonly Reading[], nowMs: number): PriceSample[] {
    const fresh: PriceSample[] = []
    for (const r of readings) {
      if (r.kind !== 'five_hour' && r.kind !== 'seven_day') continue
      const resetsAtMs = r.resetsAt ? Date.parse(r.resetsAt) : NaN
      const window: ObservedWindow = {
        kind: r.kind,
        percentUsed: r.percentUsed,
        resetsAtMs: Number.isFinite(resetsAtMs) ? resetsAtMs : null,
        observedAtMs: nowMs,
      }
      const held = this.windows.get(r.kind)
      this.windows.set(r.kind, window)
      if (held && held.percentUsed === window.percentUsed && held.resetsAtMs === window.resetsAtMs) continue
      const step = advanceSegment(this.segments.get(r.kind) ?? null, r.kind, r.percentUsed, window.resetsAtMs, nowMs, this.ledger(), this.params)
      this.segments.set(r.kind, step.segment)
      if (step.sample) fresh.push(step.sample)
    }
    if (fresh.length > 0) this.samples = mergeSamples(this.samples, fresh)
    return fresh
  }

  /** 一次请求结束:计入本进程账本、所属 agent、主循环近 15 分钟。 */
  observeStep(agentId: string | undefined, answeredModel: string | undefined, usage: Usage4 | undefined, nowMs: number): void {
    if (!usage || !answeredModel) return
    const model = modelKey(answeredModel)
    const eq = eqOf(usage)
    this.spent[model] = (this.spent[model] ?? 0) + eq
    if (agentId === undefined) {
      this.mainSteps.push({ atMs: nowMs, model, eq })
      this.mainSteps = this.mainSteps.filter(s => nowMs - s.atMs <= MAIN_WINDOW_MS)
      return
    }
    const agent = this.agents.get(agentId)
    if (agent) {
      agent.steps += 1
      agent.eq += eq
      agent.model = model
      agent.lastStepAtMs = nowMs
    }
  }

  /** 主循环近 15 分钟的消耗速率:模型键 → 等价 token / 分钟。 */
  mainEqPerMinute(nowMs: number): Record<string, number> {
    const rate: Record<string, number> = {}
    for (const s of this.mainSteps) {
      if (nowMs - s.atMs > MAIN_WINDOW_MS) continue
      rate[s.model] = (rate[s.model] ?? 0) + s.eq / (MAIN_WINDOW_MS / 60_000)
    }
    return rate
  }

  /** 本进程在途的 agent:已启动、没结束、两小时内还有过请求。 */
  ownInflight(nowMs: number): TrackedAgent[] {
    return [...this.agents.values()].filter(a => !a.ended && nowMs - a.lastStepAtMs <= INFLIGHT_STALE_MS)
  }

  /** 本会话在途总数:已启动未结束 + 已预留未启动。 */
  ownInflightCount(nowMs: number): number {
    return this.ownInflight(nowMs).length + this.reserved.size
  }

  /** 候选模型族的周额度:同族多条(多个版本)取用得最多的那条,重置时刻已过的不算。 */
  scopedFor(model: string, nowMs: number): ScopedState | null {
    const family = familyOf(model)
    let best: ScopedState | null = null
    for (const s of this.scoped) {
      if (s.family !== family || s.percentUsed === null || (s.resetsAtMs !== null && s.resetsAtMs <= nowMs)) continue
      if (!best || (best.percentUsed ?? -1) < s.percentUsed) best = s
    }
    return best
  }

  /** 判断一个候选派发(只读状态,不预留;影子与启用模式都调用)。 */
  judgeSpawn(f: SpawnFacts): SpawnJudgement {
    const nowMs = f.nowMs
    const model = resolveModelKey(f.requestedModel, f.parentModel, this.knownModels())
    const cost = estimateCost(this.history, { type: f.type, runId: f.runId, fork: f.fork }, nowMs, this.params)
    const rateP80 = rateQuantile(this.history, nowMs, this.params)
    const inflight: Inflight[] = []
    for (const a of this.ownInflight(nowMs)) {
      const prior = a.predicted ?? estimateCost(this.history, { type: a.type, runId: a.runId, fork: false }, nowMs, this.params)
      inflight.push({ agentId: a.agentId, model: a.model, steps: a.steps, spentEq: a.eq, remainingP80Eq: remainingP80(a.steps, a.eq, prior.p80Eq) })
    }
    for (const [seq, r] of this.reserved) {
      inflight.push({ agentId: `seq:${seq}`, model: r.model, steps: 0, spentEq: 0, remainingP80Eq: r.costP80Eq })
    }
    for (const p of this.peers) {
      if (nowMs - p.beatAtMs > PEER_FRESH_MS) continue
      for (const a of p.inflight) {
        const prior = estimateCost(this.history, { type: a.type, runId: null, fork: false }, nowMs, this.params)
        inflight.push({ agentId: a.agentId, model: a.model, steps: a.steps, spentEq: a.spentEq, remainingP80Eq: remainingP80(a.steps, a.spentEq, prior.p80Eq) })
      }
    }
    const main: Record<string, number> = { ...this.mainEqPerMinute(nowMs) }
    for (const p of this.peers) {
      if (nowMs - p.beatAtMs > PEER_FRESH_MS) continue
      for (const [m, r] of Object.entries(p.mainEqPerMinute)) main[m] = (main[m] ?? 0) + r
    }
    const limitedUntil = this.limited.get(model) ?? null
    const decision = decide({
      nowMs,
      windows: this.effectiveWindows(nowMs),
      price: (w, m) => this.price(w, m, nowMs),
      candidate: {
        model,
        costMedianEq: cost.medianEq,
        costP80Eq: cost.p80Eq,
        rateP80EqPerMinute: rateP80,
        runId: f.runId,
        agentIndex: f.agentIndex,
        isResume: f.isResume,
      },
      inflight,
      ownInflight: this.ownInflightCount(nowMs),
      mainEqPerMinute: main,
      closure: f.runId === null ? null : this.closures.get(f.runId) ?? null,
      scoped: this.scopedFor(model, nowMs),
      limitedUntilMs: limitedUntil,
      params: this.params,
    })
    const judgement: SpawnJudgement = { seq: f.seq, model, cost, rateP80, decision }
    this.judged.set(f.seq, judgement)
    return judgement
  }

  /**
   * 一次推迟(影子模式下是"本来会推迟")进队列并更新前缀闭合。影子模式也要更新闭合,
   * 否则"如果启用会怎么做"的后续判断就不是启用后的真实行为。
   */
  noteDeferral(f: SpawnFacts, judgement: SpawnJudgement, applied: boolean): PendingItem {
    const d = judgement.decision
    if (f.runId !== null && f.agentIndex !== null) {
      this.closures.set(f.runId, closeAfter(this.closures.get(f.runId) ?? null, f.agentIndex, d.deferUntilMs))
    }
    const item: PendingItem = {
      seq: f.seq,
      runId: f.runId,
      agentIndex: f.agentIndex,
      type: f.type,
      model: judgement.model,
      rule: d.rule,
      deferUntilMs: d.deferUntilMs,
      atMs: f.nowMs,
      sessionId: f.sessionId,
      nested: f.nested,
      applied,
      attempts: 0,
    }
    this.pending.push(item)
    if (this.pending.length > PENDING_CAP) this.pending = this.pending.slice(-PENDING_CAP)
    return item
  }

  /**
   * 这次派发会继续往下走(放行;或影子模式下照常启动):立即预留,必须与 judgeSpawn 之间没有任何 await。
   * 影子模式下"本来会推迟"的 agent 在现实里照样在跑,按现实计入在途。
   */
  beginSpawn(seq: number, f: SpawnFacts): void {
    const judgement = this.judged.get(seq)
    if (!judgement) return
    this.reserved.set(seq, { model: judgement.model, type: f.type, runId: f.runId, costP80Eq: judgement.cost.p80Eq })
  }

  /** 判断之后没有往下走(启用模式推迟、下游出错):丢掉判断与预留。 */
  forget(seq: number): void {
    this.reserved.delete(seq)
    this.judged.delete(seq)
  }

  /** 派发落定:真的启动了就转为跟踪;被下游拒绝或没启动就撤销预留。 */
  spawnSettled(seq: number, agentId: string | undefined, model: string | undefined, f: SpawnFacts): void {
    this.reserved.delete(seq)
    const judgement = this.judged.get(seq)
    this.judged.delete(seq)
    if (agentId === undefined) return
    const known = this.agents.get(agentId)
    if (known) {
      known.ended = false
      known.lastStepAtMs = f.nowMs
      return
    }
    this.agents.set(agentId, {
      agentId,
      type: f.type,
      model: model ? modelKey(model) : judgement?.model ?? modelKey(f.parentModel),
      runId: f.runId,
      agentIndex: f.agentIndex,
      startedAtMs: f.nowMs,
      lastStepAtMs: f.nowMs,
      steps: 0,
      eq: 0,
      ended: false,
      landed: false,
      predicted: judgement?.cost ?? null,
      seq,
    })
  }

  /** 工作流从 runId 续跑:编号从头数起,前缀闭合与该 run 的推迟项一并解除。 */
  workflowResumed(runId: string): void {
    this.closures.delete(runId)
    this.pending = this.pending.filter(p => p.runId !== runId)
  }

  /** 软着陆拦过这个 agent 的工具调用:它之后的结束是截断的。 */
  markLanded(agentId: string): void {
    const agent = this.agents.get(agentId)
    if (agent) agent.landed = true
  }

  /**
   * 子代理结束:给出复盘结果(预测 vs 实际)。只有正常作答(`answer`)且没被软着陆截断的才进成本历史、才算覆盖率——
   * 撞限被杀、被打断、被截断的只花了一部分,算进去会把先验拉低、导致少拦。没跟踪过的 agent 返回 null。
   */
  agentEnded(agentId: string, nowMs: number, reason = 'answer'): AgentOutcome | null {
    const agent = this.agents.get(agentId)
    if (!agent || agent.ended) return null
    agent.ended = true
    const durationMs = Math.max(0, nowMs - agent.startedAtMs)
    const complete = reason === 'answer' && !agent.landed
    if (complete && agent.eq > 0) {
      this.absorbHistory([{
        agentId,
        type: agent.type,
        model: agent.model,
        runId: agent.runId,
        eq: agent.eq,
        steps: agent.steps,
        durationMs,
        endedAtMs: nowMs,
      }])
    }
    const p = agent.predicted
    return {
      agentId,
      seq: agent.seq,
      type: agent.type,
      model: agent.model,
      runId: agent.runId,
      eq: agent.eq,
      steps: agent.steps,
      durationMs,
      complete,
      predictedMedianEq: p ? Math.round(p.medianEq) : null,
      predictedP80Eq: p ? Math.round(p.p80Eq) : null,
      covered: p && complete && agent.eq > 0 ? agent.eq <= p.p80Eq : null,
    }
  }

  /** 被动发现:某模型首个请求就撞上限额,标成"受限至重置"。 */
  markLimited(model: string, untilMs: number): void {
    this.limited.set(modelKey(model), untilMs)
  }

  landing(nowMs: number): WindowKind[] {
    return landingWindows(this.effectiveWindows(nowMs), nowMs, this.params)
  }

  /**
   * 到期该补派的推迟项(启用模式):真推迟过、重置时刻已过、属于当前对话的主循环派发。
   * 嵌套派发的拒绝理由已经交给那个子代理;/clear 之前的推迟项属于旧对话,新对话没有它的上下文。
   */
  dueRedispatch(nowMs: number, sessionId: string): PendingItem[] {
    return this.pending.filter(p => p.applied && !p.nested && p.sessionId === sessionId &&
      p.deferUntilMs !== null && p.deferUntilMs <= nowMs)
  }

  dropPending(items: readonly PendingItem[]): void {
    const seqs = new Set(items.map(i => i.seq))
    this.pending = this.pending.filter(p => !seqs.has(p.seq))
  }

  /** 当前价格表(界面"各模型的校准价格与置信度")。 */
  priceTable(nowMs: number): { window: WindowKind; model: string; period: string; k: number; source: string; samples: number; spread: number | null }[] {
    const models = this.knownModels()
    const rows: { window: WindowKind; model: string; period: string; k: number; source: string; samples: number; spread: number | null }[] = []
    for (const window of WINDOWS) {
      for (const model of models.length > 0 ? models : ['opus-5-5']) {
        const p = this.price(window, model, nowMs)
        rows.push({ window, model, period: periodOf(nowMs), k: Math.round(p.k * 10_000) / 10_000, source: p.source, samples: p.samples, spread: p.spread === null ? null : Math.round(p.spread * 100) / 100 })
      }
    }
    return rows
  }
}
