import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  advanceSegment,
  ledgerDelta,
  mergeSamples,
  priceOf,
  SEED_OFFPEAK,
  type PriceSample,
} from '../airesume-budget-gate/core/calibrate.ts'
import { DEFAULT_PARAMS } from '../airesume-budget-gate/core/config.ts'

const P = DEFAULT_PARAMS
const OFFPEAK = Date.parse('2026-11-02T20:00:00Z') // 周一 12:00 PST
const PEAK = Date.parse('2026-11-02T16:00:00Z') // 周一 08:00 PST
const RESET = Date.parse('2026-11-02T23:00:00Z')
const min = (n: number) => n * 60_000
/** 账本条目:进程启动时刻 + 各模型累计。默认在段开始之前就启动。 */
const L = (models: Record<string, number>, startedAtMs = OFFPEAK - min(600)) => ({ startedAtMs, models })

test('切段:单模型占 ≥ 80% 时涨满 3 点结段出样本,k = Δp / 百万等价 token', () => {
  let step = advanceSegment(null, 'five_hour', 40, RESET, OFFPEAK, { s1: L({ 'opus-5-5': 10_000_000 }) }, P)
  assert.equal(step.sample, null)
  step = advanceSegment(step.segment, 'five_hour', 42, RESET, OFFPEAK + min(3), { s1: L({ 'opus-5-5': 11_000_000 }) }, P)
  assert.equal(step.sample, null, '只涨 2 点、不满 10 分钟,不结段')
  step = advanceSegment(step.segment, 'five_hour', 43, RESET, OFFPEAK + min(5), {
    s1: L({ 'opus-5-5': 12_000_000, 'haiku-5-5': 100_000 }),
    s2: L({ 'opus-5-5': 200_000 }, OFFPEAK + min(1)), // 段开始之后才启动的进程按 0 起算
    s3: L({ 'opus-5-5': 90_000_000 }), // 段开始前就在跑、起点账本里却没有它(心跳当时没读到):基线不明,不计
  }, P)
  assert.ok(step.sample)
  const s = step.sample!
  assert.equal(s.model, 'opus-5-5')
  assert.equal(s.dp, 3)
  assert.equal(s.eq, 2_300_000)
  assert.ok(Math.abs(s.k - 3 / 2.3) < 1e-9)
  assert.equal(s.period, 'offpeak')
  assert.equal(s.key, `five_hour|${RESET}|40|43`)
  assert.equal(step.segment.startPercent, 43, '结段后从当前读数重新起段')
})

test('切段:满 10 分钟且至少涨 1 点也结段;混合段、看不到消耗、重置换代都不出样本', () => {
  const start = advanceSegment(null, 'seven_day', 10, RESET, OFFPEAK, { s: L({ 'opus-5-5': 0 }) }, P).segment
  const timed = advanceSegment(start, 'seven_day', 11, RESET, OFFPEAK + min(12), { s: L({ 'opus-5-5': 4_000_000 }) }, P)
  assert.ok(timed.sample)
  assert.equal(timed.sample!.dp, 1)
  const mixed = advanceSegment(start, 'seven_day', 13, RESET, OFFPEAK + min(5), { s: L({ 'opus-5-5': 1_000_000, 'sonnet-5-5': 1_000_000 }) }, P)
  assert.equal(mixed.sample, null)
  assert.equal(mixed.note, 'mixed')
  const unseen = advanceSegment(start, 'seven_day', 13, RESET, OFFPEAK + min(5), { s: L({ 'opus-5-5': 10_000 }) }, P)
  assert.equal(unseen.note, 'unobserved')
  const reset = advanceSegment(start, 'seven_day', 2, RESET + 7 * 86_400_000, OFFPEAK + min(5), { s: L({ 'opus-5-5': 9_000_000 }) }, P)
  assert.equal(reset.sample, null)
  assert.equal(reset.note, 'reset')
})

test('账本增量:心跳被清理的进程按没动处理;段内新启动的按 0 起算;段前就在跑却缺席起点的不计', () => {
  const segmentStart = OFFPEAK
  const d = ledgerDelta(
    { a: L({ m: 100 }), gone: L({ m: 500 }) },
    { a: L({ m: 160 }), b: L({ m: 40 }, OFFPEAK + min(1)), late: L({ m: 9_999 }) },
    segmentStart)
  assert.deepEqual(d, { m: 100 })
})

test('/clear 之后同一进程的账本键不变,增量只算真花的(审查:旧键换新键会把累计当增量,价格低估 14 倍)', () => {
  const start = advanceSegment(null, 'five_hour', 50, RESET, OFFPEAK, { proc: L({ 'opus-5-5': 40_000_000 }) }, P).segment
  const after = advanceSegment(start, 'five_hour', 53, RESET, OFFPEAK + min(6), { proc: L({ 'opus-5-5': 43_000_000 }) }, P)
  assert.ok(after.sample)
  assert.ok(Math.abs(after.sample!.k - 1) < 1e-9, `3 点 / 3M = 1.0,实际 ${after.sample!.k}`)
})

function sample(over: Partial<PriceSample>): PriceSample {
  return { key: Math.random().toString(), window: 'five_hour', model: 'opus-5-5', period: 'offpeak', atMs: OFFPEAK - min(30), k: 1, dp: 3, eq: 3_000_000, share: 1, ...over }
}

