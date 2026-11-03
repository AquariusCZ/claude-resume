import assert from 'node:assert/strict'
import { test } from 'node:test'

import { priceOf } from '../airesume-budget-gate/core/calibrate.ts'
import { DEFAULT_PARAMS, type WindowKind } from '../airesume-budget-gate/core/config.ts'
import { estimateCost, rateQuantile, remainingP80, type CompletedAgent } from '../airesume-budget-gate/core/cost.ts'
import { closeAfter, decide, landingWindows, type DecideInput, type Inflight } from '../airesume-budget-gate/core/decide.ts'

const P = DEFAULT_PARAMS
const min = (n: number) => n * 60_000

test('成本:无历史时 p80 ≈ 中位 × e^(0.8416·√(σb²+σe²)),fork 先按 ×2;兄弟越多越贴近兄弟', () => {
  const now = Date.parse('2026-11-02T20:00:00Z')
  const seed = estimateCost([], { type: 'general-purpose', runId: null, fork: false }, now, P)
  assert.equal(seed.basis, 'seed')
  assert.ok(Math.abs(seed.medianEq - 4_580_000) < 1)
  assert.ok(Math.abs(seed.p80Eq / seed.medianEq - Math.exp(0.8416212 * Math.sqrt(0.16 + 0.0625))) < 1e-4)
  assert.ok(Math.abs(estimateCost([], { type: 't', runId: null, fork: true }, now, P).medianEq - 9_160_000) < 1)
  const sib = (i: number, eq: number): CompletedAgent => ({ agentId: `a${i}`, type: 'workflow-subagent', model: 'opus-5-5', runId: 'wf_1', eq, steps: 100, durationMs: min(20), endedAtMs: now - min(i) })
  const one = estimateCost([sib(1, 8_000_000)], { type: 'workflow-subagent', runId: 'wf_1', fork: false }, now, P)
  const four = estimateCost([1, 2, 3, 4].map(i => sib(i, 8_000_000)), { type: 'workflow-subagent', runId: 'wf_1', fork: false }, now, P)
  assert.equal(one.basis, 'siblings')
  assert.ok(one.medianEq > seed.medianEq && four.medianEq > one.medianEq && four.medianEq < 8_000_000)
  assert.ok(four.logSigma < one.logSigma, '兄弟越多,批次偏差越确定')
})

test('速率:历史不足 5 个用种子,够了取分位', () => {
  const now = Date.parse('2026-11-02T20:00:00Z')
  assert.equal(rateQuantile([], now, P), P.rateSeedEqPerMinute)
  const h = [1, 2, 3, 4, 5].map(i => ({ agentId: `r${i}`, type: 't', model: 'm', runId: null, eq: i * 1_000_000, steps: 10, durationMs: min(10), endedAtMs: now }))
  assert.equal(rateQuantile(h, now, P), 400_000)
})

test('在途剩余 p80:随已跑轮数下降,尾部长;花得多的 agent 剩得也多', () => {
  const total = 5_800_000
  const series = [0, 20, 40, 60, 80, 100, 150].map(n => remainingP80(n, n * 30_000, total))
  for (let i = 1; i < series.length; i++) assert.ok(series[i]! <= series[i - 1]! + 1e-6, `第 ${i} 点不应比前一点大`)
  assert.ok(remainingP80(100, 9_000_000, total) > remainingP80(100, 3_000_000, total))
})

const OFFPEAK = Date.parse('2026-10-31T04:51:00Z') // 北京 10-31 12:51 = 10-30(周五)21:51 PDT,非高峰
const at = (base: number, minutes: number) => base + min(minutes)

function input(over: Partial<DecideInput> & { nowMs: number; windows: DecideInput['windows'] }): DecideInput {
  return {
    price: (w: WindowKind, m: string) => priceOf([], w, m, over.nowMs, P),
    candidate: { model: 'opus-5-5', costMedianEq: 4_580_000, costP80Eq: 6_810_000, rateP80EqPerMinute: P.rateSeedEqPerMinute, runId: null, agentIndex: null, isResume: false },
    inflight: [],
    ownInflight: 0,
    mainEqPerMinute: {},
    closure: null,
    scoped: null,
    limitedUntilMs: null,
    params: P,
    ...over,
  }
}

