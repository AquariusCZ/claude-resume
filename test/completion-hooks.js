'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const hooks = require('../src/install-completion-hooks');

let failed = 0;
function check(name, condition, detail) {
  console.log((condition ? '  ✓ ' : '  ✗ ') + name + (condition ? '' : ' — ' + detail));
  if (!condition) failed++;
}

function commandFromConfig(file) {
  const match = /^\s*notify\s*=\s*(\[[^\r\n]*\])/m.exec(fs.readFileSync(file, 'utf8'));
  return match ? JSON.parse(match[1]) : null;
}

function main() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ai-resume-completion-hooks-'));
  const nodePath = process.execPath;
  const handlerPath = path.resolve(__dirname, '..', 'src', 'completion-notify.js');
  try {
    const codexConfig = path.join(root, '.codex', 'config.toml');
    fs.mkdirSync(path.dirname(codexConfig), { recursive: true });
    const oldPrevious = ['C:\\Tools\\cod-use.exe', 'turn-ended'];
    const desktop = ['C:\\Tools\\codex-computer-use.exe', 'turn-ended', '--previous-notify', JSON.stringify(oldPrevious)];
    fs.writeFileSync(codexConfig, `notify = ${JSON.stringify(desktop)}\n[features]\nhooks = true\n`);
    hooks.installCodex(codexConfig, nodePath, handlerPath);
    const merged = commandFromConfig(codexConfig);
    const nested = JSON.parse(merged[merged.indexOf('--previous-notify') + 1]);
    const nestedPrevious = JSON.parse(nested[nested.indexOf('--previous-notify') + 1]);
    check('Codex Desktop 包装器保持在通知链顶层', merged[0] === desktop[0] && merged[1] === 'turn-ended', JSON.stringify(merged));
    check('AI Resume 插入链中且保留原通知器', nested[1] === handlerPath && nested[2] === 'codex' && JSON.stringify(nestedPrevious) === JSON.stringify(oldPrevious), JSON.stringify(nested));
    const codexOnce = fs.readFileSync(codexConfig, 'utf8');
    hooks.installCodex(codexConfig, nodePath, handlerPath);
    check('Codex hook 安装幂等', fs.readFileSync(codexConfig, 'utf8') === codexOnce);
    const movedNodePath = 'C:\\Program Files\\nodejs-new\\node.exe';
    hooks.installCodex(codexConfig, movedNodePath, handlerPath);
    const moved = commandFromConfig(codexConfig);
    const movedNested = JSON.parse(moved[moved.indexOf('--previous-notify') + 1]);
    const movedPrevious = JSON.parse(movedNested[movedNested.indexOf('--previous-notify') + 1]);
    check('Codex 重装会刷新托管 Node 路径并保留 Desktop/旧通知链', moved[0] === desktop[0] && movedNested[0] === movedNodePath && JSON.stringify(movedPrevious) === JSON.stringify(oldPrevious), JSON.stringify(moved));
    hooks.installCodex(codexConfig, nodePath, handlerPath);

    const noNotify = path.join(root, 'new-codex.toml');
    fs.writeFileSync(noNotify, '# top-level comment\n[features]\nfoo = true');
    hooks.installCodex(noNotify, nodePath, handlerPath);
    const inserted = fs.readFileSync(noNotify, 'utf8');
    check('无 notify 时在首个 TOML section 前插入', inserted.indexOf('notify =') > 0 && inserted.indexOf('notify =') < inserted.indexOf('[features]'), inserted);
    const missingCodex = path.join(root, 'missing', '.codex', 'config.toml');
    hooks.installCodex(missingCodex, nodePath, handlerPath);
    check('Codex 配置尚不存在时自动创建', fs.existsSync(missingCodex) && hooks.commandContainsHandler(commandFromConfig(missingCodex), handlerPath));
    const multilineCodex = path.join(root, '.codex-multiline', 'config.toml');
    fs.mkdirSync(path.dirname(multilineCodex), { recursive: true });
    const multilineRaw = 'notify = [\n  "C:\\\\Tools\\\\notify.exe",\n  "turn-ended",\n]\n[features]\nhooks = true\n';
    fs.writeFileSync(multilineCodex, multilineRaw);
    let multilineRejected = false;
    try { hooks.installCodex(multilineCodex, nodePath, handlerPath); } catch (e) { multilineRejected = /single-line/.test(String(e.message)); }
    check('Codex 多行 notify 无法安全解析时拒绝安装且不改配置', multilineRejected && fs.readFileSync(multilineCodex, 'utf8') === multilineRaw);
    let batchRejected = false;
    try { hooks.mergeCodexNotify(['C:\\Tools\\notify.cmd'], nodePath, handlerPath); } catch (e) { batchRejected = /batch notify/.test(String(e.message)); }
    check('Codex 批处理通知链因参数注入风险而拒绝接管', batchRejected);
    const wrapperOnly = ['C:\\Tools\\codex-computer-use.exe', 'turn-ended'];
    const wrapperOnlyMerged = hooks.mergeCodexNotify(wrapperOnly, nodePath, handlerPath);
    check('Codex Desktop 无 previous 链时仍保持包装器在顶层', wrapperOnlyMerged[0] === wrapperOnly[0] && wrapperOnlyMerged.includes('--previous-notify'), JSON.stringify(wrapperOnlyMerged));

    const claudeSettings = path.join(root, '.claude', 'settings.json');
    fs.mkdirSync(path.dirname(claudeSettings), { recursive: true });
    fs.writeFileSync(claudeSettings, JSON.stringify({
      hooks: {
        Stop: [{ matcher: '*', hooks: [{ type: 'command', command: 'existing-stop-hook' }] }],
        PreToolUse: [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'existing-pre-hook' }] }],
      },
    }, null, 2));
    hooks.installClaude(claudeSettings, nodePath, handlerPath);
    const claude = JSON.parse(fs.readFileSync(claudeSettings, 'utf8'));
    check('Claude Code 保留既有 Stop 与其他事件 hooks', claude.hooks.Stop.some(x => x.hooks.some(h => h.command === 'existing-stop-hook')) && claude.hooks.PreToolUse[0].hooks[0].command === 'existing-pre-hook');
    const managedClaudeHooks = claude.hooks.Stop.flatMap(x => x.hooks).filter(h => Array.isArray(h.args) && h.args.includes(handlerPath));
    check('Claude Code 使用无 shell 的 command+args 安装一个 Stop hook', managedClaudeHooks.length === 1 && managedClaudeHooks[0].command === nodePath && managedClaudeHooks[0].args[1] === 'claude');
    const claudeOnce = fs.readFileSync(claudeSettings, 'utf8');
    hooks.installClaude(claudeSettings, nodePath, handlerPath);
    check('Claude hook 安装幂等', fs.readFileSync(claudeSettings, 'utf8') === claudeOnce);

    const appDir = path.join(root, 'app');
    const clineDir = path.join(root, 'Documents', 'Cline', 'Hooks');
    fs.mkdirSync(clineDir, { recursive: true });
    const clinePath = path.join(clineDir, 'TaskComplete.ps1');
    const previous = '$raw = [Console]::In.ReadToEnd()\r\n@{ cancel = $true } | ConvertTo-Json -Compress\r\n';
    fs.writeFileSync(clinePath, previous);
    hooks.installCline(clineDir, appDir, nodePath, handlerPath);
    const backupPath = path.join(clineDir, 'TaskComplete.ai-resume-previous.ps1');
    check('Cline 既有 TaskComplete hook 被保留为前置链', fs.readFileSync(backupPath, 'utf8') === previous);
    const wrapperOnce = fs.readFileSync(clinePath, 'utf8');
    hooks.installCline(clineDir, appDir, nodePath, handlerPath);
    check('Cline hook 安装幂等且不会覆盖既有备份', fs.readFileSync(clinePath, 'utf8') === wrapperOnce && fs.readFileSync(backupPath, 'utf8') === previous);

    const queueDir = path.join(root, 'queue');
    // 宿主环境可能带 AI_RESUME_INTERNAL_RUN=1(本会话由 AI Resume 内部续起时),会按设计抑制入队;测试必须显式清除。
    const spawnEnv = Object.assign({}, process.env, { AI_RESUME_COMPLETION_DIR: queueDir });
    delete spawnEnv.AI_RESUME_INTERNAL_RUN;
    const payload = JSON.stringify({ taskId: 'cline-hook-test', timestamp: new Date().toISOString(), workspaceRoots: [process.cwd()] });
    const result = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', clinePath], {
      input: payload,
      encoding: 'utf8',
      env: spawnEnv,
    });
    check('Cline 包装器可执行并透传既有 hook 的取消返回值', result.status === 0 && /"cancel":true/.test(result.stdout), `${result.status} ${result.stdout} ${result.stderr}`);
    check('Cline 既有 hook 取消完成时不生成通知事件', !fs.existsSync(queueDir) || !fs.readdirSync(queueDir).some(name => name.endsWith('.json')), fs.existsSync(queueDir) ? fs.readdirSync(queueDir).join(',') : 'empty');
    fs.writeFileSync(backupPath, '$raw = [Console]::In.ReadToEnd()\r\n@{ cancel = $false } | ConvertTo-Json -Compress\r\n');
    const allowedPayload = JSON.stringify({ taskId: 'cline-hook-test-allowed', timestamp: new Date().toISOString(), workspaceRoots: [process.cwd()] });
    const allowed = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', clinePath], {
      input: allowedPayload, encoding: 'utf8', env: spawnEnv,
    });
    check('Cline 既有 hook 允许完成时才写入队列',
      allowed.status === 0 && /"cancel":false/.test(allowed.stdout)
        && fs.existsSync(queueDir) && fs.readdirSync(queueDir).some(name => name.endsWith('.json')),
      `${allowed.status} ${allowed.stdout} ${allowed.stderr} queue=${fs.existsSync(queueDir)}`);

    const brokenHome = path.join(root, 'broken-home');
    fs.mkdirSync(path.join(brokenHome, '.claude'), { recursive: true });
    fs.writeFileSync(path.join(brokenHome, '.claude', 'settings.json'), '{bad json');
    const partial = hooks.installAll({ appDir: path.join(root, 'partial-app'), home: brokenHome, documentsDir: path.join(root, 'partial-docs'), nodePath });
    check('单个客户端配置损坏不阻断另外两个 hook 安装', partial.claude.status === 'error' && partial.codex.status === 'installed' && partial.cline.status === 'installed', JSON.stringify(partial));
    const completionContext = JSON.parse(fs.readFileSync(path.join(root, 'partial-app', 'completion-context.json'), 'utf8'));
    check('hook 安装器固化 Windows 真实 Documents 下的 Codex projectless 根',
      completionContext.codexDocumentsRoot === path.join(root, 'partial-docs', 'Codex'), JSON.stringify(completionContext));
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
  if (failed) process.exitCode = 1;
  else console.log('completion hooks: all tests passed');
}

main();
