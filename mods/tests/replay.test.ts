import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'

import { parseJsonl, replay } from '../airesume-budget-gate/core/replay.ts'

// fixture 是 V0 mod 在 2.1.309 下真实录到的日志(临时目录、haiku、-p),不是手写的 mock。
const fixture = (name: string) => readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8')

test('回放:工作流探针的真实日志 → 5 次派发逐个判断,被拒的不跟踪,跟踪到的 agent 计量与 turn_end 对得上', () => {
  const rows = parseJsonl(fixture('v0-workflow-deny-probe.jsonl'))
  const ev = (name: string) => rows.filter(r => r.ev === name)
  // 形状核对:录到的事件种类
  assert.deepEqual([...new Set(rows.map(r => r.ev))].sort(), ['end', 'load', 'measure', 'prompt', 'rl', 'spawn', 'step', 'sub_start', 'sub_stop', 'tool', 'turn_end'])
  const pre = ev('spawn').filter(r => r.phase === 'pre')
  assert.deepEqual(pre.map(r => (r.wf as { idx: number }).idx), [1, 2, 3, 4, 5], 'agent.spawn 按 agentIndex 顺序触发')
  assert.ok(pre.every(r => r.type === 'workflow-subagent' && r.req === 'haiku' && r.plen === 53))
  const denied = ev('spawn').filter(r => r.phase === 'post' && r.deny !== null).map(r => r.seq)
  assert.deepEqual(denied, [2, 4])

  const result = replay(rows)
  assert.equal(result.judgements.length, 5)
  // 录制时 5h 75%、7d 8%,周一 08:52 PST 属高峰;haiku 按族先验,单个需求约 7 点,余量够 → 影子判断全部放行
  for (const j of result.judgements) {
    assert.equal(j.decision.verdict, 'allow', JSON.stringify(j.decision))
    assert.equal(j.model, 'haiku-5-5', '别名 haiku 解析到本机见过的 haiku-5-5')
    const five = j.decision.windows.find(w => w.kind === 'five_hour')!
    assert.equal(five.percentUsed, 75)
    assert.equal(five.kSource, 'family')
  }
  // 第 3 个派发时第 1 个还在途,在途占用应计入
  const third = result.judgements.find(j => j.agentIndex === 3)!
  assert.ok(third.decision.windows.find(w => w.kind === 'five_hour')!.inflight > 0)
  // 3 个真启动的 agent 各自结束,计量 = 日志里 turn_end 的 eq
  assert.equal(result.outcomes.length, 3)
  const ends = new Map(ev('turn_end').filter(r => r.agent !== null).map(r => [r.agent as string, r.eq as number]))
  for (const o of result.outcomes) {
    assert.equal(o.eq, ends.get(o.agentId), `agent ${o.agentId} 的逐步计量之和应等于 turn_end`)
    assert.equal(o.covered, true, '实际远低于种子 p80')
  }
})

test('回放:单次 -p 会话的真实日志 → 无派发,读数与计量照常进入引擎', () => {
  const rows = parseJsonl(fixture('v0-load-haiku-p.jsonl'))
  const result = replay(rows)
  assert.equal(result.judgements.length, 0)
  assert.equal(result.engine.spent['haiku-5-5'], 46401)
  const five = result.engine.windows.get('five_hour')!
  assert.equal(five.percentUsed, 75)
  assert.equal(five.resetsAtMs, Date.parse('2026-11-02T19:00:00.000Z'))
})

test('回放:坏行与半行被跳过', () => {
  const rows = parseJsonl('{"ts":"2026-11-02T16:51:15.628Z","ev":"load"}\n{"ts":\n\nnot json\n{"ev":"x"}\n')
  assert.equal(rows.length, 1)
})

test('回放 = 线上:影子模式真实运行的日志离线重放,逐个派发的判断与当时记下的判断一致', () => {
  const rows = parseJsonl(fixture('v2-shadow-workflow.jsonl'))
  const live = rows.filter(r => r.ev === 'decision').map(r => ({ seq: r.seq, rule: r.rule, verdict: r.verdict }))
  assert.deepEqual(live.map(d => d.rule), ['fits', 'fits', 'over-budget', 'prefix-closure', 'prefix-closure'])
  const replayed = replay(rows).judgements.map(j => ({ seq: j.seq, rule: j.decision.rule, verdict: j.decision.verdict }))
  assert.deepEqual(replayed, live)
})

test('启用模式真实日志:推迟理由写进 spawn post 的 deny,判断行标 applied;软着陆每次拦截都留痕', () => {
  const defer = parseJsonl(fixture('v3-enforce-defer.jsonl'))
  const decisions = defer.filter(r => r.ev === 'decision')
  assert.ok(decisions.length >= 3 && decisions.every(d => d.mode === 'enforce' && d.applied === true))
  const denies = defer.filter(r => r.ev === 'spawn' && r.phase === 'post').map(r => String(r.deny))
  assert.ok(denies.every(d => d.startsWith('[AI Resume 预算闸门]')))
  const landing = parseJsonl(fixture('v3-enforce-landing.jsonl')).filter(r => r.ev === 'landing')
  assert.ok(landing.length >= 1 && landing.every(l => l.applied === true && l.tool === 'Read'))
})