const drafting = (n: number): Inflight[] => Array.from({ length: n }, (_, i) => ({
  agentId: `d${i}`, model: 'opus-5-5', steps: 30, spentEq: 800_000, remainingP80Eq: remainingP80(30, 800_000, 6_810_000),
}))

// 以下场景取自报告 §7(10-31 会话 00bd46fd)的决策点,读数为反推值。断言的是本策略的行为,
// 与报告手算的"推荐"列不完全相同处在注释里说明。

/** 读数固定时依次派发 n 个(每放行一个就计入在途),返回放行了几个。 */
function admitCount(nowMs: number, windows: DecideInput['windows'], n: number): number {
  const inflight: Inflight[] = []
  for (let i = 0; i < n; i++) {
    const d = decide(input({ nowMs, windows, inflight: [...inflight], ownInflight: inflight.length }))
    if (d.verdict !== 'allow') return i
    inflight.push({ agentId: `r${i}`, model: 'opus-5-5', steps: 0, spentEq: 0, remainingP80Eq: 6_810_000 })
  }
  return n
}

test('§7 01:05 调研 ×8:读数 3% / 58%。非高峰全部放行;这一刻其实是美西周五 10:05(高峰),种子价 ×2.3 只放 4 个', () => {
  const windows = (now: number) => [
    { kind: 'five_hour' as const, percentUsed: 3, resetsAtMs: at(now, 235) },
    { kind: 'seven_day' as const, percentUsed: 58, resetsAtMs: Date.parse('2026-11-02T14:00:00Z') },
  ]
  const peak = Date.parse('2026-10-30T17:05:00Z')
  assert.equal(admitCount(peak, windows(peak), 8), 4)
  // 实际 8 个全部完成、没有撞限:高峰先验 2.3 在这一刻偏高(报告也没排除 W2 混入了看不到的消耗)。
  // 在线校准攒到高峰样本后会覆盖先验;影子模式要盯的正是这类"估大"。
  const offpeak = Date.parse('2026-10-30T19:05:00Z')
  assert.equal(admitCount(offpeak, windows(offpeak), 8), 8)
})

test('§7 12:51 审查 ×4:5h 40%、7 个起草在途 → 推迟(本策略比报告手算更保守:在途按 p80 估约 47 点)', () => {
  const windows = [
    { kind: 'five_hour' as const, percentUsed: 40, resetsAtMs: Date.parse('2026-10-31T06:00:00Z') },
    { kind: 'seven_day' as const, percentUsed: 75, resetsAtMs: Date.parse('2026-11-02T14:00:00Z') },
  ]
  const d = decide(input({ nowMs: OFFPEAK, windows, inflight: drafting(7), ownInflight: 7, mainEqPerMinute: { 'opus-5-5': 400_000 } }))
  assert.equal(d.verdict, 'defer')
  assert.equal(d.rule, 'over-budget')
  assert.equal(d.binding, 'five_hour')
  assert.equal(d.deferUntilMs, Date.parse('2026-10-31T06:00:00Z'), '推迟到 5 小时重置(北京 14:00)')
  const five = d.windows.find(w => w.kind === 'five_hour')!
  assert.ok(five.inflight > 30 && five.inflight < 60, `在途 ${five.inflight}`)
  assert.equal(five.reserve, 10, '主会话预留封顶 10 点')
})

test('§7 15:30 修复轮审查 ×4:5h 81%、周 96%、无在途 → 保底放 1 个,其余推迟到 19:00', () => {
  const now = Date.parse('2026-10-31T07:30:00Z')
  const windows = [
    { kind: 'five_hour' as const, percentUsed: 81, resetsAtMs: Date.parse('2026-10-31T11:00:00Z') },
    { kind: 'seven_day' as const, percentUsed: 96, resetsAtMs: Date.parse('2026-11-02T14:00:00Z') },
  ]
  const first = decide(input({ nowMs: now, windows, mainEqPerMinute: { 'opus-5-5': 400_000 } }))
  assert.equal(first.verdict, 'allow')
  assert.equal(first.rule, 'guaranteed-progress')
  const one = [{ agentId: 'x', model: 'opus-5-5', steps: 0, spentEq: 0, remainingP80Eq: 6_810_000 }]
  const second = decide(input({ nowMs: now, windows, inflight: one, ownInflight: 1, mainEqPerMinute: { 'opus-5-5': 400_000 } }))
  assert.equal(second.verdict, 'defer')
  assert.equal(second.deferUntilMs, Date.parse('2026-11-02T14:00:00Z'), '5 小时和周都不够时按更晚的周重置推迟')
})

