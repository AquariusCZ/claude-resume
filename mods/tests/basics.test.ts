import assert from 'node:assert/strict'
import { test } from 'node:test'

import { pacificOffsetHours, periodOf } from '../airesume-budget-gate/core/clock.ts'
import { DEFAULT_PARAMS, normalQuantile, parseConfig } from '../airesume-budget-gate/core/config.ts'
import { Journal } from '../airesume-budget-gate/core/journal.ts'
import { decimalsOf, eqOf, normalizeReadings, readingsSignature, usageOf } from '../airesume-budget-gate/core/meter.ts'
import { familyOf, modelKey, resolveModelKey } from '../airesume-budget-gate/core/model.ts'
import { LIMIT_LIKE, normalizeCwd, promptRow } from '../airesume-budget-gate/core/rows.ts'

test('计量:API 拼写的 usage → 四项,缺项与坏值按 0,等价 token 按口径 A', () => {
  const u = usageOf({ input_tokens: 2, output_tokens: 4, cache_read_input_tokens: 0, cache_creation_input_tokens: 37103 })
  assert.deepEqual(u, { in: 2, out: 4, cr: 0, cw: 37103 })
  // 录下来的真实一行(v0-load-haiku-p.jsonl):eq = 2 + 4×5 + 37103×1.25 = 46400.75 → 46401
  assert.equal(eqOf(u), 46401)
  assert.deepEqual(usageOf({ input_tokens: -1, output_tokens: 'x' }), { in: 0, out: 0, cr: 0, cw: 0 })
  assert.deepEqual(usageOf(null), { in: 0, out: 0, cr: 0, cw: 0 })
})

test('读数:规整、排序、去坏值;签名随百分比或重置时刻变化;小数位', () => {
  const r = normalizeReadings([
    { kind: 'seven_day', percentUsed: 8, resetsAt: '2026-11-09T14:00:00.000Z' },
    { kind: 'five_hour', percentUsed: 75, resetsAt: '2026-11-02T19:00:00.000Z' },
    { kind: '', percentUsed: 1 },
    { kind: 'five_hour', percentUsed: Number.NaN },
  ])
  assert.deepEqual(r.map(x => x.kind), ['five_hour', 'seven_day'])
  const a = readingsSignature(r)
  const b = readingsSignature([{ ...r[0]!, percentUsed: 75.5 }, r[1]!])
  assert.notEqual(a, b)
  assert.equal(decimalsOf(23.5), 1)
  assert.equal(decimalsOf(7), 0)
  assert.equal(normalizeReadings('nope').length, 0)
})

test('日志分段:写满换段,写盘期间新追加的行挪到新段;持续写不出时按硬上限丢行', () => {
  const j = new Journal(40, 200)
  j.append({ a: 'x'.repeat(30) })
  const s1 = j.snapshot()
  j.append({ b: 1 })
  j.written(s1)
  assert.equal(j.part, 0)
  assert.equal(j.isDirty, true)
  const s2 = j.snapshot()
  j.append({ c: 2 })
  j.written(s2)
  assert.equal(j.part, 1, '写满 40 字符后换段')
  assert.equal(j.snapshot().text, '{"c":2}\n')
  const k = new Journal(1_000, 30)
  for (let i = 0; i < 10; i++) k.append({ i })
  assert.ok(k.dropped > 0)
})

test('时段:太平洋时间工作日 05:00–11:00 为高峰,夏令时按美国规则', () => {
  assert.equal(pacificOffsetHours(Date.parse('2026-07-01T12:00:00Z')), -7)
  assert.equal(pacificOffsetHours(Date.parse('2026-12-01T12:00:00Z')), -8)
  // 2026 年夏令时:3 月 8 日 10:00Z 开始,11 月 1 日 09:00Z 结束
  assert.equal(pacificOffsetHours(Date.parse('2026-03-08T09:59:00Z')), -8)
  assert.equal(pacificOffsetHours(Date.parse('2026-03-08T10:00:00Z')), -7)
  assert.equal(pacificOffsetHours(Date.parse('2026-11-01T08:59:00Z')), -7)
  assert.equal(pacificOffsetHours(Date.parse('2026-11-01T09:00:00Z')), -8)
  // 录到的 V0 数据:2026-11-02(周一)16:52Z = 08:52 PST → 高峰
  assert.equal(periodOf(Date.parse('2026-11-02T16:52:31Z')), 'peak')
  assert.equal(periodOf(Date.parse('2026-11-02T20:00:00Z')), 'offpeak')
  assert.equal(periodOf(Date.parse('2026-10-31T17:00:00Z')), 'offpeak', '周六')
  // 北京时间 10-27(周二)20:30 = 05:30 PDT → 高峰(报告 W2 后半段)
  assert.equal(periodOf(Date.parse('2026-10-27T12:30:00Z')), 'peak')
})

