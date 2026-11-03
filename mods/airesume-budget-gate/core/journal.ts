// 本地 JSONL 日志的缓冲与分段。`$.fs.write` 只能整文件写、单次上限 4 MiB,
// 所以每个进程把当前分段整段留在内存里,定时整段写回;写满一段就换下一段。
// 纯数据结构:何时写、写到哪由适配层决定,这里只管"该写什么、写完怎么推进"。

export type JournalSnapshot = { part: number; text: string }

export class Journal {
  /** 当前分段的全文(已写 + 未写)。 */
  private text = ''
  /** 当前分段已经成功写盘的长度。 */
  private writtenLength = 0
  /** 写不出去时为了不无限涨内存而丢掉的行数。 */
  dropped = 0
  part = 0
  readonly partChars: number
  readonly hardCapChars: number

  constructor(partChars = 512 * 1024, hardCapChars = 2 * 1024 * 1024) {
    this.partChars = partChars
    this.hardCapChars = hardCapChars
  }

  /** 追加一行;持续写盘失败、缓冲超过硬上限时丢弃新行并计数。 */
  append(row: unknown): void {
    const line = JSON.stringify(row) + '\n'
    if (this.text.length - this.writtenLength + line.length > this.hardCapChars) {
      this.dropped += 1
      return
    }
    this.text += line
  }

  get isDirty(): boolean {
    return this.text.length !== this.writtenLength
  }

  /** 现在该写的内容:当前分段的全文。 */
  snapshot(): JournalSnapshot {
    return { part: this.part, text: this.text }
  }

  /** `snapshot` 已成功写盘后调用;分段写满就换段,写盘期间新追加的行挪到新段。 */
  written(snapshot: JournalSnapshot): void {
    if (snapshot.part !== this.part) return
    this.writtenLength = Math.max(this.writtenLength, snapshot.text.length)
    if (this.writtenLength >= this.partChars) {
      this.text = this.text.slice(snapshot.text.length)
      this.writtenLength = 0
      this.part += 1
    }
  }
}
