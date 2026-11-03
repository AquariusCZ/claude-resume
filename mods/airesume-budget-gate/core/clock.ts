// 时段:美西工作日高峰(太平洋时间周一至周五 05:00–11:00)里 5 小时额度消耗约为平时的 2.1–2.6 倍(报告 §2.1),
// 所以价格按"高峰 / 非高峰"分桶学习。夏令时按美国规则手算,不依赖 Intl(mod 环境未必带完整时区数据)。

const HOUR = 3_600_000

/** 某年某月第 n 个星期日的 UTC 零点(month 从 0 起)。 */
function nthSundayUtc(year: number, month: number, n: number): number {
  const first = Date.UTC(year, month, 1)
  const weekday = new Date(first).getUTCDay()
  const offsetDays = (7 - weekday) % 7 + (n - 1) * 7
  return first + offsetDays * 24 * HOUR
}

/** 太平洋时间相对 UTC 的小时数:夏令时 -7,否则 -8。 */
export function pacificOffsetHours(utcMs: number): number {
  const year = new Date(utcMs).getUTCFullYear()
  // 3 月第二个星期日 02:00 PST = 10:00 UTC 开始;11 月第一个星期日 02:00 PDT = 09:00 UTC 结束。
  const start = nthSundayUtc(year, 2, 2) + 10 * HOUR
  const end = nthSundayUtc(year, 10, 1) + 9 * HOUR
  return utcMs >= start && utcMs < end ? -7 : -8
}

export type Period = 'peak' | 'offpeak'

export function periodOf(utcMs: number): Period {
  const local = new Date(utcMs + pacificOffsetHours(utcMs) * HOUR)
  const weekday = local.getUTCDay()
  const hour = local.getUTCHours()
  return weekday >= 1 && weekday <= 5 && hour >= 5 && hour < 11 ? 'peak' : 'offpeak'
}

export function parseIsoMs(text: string | null | undefined): number | null {
  if (typeof text !== 'string' || text === '') return null
  const ms = Date.parse(text)
  return Number.isFinite(ms) ? ms : null
}
