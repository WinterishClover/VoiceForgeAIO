// Screenshots every page with realistic data (a generated plugin, mapped voices, local voices). Usage: node shots.cjs <outdir> [width height]
const h = require('./e2e.cjs'); const fs = require('fs'); const path = require('path');
const T = (ms) => new Promise(r => setTimeout(r, ms));
(async () => {
  const outDir = process.argv[2]; fs.mkdirSync(outDir, { recursive: true }); const W = Number(process.argv[3] || 1440), H = Number(process.argv[4] || 900);
  const DATA = path.join(h.OUT, 'Data'); fs.mkdirSync(DATA, { recursive: true }); await h.startHost(); await fetch(`http://localhost:${h.port()}/test/makeplugin?dir=${encodeURIComponent(DATA)}`);
  const b = await h.chromium.launch({ executablePath: process.env.CHROMIUM_PATH }); const p = await b.newPage({ viewport: { width: W, height: H } });
  p.on('pageerror', e => console.log('PAGE ERROR', e.message)); p.on('dialog', d => d.accept());
  await p.route(/fonts\.(googleapis|gstatic)/, r => r.abort()); await p.addInitScript(() => { const ls = []; window.external = { sendMessage: raw => fetch('/rpc', { method: 'POST', body: raw }), receiveMessage: f => ls.push(f) }; (async function loop() { for (;;) { try { const a = await (await fetch('/poll')).json(); for (const m of a) ls.forEach(f => f(m)); } catch { await new Promise(r => setTimeout(r, 200)); } } })(); });
  await p.goto(`http://localhost:${h.port()}/`); await T(700);
  const shot = async (n) => { await T(250); await p.screenshot({ path: path.join(outDir, n + '.png') }); };
  await shot('00-splash');
  if (await p.isVisible('#splash-overlay')) await p.click('#splash-continue-btn');
  await p.click('[data-page="settings-page"]'); await shot('01-settings');
  await p.evaluate(() => { document.querySelector('.provider-key-input[data-provider="Fake"]') && 0; });
  await p.click('[data-page="plugin-dialogue-page"]'); await p.fill('#plugin-data-path', DATA); await p.click('#plugin-scan-btn'); await T(500);
  await p.selectOption('#plugin-list', { index: (await p.$$eval('#plugin-list option', o => o.map(x => x.textContent))).findIndex(n => n.startsWith('TestMod.esp')) }); await shot('02-plugin-list');
  await p.click('#plugin-read-btn'); await p.waitForSelector('#plugin-result:not(.hidden)'); await T(400); await shot('03-plugin-read');
  await p.evaluate(() => document.getElementById('content-area').scrollTo(0, 99999)); await shot('04-plugin-lines');
  await p.click('#plugin-add-mapping-btn'); await T(400); await shot('05-mapping-empty-voices');
  await p.click('[data-page="single-line-page"]'); await shot('06-single');
  await p.click('[data-page="batch-page"]'); await shot('07-batch-csv');
  await p.selectOption('#batch-source', 'plugin').catch(() => {}); await shot('08-batch-plugin');
  await p.click('[data-page="local-voices-page"]'); await p.click('#omniSection > summary'); await T(300); await shot('09-local');
  await p.click('[data-open-modal="help-modal-overlay"]').catch(() => {}); await shot('10-help'); await p.keyboard.press('Escape');
  await p.click('[data-open-modal="about-modal-overlay"]').catch(() => {}); await shot('11-about'); await p.keyboard.press('Escape');
  await b.close(); await h.stopHost();
})().catch(e => { console.error(e); process.exit(1); });
