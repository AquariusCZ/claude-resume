'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const iconPath = path.join(root, 'src', 'icon.ico');
const installerPath = path.join(root, 'src', 'install.ps1');
const deployHelperPath = path.join(root, 'src', 'deploy-files.ps1');
const icon = fs.readFileSync(iconPath);

assert(icon.length > 6, 'icon.ico is empty');
assert.strictEqual(icon.readUInt16LE(0), 0, 'invalid ICO reserved field');
assert.strictEqual(icon.readUInt16LE(2), 1, 'icon.ico is not an icon resource');

const count = icon.readUInt16LE(4);
assert(count >= 8, `expected a multi-resolution icon, got ${count} image(s)`);
assert(icon.length >= 6 + count * 16, 'truncated ICO directory');

const sizes = [];
for (let index = 0; index < count; index++) {
  const entry = 6 + index * 16;
  const width = icon[entry] || 256;
  const height = icon[entry + 1] || 256;
  const length = icon.readUInt32LE(entry + 8);
  const offset = icon.readUInt32LE(entry + 12);
  assert.strictEqual(width, height, `non-square ICO entry at index ${index}`);
  assert(offset + length <= icon.length, `ICO entry ${index} exceeds the file`);
  assert(icon.subarray(offset, offset + 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
    `ICO entry ${index} is not PNG encoded`);
  sizes.push(width);
}

for (const expected of [16, 20, 24, 32, 40, 48, 64, 128, 256]) {
  assert(sizes.includes(expected), `icon.ico is missing ${expected}x${expected}`);
}

const installer = fs.readFileSync(installerPath, 'utf8');
const deployHelper = fs.readFileSync(deployHelperPath, 'utf8');
assert(/'icon\.ico'/.test(installer), 'install.ps1 does not deploy icon.ico');
assert(/deploy-files\.ps1/.test(installer) && /Invoke-CcuFileDeployment/.test(installer),
  'install.ps1 does not use the transactional deploy helper');
assert(/Test-SameFileContent/.test(deployHelper) && /Get-FileHash\s+-Algorithm\s+SHA256/.test(deployHelper),
  'install.ps1 does not skip byte-identical locked assets');
assert(/\[IO\.File\]::Replace/.test(deployHelper), 'deploy helper does not atomically replace existing files');
assert(/\$cleanupTransaction\s*=\s*\$false/.test(deployHelper) && /recovery files kept at:/.test(deployHelper),
  'deploy helper does not retain recovery files after rollback failure');
assert(/\$sc\.IconLocation\s*=\s*"\$IcoPath,0"/.test(installer), 'Desktop shortcut does not use the brand icon');
assert(/Desktop shortcut icon was not saved/.test(installer), 'shortcut icon assignment is not verified');

console.log(`icon asset: ${count} PNG sizes (${sizes.sort((a, b) => a - b).join(', ')})`);
