import assert from 'node:assert/strict'
import { test } from 'node:test'

import { briefingText, deferMessage, landingMessage, redispatchText, TAG } from '../airesume-budget-gate/core/messages.ts'
import type { Decision } from '../airesume-budget-gate/core/decide.ts'
import type { PendingItem } from '../airesume-budget-gate/core/engine.ts'

const RESET = Date.parse('2026-11-02T19:00:00Z')
const decision: Decision = {
  verdict: 'defer', rule: 'over-budget', binding: 'five_hour', deferUntilMs: RESET,
  windows: [{ kind: 'five_hour', percentUsed: 83, minutesToReset: 108, k: 1.02, kSource: 'family', need: 38.3, needMedian: 25, inflight: 0, reserve: 0.1, margin: 16.9, fits: false, src: 'self', at: null }],
}

// 工具结果与本轮 context 里的文字都会被模型当成不可信输入:只陈述事实,不写"不要/请/立即"一类祈使句
// (2026-11-02 实测软着陆的祈使句被子代理当成提示注入拒绝执行)。
const IMPERATIVE = /不要|请|立即|必须|务必|先收尾|不必须/

const item = (over: Partial<PendingItem>): PendingItem => ({
  seq: 1, runId: null, agentIndex: null, type: 'general-purpose', model: 'opus-5-5', rule: 'over-budget',
  deferUntilMs: RESET, atMs: 0, sessionId: 's', nested: false, applied: true, attempts: 0, ...over,
})

test('推迟理由:说清余量与需求、何时恢复;递进措辞;补派关掉时不承诺提醒;不含祈使句', () => {
  const first = deferMessage(decision, 1, true)
  assert.ok(first.startsWith(TAG))
  assert.ok(first.includes('83%') && first.includes('16.9') && first.includes('38.3'))
  assert.match(first, /提醒补派/)
  assert.ok(!IMPERATIVE.test(first), first)
  const again = deferMessage(decision, 3, true)
  assert.ok(again.includes('第 3 次'))
  assert.ok(!IMPERATIVE.test(again), again)
  const silent = deferMessage(decision, 1, false)
  assert.doesNotMatch(silent, /提醒/)
  assert.match(silent, /恢复/)
  const single = deferMessage({ ...decision, rule: 'new-model-single-flight', binding: null, deferUntilMs: RESET }, 1, true)
  assert.match(single, /前后/, '到点复判不是"重置"')
})

test('软着陆理由:带读数与重置时刻,说明与工具本身无关;不含祈使句', () => {
  const text = landingMessage([{ kind: 'five_hour', percentUsed: 97, resetsAtMs: RESET }], 2)
  assert.ok(text.includes('97%') && text.includes('与文件或工具本身无关') && text.includes('第 2 次'))
  assert.ok(!IMPERATIVE.test(text), text)
})

test('补派消息:工作流按 run 合并成 resumeFromRunId 说明;单独派发只给个数,不回显任何描述;不含祈使句', () => {
  const items = [
    item({ seq: 3, runId: 'wf_a', agentIndex: 5, type: 'workflow-subagent' }),
    item({ seq: 4, runId: 'wf_a', agentIndex: 6, type: 'workflow-subagent', rule: 'prefix-closure' }),
    item({ seq: 9 }),
  ]
  const text = redispatchText(items, RESET)
  assert.ok(text.includes('resumeFromRunId="wf_a"') && text.includes('第 5 个') && text.includes('共 2 个'))
  assert.match(text, /单独派发的子代理 1 个/)
  assert.ok(!IMPERATIVE.test(text), text)
})

test('续跑简报:读数、待补派的 run、收尾线;只陈述事实', () => {
  const text = briefingText([{ kind: 'seven_day', percentUsed: 99.2, resetsAtMs: RESET }], [item({ runId: 'wf_b', agentIndex: 2 })], ['seven_day'])
  assert.ok(text.includes('周额度 99.2%') && text.includes('wf_b') && text.includes('已到收尾线'))
  assert.ok(!IMPERATIVE.test(text), text)
})
