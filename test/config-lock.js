'use strict';
/*
  S1-C1 迁移:Node/PowerShell 双向写锁竞争与字段保留全部在 prepareTestConfig 创建的
  synthetic LOCALAPPDATA/临时 config 内完成;不读取/备份/写回真实 config。
  Phase 1:PowerShell 持锁(800ms)→ 本进程 Node 等待,两边字段都必须保留;
  Phase 2:Node 在独立子进程持锁(子进程自建测试根,marker PID 与其自身一致)→
  PowerShell 等待,两边字段都必须保留。
  Run: node test/config-lock.js
*/
const assert = require('assert');
const cp = require('child_process');
const fs = require('fs');
const path = require('path');

const helper = require('./feishu-test-config');
const agentPath = path.join(__dirname, '..', 'src', 'feishu-agent.js');
const libPath = path.join(__dirname, '..', 'src', 'lib.ps1').replace(/'/g, "''");

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const waitFor = async (fn, timeoutMs = 15000) => {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) { if (fn()) return true; await sleep(25); }
  return !!fn();
};
const waitChild = child => child.exitCode !== null ? Promise.resolve(child.exitCode) : new Promise(resolve => child.once('close', resolve));

// 与 config-isolation 的 EPERM 契约一致:本环境禁止创建子进程时无法做跨进程竞争验证,
// 明确跳过并保持退出码 0;在允许 spawn 的机器上执行完整双向竞争断言。
function childSpawnBlocked() {
  try {
    const r = cp.spawnSync(process.execPath, ['-e', '0'], { timeout: 5000, windowsHide: true });
    return !!(r.error && r.error.code === 'EPERM');
  } catch (e) {
    if (e && e.code === 'EPERM') return true;
    throw e;
  }
}

const BASELINE = {
  enabled: true,
  armed: true,
  armCycleId: 'config-lock-cycle',
  continuous: false,
  feishuAuthOpenIds: ['ou_owner'],
  feishuChatProfile: 'openai-sol',
  feishuUserProfiles: {},
};
const holderMode = process.env.CONFIG_LOCK_HOLDER === '1';
let h = null;
if (!holderMode) {
  h = helper.prepareTestConfig({ real: false, source: BASELINE });
}

