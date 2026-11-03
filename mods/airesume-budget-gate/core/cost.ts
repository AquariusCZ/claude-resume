// 任务量:单个子代理要花多少等价 token(与价格无关,价格由校准管)。
// 报告 §2.3–2.5 / §4.3:同一工作流 run 里已完成的兄弟优先,其次同类型 agent 的历史,再其次全局种子;
// 对数尺度上批次共同偏差 σ_b、个体偏差 σ_e,完成一个兄弟就做一次贝叶斯更新。
// 在途 agent 的剩余成本按"已跑轮数 → 剩余"表的 p80 估,不能按"已经花了一半"估(尾部很长)。

import { normalQuantile, type Params } from './config.ts'

/** 一个跑完的子代理(进历史,跨会话共享)。 */
export type CompletedAgent = {
  agentId: string
  type: string
  model: string
  runId: string | null
  eq: number
  steps: number
  durationMs: number
  endedAtMs: number
}

export type CostEstimate = {
  medianEq: number
  p80Eq: number
  /** 先验来自哪一层:兄弟(含几个)、同类型历史、全局种子。 */
  basis: 'siblings' | 'type' | 'seed'
  siblings: number
  typeSamples: number
  /** 预测分布在对数尺度上的均值与标准差(复盘覆盖率时用)。 */
  logMean: number
  logSigma: number
}

const HISTORY_KEEP_MS = 14 * 86_400_000
const TYPE_SAMPLES_USED = 30

function mean(values: readonly number[]): number {
  return values.reduce((a, b) => a + b, 0) / values.length
}

/**
 * 候选子代理的成本估计。`fork` 继承父会话上下文,先按种子 ×2 估(假设,报告 §4.3 第 3 条说要另算,待影子数据校正)。
 */
export function estimateCost(
  history: readonly CompletedAgent[],
  candidate: { type: string; runId: string | null; fork: boolean },
  nowMs: number,
  params: Params,
): CostEstimate {
  const live = history.filter(h => nowMs - h.endedAtMs <= HISTORY_KEEP_MS && h.eq > 0)
  const sameType = live.filter(h => h.type === candidate.type).slice(-TYPE_SAMPLES_USED)
  const seedLog = Math.log(params.costSeedMedianEq * (candidate.fork ? 2 : 1))
  // 同类型历史:样本少时向种子收缩(每个样本按一个"伪观测"算,种子相当于 3 个)。
  const typeLogs = sameType.map(h => Math.log(h.eq))
  const priorLog = typeLogs.length > 0
    ? (seedLog * 3 + typeLogs.reduce((a, b) => a + b, 0)) / (3 + typeLogs.length)
    : seedLog
  const siblings = candidate.runId === null ? [] : live.filter(h => h.runId === candidate.runId)
  const sb2 = params.sigmaBatch * params.sigmaBatch
  const se2 = params.sigmaAgent * params.sigmaAgent
  let logMean = priorLog
  let postVar = sb2
  if (siblings.length > 0) {
    const n = siblings.length
    const ybar = mean(siblings.map(h => Math.log(h.eq)))
    postVar = 1 / (1 / sb2 + n / se2)
    logMean = postVar * (priorLog / sb2 + (n * ybar) / se2)
  }
  const logSigma = Math.sqrt(postVar + se2)
  const z = normalQuantile(params.quantile)
  return {
    medianEq: Math.exp(logMean),
    p80Eq: Math.exp(logMean + z * logSigma),
    basis: siblings.length > 0 ? 'siblings' : sameType.length > 0 ? 'type' : 'seed',
    siblings: siblings.length,
    typeSamples: sameType.length,
    logMean,
    logSigma,
  }
}

/** 子代理消耗速率(等价 token / 分钟)的高分位:历史够 5 个用历史,否则用种子。 */
export function rateQuantile(history: readonly CompletedAgent[], nowMs: number, params: Params): number {
  const rates = history
    .filter(h => nowMs - h.endedAtMs <= HISTORY_KEEP_MS && h.durationMs >= 30_000 && h.eq > 0)
    .slice(-TYPE_SAMPLES_USED)
    .map(h => h.eq / (h.durationMs / 60_000))
    .sort((a, b) => a - b)
  if (rates.length < 5) return params.rateSeedEqPerMinute
  const at = Math.min(rates.length - 1, Math.max(0, Math.ceil(params.quantile * rates.length) - 1))
  return rates[at]!
}

/**
 * 报告 §2.5 的在途剩余表折算成两条曲线(由中位与 p90 在对数尺度插值出 p80):
 * - `share`:剩余 p80 占"整只 agent 成本 p80"的比例,按已跑轮数;
 * - `ratio`:最终 / 已花 的 p80,按已跑轮数(≥ 20 轮后更贴合这只 agent 自己)。
 * 100 轮以后的点是外推假设,影子数据会校正。
 */
const REMAINING_SHARE: ReadonlyArray<readonly [number, number]> = [
  [0, 1], [20, 0.88], [40, 0.75], [60, 0.63], [80, 0.48], [100, 0.36], [150, 0.21], [200, 0.14], [300, 0.1],
]
const FINAL_RATIO: ReadonlyArray<readonly [number, number]> = [
  [20, 9.92], [40, 4.65], [60, 2.81], [80, 1.93], [100, 1.67], [150, 1.4], [200, 1.25], [300, 1.15],
]

function interpolate(table: ReadonlyArray<readonly [number, number]>, x: number): number {
  if (x <= table[0]![0]) return table[0]![1]
  for (let i = 1; i < table.length; i++) {
    const [x1, y1] = table[i]!
    const [x0, y0] = table[i - 1]!
    if (x <= x1) return y0 + ((y1 - y0) * (x - x0)) / (x1 - x0)
  }
  return table[table.length - 1]![1]
}

/** 在途子代理剩余成本的 p80(等价 token):两条曲线取较大者,保守。 */
export function remainingP80(steps: number, spentEq: number, totalP80Eq: number): number {
  const byShare = totalP80Eq * interpolate(REMAINING_SHARE, steps)
  if (steps < 20) return Math.max(totalP80Eq - spentEq, byShare)
  const byRatio = spentEq * (interpolate(FINAL_RATIO, steps) - 1)
  return Math.max(byRatio, byShare)
}
