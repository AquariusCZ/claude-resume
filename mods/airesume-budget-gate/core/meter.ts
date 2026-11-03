// 计量与读数:把引擎给的 token 用量折成固定权重的"等价 token",把额度读数规整成可比较的形状。
// 纯函数,不碰 `$`;Node(--experimental-strip-types)与 mod 环境都能直接加载。

/** 一次请求的四项 token 用量(引擎不区分 5 分钟与 1 小时缓存写入)。 */
export type Usage4 = { in: number; out: number; cr: number; cw: number }

/**
 * 等价 token 的固定权重(报告 §2「口径 A」,缓存写入统一按 5 分钟价)。
 * 只是计量单位:每个单位值多少额度百分点由在线校准按窗口 × 模型 × 时段去学,权重写死不影响校准。
 */
export const EQ_WEIGHTS: Readonly<Usage4> = { in: 1, out: 5, cr: 0.1, cw: 1.25 }

export const ZERO_USAGE: Readonly<Usage4> = { in: 0, out: 0, cr: 0, cw: 0 }

const count = (value: unknown): number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0

/** 引擎的 ModelUsage(API 拼写)→ Usage4;缺项、负数、非数一律按 0。 */
export function usageOf(raw: unknown): Usage4 {
  const u = (raw ?? {}) as Record<string, unknown>
  return {
    in: count(u.input_tokens),
    out: count(u.output_tokens),
    cr: count(u.cache_read_input_tokens),
    cw: count(u.cache_creation_input_tokens),
  }
}

export function addUsage(a: Readonly<Usage4>, b: Readonly<Usage4>): Usage4 {
  return { in: a.in + b.in, out: a.out + b.out, cr: a.cr + b.cr, cw: a.cw + b.cw }
}

/** 等价 token(四舍五入到整数)。 */
export function eqOf(u: Readonly<Usage4>, w: Readonly<Usage4> = EQ_WEIGHTS): number {
  return Math.round(u.in * w.in + u.out * w.out + u.cr * w.cr + u.cw * w.cw)
}

/** 一个额度窗口的读数:`kind` 原样保留(five_hour / seven_day / spend_limit / 将来的新窗口)。 */
export type Reading = { kind: string; percentUsed: number; resetsAt?: string }

/** 引擎的 SessionRateLimit[] → 按 kind 排序、去掉坏值的 Reading[]。 */
export function normalizeReadings(raw: unknown): Reading[] {
  if (!Array.isArray(raw)) return []
  const out: Reading[] = []
  for (const item of raw) {
    const r = (item ?? {}) as Record<string, unknown>
    if (typeof r.kind !== 'string' || r.kind === '') continue
    if (typeof r.percentUsed !== 'number' || !Number.isFinite(r.percentUsed)) continue
    const reading: Reading = { kind: r.kind, percentUsed: r.percentUsed }
    if (typeof r.resetsAt === 'string' && r.resetsAt !== '') reading.resetsAt = r.resetsAt
    out.push(reading)
  }
  return out.sort((a, b) => (a.kind < b.kind ? -1 : a.kind > b.kind ? 1 : 0))
}

/** 去重用的签名:百分比与重置时刻都没变就不再记一行。 */
export function readingsSignature(readings: readonly Reading[]): string {
  return readings.map(r => `${r.kind}=${r.percentUsed}@${r.resetsAt ?? ''}`).join('|')
}

/** 一个百分比读数有几位小数(23.5 → 1,7 → 0);用来核实服务端到底给不给小数。 */
export function decimalsOf(value: number): number {
  if (!Number.isFinite(value)) return 0
  const text = String(value)
  const dot = text.indexOf('.')
  return dot < 0 ? 0 : text.length - dot - 1
}
