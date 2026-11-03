// 决策:每个 agent.spawn(含工作流里的每个 agent())逐个判断,报告 §4.3。
//
//   T_w     = 本窗口重置时刻 − 现在
//   需求_w  = k_w(模型, 时段) × min(成本 p80, 速率 p80 × T_w)            只算到本窗口重置为止
//   在途_w  = Σ 本机在途 agent  k_w × min(剩余 p80(已跑轮数), 速率 × T_w)
//   预留_w  = 5 小时:主会话近 15 分钟速率 × T_w,封顶;周:固定点数,重置前若干小时线性降到 0
//   余量_w  = 100 − 读数_w − 在途_w − 预留_w
//   放行 ⇔ 每个有读数的窗口都有 需求_w ≤ 余量_w
//
// 另有:按模型族周额度用尽(来自 AI Resume 快照)或被动发现的受限模型 → 推迟到重置;同一工作流 run 一旦推迟第 i 个,
// 它之后的都推迟(前缀闭合,失败的永远是启动顺序上的后缀);新模型单路放行;本会话没有在途时保底放行一个。
// 只返回判断,不做任何事:影子模式照记不拦,启用模式才由适配层执行。

import type { Params, WindowKind } from './config.ts'
import type { Price } from './calibrate.ts'

/** 一个窗口的读数;`source` / `observedAtMs` 说明它来自哪里、何时观测(判断行据此留痕)。 */
export type WindowState = {
  kind: WindowKind
  percentUsed: number
  resetsAtMs: number | null
  source?: 'self' | 'peer' | 'snapshot'
  observedAtMs?: number
}

export type Inflight = {
  agentId: string
  model: string
  steps: number
  spentEq: number
  remainingP80Eq: number
}

export type Candidate = {
  model: string
  costMedianEq: number
  costP80Eq: number
  rateP80EqPerMinute: number
  runId: string | null
  agentIndex: number | null
  /** 续派(SendMessage 唤醒、队友回来):拒绝只会让它停住、lead 等不到回复,一律放行。 */
  isResume: boolean
}

export type ScopedState = { family: string; percentUsed: number | null; resetsAtMs: number | null }

/** 同一工作流 run 的前缀闭合:从第几个开始推迟、推迟到什么时候。 */
export type Closure = { fromIndex: number; untilMs: number | null }

export type DecideInput = {
  nowMs: number
  windows: readonly WindowState[]
  /** 每个窗口下候选模型与在途各模型的价格:`price(window, model)`。 */
  price: (window: WindowKind, model: string) => Price
  candidate: Candidate
  inflight: readonly Inflight[]
  /** 本会话自己的在途数(保底推进只看本会话)。 */
  ownInflight: number
  /** 主会话(全机非 agent)近 15 分钟的消耗:模型键 → 等价 token / 分钟。 */
  mainEqPerMinute: Readonly<Record<string, number>>
  closure: Closure | null
  /** 候选模型族的周额度(AI Resume 快照);认不出族时由调用方给 null。 */
  scoped: ScopedState | null
  /** 被动发现受限的模型(首个请求就撞上族额度)→ 受限到何时。 */
  limitedUntilMs: number | null
  params: Params
}

export type Rule =
  | 'fits'
  | 'no-readings'
  | 'guaranteed-progress'
  | 'over-budget'
  | 'prefix-closure'
  | 'new-model-single-flight'
  | 'scoped-exhausted'
  | 'model-limited'
  | 'resume-exempt'

export type WindowCheck = {
  kind: WindowKind
  percentUsed: number
  minutesToReset: number | null
  k: number
  kSource: Price['source']
  need: number
  needMedian: number
  inflight: number
  reserve: number
  margin: number
  fits: boolean
  /** 读数来源(self 本会话 / peer 其它会话心跳 / snapshot AI Resume 快照)与观测时刻。 */
  src: string | null
  at: number | null
}

export type Decision = {
  verdict: 'allow' | 'defer'
  rule: Rule
  windows: WindowCheck[]
  /** 推迟到何时:最紧的那个窗口的重置时刻;算不出明确时刻时为现在 + recheckMinutes(到点复判并提醒补派)。 */
  deferUntilMs: number | null
  binding: WindowKind | 'scoped' | 'model' | 'closure' | null
}

const round = (x: number, digits = 2): number => {
  const f = 10 ** digits
  return Math.round(x * f) / f
}

/**
 * 读数 ≥ 100 − δ 的窗口:在途子代理该收尾(软着陆)。重置时刻已过的不算;离重置不到 landingMinMinutes 的也不算——
 * 剩下几分钟大多撑得到重置,逼它带半成品收尾、重置后重跑反而更贵。
 */
export function landingWindows(windows: readonly WindowState[], nowMs: number, params: Params): WindowKind[] {
  return windows
    .filter(w => (w.resetsAtMs === null || w.resetsAtMs - nowMs > params.landingMinMinutes * 60_000) &&
      w.percentUsed >= 100 - params.landingDelta[w.kind])
    .map(w => w.kind)
}

