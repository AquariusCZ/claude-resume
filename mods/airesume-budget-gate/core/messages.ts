// 交给模型读的文字(启用模式):推迟理由、软着陆、重置后补派、续跑简报。不弹窗,只走工具结果与本轮 context。
// 拒绝理由按报告 §4.3 第 6 条:说清余量与需求、何时恢复、主会话自己做更贵;同一任务再被推迟时措辞递进,避免重试死循环。
// 只陈述事实、不写祈使句:2026-11-02 实测,子代理把工具结果里"不要再调用工具、写部分完成"当成提示注入明确拒绝执行
// (它仍然很快收尾了)。所以文字只说"为什么没执行、什么时候恢复、有哪些现成的续跑办法",怎么做交给模型自己。

import type { WindowKind } from './config.ts'
import type { Decision } from './decide.ts'
import type { PendingItem } from './engine.ts'

export const TAG = '[AI Resume 预算闸门]'

const pad = (n: number): string => String(n).padStart(2, '0')

/** 本机时间 MM/DD HH:mm(界面与日志的时间也是本机时间)。 */
export function formatLocal(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms)) return '时间未知'
  const d = new Date(ms)
  return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

const WINDOW_NAME: Readonly<Record<WindowKind, string>> = { five_hour: '5 小时额度', seven_day: '周额度' }

function bindingText(d: Decision): string {
  switch (d.rule) {
    case 'scoped-exhausted':
      return '这个模型的周额度已用尽'
    case 'model-limited':
      return '这个模型刚被限额拒绝过'
    case 'prefix-closure':
      return '同一工作流里更早的 agent 已被推迟(只推迟后缀,续跑时前缀从缓存返回)'
    case 'new-model-single-flight':
      return '这个模型还没校准好,同一时刻只放一个'
    default: {
      const w = d.windows.find(x => x.kind === d.binding)
      if (!w) return '额度不够'
      return `${WINDOW_NAME[w.kind]}已用 ${w.percentUsed}%,扣掉在途与预留后余量约 ${Math.max(0, w.margin).toFixed(1)} 点,这个 agent 预计需要 ${w.need.toFixed(1)} 点`
    }
  }
}

/** 推迟的时刻是窗口重置,还是"到点复判"(新模型单路、算不出重置时刻)。 */
function whenText(d: Decision): string {
  const isReset = d.binding === 'five_hour' || d.binding === 'seven_day' || d.binding === 'scoped' || d.binding === 'model' ||
    d.binding === 'closure'
  return isReset && d.rule !== 'new-model-single-flight' ? `${formatLocal(d.deferUntilMs)} 重置后` : `${formatLocal(d.deferUntilMs)} 前后`
}

/**
 * 推迟一个子代理时的拒绝理由。`attempt` 是同一任务第几次被推迟;`willRemind` 为 false(补派关掉、嵌套派发)时不承诺提醒。
 */
export function deferMessage(d: Decision, attempt: number, willRemind: boolean): string {
  const when = whenText(d)
  const again = attempt >= 2 ? `同一任务已第 ${attempt} 次被推迟,重试结果相同。` : ''
  const remind = willRemind ? `会话仍开着时,AI Resume 会在${when}以插件消息提醒补派。` : `预计在${when}恢复。`
  return `${TAG} 这个子代理没有启动:${bindingText(d)}。${again}${remind}` +
    '在主会话里直接做这件事通常更贵(主会话上下文更大,每轮更贵),也同样受这个额度限制。'
}

export type LandingWindow = { kind: WindowKind; percentUsed: number; resetsAtMs: number | null }

/** 软着陆:在途子代理的工具调用没有执行。`count` 是这个 agent 第几次被拦。 */
export function landingMessage(windows: readonly LandingWindow[], count: number): string {
  const what = windows.map(w => `${WINDOW_NAME[w.kind]}已用 ${w.percentUsed}%(${formatLocal(w.resetsAtMs)} 重置)`).join(',')
  const nth = count >= 2 ? `这是第 ${count} 次。` : ''
  return `${TAG} 这次工具调用没有执行,与文件或工具本身无关:${what},已到收尾线。` +
    `为避免中途撞限被杀,重置前这个子代理的工具调用都不会执行。${nth}`
}

/**
 * 重置后补派(会话空闲时以插件消息提交)。不回显任务描述:描述来自模型或子代理写的派发参数,原样回放到主会话等于开了
 * 一条注入通道;主会话自己的上下文里有当时 Agent 工具返回的推迟理由。
 */
export function redispatchText(items: readonly PendingItem[], resetAtMs: number | null): string {
  const runs = new Map<string, number[]>()
  let singles = 0
  for (const item of items) {
    if (item.runId !== null && item.agentIndex !== null) runs.set(item.runId, [...(runs.get(item.runId) ?? []), item.agentIndex])
    else singles += 1
  }
  const parts: string[] = []
  for (const [runId, indexes] of runs) {
    const sorted = [...indexes].sort((a, b) => a - b)
    parts.push(`工作流 ${runId} 从第 ${sorted[0]} 个 agent 起被推迟(共 ${sorted.length} 个);用 Workflow 的 resumeFromRunId="${runId}" 续跑时,已完成的前缀从缓存返回,只补跑缺的部分`)
  }
  if (singles > 0) parts.push(`单独派发的子代理 ${singles} 个,当时 Agent 工具返回的错误里写着推迟原因`)
  return `${TAG} 额度已在 ${formatLocal(resetAtMs)} 重置。之前为避免撞限推迟了这些工作:\n- ${parts.join('\n- ')}\n` +
    '已经完成的部分不必重派;这些工作若已不需要,这条消息可以忽略。'
}

/** 续跑或工作流回报时附在本轮 context 里的预算简报(不改系统提示,不冲缓存)。只陈述事实。 */
export function briefingText(
  windows: readonly { kind: WindowKind; percentUsed: number; resetsAtMs: number | null }[],
  pending: readonly PendingItem[],
  landing: readonly WindowKind[],
): string {
  const lines = windows.map(w => `${WINDOW_NAME[w.kind]} ${w.percentUsed}%(${formatLocal(w.resetsAtMs)} 重置)`)
  const runs = [...new Set(pending.filter(p => p.runId !== null).map(p => p.runId as string))]
  const facts = ['工作流可用 resumeFromRunId 续跑,已完成的前缀从缓存返回;被打断的 Agent 子代理可用 SendMessage 续']
  if (landing.length > 0) facts.unshift(`${landing.map(w => WINDOW_NAME[w]).join('、')}已到收尾线,在途子代理的工具调用暂不执行`)
  const runText = runs.length > 0 ? `待补派的工作流:${runs.join('、')}。` : ''
  return `${TAG} 当前${lines.join(',')}。${runText}${facts.join(';')}。`
}
