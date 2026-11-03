// 模型 id 的规整与别名解析。价格按"模型键"(族 + 版本,如 opus-5-5)学习,
// 派发时调用方给的可能是别名(haiku)、完整 id 或干脆没写(继承父会话)。

/** `claude-opus-5-5` / `us.anthropic.claude-opus-5-5-20261001-v1:0` / `claude-opus-5-5[1m]` → `opus-5-5`。 */
export function modelKey(id: string): string {
  let key = id.trim().toLowerCase().replace(/\[[^\]]*\]/g, '')
  const at = key.lastIndexOf('claude-')
  if (at >= 0) key = key.slice(at + 'claude-'.length)
  key = key.replace(/[@:].*$/, '').replace(/-v\d+$/, '').replace(/-\d{8}$/, '')
  return key
}

/** 模型族:键的第一段(opus / sonnet / haiku / fable / …)。 */
export function familyOf(key: string): string {
  const dash = key.indexOf('-')
  return dash < 0 ? key : key.slice(0, dash)
}

const ALIASES = new Set(['opus', 'sonnet', 'haiku', 'fable'])

/**
 * 派发前估计候选 agent 会跑在哪个模型上:
 * 没写或 `inherit` → 父会话模型;别名 → 本机见过的同族最新键(按出现顺序取最后一个),没见过就是 `<族>-?`(新模型);完整 id → 规整。
 * 真正的模型要等 `next()` 返回才知道,这里只是为了在派发前取价。
 */
export function resolveModelKey(requested: string | undefined, parentModel: string, seenKeys: readonly string[]): string {
  const raw = requested === undefined || requested === '' || requested === 'inherit' ? parentModel : requested
  const lowered = raw.trim().toLowerCase()
  if (ALIASES.has(lowered)) {
    // 父会话就是这个族时,别名多半解析到同一个模型(实测:-p 主循环 haiku,工作流 agent 写 haiku → claude-haiku-5-5)。
    const parent = modelKey(parentModel)
    if (familyOf(parent) === lowered) return parent
    const seen = seenKeys.filter(k => familyOf(k) === lowered)
    return seen.length > 0 ? seen[seen.length - 1]! : `${lowered}-?`
  }
  return modelKey(raw)
}
