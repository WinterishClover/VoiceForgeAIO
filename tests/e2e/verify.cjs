const h = require('./e2e.cjs'); const fs = require('fs'); const path = require('path'); const assert = require('assert');
const results = []; const check = (name, ok, detail = '') => { results.push([name, ok]); console.log((ok ? 'PASS ' : 'FAIL ') + name + (ok ? '' : '  -> ' + detail)); };
const T = (ms) => new Promise(r => setTimeout(r, ms));
const csvRun = async (p, name, content, conc) => { const csv = path.join(h.OUT, name + '.csv'); fs.writeFileSync(csv, content); await p.click('[data-page="batch-page"]'); await p.fill('#batch-csv-path', csv); await p.fill('#batch-voice-folder', path.join(h.OUT, 'v_' + name)); if (conc) await p.fill('#batch-concurrency', String(conc)); await p.click('#start-batch-btn'); await p.waitForFunction(() => !document.getElementById('start-batch-btn').disabled, null, { timeout: 20000 }); await T(200); return h.logText(p); };
(async () => {
  await h.startHost(); const b = await h.chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined }); let p = await h.open(b);
  await p.click('[data-open-modal="settings-modal-overlay"]'); await p.fill('.provider-key-input[data-provider="Fake"]', 'good'); await p.click('#save-settings-btn'); await p.keyboard.press('Escape');
  await p.click('#refresh-voices-btn'); await p.waitForTimeout(800);
  const mapFile = () => JSON.parse(fs.readFileSync(h.HOME + '/.config/VoiceForgeAIO/voice_map.json', 'utf8'));

  // V1 trim (typed with trailing space, saved without leaving the field)
  await p.click('#add-row-btn'); await p.keyboard.type('Eldis '); // focus is on the new row's input
  let r = await h.rows(p); check('add-row focuses the new VoiceType box', r.length === 1 && r[0].type === 'Eldis ');
  const tr0 = (await p.$$('#voice-map-body tr'))[0]; await (await tr0.$$('select'))[0].selectOption('Fake'); await (await tr0.$$('select'))[1].selectOption('f2');
  check('Save button shows unsaved marker', (await p.textContent('#save-mapping-btn')).includes('*'));
  await p.click('#save-mapping-btn'); await p.waitForTimeout(400);
  check('VoiceType trimmed on save', mapFile()[0].VoiceType === 'Eldis', JSON.stringify(mapFile()));
  check('marker cleared after save', !(await p.textContent('#save-mapping-btn')).includes('*'));
  // V2 duplicates
  await h.addRow(p, 'eldis', 'Fake', 'f1'); await p.click('#save-mapping-btn'); await p.waitForTimeout(300);
  let log = await h.logText(p); check('case-variant duplicate blocks save with a message', log.some(l => /Not saved.*more than once.*Eldis/i.test(l)) && mapFile().length === 1, log.slice(-1)[0]);
  // V3 missing voice warning
  await p.click('#voice-map-body tr:nth-child(2) input', { clickCount: 3 }); await p.keyboard.type('Beta'); await p.keyboard.press('Tab');
  const t2 = (await p.$$('#voice-map-body tr'))[1]; await (await t2.$$('select'))[1].selectOption(''); 
  check('row without a voice is marked', await (await t2.$$('select'))[1].evaluate(e => e.classList.contains('warn')));
  await p.click('#save-mapping-btn'); await p.waitForTimeout(300); log = await h.logText(p);
  check('save warns about the row without a voice', log.some(l => /'Beta' has no voice/.test(l)), log.slice(-3).join('|'));
  // V4 selection
  await h.addRow(p, 'Gamma', 'Fake', 'f1'); await h.addRow(p, 'Delta', 'Fake', 'f1');
  await p.click('#voice-map-body tr:nth-child(1) td:nth-child(2)', { position: { x: 1, y: 1 } });
  check('clicking a cell selects the row', (await p.$$eval('#voice-map-body tr.selected', e => e.length)) === 1);
  await p.click('#voice-map-body tr:nth-child(3) td:nth-child(2)', { position: { x: 1, y: 1 }, modifiers: ['Shift'] });
  check('shift-click selects a range', (await p.$$eval('#voice-map-body tr.selected', e => e.length)) === 3);
  await p.click('#voice-map-body tr:nth-child(2) td:nth-child(2)', { position: { x: 1, y: 1 }, modifiers: ['Control'] });
  check('ctrl-click toggles one row', (await p.$$eval('#voice-map-body tr.selected', e => e.length)) === 2);
  await p.click('#remove-row-btn'); r = await h.rows(p); check('Remove Selected removes all selected', r.length === 2 && r[0].type === 'Beta' && r[1].type === 'Delta', JSON.stringify(r.map(x => x.type)));
  await p.click('#remove-row-btn'); await p.waitForTimeout(100); log = await h.logText(p); // selection is gone now
  check('Remove Selected with nothing selected explains itself', log.some(l => /Select one or more rows first/.test(l)));
  // reset table to a known good state
  await p.click('#voice-map-body tr:nth-child(1) td:nth-child(2)', { position: { x: 1, y: 1 } }); await p.click('#voice-map-body tr:nth-child(2) td:nth-child(2)', { position: { x: 1, y: 1 }, modifiers: ['Shift'] }); await p.click('#remove-row-btn');
  await h.addRow(p, 'Kaylin', 'Fake', 'f1'); await p.click('#save-mapping-btn'); await p.waitForTimeout(300);
  // V5 import: legacy + lowercase provider + unknown provider
  const imp = path.join(h.OUT, 'imp.csv'); fs.writeFileSync(imp, 'VoiceType,VoiceId,VoiceDisplay\r\nLegacyKnown,f2,Gruff\r\nLegacyUnknown,zzz,Nobody\r\n');
  await h.dialog(imp); await p.click('#import-csv-btn'); await p.waitForTimeout(700); r = await h.rows(p);
  const lk = r.find(x => x.type === 'LegacyKnown'), lu = r.find(x => x.type === 'LegacyUnknown');
  check('legacy 3-column import: provider found from the voice id', lk && lk.provider === 'Fake' && lk.voice === 'f2', JSON.stringify(lk));
  check('legacy import with unknown voice is flagged, not corrupted', lu && lu.provider === '' && lu.voice === 'zzz', JSON.stringify(lu));
  log = await h.logText(p); check('legacy import explains what to do', log.some(l => /older format/.test(l)));
  const imp2 = path.join(h.OUT, 'imp2.csv'); fs.writeFileSync(imp2, '﻿VoiceType,ProviderId,VoiceId,VoiceDisplay\nLower,fake,f1,Fiona\nBogus,nope,x,y\n');
  await h.dialog(imp2); await p.click('#import-csv-btn'); await p.waitForTimeout(700); r = await h.rows(p);
  check('lowercase provider id is normalised', r.find(x => x.type === 'Lower')?.provider === 'Fake');
  check('unknown provider is flagged in the table', await (await (await p.$$('#voice-map-body tr')).find(async () => true)).evaluate(() => true) && (await p.$$eval('#voice-map-body tr', trs => trs.find(t => t.querySelector('input').value === 'Bogus').querySelector('select').classList.contains('warn'))));
  // remove import junk
  for (const name of ['LegacyKnown', 'LegacyUnknown', 'Lower', 'Bogus']) { const idx = (await h.rows(p)).findIndex(x => x.type === name); await p.click(`#voice-map-body tr:nth-child(${idx + 1}) td:nth-child(2)`, { position: { x: 1, y: 1 } }); await p.click('#remove-row-btn'); }
  await p.click('#save-mapping-btn'); await p.waitForTimeout(300);
  // V6 failed refresh keeps lists
  await fetch(`http://localhost:${h.port()}/test/fake?voicesFail=1`); await p.click('#refresh-voices-btn'); await p.waitForTimeout(800);
  const opts = await p.$$eval('#voice-map-body tr:nth-child(1) td:nth-child(3) option', o => o.map(x => x.textContent));
  check('failed refresh keeps the previous voice list', opts.length === 2, JSON.stringify(opts));
  await fetch(`http://localhost:${h.port()}/test/fake?voicesFail=0`);
  // V8 batch behaviours
  log = await csvRun(p, 'multi', 'H1,Kaylin,"first line\nsecond line"\nH2,Kaylin,"ok"\nN1,Nobody,"unmapped"\nH2,Kaylin,"dup id"\n');
  check('quoted multi-line text is read', fs.existsSync(path.join(h.OUT, 'v_multi/Kaylin/H1.wav')), log.slice(-5).join('|'));
  check('duplicate EditorID is reported, not silently overwritten', fs.readFileSync(path.join(h.OUT, 'v_multi/_failures.csv'), 'utf8').includes("Duplicate EditorID 'H2'"));
  const w = await p.$eval('#batch-progress-fill', e => e.style.width); check('progress bar reaches 100%', w === '100%', w + ' ' + await p.textContent('#batch-progress-label'));
  log = await csvRun(p, 'hdr', 'EditorID,VoiceType,Text\nI1,Kaylin,"ok"\n'); check('header row is skipped', log.some(l => /Batch finished. Success: 1, Failed: 0/.test(l)), log.slice(-3).join('|'));
  log = await csvRun(p, 'allfail', 'J1,Nobody,"x"\nJ2,Nobody,"y"\n'); check('failure log written even when no voice folder exists yet', fs.existsSync(path.join(h.OUT, 'v_allfail/_failures.csv')), log.slice(-3).join('|'));
  fs.writeFileSync(path.join(h.OUT, 'allfail.csv'), 'J1,Kaylin,"x"\n'); await p.click('#start-batch-btn'); await p.waitForFunction(() => !document.getElementById('start-batch-btn').disabled); await T(200);
  check('stale _failures.csv removed after a clean run', !fs.existsSync(path.join(h.OUT, 'v_allfail/_failures.csv')));
  // stop
  fs.writeFileSync(path.join(h.OUT, 'slow.csv'), [1, 2, 3, 4, 5, 6].map(i => `S${i},Kaylin,"SLOW ${i}"`).join('\n')); await p.fill('#batch-csv-path', path.join(h.OUT, 'slow.csv')); await p.fill('#batch-voice-folder', path.join(h.OUT, 'v_slow')); await p.fill('#batch-concurrency', '1');
  await p.click('#start-batch-btn'); await T(2000); await p.click('#stop-batch-btn'); await p.waitForFunction(() => !document.getElementById('start-batch-btn').disabled); await T(200);
  log = await h.logText(p); const stopLine = log.find(l => /stopped by user/.test(l)); const made = (stopLine || '').match(/Made: (\d+)/);
  check('Stop reports the real count', !!made && Number(made[1]) < 6 && Number(made[1]) === fs.readdirSync(path.join(h.OUT, 'v_slow/Kaylin')).length, stopLine);
  // V9/V10/V11 local voices + single line
  await p.click('[data-page="local-voices-page"]'); await p.click('#qwenSection > summary'); await p.fill('#qwenPython', '/usr/bin/python3'); await T(800);
  check('opening a card does not wipe typed text', (await p.inputValue('#qwenPython')) === '/usr/bin/python3');
  await p.click('#qwenSaveButton'); await T(500); await p.fill('#qwenVoiceName', 'Sage'); await p.fill('#qwenVoiceInstruct', 'wise'); await p.click('#qwenVoiceSaveButton'); await T(500);
  await p.click('[data-page="voice-mapping-page"]'); await h.addRow(p, 'Sagey', 'Qwen3TTS'); const lastRow = (await p.$$('#voice-map-body tr')).pop(); const sel = (await lastRow.$$('select'))[1]; const qid = await sel.$$eval('option', o => o.map(x => x.value).filter(Boolean)[0]); await sel.selectOption(qid);
  check('local voice shown by name, not raw id', (await sel.$$eval('option', o => o.map(x => x.textContent))).includes('Sage'), JSON.stringify(await sel.$$eval('option', o => o.map(x => x.textContent))));
  await p.click('#save-mapping-btn'); await T(300);
  await p.click('[data-page="local-voices-page"]'); await p.click('#qwenVoiceDeleteButton'); await T(500); await p.click('[data-page="voice-mapping-page"]');
  const sag = (await p.$$eval('#voice-map-body tr', trs => trs.map(t => ({ n: t.querySelector('input').value, w: t.querySelectorAll('select')[1].classList.contains('warn'), t: t.querySelectorAll('select')[1].selectedOptions[0]?.textContent })))).find(x => x.n === 'Sagey');
  check('deleted local voice is flagged on the row', sag.w && /not found/.test(sag.t), JSON.stringify(sag));
  await p.click('[data-page="single-line-page"]'); await p.selectOption('#single-provider', 'Fake'); await p.selectOption('#single-model', 'fake-2');
  await p.click('[data-page="local-voices-page"]'); await p.click('#qwenNewButton'); await p.fill('#qwenVoiceName', 'Tmp'); await p.fill('#qwenVoiceInstruct', 'x'); await p.click('#qwenVoiceSaveButton'); await T(500);
  await p.click('[data-page="single-line-page"]'); check('Single-Line model survives a voice list change', (await p.inputValue('#single-model')) === 'fake-2');
  // V7 restart: remembered cloud voices
  await p.close(); await h.stopHost(); await h.startHost(); p = await h.open(b);
  r = await h.rows(p); const kay = r.find(x => x.type === 'Kaylin');
  check('after restart the voice list is already there', kay && kay.voiceLabels.length === 2, JSON.stringify(kay));
  check('badge says the lists are remembered', /remembered/.test(await p.textContent('#voice-count-badge')), await p.textContent('#voice-count-badge'));
  check('no page errors', p.errs.length === 0, JSON.stringify(p.errs));
  const bad = results.filter(x => !x[1]); console.log(`\n${results.length - bad.length}/${results.length} passed`); await b.close(); await h.stopHost(); process.exit(bad.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