function reserveOf(w: WindowState, minutes: number | null, mainPointsPerMinute: number, params: Params): number {
  if (w.kind === 'five_hour') {
    const horizon = minutes ?? 300
    return Math.min(params.reserveCapFiveHour, mainPointsPerMinute * horizon)
  }
  if (minutes === null || params.weeklyReserveTaperHours <= 0) return params.weeklyReserve
  return params.weeklyReserve * Math.min(1, Math.max(0, minutes / 60 / params.weeklyReserveTaperHours))
}

export function decide(input: DecideInput): Decision {
  const { nowMs, candidate, params } = input
  const recheck = nowMs + params.recheckMinutes * 60_000
  const defer = (rule: Rule, binding: Decision['binding'], untilMs: number | null, windows: WindowCheck[] = []): Decision =>
    ({ verdict: 'defer', rule, windows, deferUntilMs: untilMs ?? recheck, binding })

  if (candidate.isResume) return { verdict: 'allow', rule: 'resume-exempt', windows: [], deferUntilMs: null, binding: null }

  if (input.limitedUntilMs !== null && input.limitedUntilMs > nowMs) {
    return defer('model-limited', 'model', input.limitedUntilMs)
  }
  const scoped = input.scoped
  if (scoped && scoped.percentUsed !== null && (scoped.resetsAtMs === null || scoped.resetsAtMs > nowMs) &&
      scoped.percentUsed >= 100 - params.landingDelta.seven_day) {
    return defer('scoped-exhausted', 'scoped', scoped.resetsAtMs)
  }
  const closure = input.closure
  if (closure && candidate.agentIndex !== null && candidate.agentIndex > closure.fromIndex &&
      (closure.untilMs === null || closure.untilMs > nowMs)) {
    return defer('prefix-closure', 'closure', closure.untilMs)
  }

  const live = input.windows.filter(w => w.resetsAtMs === null || w.resetsAtMs > nowMs)
  const checks: WindowCheck[] = live.map(w => {
    const minutes = w.resetsAtMs === null ? null : (w.resetsAtMs - nowMs) / 60_000
    const cap = (eq: number, rate: number): number => (minutes === null ? eq : Math.min(eq, rate * minutes))
    const price = input.price(w.kind, candidate.model)
    const need = (price.k * cap(candidate.costP80Eq, candidate.rateP80EqPerMinute)) / 1e6
    const needMedian = (price.k * cap(candidate.costMedianEq, candidate.rateP80EqPerMinute)) / 1e6
    let inflight = 0
    for (const a of input.inflight) {
      inflight += (input.price(w.kind, a.model).k * cap(a.remainingP80Eq, candidate.rateP80EqPerMinute)) / 1e6
    }
    let mainPoints = 0
    for (const [model, eqPerMinute] of Object.entries(input.mainEqPerMinute)) {
      mainPoints += (input.price(w.kind, model).k * eqPerMinute) / 1e6
    }
    const reserve = reserveOf(w, minutes, mainPoints, params)
    const margin = 100 - w.percentUsed - inflight - reserve
    return {
      kind: w.kind,
      percentUsed: w.percentUsed,
      minutesToReset: minutes === null ? null : round(minutes, 1),
      k: round(price.k, 4),
      kSource: price.source,
      need: round(need),
      needMedian: round(needMedian),
      inflight: round(inflight),
      reserve: round(reserve),
      margin: round(margin),
      fits: need <= margin,
      src: w.source ?? null,
      at: w.observedAtMs ?? null,
    }
  })
  if (checks.length === 0) return { verdict: 'allow', rule: 'no-readings', windows: [], deferUntilMs: null, binding: null }

  const isNewModel = live.some(w => input.price(w.kind, candidate.model).isNewModel)
  const sameModelInflight = input.inflight.some(a => a.model === candidate.model)
  if (isNewModel && sameModelInflight) return defer('new-model-single-flight', null, null, checks)

  const failing = checks.filter(c => !c.fits)
  if (failing.length === 0) return { verdict: 'allow', rule: 'fits', windows: checks, deferUntilMs: null, binding: null }

  // 保底推进:本会话没有在途,且中位需求在扣除最小预留后放得下,就放行一个(软着陆兜底)。
  if (input.ownInflight === 0 && checks.every(c => c.needMedian <= 100 - c.percentUsed - params.minimumReserve[c.kind])) {
    return { verdict: 'allow', rule: 'guaranteed-progress', windows: checks, deferUntilMs: null, binding: null }
  }
  // 推迟到最晚的那个不够的窗口重置:早于它补派还是会被它挡住。
  let binding = failing[0]!
  let untilMs: number | null = null
  for (const c of failing) {
    const w = live.find(x => x.kind === c.kind)!
    if (w.resetsAtMs !== null && (untilMs === null || w.resetsAtMs > untilMs)) {
      untilMs = w.resetsAtMs
      binding = c
    }
  }
  return defer('over-budget', binding.kind, untilMs, checks)
}

/** 推迟了工作流的第 i 个之后,更新这个 run 的前缀闭合。 */
export function closeAfter(closure: Closure | null, agentIndex: number, untilMs: number | null): Closure {
  if (!closure) return { fromIndex: agentIndex, untilMs }
  const latest = closure.untilMs === null || untilMs === null ? (closure.untilMs ?? untilMs) : Math.max(closure.untilMs, untilMs)
  return { fromIndex: Math.min(closure.fromIndex, agentIndex), untilMs: latest }
}
