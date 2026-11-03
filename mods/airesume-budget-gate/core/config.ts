// 模式与参数。控制面(AI Resume)写 config.json,mod 读;读不懂的字段一律回落默认值,模式读不懂按 observe(只记录)。
//
// 模式:
//   off      不记录、不判断(心跳仍写,方便控制面看到"已关闭")
//   observe  只记录读数与计量(V0)
//   shadow   照常判断但一律放行,只记"如果启用会怎么做"(V2,install 后默认)
//   enforce  按判断推迟子代理、软着陆、重置后补派(V3,只能由用户在界面上打开)

export type Mode = 'off' | 'observe' | 'shadow' | 'enforce'
export const MODES: readonly Mode[] = ['off', 'observe', 'shadow', 'enforce']

export type WindowKind = 'five_hour' | 'seven_day'
export const WINDOWS: readonly WindowKind[] = ['five_hour', 'seven_day']

export type Params = {
  /** 成本、速率、在途剩余都取这个分位(报告 §4.5:p80)。 */
  quantile: number
  /** 软着陆阈值:读数 ≥ 100 − δ 时在途子代理收尾(5 小时 3 点,周 1 点)。 */
  landingDelta: Record<WindowKind, number>
  /** 5 小时预留:主会话近 15 分钟速率 × 剩余时长,上限这么多点。 */
  reserveCapFiveHour: number
  /** 周预留点数,周重置前这么多小时内线性降到 0。 */
  weeklyReserve: number
  weeklyReserveTaperHours: number
  /** 保底推进时至少留的余量(点)。 */
  minimumReserve: Record<WindowKind, number>
  /** 新模型:已知最贵价格 × 这个倍数,单路放行,攒够这么多段以它为主的样本后解除。 */
  newModelMultiplier: number
  newModelSamples: number
  /** 高峰 / 非高峰的价格倍率先验(只作用于 5 小时窗口,周窗口不分时段)。 */
  peakMultiplier: number
  /** 单个 agent 成本的种子中位数(等价 token)与对数尺度上的批次偏差、个体偏差(报告 §2.2–2.4、§4.2)。 */
  costSeedMedianEq: number
  sigmaBatch: number
  sigmaAgent: number
  /** agent 消耗速率 p80 的种子(等价 token / 分钟)。 */
  rateSeedEqPerMinute: number
  /** 离重置不到这么多分钟就不软着陆(撑到重置比带半成品收尾、重置后重跑划算)。 */
  landingMinMinutes: number
  /** 推迟时算不出明确时刻(新模型单路、窗口缺重置时刻)时,过这么多分钟复判并提醒补派。 */
  recheckMinutes: number
  /** 校准切段:读数涨满这么多点,或满这么多分钟且至少涨 1 点;某模型占比达到这个比例才算单模型样本。 */
  segmentPoints: number
  segmentMinutes: number
  dominantShare: number
  /** enforce 下的子开关。 */
  softLanding: boolean
  redispatch: boolean
  briefing: boolean
}

export const DEFAULT_PARAMS: Readonly<Params> = {
  quantile: 0.8,
  landingDelta: { five_hour: 3, seven_day: 1 },
  reserveCapFiveHour: 10,
  weeklyReserve: 2,
  weeklyReserveTaperHours: 12,
  minimumReserve: { five_hour: 1, seven_day: 0.5 },
  newModelMultiplier: 2,
  newModelSamples: 3,
  peakMultiplier: 2.3,
  costSeedMedianEq: 4_580_000,
  sigmaBatch: 0.4,
  sigmaAgent: 0.25,
  rateSeedEqPerMinute: 350_000,
  landingMinMinutes: 10,
  recheckMinutes: 30,
  segmentPoints: 3,
  segmentMinutes: 10,
  dominantShare: 0.8,
  softLanding: true,
  redispatch: true,
  briefing: true,
}

export type Config = { mode: Mode; version: number | null; params: Params; problems: string[] }

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

function pickNumber(source: Record<string, unknown>, key: string, fallback: number, min: number, max: number, problems: string[]): number {
  const v = source[key]
  if (v === undefined) return fallback
  if (typeof v !== 'number' || !Number.isFinite(v) || v < min || v > max) {
    problems.push(`${key} 超出范围,用默认值 ${fallback}`)
    return fallback
  }
  return v
}

function pickBool(source: Record<string, unknown>, key: string, fallback: boolean, problems: string[]): boolean {
  const v = source[key]
  if (v === undefined) return fallback
  if (typeof v !== 'boolean') {
    problems.push(`${key} 不是布尔值,用默认值 ${fallback}`)
    return fallback
  }
  return v
}