test('§7 19:00 自动续跑后重派:5h 0%、周 98.7% → 周窗口挡住', () => {
  const now = Date.parse('2026-10-31T11:00:00Z')
  const windows = [
    { kind: 'five_hour' as const, percentUsed: 0, resetsAtMs: Date.parse('2026-10-31T16:00:00Z') },
    { kind: 'seven_day' as const, percentUsed: 98.7, resetsAtMs: Date.parse('2026-11-02T14:00:00Z') },
  ]
  const d = decide(input({ nowMs: now, windows }))
  assert.equal(d.verdict, 'defer')
  assert.equal(d.binding, 'seven_day')
  assert.deepEqual(landingWindows(windows, now, P), [], '98.7 还没到周的软着陆线(≥ 99)')
  const later = [windows[0]!, { ...windows[1]!, percentUsed: 99.2 }]
  assert.deepEqual(landingWindows(later, now, P), ['seven_day'])
})

test('§7 15:06 一致性审查 ×1:5h 67%、周 94%、无在途 → 放行', () => {
  const now = Date.parse('2026-10-31T07:06:00Z')
  const windows = [
    { kind: 'five_hour' as const, percentUsed: 67, resetsAtMs: Date.parse('2026-10-31T11:00:00Z') },
    { kind: 'seven_day' as const, percentUsed: 94, resetsAtMs: Date.parse('2026-11-02T14:00:00Z') },
  ]
  assert.equal(decide(input({ nowMs: now, windows })).rule, 'fits')
})

test('只算到重置为止:离重置 10 分钟时,读数 95% 也放得下一个 agent', () => {
  const now = OFFPEAK
  const windows = [{ kind: 'five_hour' as const, percentUsed: 85, resetsAtMs: at(now, 10) }]
  const d = decide(input({ nowMs: now, windows, ownInflight: 1, inflight: drafting(1) }))
  const five = d.windows[0]!
  assert.ok(five.need < 5, `只计重置前 10 分钟:${five.need}`)
  assert.equal(d.verdict, 'allow')
})

test('没有读数(非订阅、或首个响应前)→ 放行,退化为现状', () => {
  assert.equal(decide(input({ nowMs: OFFPEAK, windows: [] })).rule, 'no-readings')
  const expired = [{ kind: 'five_hour' as const, percentUsed: 100, resetsAtMs: at(OFFPEAK, -1) }]
  assert.equal(decide(input({ nowMs: OFFPEAK, windows: expired })).rule, 'no-readings', '重置时刻已过的读数作废')
})

test('前缀闭合:同一 run 推迟了第 3 个,之后的都推迟;重置后解除;别的 run 不受影响', () => {
  const windows = [{ kind: 'five_hour' as const, percentUsed: 10, resetsAtMs: at(OFFPEAK, 120) }]
  const closure = closeAfter(null, 3, at(OFFPEAK, 120))
  const candidate = (run: string, idx: number) => ({ model: 'opus-5-5', costMedianEq: 4_580_000, costP80Eq: 6_810_000, rateP80EqPerMinute: P.rateSeedEqPerMinute, runId: run, agentIndex: idx, isResume: false })
  assert.equal(decide(input({ nowMs: OFFPEAK, windows, closure, candidate: candidate('wf_a', 5) })).rule, 'prefix-closure')
  assert.equal(decide(input({ nowMs: OFFPEAK, windows, closure, candidate: candidate('wf_a', 2) })).rule, 'fits', '闭合点之前的不受影响')
  assert.equal(decide(input({ nowMs: at(OFFPEAK, 121), windows: [], closure, candidate: candidate('wf_a', 5) })).rule, 'no-readings', '过了重置就解除')
  const widened = closeAfter(closure, 2, at(OFFPEAK, 300))
  assert.deepEqual(widened, { fromIndex: 2, untilMs: at(OFFPEAK, 300) })
})