test('模型键与别名解析', () => {
  assert.equal(modelKey('claude-opus-5-5'), 'opus-5-5')
  assert.equal(modelKey('claude-opus-5-5[1m]'), 'opus-5-5')
  assert.equal(modelKey('us.anthropic.claude-sonnet-5-5-20261001-v1:0'), 'sonnet-5-5')
  assert.equal(modelKey('claude-haiku-5-5'), 'haiku-5-5')
  assert.equal(familyOf('fable-5-1'), 'fable')
  assert.equal(resolveModelKey(undefined, 'claude-opus-5-5', []), 'opus-5-5')
  assert.equal(resolveModelKey('inherit', 'claude-opus-5-5', []), 'opus-5-5')
  assert.equal(resolveModelKey('haiku', 'claude-opus-5-5', ['opus-5-5', 'haiku-4-5', 'haiku-5-5']), 'haiku-5-5')
  assert.equal(resolveModelKey('fable', 'claude-opus-5-5', ['opus-5-5']), 'fable-?')
  assert.equal(resolveModelKey('claude-fable-5-1', 'claude-opus-5-5', []), 'fable-5-1')
})

test('配置:缺省、越界回落默认、未知模式按回落模式', () => {
  const none = parseConfig(undefined)
  assert.equal(none.mode, 'observe')
  assert.deepEqual(none.params, DEFAULT_PARAMS)
  assert.equal(parseConfig(undefined, 'shadow').mode, 'shadow')
  const odd = parseConfig({ mode: 'turbo', version: 3, params: { quantile: 7, softLanding: 'yes', landingDelta: { five_hour: 4 } } }, 'shadow')
  assert.equal(odd.mode, 'shadow')
  assert.equal(odd.version, 3)
  assert.equal(odd.params.quantile, DEFAULT_PARAMS.quantile)
  assert.equal(odd.params.softLanding, true)
  assert.equal(odd.params.landingDelta.five_hour, 4)
  assert.ok(odd.problems.length >= 3)
  assert.equal(parseConfig({ mode: 'enforce' }).mode, 'enforce')
  assert.equal(parseConfig({ mode: 'off' }).mode, 'off')
})

test('正态分位数', () => {
  assert.ok(Math.abs(normalQuantile(0.8) - 0.8416212) < 1e-6)
  assert.ok(Math.abs(normalQuantile(0.5)) < 1e-9)
  assert.ok(Math.abs(normalQuantile(0.01) + 2.3263479) < 1e-6)
})

test('提示行只留长度与特征,不留正文;自动续跑类提示能认出', () => {
  const row = promptRow({
    originKind: 'sdk', isMeta: false, wait: false, hasCommand: false,
    text: "I hit my usage limit while you were working, but it has reset now. Continue.",
    turnRunning: false, contextBlocks: 0,
  })
  assert.equal(row.limitLike, true)
  assert.equal(JSON.stringify(row).includes('usage limit'), false)
  assert.equal(LIMIT_LIKE.test('请继续修复 GUI'), false)
  assert.equal(LIMIT_LIKE.test('子代理派发前先看额度,限额重置后自动续跑'), false, '中文讨论额度的普通提示不算')
  assert.equal(LIMIT_LIKE.test('Your usage limit resets at 11am, then continue.'), true)
  assert.equal(normalizeCwd('C:/Users/X/Desktop/AI Resume/'), 'c:\\users\\x\\desktop\\ai resume')
})

test('别名优先对上父会话的同族模型', () => {
  assert.equal(resolveModelKey('haiku', 'claude-haiku-5-5', []), 'haiku-5-5')
  assert.equal(resolveModelKey('opus', 'claude-haiku-5-5', ['opus-5-5']), 'opus-5-5')
})
