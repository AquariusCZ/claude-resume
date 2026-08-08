// Live provider smoke test. Uses credentials from the runtime config and never prints them.
// Run one or more profiles: node test/provider-live.js openai-sol deepseek-v4 deepseek-v4-pro
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { createAIRunner } = require('../src/ai/runners');
const { createCodexSessions } = require('../src/ai/codex-sessions');
const { profileById } = require('../src/ai/profiles');
const { probeProviders } = require('../src/provider-health');

const APP = path.join(process.env.LOCALAPPDATA || '', 'ClaudeResume');
const CFG = path.join(APP, 'config.json');
const selected = process.argv.slice(2);
const ids = selected.length ? selected : ['openai-sol', 'deepseek-v4', 'deepseek-v4-pro'];

function readConfig() {
  return JSON.parse(fs.readFileSync(CFG, 'utf8').replace(/^\uFEFF/, ''));
}

const runner = createAIRunner({ readConfig, logLine: line => console.log('  ' + line) });
const codexSessions = createCodexSessions({ logLine: line => console.log('  ' + line) });
const blockedTools = ['Task', 'Bash', 'Read', 'Write', 'Edit', 'Glob', 'Grep', 'NotebookEdit'];

async function main() {
  let failed = 0;
  const includeClaude = ids.some(id => profileById(id) && profileById(id).provider === 'claude');
  const health = await probeProviders({ runner, readConfig, includeClaude, timeoutMs: 120000 });
  for (const id of ids) {
    const profile = profileById(id);
    if (!profile) throw new Error('unknown profile: ' + id);
    const providerHealth = health.providers[profile.provider];
    if (!providerHealth || providerHealth.status !== 'available') {
      console.log(`FAIL ${profile.fullLabel} route unavailable ${providerHealth && providerHealth.reason || 'missing'}`);
      failed++;
      continue;
    }
    const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-resume-provider-live-'));
    let testThreadId = null;
    try {
      const result = await runner.run(cwd, 'provider live smoke', '只回答 OK,不要调用任何工具。', {
        profile,
        readOnly: true,
        noTools: true,
        disallowedTools: blockedTools,
        skipPermissions: false,
        useContinue: false,
        timeoutMs: 180000,
        networkRoute: providerHealth.route,
      });
      const preview = String(result.text || '').replace(/[\r\n]+/g, ' ').slice(0, 100);
      console.log(`${result.ok ? 'PASS' : 'FAIL'} ${profile.fullLabel} ${providerHealth.route || 'default'} ${Math.round(result.ms / 1000)}s ${result.errorCode || ''} ${preview}`);
      if (!result.ok || !/\bOK\b/i.test(result.text || '')) failed++;
      if (profile.provider === 'openai' && result.sessionId) testThreadId = result.sessionId;
      if (profile.provider === 'openai' && result.ok && result.sessionId) {
        const resumed = await runner.run(cwd, 'provider live resume smoke', '再次只回答 OK,不要调用任何工具。', {
          profile,
          sessionId: result.sessionId,
          readOnly: true,
          noTools: true,
          disallowedTools: blockedTools,
          skipPermissions: false,
          timeoutMs: 180000,
          networkRoute: providerHealth.route,
        });
        const resumedPreview = String(resumed.text || '').replace(/[\r\n]+/g, ' ').slice(0, 100);
        console.log(`${resumed.ok ? 'PASS' : 'FAIL'} ${profile.fullLabel} resume/non-git ${providerHealth.route || 'default'} ${Math.round(resumed.ms / 1000)}s ${resumed.errorCode || ''} ${resumedPreview}`);
        if (!resumed.ok || !/\bOK\b/i.test(resumed.text || '')) failed++;
      }
    } finally {
      if (testThreadId) {
        try { await codexSessions.remove(testThreadId); }
        catch (e) { console.log(`FAIL OpenAI smoke thread cleanup ${e && e.message || e}`); failed++; }
      }
      const resolved = path.resolve(cwd);
      const tempRoot = path.resolve(os.tmpdir()) + path.sep;
      if (resolved.startsWith(tempRoot) && path.basename(resolved).startsWith('claude-resume-provider-live-')) {
        fs.rmSync(resolved, { recursive: true, force: true });
      }
    }
  }
  process.exitCode = failed ? 1 : 0;
}

main().catch(err => { console.error(err && (err.stack || err)); process.exit(1); });