test('按模型族周额度用尽、被动受限模型 → 推迟到各自重置', () => {
  const windows = [{ kind: 'five_hour' as const, percentUsed: 10, resetsAtMs: at(OFFPEAK, 120) }]
  const scoped = decide(input({ nowMs: OFFPEAK, windows, scoped: { family: 'opus', percentUsed: 99.5, resetsAtMs: at(OFFPEAK, 3000) } }))
  assert.equal(scoped.rule, 'scoped-exhausted')
  assert.equal(scoped.deferUntilMs, at(OFFPEAK, 3000))
  assert.equal(decide(input({ nowMs: OFFPEAK, windows, scoped: { family: 'opus', percentUsed: 80, resetsAtMs: null } })).rule, 'fits')
  assert.equal(decide(input({ nowMs: OFFPEAK, windows, limitedUntilMs: at(OFFPEAK, 60) })).rule, 'model-limited')
})

test('新模型单路:同模型已有在途就推迟,没有在途按悲观价放行', () => {
  const windows = [{ kind: 'five_hour' as const, percentUsed: 10, resetsAtMs: at(OFFPEAK, 200) }]
  const candidate = { model: 'mythos-1', costMedianEq: 4_580_000, costP80Eq: 6_810_000, rateP80EqPerMinute: P.rateSeedEqPerMinute, runId: null, agentIndex: null, isResume: false }
  const price = (w: WindowKind, m: string) => priceOf([], w, m, OFFPEAK, P, ['opus-5-5'])
  const alone = decide(input({ nowMs: OFFPEAK, windows, candidate, price }))
  assert.equal(alone.verdict, 'allow')
  assert.ok(alone.windows[0]!.k > 2.6)
  const busy = decide(input({ nowMs: OFFPEAK, windows, candidate, price, inflight: [{ agentId: 'f', model: 'mythos-1', steps: 3, spentEq: 1, remainingP80Eq: 6_000_000 }], ownInflight: 1 }))
  assert.equal(busy.rule, 'new-model-single-flight')
  assert.equal(busy.deferUntilMs, OFFPEAK + P.recheckMinutes * 60_000, '算不出重置时刻的推迟到点复判,不会永远挂着')
})

test('软着陆阈值:5 小时 ≥ 97、周 ≥ 99;已过重置的不算', () => {
  const now = OFFPEAK
  const windows = [
    { kind: 'five_hour' as const, percentUsed: 96.9, resetsAtMs: at(now, 30) },
    { kind: 'seven_day' as const, percentUsed: 99, resetsAtMs: at(now, 3000) },
  ]
  assert.deepEqual(landingWindows(windows, now, P), ['seven_day'])
  assert.deepEqual(landingWindows([{ kind: 'five_hour', percentUsed: 97, resetsAtMs: at(now, -1) }], now, P), [])
})

test('续派(SendMessage 唤醒、队友回来)一律放行:拒绝只会让它停住', () => {
  const windows = [{ kind: 'five_hour' as const, percentUsed: 99, resetsAtMs: at(OFFPEAK, 60) }]
  const candidate = { model: 'opus-5-5', costMedianEq: 4_580_000, costP80Eq: 6_810_000, rateP80EqPerMinute: P.rateSeedEqPerMinute, runId: null, agentIndex: null, isResume: true }
  assert.equal(decide(input({ nowMs: OFFPEAK, windows, candidate, ownInflight: 3 })).rule, 'resume-exempt')
})

test('离重置不到 10 分钟不软着陆;判断行带读数来源', () => {
  const near = [{ kind: 'five_hour' as const, percentUsed: 99, resetsAtMs: at(OFFPEAK, 8) }]
  assert.deepEqual(landingWindows(near, OFFPEAK, P), [])
  const far = [{ kind: 'five_hour' as const, percentUsed: 99, resetsAtMs: at(OFFPEAK, 40) }]
  assert.deepEqual(landingWindows(far, OFFPEAK, P), ['five_hour'])
  const sourced = [{ kind: 'five_hour' as const, percentUsed: 20, resetsAtMs: at(OFFPEAK, 100), source: 'peer' as const, observedAtMs: OFFPEAK - 5000 }]
  const w = decide(input({ nowMs: OFFPEAK, windows: sourced })).windows[0]!
  assert.equal(w.src, 'peer')
  assert.equal(w.at, OFFPEAK - 5000)
})