test('取价:没有样本用种子;高峰 ×2.3;周窗口不分时段', () => {
  const off = priceOf([], 'five_hour', 'opus-5-5', OFFPEAK, P)
  assert.equal(off.source, 'seed')
  assert.equal(off.k, SEED_OFFPEAK.five_hour)
  const peak = priceOf([], 'five_hour', 'opus-5-5', PEAK, P)
  assert.ok(Math.abs(peak.k - SEED_OFFPEAK.five_hour * 2.3) < 1e-9)
  assert.equal(priceOf([], 'seven_day', 'opus-5-5', PEAK, P).k, SEED_OFFPEAK.seven_day)
})

test('取价:桶内样本不足 3 段时用后备价补伪样本取中位数,够了只用实测中位数;另一时段的实测按倍率折算', () => {
  const two = [sample({ k: 0.9 }), sample({ k: 1.1 })]
  assert.equal(priceOf(two, 'five_hour', 'opus-5-5', OFFPEAK, P).k, 1.1, '中位数(0.9, 1.1, 种子 1.34)')
  const four = [sample({ k: 0.9 }), sample({ k: 1.0 }), sample({ k: 1.1 }), sample({ k: 5 })]
  const p = priceOf(four, 'five_hour', 'opus-5-5', OFFPEAK, P)
  assert.equal(p.source, 'measured')
  assert.equal(p.k, 1.05)
  assert.equal(p.samples, 4)
  const atPeak = priceOf(four, 'five_hour', 'opus-5-5', PEAK, P)
  assert.ok(Math.abs(atPeak.k - 1.05 * 2.3) < 1e-9, '高峰没有样本时用非高峰实测 × 倍率')
  const stale = four.map(s => ({ ...s, atMs: OFFPEAK - 80 * 3_600_000 }))
  assert.equal(priceOf(stale, 'five_hour', 'opus-5-5', OFFPEAK, P).source, 'seed', '超过 72 小时的样本不用')
})

test('取价:高峰桶只有一个被污染的样本时,主导不了高峰价(2026-11-02 真实数据的教训)', () => {
  const offpeak = [0.9, 1.0, 1.1, 1.2].map(k => sample({ k, period: 'offpeak' }))
  const contaminated = sample({ k: 5.54, period: 'peak', atMs: PEAK - min(10) })
  const p = priceOf([...offpeak, contaminated], 'five_hour', 'opus-5-5', PEAK, P)
  assert.ok(Math.abs(p.k - 1.05 * 2.3) < 1e-9, `应取非高峰实测 × 倍率,而不是 5.54:${p.k}`)
  assert.equal(p.samples, 1)
  const three = [contaminated, sample({ k: 2.4, period: 'peak' }), sample({ k: 2.6, period: 'peak' })]
  assert.equal(priceOf([...offpeak, ...three], 'five_hour', 'opus-5-5', PEAK, P).k, 2.6, '桶内够 3 个就只看实测中位数')
})

test('取价:已知族按 Opus 参照 × 比例;没见过的族是新模型(已知最贵 × 2,单路放行)', () => {
  const haiku = priceOf([], 'five_hour', 'haiku-5-5', OFFPEAK, P)
  assert.equal(haiku.source, 'family')
  assert.ok(Math.abs(haiku.k - SEED_OFFPEAK.five_hour * 0.33) < 1e-9)
  // fable 是已知族(比 Opus 贵,先验 ×2),不按新模型单路(审查:混跑时攒不够样本会一直单路、前缀闭合永不解除)
  const fable = priceOf([], 'five_hour', 'fable-5-1', OFFPEAK, P, ['opus-5-5', 'haiku-5-5'])
  assert.equal(fable.source, 'family')
  assert.equal(fable.isNewModel, false)
  assert.ok(Math.abs(fable.k - SEED_OFFPEAK.five_hour * 2) < 1e-9)
  // 真没见过的族:已知最贵 × 2,单路放行,攒够 3 段解除
  const unknown = priceOf([], 'five_hour', 'mythos-1', OFFPEAK, P, ['opus-5-5', 'haiku-5-5'])
  assert.equal(unknown.source, 'new-model')
  assert.equal(unknown.isNewModel, true)
  assert.ok(Math.abs(unknown.k - SEED_OFFPEAK.five_hour * 2) < 1e-9)
  const learned = [1, 2, 3].map(i => sample({ model: 'mythos-1', k: 2 + i / 10 }))
  const after = priceOf(learned, 'five_hour', 'mythos-1', OFFPEAK, P)
  assert.equal(after.isNewModel, false, '攒够 3 段解除单路')
  assert.equal(after.k, 2.2)
})

test('合并样本:同 key 去重(跨会话切出的同一段),按时间排序并封顶', () => {
  const a = sample({ key: 'k1', atMs: 3 })
  const b = sample({ key: 'k1', atMs: 9, k: 99 })
  const c = sample({ key: 'k2', atMs: 1 })
  const merged = mergeSamples([a], [b, c, { bogus: true } as unknown as PriceSample])
  assert.deepEqual(merged.map(s => s.key), ['k2', 'k1'])
  assert.equal(merged[1]!.k, 1)
  assert.equal(mergeSamples([], Array.from({ length: 10 }, (_, i) => sample({ key: `x${i}`, atMs: i })), 4).length, 4)
})
