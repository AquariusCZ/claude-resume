'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const assert = require('assert');
const { createSessionManager, DAY_MS } = require('../src/session-manager');

class FakeCodex {
  constructor(active) { this.active = new Map((active || []).map(x => [x.id, x])); this.archived = new Map(); this.deleted = []; }
  async listAll(options) { return Array.from((options && options.archived ? this.archived : this.active).values()); }
  async archive(id) { const row = this.active.get(id); if (!row) throw new Error('not found'); this.active.delete(id); this.archived.set(id, { ...row, archived: true }); }
  async unarchive(id) { const row = this.archived.get(id); if (!row) throw new Error('not found'); this.archived.delete(id); this.active.set(id, { ...row, archived: false }); }
  async remove(id) { this.active.delete(id); this.archived.delete(id); this.deleted.push(id); }
}

function writeJson(file, value) { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify(value), 'utf8'); }
function writeClaude(root, folder, id, cwd, mtime) {
  const base = path.join(root, folder); fs.mkdirSync(path.join(base, id), { recursive: true });
  const file = path.join(base, id + '.jsonl');
  fs.writeFileSync(file, JSON.stringify({ type: 'user', cwd, message: { content: '测试会话' } }) + '\n', 'utf8');
  fs.writeFileSync(path.join(base, id, 'artifact.txt'), 'artifact', 'utf8');
  fs.utimesSync(file, new Date(mtime), new Date(mtime));
  return file;
}

(async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-resume-session-test-'));
  const appDir = path.join(root, 'app');
  const claudeRoot = path.join(root, 'claude');
  const project = 'C:\\Projects\\RealProject';
  const now = Date.UTC(2026, 6, 30, 12, 0, 0);
  let clock = now;
  const fake = new FakeCodex([{ id: 'codex-old-query', cwd: path.join(appDir, 'feishu-query-cwd', 'q-old'), title: 'old query', mtime: now - 31 * DAY_MS }]);
  try {
    writeJson(path.join(appDir, 'config.json'), {
      sessionAutoCleanup: true, feishuSessionArchiveDays: 14,
      feishuSessionDeleteDays: 30, sessionCleanupIntervalHours: 6,
    });

    const chatHash = 'chat-old';
    const chatMarker = path.join(appDir, 'feishu-chat', chatHash, '.started');
    writeJson(chatMarker, {
      kind: 'chat', openId: 'ou_owner', profileId: 'claude-default', engine: 'claude',
      sessionId: 'claude-chat-old', updatedAt: new Date(now - 15 * DAY_MS).toISOString(),
    });
    const chatFile = writeClaude(claudeRoot, 'chat-folder', 'claude-chat-old', path.dirname(chatMarker), now - 15 * DAY_MS);

    const queryMarker = path.join(appDir, 'feishu-query', 'q-old.started');
    writeJson(queryMarker, {
      kind: 'query', openId: 'ou_owner', profileId: 'openai-sol', engine: 'codex',
      sessionId: 'codex-old-query', path: project, name: 'real-project', updatedAt: new Date(now - 31 * DAY_MS).toISOString(),
    });
    fs.mkdirSync(path.join(appDir, 'feishu-query-cwd', 'q-old'), { recursive: true });

    const workFile = writeClaude(claudeRoot, 'work-folder', 'claude-work-old', project, now - 90 * DAY_MS);
    const probeFile = writeClaude(claudeRoot, 'probe-folder', 'probe-old', appDir, now - 30 * 60 * 1000);

    const manager = createSessionManager({ appDir, claudeRoot, codexSessions: fake, now: () => clock });
    const cleaned = await manager.cleanup();
    assert.strictEqual(cleaned.summary.archived, 1, '15 天闲聊应归档');
    assert.strictEqual(cleaned.summary.deleted, 1, '31 天查询应删除');
    assert.strictEqual(cleaned.summary.safeDeleted, 1, '旧探针会话应安全清理');
    assert.ok(!fs.existsSync(chatMarker) && !fs.existsSync(chatFile), '归档后活动闲聊标记和 transcript 应移走');
    assert.ok(!fs.existsSync(queryMarker) && fake.deleted.includes('codex-old-query'), 'Codex 查询应调用原生删除');
    assert.ok(fs.existsSync(workFile), '项目工作会话绝不能自动归档或删除');
    assert.ok(!fs.existsSync(probeFile), 'AppDir 探针垃圾应删除');

    let report = await manager.report();
    const archivedChat = report.records.find(x => x.state === 'archived' && x.kind === 'chat');
    assert.ok(archivedChat && archivedChat.restorable, '归档闲聊应在清单中且可恢复');
    assert.ok(report.records.some(x => x.state === 'active' && x.kind === 'work' && x.sessionId === 'claude-work-old'), '工作会话应出现在手动管理列表');

    await manager.restore(archivedChat.key);
    assert.ok(fs.existsSync(chatMarker) && fs.existsSync(chatFile), '恢复应还原 Claude transcript 与飞书标记');
    const restored = JSON.parse(fs.readFileSync(chatMarker, 'utf8'));
    assert.strictEqual(restored.updatedAt, new Date(now).toISOString(), '恢复后应刷新最后使用时间，避免立即再次归档');

    report = await manager.report();
    const work = report.records.find(x => x.sessionId === 'claude-work-old');
    await manager.archive(work.key);
    assert.ok(!fs.existsSync(workFile), '手动归档工作会话应移动 transcript');
    report = await manager.report();
    const workArchive = report.records.find(x => x.state === 'archived' && x.sessionId === 'claude-work-old');
    assert.ok(workArchive, '手动归档工作会话应进入归档列表');
    await manager.restore(workArchive.key);
    assert.ok(fs.existsSync(workFile), '工作会话应可恢复');

    const forgotten = await manager.forgetChat('ou_owner');
    assert.strictEqual(forgotten.deleted, 1, '忘记闲聊应删除当前用户所有 profile 的实际会话');
    assert.ok(!fs.existsSync(chatMarker) && !fs.existsSync(chatFile), '忘记闲聊应删除标记和底层文件');

    const expiryHash = 'chat-expiry';
    const expiryMarker = path.join(appDir, 'feishu-chat', expiryHash, '.started');
    writeJson(expiryMarker, {
      kind: 'chat', openId: 'ou_expiry', profileId: 'claude-default', engine: 'claude',
      sessionId: 'claude-chat-expiry', updatedAt: new Date(now - 15 * DAY_MS).toISOString(),
    });
    writeClaude(claudeRoot, 'expiry-folder', 'claude-chat-expiry', path.dirname(expiryMarker), now - 15 * DAY_MS);
    const expiryRecord = manager.scratchRecords().find(x => x.sessionId === 'claude-chat-expiry');
    await manager.archive(expiryRecord.key);
    clock = now + 16 * DAY_MS;
    const expired = await manager.cleanup();
    assert.strictEqual(expired.summary.deleted, 1, '归档 scratch 达到最后使用 30 天后应永久删除');
    const afterExpiry = await manager.report();
    assert.ok(!afterExpiry.records.some(x => x.sessionId === 'claude-chat-expiry'), '过期归档不应继续出现在会话列表');

    console.log('session-manager: all checks passed');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
