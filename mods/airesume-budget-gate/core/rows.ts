// 日志行的构造。红线:不记令牌、提示正文和项目私密内容,只记 agent 类型、模型、token 计量、读数和决策。
// 提示与描述只留长度和不可逆哈希(用来认出"同一提示重跑"),工作目录只留哈希(用来和 AI Resume 的项目对上)。

import { decimalsOf, eqOf, type Reading, type Usage4 } from './meter.ts'

/**
 * 自动续跑一类提示的特征(Desktop 的 "I hit my usage limit while you were working, but it has reset now…")。只产出布尔值,不记原文。
 * 只认英文原文:2026-11-02 真实日志里,含「额度/限额」的普通中文提示把早先的宽正则触发了 6 次,全是误报。
 */
export const LIMIT_LIKE = /\b(?:hit|reached) (?:my|your|the) (?:usage|rate) limit\b|\b(?:usage|rate) limit\b[^.\n]{0,80}\bresets?\b|\bhas reset now\b/i

/** 工作目录规整:统一反斜杠、去尾部分隔符、小写;C# 侧按同一规则算项目键。 */
export function normalizeCwd(cwd: string): string {
  return cwd.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase()
}

/** 截短一行机器生成的文字(插件的拒绝理由等),去掉换行。 */
export function clip(text: unknown, max = 160): string | undefined {
  if (typeof text !== 'string' || text === '') return undefined
  const line = text.replace(/\s+/g, ' ').trim()
  return line.length > max ? line.slice(0, max) + '…' : line
}

export function readingsRow(readings: readonly Reading[], src: string) {
  return {
    ev: 'rl',
    src,
    rl: readings.map(r => ({ k: r.kind, p: r.percentUsed, r: r.resetsAt ?? null, d: decimalsOf(r.percentUsed) })),
  }
}

export type StepFacts = {
  agentId?: string
  turnId: string
  index: number
  requestedModel: string
  answeredModel?: string
  usage?: Usage4
  stopReason: string | null
  ms: number
}

export function stepRow(f: StepFacts) {
  return {
    ev: 'step',
    agent: f.agentId ?? null,
    turn: f.turnId,
    idx: f.index,
    req: f.requestedModel,
    model: f.answeredModel ?? null,
    u: f.usage ?? null,
    eq: f.usage ? eqOf(f.usage) : 0,
    stop: f.stopReason,
    ms: f.ms,
  }
}

/** agent.spawn 的输入里可以记的部分(提示只留长度与哈希)。 */
export type SpawnFacts = {
  seq: number
  toolUseId: string
  subagentType: string
  provider: { plugin?: string; tier?: string }
  requestedModel?: string
  parentModel: string
  parentAgentId?: string
  background: boolean
  fork: boolean
  isTeammate: boolean
  workflow?: { runId: string; agentIndex: number }
  resumeAgentId?: string
  isNamed: boolean
  promptLength: number
  promptHash: string
  descriptionHash: string
}

export function spawnPreRow(f: SpawnFacts) {
  return {
    ev: 'spawn',
    phase: 'pre',
    seq: f.seq,
    tuid: f.toolUseId,
    type: f.subagentType,
    prov: `${f.provider.plugin ?? '?'}/${f.provider.tier ?? '?'}`,
    req: f.requestedModel ?? null,
    parent: f.parentModel,
    parentAgent: f.parentAgentId ?? null,
    bg: f.background,
    fork: f.fork,
    mate: f.isTeammate,
    wf: f.workflow ? { run: f.workflow.runId, idx: f.workflow.agentIndex } : null,
    resume: f.resumeAgentId ?? null,
    named: f.isNamed,
    plen: f.promptLength,
    ph: f.promptHash,
    dh: f.descriptionHash,
  }
}

export function spawnPostRow(seq: number, result: unknown, ms: number) {
  const r = (result ?? {}) as Record<string, unknown>
  const deny = clip(r.deny)
  return {
    ev: 'spawn',
    phase: 'post',
    seq,
    agentId: typeof r.agentId === 'string' ? r.agentId : null,
    model: typeof r.model === 'string' ? r.model : null,
    deny: deny ?? null,
    ms,
  }
}

export type PromptFacts = {
  originKind: string
  originPlugin?: string
  isMeta: boolean
  wait: boolean
  hasCommand: boolean
  text: string
  turnRunning: boolean
  contextBlocks: number
}

export function promptRow(f: PromptFacts) {
  return {
    ev: 'prompt',
    origin: f.originKind,
    plugin: f.originPlugin ?? null,
    meta: f.isMeta,
    wait: f.wait,
    cmd: f.hasCommand,
    len: f.text.length,
    limitLike: LIMIT_LIKE.test(f.text),
    running: f.turnRunning,
    ctx: f.contextBlocks,
  }
}
