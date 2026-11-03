import assert from 'node:assert/strict'
import { test } from 'node:test'

import { DEFAULT_PARAMS } from '../airesume-budget-gate/core/config.ts'
import { BudgetEngine, type SpawnFacts } from '../airesume-budget-gate/core/engine.ts'

const NOW = Date.parse('2026-11-02T20:00:00Z') // 周一 12:00 PST,非高峰
const RESET = Date.parse('2026-11-02T22:00:00Z')
const facts = (seq: number, over: Partial<SpawnFacts> = {}): SpawnFacts => ({
  seq, type: 'workflow-subagent', requestedModel: undefined, parentModel: 'claude-opus-5-5',
  runId: 'wf_x', agentIndex: seq, fork: false, isResume: false, sessionId: 'sess-1', nested: false, nowMs: NOW, ...over,
})

function engineAt(percent: number): BudgetEngine {
  const e = new BudgetEngine('self', DEFAULT_PARAMS)
  e.observeReadings([
    { kind: 'five_hour', percentUsed: percent, resetsAt: new Date(RESET).toISOString() },
    { kind: 'seven_day', percentUsed: 20, resetsAt: '2026-11-09T14:00:00.000Z' },
  ], NOW)
  return e
}

test('并行批次:8 个 spawn 先后判断、尚未启动,启用语义下只放行放得下的 3 个,其余前缀闭合', () => {
  const e = engineAt(70)
  const verdicts: string[] = []
  for (let seq = 1; seq <= 8; seq++) {
    const f = facts(seq)
    const j = e.judgeSpawn(f)
    verdicts.push(j.decision.rule)
    if (j.decision.verdict === 'allow') e.beginSpawn(seq, f)
    else e.noteDeferral(f, j, true)
  }
  assert.deepEqual(verdicts, ['fits', 'fits', 'fits', 'over-budget', 'prefix-closure', 'prefix-closure', 'prefix-closure', 'prefix-closure'])
  assert.equal(e.ownInflightCount(NOW), 3)
  assert.equal(e.pending.length, 5)
  assert.equal(e.pending[0]!.deferUntilMs, RESET)
})

test('派发落定:启动的转为跟踪,被下游拒绝的撤销预留;结束时给出 p80 覆盖复盘并进历史', () => {
  const e = engineAt(10)
  const a = facts(1)
  const b = facts(2)
  e.judgeSpawn(a)
  e.beginSpawn(1, a)
  e.judgeSpawn(b)
  e.beginSpawn(2, b)
  e.spawnSettled(1, 'agent-a', 'claude-opus-5-5', a)
  e.spawnSettled(2, undefined, undefined, b)
  assert.equal(e.reserved.size, 0)
  assert.equal(e.ownInflightCount(NOW), 1)
  e.observeStep('agent-a', 'claude-opus-5-5', { in: 10, out: 1000, cr: 400_000, cw: 20_000 }, NOW + 60_000)
  const outcome = e.agentEnded('agent-a', NOW + 10 * 60_000)!
  assert.equal(outcome.eq, 10 + 5_000 + 40_000 + 25_000)
  assert.equal(outcome.covered, true)
  assert.equal(e.history.length, 1)
  assert.equal(e.history[0]!.runId, 'wf_x')
  assert.equal(e.agentEnded('agent-a', NOW + 11 * 60_000), null, '同一 agent 只复盘一次')
  assert.equal(e.ownInflightCount(NOW), 0)
})

test('工作流 resumeFromRunId:前缀闭合与该 run 的推迟项一并清除', () => {
  const e = engineAt(90)
  const f = facts(4)
  assert.equal(e.judgeSpawn(f).decision.verdict, 'allow')
  e.beginSpawn(4, f)
  const second = e.judgeSpawn(facts(5))
  assert.equal(second.decision.verdict, 'defer')
  e.noteDeferral(facts(5), second, true)
  assert.equal(e.judgeSpawn(facts(6)).decision.rule, 'prefix-closure')
  assert.ok(e.closures.has('wf_x'))
  e.workflowResumed('wf_x')
  assert.equal(e.closures.has('wf_x'), false)
  assert.equal(e.pending.length, 0)
})

test('到期补派只挑启用模式下真推迟过、且重置时刻已过的项', () => {
  const e = engineAt(90)
  e.beginSpawn(1, facts(1))
  e.judgeSpawn(facts(1))
  e.beginSpawn(1, facts(1))
  const j = e.judgeSpawn(facts(2))
  assert.equal(j.decision.verdict, 'defer')
  e.noteDeferral(facts(2), j, true)
  e.noteDeferral(facts(3, { runId: 'wf_y' }), j, false)
  assert.equal(e.dueRedispatch(RESET - 1, 'sess-1').length, 0)
  const due = e.dueRedispatch(RESET + 1, 'sess-1')
  assert.deepEqual(due.map(d => d.seq), [2])
  e.dropPending(due)
  assert.equal(e.pending.length, 1)
})

