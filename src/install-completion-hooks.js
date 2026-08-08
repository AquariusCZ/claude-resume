'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');

const MARKER = 'AI Resume managed completion hook';

function atomicWrite(file, content) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = `${file}.tmp-${process.pid}-${Date.now()}`;
  let fd;
  try {
    fd = fs.openSync(temp, 'w');
    fs.writeFileSync(fd, content, 'utf8');
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = null;
    fs.renameSync(temp, file);
  } finally {
    if (fd !== undefined && fd !== null) try { fs.closeSync(fd); } catch (e) {}
    try { fs.unlinkSync(temp); } catch (e) {}
  }
}

function normalizePath(value) {
  try { return path.resolve(String(value || '')).toLowerCase(); }
  catch (e) { return String(value || '').toLowerCase(); }
}

function parseCommand(value) {
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) && parsed.length ? parsed.map(String) : null;
  } catch (e) { return null; }
}

function previousIndex(command) {
  return command.findIndex(value => value === '--previous-notify');
}

function commandContainsHandler(command, handlerPath, depth = 0) {
  if (!Array.isArray(command) || depth > 8) return false;
  const wanted = normalizePath(handlerPath);
  if (command.some(value => normalizePath(value) === wanted)) return true;
  const index = previousIndex(command);
  if (index >= 0 && command[index + 1]) {
    return commandContainsHandler(parseCommand(command[index + 1]), handlerPath, depth + 1);
  }
  return false;
}

function refreshManagedNotify(command, nodePath, handlerPath, depth = 0) {
  if (!Array.isArray(command) || depth > 8) return null;
  const wanted = normalizePath(handlerPath);
  if (command.length >= 3 && normalizePath(command[1]) === wanted) {
    const refreshed = command.slice();
    refreshed[0] = nodePath;
    refreshed[2] = 'codex';
    return refreshed;
  }
  const index = previousIndex(command);
  if (index < 0 || !command[index + 1]) return null;
  const previous = parseCommand(command[index + 1]);
  if (!previous) return null;
  const refreshedPrevious = refreshManagedNotify(previous, nodePath, handlerPath, depth + 1);
  if (!refreshedPrevious) return null;
  const refreshed = command.slice();
  refreshed[index + 1] = JSON.stringify(refreshedPrevious);
  return refreshed;
}

function wrapNotify(previous, nodePath, handlerPath) {
  if (previous && previous.length && /\.(?:cmd|bat)$/i.test(String(previous[0] || ''))) {
    throw new Error('Codex batch notify chains are not supported safely; keep the existing notify or replace it with an executable');
  }
  const command = [nodePath, handlerPath, 'codex'];
  if (previous && previous.length) command.push('--previous-notify', JSON.stringify(previous));
  return command;
}

function isCodexDesktopWrapper(command) {
  if (!Array.isArray(command) || !command.length) return false;
  const name = path.basename(command[0]).toLowerCase();
  return name === 'codex-computer-use.exe' || name === 'cod-use.exe';
}

function mergeCodexNotify(existing, nodePath, handlerPath) {
  const refreshed = refreshManagedNotify(existing, nodePath, handlerPath);
  if (refreshed) return refreshed;
  if (commandContainsHandler(existing, handlerPath)) return existing;
  if (isCodexDesktopWrapper(existing)) {
    const index = previousIndex(existing);
    if (index >= 0 && existing[index + 1]) {
      const previous = parseCommand(existing[index + 1]);
      if (!previous) throw new Error('Codex notify previous chain is not a JSON array');
      const merged = existing.slice();
      merged[index + 1] = JSON.stringify(wrapNotify(previous, nodePath, handlerPath));
      return merged;
    }
    return existing.concat(['--previous-notify', JSON.stringify(wrapNotify(null, nodePath, handlerPath))]);
  }
  return wrapNotify(existing, nodePath, handlerPath);
}

