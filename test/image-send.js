// Offline unit test for the OUTBOUND image channel (机器人 -> 用户). Simulates claude having dropped
// PNG/JPG files into the cwd's image-out dir, then drives drainImageOut and asserts the mock Feishu
// client uploaded each image (im:resource) and sent an image message, that the files were cleared,
// and that oversize/empty files are skipped with a note (not uploaded). No real claude, no network.
// Run: node test/image-send.js
'use strict';
process.env.FEISHU_TEST = '1';
process.env.FEISHU_TEST_API_TIMEOUT_MS = '50';
process.env.FEISHU_TEST_RESOURCE_TIMEOUT_MS = '500';
const fs = require('fs');
const path = require('path');
const testConfigHelper = require('./feishu-test-config');

const testConfig = testConfigHelper.prepareTestConfig({
  real: false,
  source: { enabled: true, feishuChatId: 'oc_img_test', feishuAuthOpenIds: ['ou_img_owner'], feishuChatProfile: 'openai-sol' },
});
process.once('exit', () => { try { testConfig.cleanup(); } catch (e) {} });

const A = require(path.join(__dirname, '..', 'src', 'feishu-agent.js'));
const client = A.client;

let failed = 0;
const check = (n, c, x) => { console.log((c ? '  ✓ ' : '  ✗ ') + n + (c ? '' : ' — ' + (x || ''))); if (!c) failed++; };

async function main() {
  const CHAT = 'oc_img_test';
  // a synthetic project cwd — drainImageOut only needs the path to derive its AppDir out-dir
  const cwd = path.join(testConfig.root, 'image-project');
  try { fs.mkdirSync(cwd, { recursive: true }); } catch (e) {}
  const dir = A.imageOutDir(cwd);

  // the out dir must NOT be inside the project tree (else a modify-run `git add` could commit it)
  check('out 目录在项目外(AppDir 内)', path.resolve(dir).toLowerCase().indexOf(path.resolve(cwd).toLowerCase()) !== 0);
  check('imageHint 含目标目录', A.imageHint(dir).indexOf(dir) !== -1);

  // 1) no images -> nothing sent, no calls
  A.prepImageOut(cwd);
  client.__reset();
  let n = await A.drainImageOut(CHAT, cwd);
  check('无图片时不发送', n === 0 && client.__calls.length === 0, 'n=' + n + ' calls=' + client.__calls.length);

  // 2) two valid images -> both uploaded + sent as image messages, files cleared
  A.prepImageOut(cwd);
  fs.writeFileSync(path.join(dir, 'a.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]));
  fs.writeFileSync(path.join(dir, 'b.jpg'), Buffer.from([0xff, 0xd8, 0xff, 4, 5, 6]));
  client.__reset();
  n = await A.drainImageOut(CHAT, cwd);
  const uploads = client.__calls.filter(c => c.op === 'uploadImage').length;
  const imgMsgs = client.__calls.filter(c => c.op === 'create' && c.type === 'image');
  check('两张图都发送了', n === 2, 'sent=' + n);
  check('两次上传(im:resource)', uploads === 2, 'uploads=' + uploads);
  check('发出两条 image 消息', imgMsgs.length === 2, 'msgs=' + imgMsgs.length);
  check('image 消息都带 image_key', imgMsgs.length === 2 && imgMsgs.every(m => m.imageKey), '');
  check('image 消息带稳定 uuid(重试不会重复发送)', imgMsgs.length === 2 && imgMsgs.every(m => m.uuid), '');
  check('发送后清空了 out 目录', fs.readdirSync(dir).filter(f => /\.(png|jpg|jpeg|gif|webp|bmp)$/i.test(f)).length === 0);

  // 3) resource upload has its own longer timeout; a slow upload must not inherit the 50ms card timeout
  A.prepImageOut(cwd);
  fs.writeFileSync(path.join(dir, 'slow.png'), Buffer.from([0x89, 0x50, 0x4e, 0x47, 7, 8, 9]));
  client.__reset(); client.__setBehavior({ uploadDelayMs: 120 });
  n = await A.drainImageOut(CHAT, cwd);
  check('慢图片上传使用资源超时而非卡片短超时', n === 1 && client.__calls.some(c => c.op === 'uploadImage' && c.state === 'settled'), JSON.stringify(client.__calls));

  // 3b) a timed-out upload must preserve the generated file; otherwise the fallback path is a lie
  A.prepImageOut(cwd);
  const retained = path.join(dir, 'retain-on-failure.png');
  fs.writeFileSync(retained, Buffer.from([0x89, 0x50, 0x4e, 0x47, 10, 11, 12]));
  client.__reset(); client.__setBehavior({ uploadDelayMs: 700 });
  n = await A.drainImageOut(CHAT, cwd);
  check('图片上传超时后保留本地文件', n === 0 && fs.existsSync(retained), `sent=${n} exists=${fs.existsSync(retained)}`);
  check('失败提示里的保留路径真实存在', client.__calls.some(c => c.op === 'create' && c.type === 'text' && String(c.text || '').includes(retained)), JSON.stringify(client.__calls));

  // 4) a non-image file is ignored; an empty image is skipped (note, no upload)
  A.prepImageOut(cwd);
  fs.writeFileSync(path.join(dir, 'notes.txt'), 'hello');   // not an image -> ignored entirely
  fs.writeFileSync(path.join(dir, 'empty.png'), Buffer.alloc(0));   // empty -> skipped with a note
  client.__reset();
  n = await A.drainImageOut(CHAT, cwd);
  const uploads3 = client.__calls.filter(c => c.op === 'uploadImage').length;
  const texts3 = client.__calls.filter(c => c.op === 'create' && c.type === 'text');
  check('空图片不上传', n === 0 && uploads3 === 0, 'n=' + n + ' uploads=' + uploads3);
  check('空图片给出文字提示', texts3.length === 1 && /未发送/.test(texts3[0].text || ''), 'texts=' + texts3.length);
  check('非图片文件被忽略(仍留在盘上)', fs.existsSync(path.join(dir, 'notes.txt')));

  // cleanup
  try { A.prepImageOut(cwd); } catch (e) {}
  console.log(failed ? `\nFAILED (${failed})` : '\nALL PASS');
  process.exit(failed ? 1 : 0);
}
main().catch(e => { console.error(e); process.exit(1); });