test('其它会话:新鲜心跳的读数与在途计入,过期心跳不算;读数取最新观测', () => {
  const e = engineAt(40)
  e.setPeers([
    {
      key: 'peer-1',
      startedAtMs: NOW - 3_600_000,
      readings: [{ kind: 'five_hour', percentUsed: 60, resetsAtMs: RESET, observedAtMs: NOW + 1000 }],
      spent: { 'opus-5-5': 5_000_000 },
      inflight: [{ agentId: 'p1', model: 'opus-5-5', steps: 10, spentEq: 300_000, type: 'general-purpose' }],
      mainEqPerMinute: {},
      beatAtMs: NOW,
    },
    {
      key: 'stale',
      startedAtMs: NOW - 3_600_000,
      readings: [{ kind: 'five_hour', percentUsed: 99, resetsAtMs: RESET, observedAtMs: NOW + 2000 }],
      spent: {},
      inflight: [{ agentId: 'old', model: 'opus-5-5', steps: 1, spentEq: 1, type: 'x' }],
      mainEqPerMinute: {},
      beatAtMs: NOW - 10 * 60_000,
    },
  ])
  const j = e.judgeSpawn(facts(1, { nowMs: NOW + 5000 }))
  const five = j.decision.windows.find(w => w.kind === 'five_hour')!
  assert.equal(five.percentUsed, 60, '同一周期取最大:新鲜心跳的 60 高于本会话的 40;过期心跳的 99 不算')
  assert.ok(five.inflight > 5, '其它会话的在途计入')
  assert.equal(e.ledger()['peer-1']!.models['opus-5-5'], 5_000_000)
  assert.equal(e.ledger()['stale'] !== undefined, true, '账本保留全部会话(增量计算自己处理缺失)')
})

test('被动受限:某模型被标成受限至重置,期间推迟,过后恢复', () => {
  const e = engineAt(10)
  e.markLimited('claude-opus-5-5', NOW + 60_000)
  assert.equal(e.judgeSpawn(facts(1)).decision.rule, 'model-limited')
  assert.equal(e.judgeSpawn(facts(2, { nowMs: NOW + 61_000, runId: null, agentIndex: null })).decision.rule, 'fits')
})

test('快照兜底:本会话与其它会话都没有读数时,用 30 分钟内的 OAuth 快照;本会话一有读数就以本会话为准', () => {
  const e = new BudgetEngine('self', DEFAULT_PARAMS)
  e.snapshotWindows = [{ kind: 'five_hour', percentUsed: 96, resetsAtMs: RESET, observedAtMs: NOW - 10 * 60_000 }]
  const j = e.judgeSpawn(facts(1, { runId: null, agentIndex: null }))
  assert.equal(j.decision.windows.find(w => w.kind === 'five_hour')!.percentUsed, 96)
  e.snapshotWindows = [{ kind: 'five_hour', percentUsed: 96, resetsAtMs: RESET, observedAtMs: NOW - 40 * 60_000 }]
  assert.equal(e.judgeSpawn(facts(2, { runId: null, agentIndex: null })).decision.rule, 'no-readings', '过旧的快照不用')
  e.observeReadings([{ kind: 'five_hour', percentUsed: 12, resetsAt: new Date(RESET).toISOString() }], NOW)
  e.snapshotWindows = [{ kind: 'five_hour', percentUsed: 96, resetsAtMs: RESET, observedAtMs: NOW - 60_000 }]
  assert.equal(e.judgeSpawn(facts(3, { runId: null, agentIndex: null })).decision.windows[0]!.percentUsed, 12)
})

const peer = (over: Partial<import('../airesume-budget-gate/core/engine.ts').PeerView>) => ({
  key: 'p', startedAtMs: NOW - 3_600_000, readings: [], spent: {}, inflight: [], mainEqPerMinute: {}, beatAtMs: NOW, ...over,
})

test('读数合并:空闲会话缓存的旧读数盖不过本会话的实时读数;上一周期的读数作废(审查高 1)', () => {
  const e = engineAt(85)
  e.setPeers([
    peer({ key: 'idle', readings: [{ kind: 'five_hour', percentUsed: 30, resetsAtMs: RESET, observedAtMs: NOW + 60_000 }] }),
    peer({ key: 'old-cycle', readings: [{ kind: 'five_hour', percentUsed: 98, resetsAtMs: NOW - 60_000, observedAtMs: NOW + 90_000 }] }),
  ])
  const five = e.effectiveWindows(NOW + 30_000).find(w => w.kind === 'five_hour')!
  assert.equal(five.percentUsed, 85)
  assert.equal(five.source, 'self')
  assert.deepEqual(e.landing(NOW + 30_000), [], '85 没到收尾线')
  const high = engineAt(98)
  high.setPeers([peer({ key: 'old-cycle', readings: [{ kind: 'five_hour', percentUsed: 98, resetsAtMs: NOW - 60_000, observedAtMs: NOW + 90_000 }] })])
  assert.deepEqual(high.landing(NOW + 30_000), ['five_hour'], '上一周期的心跳不会让本会话的 98 消失')
  const carried = new BudgetEngine('self', DEFAULT_PARAMS)
  carried.snapshotWindows = [{ kind: 'five_hour', percentUsed: 60, resetsAtMs: RESET, observedAtMs: NOW - 60_000, carried: true }]
  assert.equal(carried.effectiveWindows(NOW).length, 0, '快照里的承接值不当新读数用')
})