async function runPhase1(A) {
  assert.strictEqual(A.setUserProfileId('ou_owner', 'deepseek-v4-pro'), true);
  let cfg = JSON.parse(fs.readFileSync(h.configPath, 'utf8'));
  assert.strictEqual(cfg.armCycleId, 'config-lock-cycle');
  assert.strictEqual(cfg.enabled, true);

  const psReady = path.join(h.root, 'ps-ready');
  const appPs = h.root.replace(/'/g, "''");
  const readyPs = psReady.replace(/'/g, "''");
  const psScript = `$env:CLAUDE_RESUME_APP_DIR='${appPs}'; . '${libPath}'; Invoke-CcuPortableWriteLock ($script:ConfigPath+'.write.lock') { [IO.File]::WriteAllText('${readyPs}','ready'); Start-Sleep -Milliseconds 800; $c=Get-CcuConfig; $c.continuous=$true; Write-CcuJsonAtomic $script:ConfigPath $c }`;
  const ps = cp.spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', psScript], { stdio: 'pipe' });
  assert(await waitFor(() => fs.existsSync(psReady)), 'PowerShell did not acquire the shared config lock');
  assert.strictEqual(A.setUserProfileId('ou_owner', 'openai-sol'), true);
  assert.strictEqual(await waitChild(ps), 0);
  cfg = JSON.parse(fs.readFileSync(h.configPath, 'utf8'));
  assert.strictEqual(cfg.continuous, true, 'Node overwrote the PowerShell update');
  assert.strictEqual(cfg.armCycleId, 'config-lock-cycle');
}

async function runPhase2() {
  const holderReady = path.join(h.root, 'holder-ready');
  const holderInfo = path.join(h.root, 'holder-info.json');
  const holderDone = path.join(h.root, 'holder-done');
  const holderResult = path.join(h.root, 'holder-result.json');
  const holder = cp.spawn(process.execPath, [__filename], {
    env: Object.assign({}, process.env, {
      CONFIG_LOCK_HOLDER: '1',
      CONFIG_LOCK_HOLDER_READY: holderReady,
      CONFIG_LOCK_HOLDER_INFO: holderInfo,
      CONFIG_LOCK_HOLDER_DONE: holderDone,
      CONFIG_LOCK_HOLDER_RESULT: holderResult,
    }),
    stdio: 'pipe',
  });
  assert(await waitFor(() => fs.existsSync(holderReady)), 'Node did not acquire the shared config lock');
  const info = JSON.parse(fs.readFileSync(holderInfo, 'utf8'));
  const rootPs = info.root.replace(/'/g, "''");
  const donePs = holderDone.replace(/'/g, "''");
  const psScript2 = `$env:CLAUDE_RESUME_APP_DIR='${rootPs}'; . '${libPath}'; Update-CcuConfig { param($c) $c | Add-Member -NotePropertyName psAfterNode -NotePropertyValue $true -Force } | Out-Null; [IO.File]::WriteAllText('${donePs}','done')`;
  const ps2 = cp.spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', psScript2], { stdio: 'pipe' });
  let ps2Error = ''; ps2.stderr.on('data', chunk => { ps2Error += chunk; });
  assert.strictEqual(await waitChild(holder), 0);
  assert.strictEqual(await waitChild(ps2), 0, ps2Error);
  const result = JSON.parse(fs.readFileSync(holderResult, 'utf8'));
  assert.strictEqual(result.ok, true, JSON.stringify(result));
  assert.strictEqual(result.nodeHeldWrite, true, 'PowerShell overwrote the Node update');
  assert.strictEqual(result.psAfterNode, true);
  assert.strictEqual(result.armCycleId, 'config-lock-cycle');
}

async function runHolder() {
  process.env.FEISHU_TEST = '1';
  process.env.FEISHU_TEST_NO_AI = '1';
  const hh = helper.prepareTestConfig({ real: false, source: BASELINE });
  const A = require(agentPath);
  const infoFile = process.env.CONFIG_LOCK_HOLDER_INFO;
  const readyFile = process.env.CONFIG_LOCK_HOLDER_READY;
  const doneFile = process.env.CONFIG_LOCK_HOLDER_DONE;
  const resultFile = process.env.CONFIG_LOCK_HOLDER_RESULT;
  try {
    A.updateConfig(cfg => {
      // 先持锁再暴露根路径,保证 PowerShell 必然在锁竞争窗口内启动。
      fs.writeFileSync(infoFile, JSON.stringify({ root: hh.root, configPath: hh.configPath }));
      fs.writeFileSync(readyFile, 'ready');
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 3000);
      cfg.nodeHeldWrite = true;
    });
    const done = await waitFor(() => fs.existsSync(doneFile), 20000);
    assert.strictEqual(done, true, 'PowerShell did not finish after Node released the lock');
    const cfg = JSON.parse(fs.readFileSync(hh.configPath, 'utf8'));
    const result = {
      ok: cfg.nodeHeldWrite === true && cfg.psAfterNode === true && cfg.armCycleId === 'config-lock-cycle',
      nodeHeldWrite: cfg.nodeHeldWrite,
      psAfterNode: cfg.psAfterNode,
      armCycleId: cfg.armCycleId,
    };
    fs.writeFileSync(resultFile, JSON.stringify(result));
    assert.strictEqual(result.ok, true, JSON.stringify(result));
  } finally {
    try { hh.cleanup(); } catch (e) {}
  }
}

async function main() {
  if (childSpawnBlocked()) {
    console.log('config-lock: 本环境禁止创建子进程(spawn EPERM),跳过 Node/PowerShell 跨进程竞争验证');
    return;
  }
  process.env.FEISHU_TEST = '1';
  process.env.FEISHU_TEST_NO_AI = '1';
  const A = require(agentPath);
  await runPhase1(A);
  await runPhase2();
  console.log('config-lock: cross-process atomic update checks passed');
}

if (holderMode) {
  runHolder().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
} else {
  main().catch(error => { console.error(error.stack || error); process.exitCode = 1; }).finally(() => {
    if (h) { try { h.cleanup(); } catch (e) {} }
  });
}
