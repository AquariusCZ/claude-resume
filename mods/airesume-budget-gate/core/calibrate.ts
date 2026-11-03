// 在线校准:把"读数涨了几个点"除以"同期全机等价 token",按 窗口 × 模型 × 时段 学习价格 k(百分点 / 百万等价 token)。
// 报告 §3:观测方程 Δp = Σ k·ΔE + u + ε,u ≥ 0 是看不到的消耗(网页、别的电脑),只会让 k 偏大——偏差方向是保守的。
// ΔE 必须用全机的量:本会话 + 其它会话心跳里的累计计量;读数是账号级的,所以不同会话切出的同一段按读数去重。

import { periodOf, type Period } from './clock.ts'
import type { Params, WindowKind } from './config.ts'
import { familyOf } from './model.ts'

/** 一个价格样本。`key` = 窗口|重置时刻|起点读数|终点读数,跨会话去重用。 */
export type PriceSample = {
  key: string
  window: WindowKind
  model: string
  period: Period
  atMs: number
  k: number
  dp: number
  eq: number
  share: number
}

/** 种子价格(报告 §3.2,Opus 5.5 非高峰,口径 A);上线后被实测覆盖。 */
export const SEED_MODEL = 'opus-5-5'
export const SEED_OFFPEAK: Readonly<Record<WindowKind, number>> = { five_hour: 1.34, seven_day: 0.19 }
/**
 * 同族不同版本、或已知族的相对价格先验(相对 Opus;偏贵一侧取值,保守)。没在这里的族按新模型处理(单路放行)。
 * fable 按 Opus 的 2 倍起步:它是比 Opus 更强的族,而且混跑时很难攒够"以它为主"的样本,按新模型处理会一直单路。
 */
export const FAMILY_RATIO: Readonly<Record<string, number>> = { opus: 1, sonnet: 0.6, haiku: 0.33, fable: 2 }

export type PriceSource = 'measured' | 'seed' | 'family' | 'new-model'
export type Price = { k: number; source: PriceSource; samples: number; spread: number | null; isNewModel: boolean }

const SAMPLE_KEEP_MS = 72 * 3_600_000
const SAMPLES_USED = 9

function median(values: readonly number[]): number {
  const sorted = [...values].sort((a, b) => a - b)
  const mid = Math.floor(sorted.length / 2)
  return sorted.length % 2 === 1 ? sorted[mid]! : (sorted[mid - 1]! + sorted[mid]!) / 2
}

function recent(samples: readonly PriceSample[], nowMs: number, pick: (s: PriceSample) => boolean): PriceSample[] {
  return samples.filter(s => pick(s) && nowMs - s.atMs <= SAMPLE_KEEP_MS).slice(-SAMPLES_USED)
}

/** 某窗口下某模型在某时段的直接实测(5 小时分时段;周窗口不分时段)。 */
function measured(samples: readonly PriceSample[], window: WindowKind, model: string, period: Period, nowMs: number): PriceSample[] {
  return recent(samples, nowMs, s => s.window === window && s.model === model && (window === 'seven_day' || s.period === period))
}

function peakFactor(window: WindowKind, period: Period, params: Params): number {
  return window === 'five_hour' && period === 'peak' ? params.peakMultiplier : 1
}

/** 某模型的直接实测价格:本时段实测的中位数,或另一时段的实测按高峰倍率折算;没有实测为 null。 */
function measuredPrice(samples: readonly PriceSample[], window: WindowKind, model: string, period: Period, nowMs: number, params: Params): Price | null {
  const own = measured(samples, window, model, period, nowMs)
  if (own.length > 0) {
    const ks = own.map(s => s.k)
    const m = median(ks)
    return { k: m, source: 'measured', samples: own.length, spread: m > 0 ? (Math.max(...ks) - Math.min(...ks)) / m : null, isNewModel: false }
  }
  if (window === 'five_hour') {
    const other: Period = period === 'peak' ? 'offpeak' : 'peak'
    const theirs = measured(samples, window, model, other, nowMs)
    if (theirs.length > 0) {
      const m = median(theirs.map(s => s.k))
      const factor = period === 'peak' ? params.peakMultiplier : 1 / params.peakMultiplier
      return { k: m * factor, source: 'measured', samples: theirs.length, spread: null, isNewModel: false }
    }
  }
  return null
}

/** 某模型以它为主的样本总数(任意窗口与时段),新模型规则用它判断何时解除。 */
export function sampleCount(samples: readonly PriceSample[], model: string, nowMs: number): number {
  return samples.filter(s => s.model === model && nowMs - s.atMs <= SAMPLE_KEEP_MS).length
}

