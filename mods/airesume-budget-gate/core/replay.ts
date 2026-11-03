// 离线回放(V1):把 mod 录下的 JSONL 行按时间喂给 BudgetEngine,得到"如果当时启用会怎么判"。
// 与线上适配层调用同一组引擎方法,所以回放结论就是影子模式的结论;测试用录下来的真实事件做 fixture,不凭想象写 mock。

import { DEFAULT_PARAMS, parseConfig, type Params } from './config.ts'
import { BudgetEngine, type AgentOutcome, type SpawnFacts, type SpawnJudgement } from './engine.ts'
import type { PriceSample } from './calibrate.ts'
import { normalizeReadings, type Usage4 } from './meter.ts'

export type LogRow = Record<string, unknown> & { ts: string; ev: string }

export type ReplayResult = {
  judgements: (SpawnJudgement & { atMs: number; agentIndex: number | null; runId: string | null })[]
  outcomes: AgentOutcome[]
  samples: PriceSample[]
  engine: BudgetEngine
}

export function parseJsonl(text: string): LogRow[] {
  const rows: LogRow[] = []
  for (const line of text.split(/\r?\n/)) {
    if (line.trim() === '') continue
    try {
      const row = JSON.parse(line) as LogRow
      if (typeof row.ts === 'string' && typeof row.ev === 'string') rows.push(row)
    } catch {
      // 半行(写到一半被读)跳过
    }
  }
  return rows
}

const str = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined)
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)

export function replay(rows: readonly LogRow[], params: Params = DEFAULT_PARAMS, selfKey = 'replay'): ReplayResult {
  const engine = new BudgetEngine(selfKey, params)
  const judgements: ReplayResult['judgements'] = []
  const outcomes: AgentOutcome[] = []
  const samples: PriceSample[] = []
  const facts = new Map<number, SpawnFacts>()
  const sorted = [...rows].sort((a, b) => Date.parse(a.ts) - Date.parse(b.ts))
  for (const row of sorted) {
    const nowMs = Date.parse(row.ts)
    switch (row.ev) {
      case 'load':
      case 'config': {
        // 线上用了非默认参数时,按日志里记下的参数回放,结论才与当时的影子判断一致。
        if (row.params && typeof row.params === 'object') engine.params = parseConfig({ mode: 'shadow', params: row.params }).params
        break
      }
      case 'rl': {
        const list = Array.isArray(row.rl) ? row.rl : []
        const readings = normalizeReadings(list.map(r => {
          const x = r as Record<string, unknown>
          return { kind: x.k, percentUsed: x.p, resetsAt: x.r ?? undefined }
        }))
        samples.push(...engine.observeReadings(readings, nowMs))
        break
      }
      case 'step': {
        const u = row.u as Usage4 | null
        engine.observeStep(str(row.agent) ?? undefined, str(row.model), u ?? undefined, nowMs)
        break
      }
      case 'spawn': {
        const seq = num(row.seq)
        if (seq === null) break
        if (row.phase === 'pre') {
          const wf = row.wf as { run?: string; idx?: number } | null
          const f: SpawnFacts = {
            seq,
            type: str(row.type) ?? 'unknown',
            requestedModel: str(row.req),
            parentModel: str(row.parent) ?? 'unknown',
            runId: wf?.run ?? null,
            agentIndex: wf?.idx ?? null,
            fork: row.fork === true,
            isResume: typeof row.resume === 'string',
            sessionId: str(row.sid) ?? selfKey,
            nested: typeof row.parentAgent === 'string',
            nowMs,
          }
          facts.set(seq, f)
          // 影子语义:判断照做,现实里照常启动,所以无论判断如何都立即预留(与线上适配层一致)。
          const judgement = engine.judgeSpawn(f)
          engine.beginSpawn(seq, f)
          if (judgement.decision.verdict === 'defer') engine.noteDeferral(f, judgement, false)
          judgements.push({ ...judgement, atMs: nowMs, agentIndex: f.agentIndex, runId: f.runId })
        } else if (row.phase === 'post') {
          const f = facts.get(seq)
          if (f) engine.spawnSettled(seq, row.deny == null ? str(row.agentId) : undefined, str(row.model), f)
        }
        break
      }
      case 'turn_end': {
        const agent = str(row.agent)
        if (agent !== undefined) {
          const outcome = engine.agentEnded(agent, nowMs, str(row.reason) ?? 'answer')
          if (outcome) outcomes.push(outcome)
        }
        break
      }
      case 'tool': {
        if (row.tool === 'Workflow' && typeof row.resumeFrom === 'string') engine.workflowResumed(row.resumeFrom)
        break
      }
      default:
        break
    }
  }
  return { judgements, outcomes, samples, engine }
}
