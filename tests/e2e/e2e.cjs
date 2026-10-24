const { chromium } = require('playwright');
const { spawn } = require('child_process'); const fs = require('fs'); const os = require('os'); const path = require('path');
const S = __dirname; const WWW = path.resolve(__dirname, '../../VoiceForgeAIO/wwwroot');
const HOME = fs.mkdtempSync(path.join(os.tmpdir(), 'vf-home-')); fs.mkdirSync(HOME + '/.config', { recursive: true }); const OUT = fs.mkdtempSync(path.join(os.tmpdir(), 'vf-out-'));
let host, port = 5811;
const findings = [];
const note = (id, ok, msg) => { findings.push({ id, ok, msg }); console.log((ok ? 'PASS ' : 'BUG  ') + id + ' - ' + msg); };
function startHost() { return new Promise(r => { host = spawn('dotnet', [S + '/out/E2E.Harness.dll', String(port), WWW, S + '/fake_bridge.py'], { env: { ...process.env, HOME, XDG_CONFIG_HOME: HOME + '/.config' } }); host.stdout.on('data', d => { if (String(d).includes('READY')) r(); }); host.stderr.on('data', d => process.stderr.write('[host] ' + d)); }); }
const stopHost = () => new Promise(r => { host.on('exit', r); host.kill(); });
const INIT = () => { const ls = []; window.external = { sendMessage: raw => fetch('/rpc', { method: 'POST', body: raw }), receiveMessage: f => ls.push(f) }; (async function loop() { for (;;) { try { const a = await (await fetch('/poll')).json(); for (const m of a) ls.forEach(f => f(m)); } catch { await new Promise(r => setTimeout(r, 200)); } } })(); window.__dialogs = []; };
async function open(browser) { const page = await browser.newPage(); page.errs = []; page.on('pageerror', e => page.errs.push(e.message)); page.on('dialog', d => { page.lastDialog = d.message(); d.accept(); }); await page.route(/fonts\.(googleapis|gstatic)/, r => r.abort()); await page.addInitScript(INIT); await page.goto(`http://localhost:${port}/`); await page.waitForSelector('#voice-map-body', { state: 'attached' }); await page.waitForTimeout(600); if (await page.isVisible('#splash-overlay')) await page.click('#splash-continue-btn'); return page; }
const dialog = (v) => fetch(`http://localhost:${port}/test/dialog?v=${encodeURIComponent(v)}`);
const logText = (p) => p.$$eval('#log-output .log-line', l => l.map(x => x.textContent));
const rows = (p) => p.$$eval('#voice-map-body tr', trs => trs.map(tr => ({ type: tr.querySelector('input').value, provider: tr.querySelectorAll('select')[0].value, voice: tr.querySelectorAll('select')[2].value, voiceLabels: [...tr.querySelectorAll('select')[2].options].map(o => o.textContent) })));
async function addRow(p, type, provider, voiceIdx) { await p.click('#add-row-btn'); const tr = (await p.$$('#voice-map-body tr')).pop(); await (await tr.$('input')).fill(type); if (provider) await (await tr.$$('select'))[0].selectOption(provider); if (voiceIdx !== undefined) { const sel = (await tr.$$('select'))[2]; await sel.selectOption(voiceIdx); } }
module.exports = { chromium, startHost, stopHost, open, dialog, logText, rows, addRow, note, findings, OUT, HOME, port: () => port };