/** Opus 的参照价:各版本里实测样本最多的那个,没有实测就是种子。 */
function opusReference(samples: readonly PriceSample[], window: WindowKind, period: Period, nowMs: number, params: Params): number {
  let best: Price | null = null
  for (const model of new Set(samples.filter(s => familyOf(s.model) === 'opus').map(s => s.model))) {
    const p = measuredPrice(samples, window, model, period, nowMs, params)
    if (p && (!best || p.samples > best.samples)) best = p
  }
  return best?.k ?? SEED_OFFPEAK[window] * peakFactor(window, period, params)
}

/** 先验价:种子模型用种子;已知族按 Opus 参照价 × 族比例;其它族是新模型,取已知族里最贵的 × 倍数。 */
function priorPrice(
  samples: readonly PriceSample[], window: WindowKind, model: string, period: Period, nowMs: number,
  params: Params, knownModels: readonly string[],
): Price {
  if (model === SEED_MODEL) {
    return { k: SEED_OFFPEAK[window] * peakFactor(window, period, params), source: 'seed', samples: 0, spread: null, isNewModel: false }
  }
  const reference = opusReference(samples, window, period, nowMs, params)
  const ratio = FAMILY_RATIO[familyOf(model)]
  if (ratio !== undefined) return { k: reference * ratio, source: 'family', samples: 0, spread: null, isNewModel: false }
  let maxKnown = reference
  for (const known of new Set([...knownModels, ...samples.map(s => s.model)])) {
    const knownRatio = FAMILY_RATIO[familyOf(known)]
    if (known === model || knownRatio === undefined) continue
    const p = measuredPrice(samples, window, known, period, nowMs, params)
    maxKnown = Math.max(maxKnown, p ? p.k : reference * knownRatio)
  }
  return { k: maxKnown * params.newModelMultiplier, source: 'new-model', samples: 0, spread: null, isNewModel: true }
}

/** 桶内样本不足 `need` 个时,用后备价补成 `need` 个伪样本再取中位数:一个离群样本主导不了结果(报告 §3.2 的稳健中位数)。 */
function blend(ks: readonly number[], fallbackK: number, need: number): number {
  if (ks.length >= need) return median(ks)
  return median([...ks, ...Array.from({ length: need - ks.length }, () => fallbackK)])
}

/**
 * 取价,按 窗口 × 模型 × 时段 这一桶看样本够不够(≥ newModelSamples):
 * - 够了:直接用桶内实测中位数;
 * - 不够:后备价补足伪样本后取中位数。5 小时窗口的后备价是另一时段的实测 × 高峰倍率(同样按样本数补),再没有才用先验;
 * - 先验依次是种子、同族比例、新模型(已知最贵 × 倍数,单路放行)。
 * 2026-11-02 真实数据:安装后头一小时只有一个会话带 mod,别的会话的消耗看不到,高峰桶的唯一样本偏贵一倍;
 * 按模型(而不是按桶)判断"样本够不够"时,这一个样本就直接成了高峰价。`knownModels` 是本机见过的模型键。
 */
export function priceOf(
  samples: readonly PriceSample[],
  window: WindowKind,
  model: string,
  nowMs: number,
  params: Params,
  knownModels: readonly string[] = [],
): Price {
  const period = periodOf(nowMs)
  const need = params.newModelSamples
  const own = measured(samples, window, model, period, nowMs).map(s => s.k)
  const enough = sampleCount(samples, model, nowMs) >= need
  const prior = priorPrice(samples, window, model, period, nowMs, params, knownModels)
  const isNewModel = prior.isNewModel && !enough
  let fallbackK = prior.k
  let fallbackMeasured = false
  if (window === 'five_hour') {
    const other: Period = period === 'peak' ? 'offpeak' : 'peak'
    const factor = period === 'peak' ? params.peakMultiplier : 1 / params.peakMultiplier
    const otherKs = measured(samples, window, model, other, nowMs).map(s => s.k * factor)
    if (otherKs.length > 0) {
      fallbackK = blend(otherKs, prior.k, need)
      fallbackMeasured = true
    }
  }
  if (own.length === 0) {
    return fallbackMeasured
      ? { k: fallbackK, source: 'measured', samples: 0, spread: null, isNewModel }
      : { ...prior, isNewModel }
  }
  const m = median(own)
  return {
    k: blend(own, fallbackK, need),
    source: 'measured',
    samples: own.length,
    spread: own.length > 1 && m > 0 ? (Math.max(...own) - Math.min(...own)) / m : null,
    isNewModel,
  }
}

/** 全机累计计量:进程键(启动时刻 + 随机串,/clear 换会话也不变)→ 启动时刻与各模型累计等价 token。 */
export type LedgerEntry = { startedAtMs: number; models: Readonly<Record<string, number>> }
export type EqLedger = Record<string, LedgerEntry>

/** 切段状态:某窗口从哪个读数、哪个时刻、哪份全机账本开始累积。 */
export type Segment = {
  window: WindowKind
  startMs: number
  startPercent: number
  resetsAtMs: number | null
  ledger: EqLedger
}