/** 解析控制面的 config.json;`fallbackMode` 是文件不存在或模式读不懂时的模式。 */
export function parseConfig(raw: unknown, fallbackMode: Mode = 'observe'): Config {
  const problems: string[] = []
  const root = isRecord(raw) ? raw : {}
  if (raw !== undefined && !isRecord(raw)) problems.push('config.json 不是对象')
  const modeRaw = root.mode
  const mode: Mode = typeof modeRaw === 'string' && (MODES as readonly string[]).includes(modeRaw) ? (modeRaw as Mode) : fallbackMode
  if (modeRaw !== undefined && mode !== modeRaw) problems.push(`未知模式 ${String(modeRaw)},按 ${fallbackMode}`)
  const version = typeof root.version === 'number' && Number.isFinite(root.version) ? root.version : null
  const p = isRecord(root.params) ? root.params : {}
  const d = DEFAULT_PARAMS
  const landing = isRecord(p.landingDelta) ? p.landingDelta : {}
  const minimum = isRecord(p.minimumReserve) ? p.minimumReserve : {}
  const params: Params = {
    quantile: pickNumber(p, 'quantile', d.quantile, 0.5, 0.99, problems),
    landingDelta: {
      five_hour: pickNumber(landing, 'five_hour', d.landingDelta.five_hour, 0, 50, problems),
      seven_day: pickNumber(landing, 'seven_day', d.landingDelta.seven_day, 0, 50, problems),
    },
    reserveCapFiveHour: pickNumber(p, 'reserveCapFiveHour', d.reserveCapFiveHour, 0, 50, problems),
    weeklyReserve: pickNumber(p, 'weeklyReserve', d.weeklyReserve, 0, 50, problems),
    weeklyReserveTaperHours: pickNumber(p, 'weeklyReserveTaperHours', d.weeklyReserveTaperHours, 0, 168, problems),
    minimumReserve: {
      five_hour: pickNumber(minimum, 'five_hour', d.minimumReserve.five_hour, 0, 50, problems),
      seven_day: pickNumber(minimum, 'seven_day', d.minimumReserve.seven_day, 0, 50, problems),
    },
    newModelMultiplier: pickNumber(p, 'newModelMultiplier', d.newModelMultiplier, 1, 10, problems),
    newModelSamples: pickNumber(p, 'newModelSamples', d.newModelSamples, 1, 50, problems),
    peakMultiplier: pickNumber(p, 'peakMultiplier', d.peakMultiplier, 1, 10, problems),
    costSeedMedianEq: pickNumber(p, 'costSeedMedianEq', d.costSeedMedianEq, 1e4, 1e9, problems),
    sigmaBatch: pickNumber(p, 'sigmaBatch', d.sigmaBatch, 0.01, 3, problems),
    sigmaAgent: pickNumber(p, 'sigmaAgent', d.sigmaAgent, 0.01, 3, problems),
    rateSeedEqPerMinute: pickNumber(p, 'rateSeedEqPerMinute', d.rateSeedEqPerMinute, 1e3, 1e8, problems),
    landingMinMinutes: pickNumber(p, 'landingMinMinutes', d.landingMinMinutes, 0, 300, problems),
    recheckMinutes: pickNumber(p, 'recheckMinutes', d.recheckMinutes, 5, 1440, problems),
    segmentPoints: pickNumber(p, 'segmentPoints', d.segmentPoints, 1, 50, problems),
    segmentMinutes: pickNumber(p, 'segmentMinutes', d.segmentMinutes, 1, 600, problems),
    dominantShare: pickNumber(p, 'dominantShare', d.dominantShare, 0.5, 1, problems),
    softLanding: pickBool(p, 'softLanding', d.softLanding, problems),
    redispatch: pickBool(p, 'redispatch', d.redispatch, problems),
    briefing: pickBool(p, 'briefing', d.briefing, problems),
  }
  return { mode, version, params, problems }
}

/** 标准正态分位数(Acklam 近似,|误差| < 1.2e-9),给 p80 一类的分位用。 */
export function normalQuantile(p: number): number {
  if (!(p > 0 && p < 1)) throw new RangeError('p must be in (0, 1)')
  const a = [-39.69683028665376, 220.9460984245205, -275.9285104469687, 138.357751867269, -30.66479806614716, 2.506628277459239]
  const b = [-54.47609879822406, 161.5858368580409, -155.6989798598866, 66.80131188771972, -13.28068155288572]
  const c = [-0.007784894002430293, -0.3223964580411365, -2.400758277161838, -2.549732539343734, 4.374664141464968, 2.938163982698783]
  const d = [0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416]
  const low = 0.02425
  if (p < low) {
    const q = Math.sqrt(-2 * Math.log(p))
    return (((((c[0]! * q + c[1]!) * q + c[2]!) * q + c[3]!) * q + c[4]!) * q + c[5]!) / ((((d[0]! * q + d[1]!) * q + d[2]!) * q + d[3]!) * q + 1)
  }
  if (p > 1 - low) return -normalQuantile(1 - p)
  const q = p - 0.5
  const r = q * q
  return (((((a[0]! * r + a[1]!) * r + a[2]!) * r + a[3]!) * r + a[4]!) * r + a[5]!) * q /
    (((((b[0]! * r + b[1]!) * r + b[2]!) * r + b[3]!) * r + b[4]!) * r + 1)
}
