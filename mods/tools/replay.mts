// 离线回放(V1)命令行:把一个数据目录下的全部 JSONL 日志按会话喂给 BudgetEngine,汇总"如果当时启用会怎么判"。
// 用法:node mods/tools/replay.mts [数据目录](默认 %LOCALAPPDATA%\AI Resume\state\budget-gate)
// 只读:不改任何文件。输出只有计数与比例,不含提示正文。
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

import { parseJsonl, replay, type LogRow } from '../airesume-budget-gate/core/replay.ts'

const dir = process.argv[2] ?? join(process.env.LOCALAPPDATA ?? '', 'AI Resume', 'state', 'budget-gate')
const logs = join(dir, 'logs')
const bySession = new Map<string, LogRow[]>()
for (const name of readdirSync(logs).filter(n => n.endsWith('.jsonl')).sort()) {
  for (const row of parseJsonl(readFileSync(join(logs, name), 'utf8'))) {
    const sid = typeof row.sid === 'string' ? row.sid : name
    bySession.set(sid, [...(bySession.get(sid) ?? []), row])
  }
}

const rules = new Map<string, number>()
let decisions = 0
let covered = 0
let outcomes = 0
let samples = 0
for (const [sid, rows] of bySession) {
  const r = replay(rows, undefined, sid)
  decisions += r.judgements.length
  for (const j of r.judgements) rules.set(j.decision.rule, (rules.get(j.decision.rule) ?? 0) + 1)
  for (const o of r.outcomes) {
    if (o.covered === null) continue
    outcomes += 1
    if (o.covered) covered += 1
  }
  samples += r.samples.length
}
console.log(JSON.stringify({
  sessions: bySession.size,
  decisions,
  rules: Object.fromEntries(rules),
  p80Coverage: outcomes > 0 ? Math.round((covered / outcomes) * 1000) / 1000 : null,
  outcomes,
  priceSamples: samples,
}, null, 1))