export type SegmentStep = { segment: Segment; sample: PriceSample | null; note: string | null }

/**
 * 两份账本之间各模型的增量。终点缺的进程(心跳被清理)按没动处理;起点没有的进程:段开始之后才启动的按 0 起算,
 * 段开始之前就在跑(心跳当时没读到、半写失败)的基线不明,不计——宁可少算计量(价格偏大、保守),不可多算(价格偏小)。
 */
export function ledgerDelta(start: EqLedger, end: EqLedger, segmentStartMs: number): Record<string, number> {
  const delta: Record<string, number> = {}
  for (const [key, entry] of Object.entries(end)) {
    const before = start[key]
    if (!before && entry.startedAtMs < segmentStartMs) continue
    const baseline = before?.models ?? {}
    for (const [model, eq] of Object.entries(entry.models)) {
      const d = eq - (baseline[model] ?? 0)
      if (d > 0) delta[model] = (delta[model] ?? 0) + d
    }
  }
  return delta
}

const RESET_TOLERANCE_MS = 5 * 60_000
/** 一段里全机等价 token 太少时,读数的变化主要来自看不到的消耗,不产出样本。 */
const MIN_SEGMENT_EQ = 50_000

/**
 * 推进某窗口的切段。在每次观察到该窗口读数时调用(读数变化时最有用:段的两端都落在"刚跳整点"的时刻,取整误差大体抵消)。
 * 重置换代或读数回落 → 重新起段、不出样本;涨满 segmentPoints,或满 segmentMinutes 且至少涨 1 点 → 结段。
 */
export function advanceSegment(
  segment: Segment | null,
  window: WindowKind,
  percentUsed: number,
  resetsAtMs: number | null,
  nowMs: number,
  ledger: EqLedger,
  params: Params,
): SegmentStep {
  const fresh: Segment = { window, startMs: nowMs, startPercent: percentUsed, resetsAtMs, ledger }
  if (segment === null) return { segment: fresh, sample: null, note: null }
  const resetMoved = (segment.resetsAtMs === null) !== (resetsAtMs === null) ||
    (segment.resetsAtMs !== null && resetsAtMs !== null && Math.abs(segment.resetsAtMs - resetsAtMs) > RESET_TOLERANCE_MS)
  if (resetMoved || percentUsed < segment.startPercent) return { segment: fresh, sample: null, note: 'reset' }
  const dp = percentUsed - segment.startPercent
  const minutes = (nowMs - segment.startMs) / 60_000
  const closes = dp >= params.segmentPoints || (minutes >= params.segmentMinutes && dp >= 1)
  if (!closes) return { segment, sample: null, note: null }

  const delta = ledgerDelta(segment.ledger, ledger, segment.startMs)
  const total = Object.values(delta).reduce((a, b) => a + b, 0)
  if (total < MIN_SEGMENT_EQ) return { segment: fresh, sample: null, note: 'unobserved' }
  let dominant = ''
  let dominantEq = 0
  for (const [model, eq] of Object.entries(delta)) {
    if (eq > dominantEq) {
      dominant = model
      dominantEq = eq
    }
  }
  const share = dominantEq / total
  if (share < params.dominantShare) return { segment: fresh, sample: null, note: 'mixed' }
  const midpoint = Math.round((segment.startMs + nowMs) / 2)
  const sample: PriceSample = {
    key: `${window}|${resetsAtMs ?? 'none'}|${segment.startPercent}|${percentUsed}`,
    window,
    model: dominant,
    period: periodOf(midpoint),
    atMs: nowMs,
    k: dp / (total / 1e6),
    dp,
    eq: Math.round(total),
    share: Math.round(share * 1000) / 1000,
  }
  return { segment: fresh, sample, note: null }
}

/** 合并样本(同 key 保留先到的那份),按时间排序,只留最近 `cap` 条。 */
export function mergeSamples(a: readonly PriceSample[], b: readonly PriceSample[], cap = 400): PriceSample[] {
  const byKey = new Map<string, PriceSample>()
  for (const s of [...a, ...b]) if (isPriceSample(s) && !byKey.has(s.key)) byKey.set(s.key, s)
  return [...byKey.values()].sort((x, y) => x.atMs - y.atMs).slice(-cap)
}

export function isPriceSample(v: unknown): v is PriceSample {
  const s = v as Partial<PriceSample> | null
  return typeof s === 'object' && s !== null && typeof s.key === 'string' &&
    (s.window === 'five_hour' || s.window === 'seven_day') && typeof s.model === 'string' &&
    (s.period === 'peak' || s.period === 'offpeak') && typeof s.atMs === 'number' &&
    typeof s.k === 'number' && Number.isFinite(s.k) && s.k > 0
}
