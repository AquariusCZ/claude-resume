'use strict';

const { spawn } = require('child_process');
const { findCodexCmd, killTree } = require('./runners');
const { version: APP_VERSION } = require('../package.json');

function createCodexSessions(options) {
  const codexCmd = options && options.codexCmd || findCodexCmd();
  const logLine = options && options.logLine || (() => {});

  function request(method, params, timeoutMs, signal) {
    return new Promise((resolve, reject) => {
      let child;
      try { child = spawn(codexCmd, ['app-server', '--stdio'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); }
      catch (e) { reject(e); return; }
      let buf = '', err = '', settled = false;
      const finish = (error, value) => {
        if (settled) return; settled = true; clearTimeout(timer);
        if (signal) signal.removeEventListener('abort', onAbort);
        killTree(child);
        if (error) reject(error); else resolve(value);
      };
      const onAbort = () => {
        const error = new Error('Codex app-server 请求已取消');
        error.code = 'AI_RESUME_CANCELLED';
        finish(error);
      };
      const timer = setTimeout(() => finish(new Error('Codex app-server 请求超时')), timeoutMs || 8000);
      if (signal) {
        if (signal.aborted) { onAbort(); return; }
        signal.addEventListener('abort', onAbort, { once: true });
      }
      child.stdout.on('data', d => {
        buf += d.toString('utf8'); let i;
        while ((i = buf.indexOf('\n')) >= 0) {
          const ln = buf.slice(0, i); buf = buf.slice(i + 1); if (!ln.trim()) continue;
          try {
            const j = JSON.parse(ln);
            if (j.id === 2) {
              if (j.error) finish(new Error(j.error.message || JSON.stringify(j.error)));
              else finish(null, j.result);
            }
          } catch (e) {}
        }
      });
      child.stderr.on('data', d => { err += d.toString('utf8'); if (err.length > 4000) err = err.slice(-4000); });
      child.on('error', e => finish(e));
      child.on('close', code => { if (!settled) finish(new Error(err.trim() || 'Codex app-server 提前退出: ' + code)); });
      const send = o => child.stdin.write(JSON.stringify(o) + '\n', 'utf8');
      send({ method: 'initialize', id: 1, params: { clientInfo: { name: 'ai-resume', title: 'AI Resume', version: APP_VERSION } } });
      send({ method: 'initialized', params: {} });
      send({ method, id: 2, params: params || {} });
    });
  }

  function sessionsFromResult(result) {
    return (result && result.data || []).map(t => ({
      id: t.id,
      title: String(t.name || t.preview || '(无标题)').replace(/\s+/g, ' ').trim(),
      preview: String(t.preview || '').replace(/\s+/g, ' ').trim(),
      mtime: 1000 * Number(t.recencyAt || t.updatedAt || t.createdAt || 0),
      provider: 'openai', engine: 'codex', file: null,
    }));
  }

  async function listResult(projectPath, limit, options) {
    try {
      const result = await request('thread/list', {
        cwd: projectPath, limit: limit || 5, sortKey: 'recency_at', sortDirection: 'desc',
        sourceKinds: ['cli', 'vscode', 'exec', 'appServer', 'unknown'], archived: false,
      }, 8000, options && options.signal);
      return { sessions: sessionsFromResult(result), error: null };
    } catch (e) {
      if (e && e.code !== 'AI_RESUME_CANCELLED') logLine('读取 Codex 会话失败: ' + e.message);
      return { sessions: [], error: e, cancelled: !!(e && e.code === 'AI_RESUME_CANCELLED') };
    }
  }

  async function list(projectPath, limit, options) {
    return (await listResult(projectPath, limit, options)).sessions;
  }

  async function listAll(options) {
    const opts = options || {};
    const out = [];
    let cursor;
    const max = Math.max(1, Number(opts.limit || 1000));
    try {
      while (out.length < max) {
        const params = {
          limit: Math.min(100, max - out.length),
          sortKey: 'recency_at', sortDirection: 'desc',
          sourceKinds: ['cli', 'vscode', 'exec', 'appServer', 'unknown'],
          archived: !!opts.archived,
        };
        if (opts.cwd) params.cwd = opts.cwd;
        if (cursor) params.cursor = cursor;
        const result = await request('thread/list', params, 12000);
        const rows = result && result.data || [];
        for (const t of rows) {
          out.push({
            id: t.id,
            title: String(t.name || t.preview || '(无标题)').replace(/\s+/g, ' ').trim(),
            preview: String(t.preview || '').replace(/\s+/g, ' ').trim(),
            cwd: t.cwd || '',
            mtime: 1000 * Number(t.recencyAt || t.updatedAt || t.createdAt || 0),
            createdAt: 1000 * Number(t.createdAt || 0),
            archived: !!opts.archived,
            provider: 'openai', engine: 'codex', file: null,
          });
        }
        cursor = result && (result.nextCursor || result.next_cursor);
        if (!cursor || !rows.length) break;
      }
      return out;
    } catch (e) {
      logLine('读取全部 Codex 会话失败: ' + e.message);
      return out;
    }
  }

  async function archive(threadId) { return request('thread/archive', { threadId }, 12000); }
  async function unarchive(threadId) { return request('thread/unarchive', { threadId }, 12000); }
  async function remove(threadId) { return request('thread/delete', { threadId }, 12000); }

  function textFromContent(content) {
    if (typeof content === 'string') return content;
    if (!Array.isArray(content)) return '';
    return content.map(x => x && (x.text || x.content || '')).filter(Boolean).join(' ');
  }

  async function preview(threadId, turns) {
    try {
      const result = await request('thread/read', { threadId, includeTurns: true }, 10000);
      const thread = result && result.thread || {};
      const messages = [];
      for (const turn of (thread.turns || [])) {
        for (const item of (turn.items || [])) {
          if (item.type === 'userMessage') messages.push({ who: 'you', text: textFromContent(item.content) });
          else if (item.type === 'agentMessage') messages.push({ who: 'ai', text: item.text || '' });
        }
      }
      return messages.filter(x => x.text.trim()).slice(-2 * (turns || 2)).map(x => {
        const text = x.text.replace(/\s+/g, ' ').trim();
        return (x.who === 'you' ? '· 你:' : '  我:') + (text.length > 100 ? text.slice(0, 100) + '…' : text);
      }).join('\n');
    } catch (e) { logLine('读取 Codex 会话摘要失败: ' + e.message); return ''; }
  }

  return { request, list, listResult, listAll, preview, archive, unarchive, remove };
}

module.exports = { createCodexSessions };