function installCodex(configPath, nodePath, handlerPath) {
  if (!fs.existsSync(configPath)) {
    atomicWrite(configPath, `notify = ${JSON.stringify(wrapNotify(null, nodePath, handlerPath))}\r\n`);
    return { status: 'installed' };
  }
  const raw = fs.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, '');
  const sectionIndex = raw.search(/^[ \t]*\[\[?[^\]\r\n]+\]\]?[ \t]*(?:#.*)?$/m);
  const topEnd = sectionIndex < 0 ? raw.length : sectionIndex;
  const top = raw.slice(0, topEnd);
  const match = /^([ \t]*notify[ \t]*=[ \t]*)(\[[^\r\n]*\])([ \t]*(?:#.*)?)$/m.exec(top);
  let updated;
  if (match) {
    const existing = parseCommand(match[2]);
    if (!existing) throw new Error('Codex notify must be a single-line JSON-compatible TOML array');
    const merged = mergeCodexNotify(existing, nodePath, handlerPath);
    if (JSON.stringify(merged) === JSON.stringify(existing)) return { status: 'unchanged' };
    const line = match[1] + JSON.stringify(merged) + match[3];
    updated = raw.slice(0, match.index) + line + raw.slice(match.index + match[0].length);
  } else if (/^[ \t]*notify[ \t]*=/m.test(top)) {
    throw new Error('Codex notify must be a single-line JSON-compatible TOML array');
  } else {
    const line = `notify = ${JSON.stringify(wrapNotify(null, nodePath, handlerPath))}\r\n`;
    const before = raw.slice(0, topEnd);
    const separator = before && !/[\r\n]$/.test(before) ? '\r\n' : '';
    updated = before + separator + line + raw.slice(topEnd);
  }
  atomicWrite(configPath, updated);
  return { status: 'installed' };
}

function hookHasHandler(hook, handlerPath) {
  if (!hook || typeof hook !== 'object') return false;
  const wanted = normalizePath(handlerPath);
  if (typeof hook.command === 'string' && hook.command.toLowerCase().includes(wanted)) return true;
  return Array.isArray(hook.args) && hook.args.some(value => normalizePath(value) === wanted);
}

function installClaude(settingsPath, nodePath, handlerPath) {
  let settings = {};
  if (fs.existsSync(settingsPath)) {
    settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8').replace(/^\uFEFF/, ''));
  }
  if (!settings || typeof settings !== 'object' || Array.isArray(settings)) settings = {};
  if (!settings.hooks || typeof settings.hooks !== 'object' || Array.isArray(settings.hooks)) settings.hooks = {};
  const current = Array.isArray(settings.hooks.Stop) ? settings.hooks.Stop : [];
  const cleaned = [];
  for (const entry of current) {
    if (!entry || typeof entry !== 'object') { cleaned.push(entry); continue; }
    const hooks = Array.isArray(entry.hooks) ? entry.hooks.filter(hook => !hookHasHandler(hook, handlerPath)) : [];
    if (hooks.length || !Array.isArray(entry.hooks)) cleaned.push(Object.assign({}, entry, { hooks }));
  }
  cleaned.push({
    matcher: '*',
    hooks: [{
      type: 'command',
      command: nodePath,
      args: [handlerPath, 'claude'],
      timeout: 10,
      async: true,
    }],
  });
  settings.hooks.Stop = cleaned;
  atomicWrite(settingsPath, JSON.stringify(settings, null, 2) + '\n');
  return { status: 'installed' };
}

function psQuote(value) {
  return `'${String(value).replace(/'/g, "''")}'`;
}

function clineWrapper(nodePath, handlerPath, previousPath) {
  return `# ${MARKER}\r\n` +
`$rawInput = [Console]::In.ReadToEnd()\r\n` +
`$previous = ${psQuote(previousPath)}\r\n` +
`if (Test-Path -LiteralPath $previous) {\r\n` +
`  $previousOutput = $rawInput | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $previous\r\n` +
`  $previousExit = $LASTEXITCODE\r\n` +
`  if ($previousExit -ne 0) { if ($previousOutput) { $previousOutput }; exit $previousExit }\r\n` +
`  $cancelled = $false\r\n` +
`  try { $cancelled = [bool](($previousOutput | Out-String | ConvertFrom-Json).cancel) } catch {}\r\n` +
`  if (-not $cancelled) { try { $rawInput | & ${psQuote(nodePath)} ${psQuote(handlerPath)} cline | Out-Null } catch {} }\r\n` +
`  if ($previousOutput) { $previousOutput } else { @{ cancel = $false } | ConvertTo-Json -Compress }\r\n` +
`} else {\r\n` +
`  try { $rawInput | & ${psQuote(nodePath)} ${psQuote(handlerPath)} cline | Out-Null } catch {}\r\n` +
`  @{ cancel = $false } | ConvertTo-Json -Compress\r\n` +
`}\r\n`;
}

function installCline(hooksDir, appDir, nodePath, handlerPath) {
  fs.mkdirSync(hooksDir, { recursive: true });
  fs.mkdirSync(appDir, { recursive: true });
  const hookPath = path.join(hooksDir, 'TaskComplete.ps1');
  const previousPath = path.join(hooksDir, 'TaskComplete.ai-resume-previous.ps1');
  const legacyPreviousPath = path.join(appDir, 'cline-TaskComplete.previous.ps1');
  if (fs.existsSync(hookPath)) {
    const current = fs.readFileSync(hookPath, 'utf8').replace(/^\uFEFF/, '');
    if (!current.includes(MARKER)) fs.copyFileSync(hookPath, previousPath);
    else if (!fs.existsSync(previousPath) && fs.existsSync(legacyPreviousPath)) fs.copyFileSync(legacyPreviousPath, previousPath);
  }
  atomicWrite(hookPath, '\uFEFF' + clineWrapper(nodePath, handlerPath, previousPath));
  return { status: 'installed', hookPath };
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--') && argv[i + 1]) out[argv[i].slice(2)] = argv[++i];
  }
  return out;
}

function installAll(options) {
  const appDir = options.appDir;
  const handlerPath = path.join(appDir, 'completion-notify.js');
  const nodePath = options.nodePath || process.execPath;
  const home = options.home || os.homedir();
  const documents = options.documentsDir || path.join(home, 'Documents');
  const safe = action => {
    try { return action(); }
    catch (error) { return { status: 'error', error: String(error && error.message || error) }; }
  };
  const context = safe(() => {
    atomicWrite(path.join(appDir, 'completion-context.json'), JSON.stringify({
      version: 1,
      codexDocumentsRoot: path.join(documents, 'Codex'),
    }, null, 2));
    return { status: 'installed' };
  });
  return {
    context,
    codex: safe(() => installCodex(path.join(home, '.codex', 'config.toml'), nodePath, handlerPath)),
    claude: safe(() => installClaude(path.join(home, '.claude', 'settings.json'), nodePath, handlerPath)),
    cline: safe(() => installCline(path.join(documents, 'Cline', 'Hooks'), appDir, nodePath, handlerPath)),
  };
}

if (require.main === module) {
  try {
    const args = parseArgs(process.argv.slice(2));
    if (!args['app-dir']) throw new Error('--app-dir is required');
    const result = installAll({
      appDir: args['app-dir'],
      documentsDir: args['documents-dir'],
      home: args.home,
      nodePath: process.execPath,
    });
    process.stdout.write(JSON.stringify(result));
    if (Object.values(result).some(item => item && item.status === 'error')) process.exitCode = 1;
  } catch (error) {
    process.stderr.write(String(error && error.message || error));
    process.exitCode = 1;
  }
}

module.exports = {
  MARKER, atomicWrite, parseCommand, commandContainsHandler, refreshManagedNotify, mergeCodexNotify,
  installCodex, installClaude, clineWrapper, installCline, installAll,
};