test('/clear 之后本进程的新心跳(同一进程键)不被当成别的会话:不重复计量与在途(审查高 2)', () => {
  const e = new BudgetEngine('proc-1', DEFAULT_PARAMS, NOW - 3_600_000)
  e.observeStep(undefined, 'claude-opus-5-5', { in: 0, out: 0, cr: 0, cw: 800_000 }, NOW)
  e.setPeers([peer({ key: 'proc-1', spent: { 'opus-5-5': 1_000_000 }, inflight: [{ agentId: 'x', model: 'opus-5-5', steps: 3, spentEq: 1, type: 't' }] })])
  assert.deepEqual(Object.keys(e.ledger()), ['proc-1'])
  assert.equal(e.peers.length, 0)
})

test('算不出重置时刻的推迟到点复判,前缀闭合随之解除,启用模式下到点可补派(审查高 3)', () => {
  const e = new BudgetEngine('self', DEFAULT_PARAMS)
  e.observeReadings([{ kind: 'five_hour', percentUsed: 10 }], NOW) // 窗口缺重置时刻
  e.markLimited('claude-opus-5-5', NOW - 1)
  const f1 = facts(1, { requestedModel: 'mythos-1' })
  e.beginSpawn(1, f1)
  e.judgeSpawn(f1)
  e.beginSpawn(1, f1)
  e.spawnSettled(1, 'a1', 'mythos-1', f1)
  const f2 = facts(2, { requestedModel: 'mythos-1' })
  const j = e.judgeSpawn(f2)
  assert.equal(j.decision.rule, 'new-model-single-flight')
  const recheck = NOW + DEFAULT_PARAMS.recheckMinutes * 60_000
  assert.equal(j.decision.deferUntilMs, recheck)
  e.noteDeferral(f2, j, true)
  assert.equal(e.judgeSpawn(facts(3, { nowMs: recheck - 1 })).decision.rule, 'prefix-closure')
  assert.notEqual(e.judgeSpawn(facts(4, { nowMs: recheck + 1 })).decision.rule, 'prefix-closure', '到点后前缀闭合解除')
  assert.deepEqual(e.dueRedispatch(recheck + 1, 'sess-1').map(p => p.seq), [2])
})

test('续派一律放行;被打断、撞限、软着陆截断的 agent 不进成本历史、不算覆盖率', () => {
  const e = engineAt(99)
  assert.equal(e.judgeSpawn(facts(1, { isResume: true })).decision.rule, 'resume-exempt')
  for (const [id, reason, landed] of [['a', 'error', false], ['b', 'aborted', false], ['c', 'answer', true], ['d', 'answer', false]] as const) {
    const f = facts(10 + id.charCodeAt(0), { runId: null, agentIndex: null })
    e.judgeSpawn(f)
    e.beginSpawn(f.seq, f)
    e.spawnSettled(f.seq, id, 'claude-opus-5-5', f)
    e.observeStep(id, 'claude-opus-5-5', { in: 0, out: 0, cr: 0, cw: 1_000_000 }, NOW + 1000)
    if (landed) e.markLanded(id)
    const o = e.agentEnded(id, NOW + 60_000, reason)!
    assert.equal(o.complete, reason === 'answer' && !landed, id)
    assert.equal(o.covered === null, !(reason === 'answer' && !landed), id)
  }
  assert.deepEqual(e.history.map(h => h.agentId), ['d'])
})

test('两小时没有请求的 agent 不再算在途;补派只提醒本对话主循环里的推迟', () => {
  const e = engineAt(10)
  const f = facts(1, { runId: null, agentIndex: null })
  e.judgeSpawn(f)
  e.spawnSettled(1, 'quiet', 'claude-opus-5-5', f)
  assert.equal(e.ownInflightCount(NOW + 60_000), 1)
  assert.equal(e.ownInflightCount(NOW + 3 * 3_600_000), 0)
  const high = engineAt(97)
  const j = high.judgeSpawn(facts(5, { runId: null, agentIndex: null }))
  high.noteDeferral(facts(5, { runId: null, agentIndex: null }), { ...j, decision: { ...j.decision, verdict: 'defer', deferUntilMs: RESET } }, true)
  high.noteDeferral(facts(6, { runId: null, agentIndex: null, sessionId: 'old-conversation' }), { ...j, decision: { ...j.decision, verdict: 'defer', deferUntilMs: RESET } }, true)
  high.noteDeferral(facts(7, { runId: null, agentIndex: null, nested: true }), { ...j, decision: { ...j.decision, verdict: 'defer', deferUntilMs: RESET } }, true)
  assert.deepEqual(high.dueRedispatch(RESET + 1, 'sess-1').map(p => p.seq), [5])
})
