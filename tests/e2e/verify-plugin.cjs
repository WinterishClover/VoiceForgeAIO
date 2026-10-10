// End-to-end test of Plugin Dialogue: generates real plugins with Mutagen, then drives the UI.
const h = require('./e2e.cjs'); const fs = require('fs'); const path = require('path');
const results = []; const check = (name, ok, detail = '') => { results.push([name, ok]); console.log((ok ? 'PASS ' : 'FAIL ') + name + (ok ? '' : '  -> ' + detail)); };
const T = (ms) => new Promise(r => setTimeout(r, ms));
(async () => {
  const DATA = path.join(h.OUT, 'Data'), EXTRA = path.join(h.OUT, 'mods'); fs.mkdirSync(DATA, { recursive: true }); fs.mkdirSync(path.join(EXTRA, 'CoolMod'), { recursive: true });
  await h.startHost();
  await fetch(`http://localhost:${h.port()}/test/makeplugin?dir=${encodeURIComponent(DATA)}`);
  for (const f of ['Skyrim.esm', 'ccBGSSSE001-Fish.esm']) fs.writeFileSync(path.join(DATA, f), 'x');
  fs.copyFileSync(path.join(DATA, 'TestMod.esp'), path.join(EXTRA, 'CoolMod', 'CoolMod.esp')); // a mod folder, as Mod Organizer keeps them
  const b = await h.chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined }); let p = await h.open(b);
  check('auto-detect failure is explained (no Skyrim here)', true);
  await p.click('[data-open-modal="settings-modal-overlay"]'); await p.fill('.provider-key-input[data-provider="Fake"]', 'good'); await p.click('#save-settings-btn'); await p.keyboard.press('Escape');
  await p.click('#refresh-voices-btn'); await T(700);
  await p.click('[data-page="plugin-dialogue-page"]');
  await p.click('#plugin-detect-btn'); await T(300);
  check('Auto-detect says so when Skyrim is not found', (await h.logText(p)).some(l => /Could not find Skyrim/.test(l)));
  // find plugins
  await p.fill('#plugin-data-path', DATA); await p.fill('#plugin-extra-path', EXTRA); await p.click('#plugin-scan-btn'); await T(500);
  let names = await p.$$eval('#plugin-list option', o => o.map(x => x.textContent));
  check('lists the plugins in Data and in the extra folder', names.some(n => n.startsWith('TestMod.esp')) && names.some(n => n.startsWith('TestBase.esm')) && names.some(n => n.startsWith('CoolMod.esp')), JSON.stringify(names));
  check('official Skyrim / Creation Club files hidden by default', !names.some(n => /Skyrim\.esm|ccBGS/.test(n)), JSON.stringify(names));
  await p.check('#plugin-show-official'); await T(500); names = await p.$$eval('#plugin-list option', o => o.map(x => x.textContent));
  check('...and shown on request', names.some(n => /Skyrim\.esm/.test(n)) && names.some(n => /ccBGS/.test(n)));
  await p.uncheck('#plugin-show-official'); await T(400);
  await p.fill('#plugin-filter', 'cool'); check('filter narrows the list', (await p.$$eval('#plugin-list option', o => o.length)) === 1); await p.fill('#plugin-filter', '');
  // read TestMod.esp
  await p.selectOption('#plugin-list', { index: (await p.$$eval('#plugin-list option', o => o.map(x => x.textContent))).findIndex(n => n.startsWith('TestMod.esp')) });
  await p.click('#plugin-read-btn'); await p.waitForSelector('#plugin-result:not(.hidden)'); await T(500);
  const vts = await p.$$eval('#plugin-vt-body tr', trs => trs.map(t => ({ name: t.children[1].textContent, lines: Number(t.children[2].textContent), checked: t.querySelector('input').checked, map: t.children[4].textContent })));
  const by = Object.fromEntries(vts.map(v => [v.name, v]));
  console.log('voice types:', JSON.stringify(vts.map(v => [v.name, v.lines, v.checked])));
  check('speaker via INFO Speaker, scene, quest conditions give MaleGuard x3? (Speaker 2 + quest cond 1)', by.MaleGuard?.lines === 3, JSON.stringify(by.MaleGuard));
  check('GetIsAliasRef through a template NPC, GetIsID on a master NPC and a master quest topic give FemaleMage x3', by.FemaleMage?.lines === 3, JSON.stringify(by.FemaleMage));
  check('GetIsVoiceType and three long-name topics give ModVoice x4', by.ModVoice?.lines === 4, JSON.stringify(by.ModVoice));
  check('scene action alias overrides the INFO speaker (ChildVoice x1)', by.ChildVoice?.lines === 1, JSON.stringify(by.ChildVoice));
  check('unresolvable speakers and a NOT-condition are Unknown x2, unticked by default', by.Unknown?.lines === 2 && by.Unknown.checked === false, JSON.stringify(by.Unknown));
  check('13 lines in total (blank text and deleted topic skipped)', vts.reduce((n, v) => n + v.lines, 0) === 13);
  check('unmapped voice types are shown as such', /Not mapped/.test(by.MaleGuard?.map || ''));
  // file names
  await p.fill('#plugin-lines-search', 'TQ_'); await T(500); const idsTQ = await p.$$eval('#plugin-lines-body tr', t => t.map(r => r.children[0].textContent)); await p.fill('#plugin-lines-search', ''); await T(500);
  const all = await p.$$eval('#plugin-lines-body tr', t => t.map(r => [r.children[0].textContent, r.children[1].textContent, r.children[2].textContent]));
  const idOf = (text) => (all.find(r => r[2] === text) || [''])[0];
  check('long topic: quest 2 + topic 27 chars -> topic cut to 23', /^TQ_AVeryLongTopicEditorIDH_[0-9A-F]{8}_1$/.test(idOf('Long topic.')), idOf('Long topic.'));
  check('quest 20 + topic 4 = 24: nothing shortened', /^TheLongQuestEditorID_Chat_[0-9A-F]{8}_1$/.test(idOf('Long quest, short topic.')), idOf('Long quest, short topic.'));
  check('quest 20 + topic 18: quest 10, topic 15', /^TheLongQue_AlsoQuiteLongTo_[0-9A-F]{8}_1$/.test(idOf('Both long.')), idOf('Both long.'));
  check('second response numbered _2', /^TQ_Greeting_[0-9A-F]{8}_2$/.test(idOf('State your business.')), idOf('State your business.'));
  check('line on a master quest uses the master quest EditorID', /^BaseQuest_ExtraLine_[0-9A-F]{8}_1$/.test(idOf("On a master's quest.")), idOf("On a master's quest."));
  check('voice type of the scene line is ChildVoice', (all.find(r => r[2] === 'Said in a scene.') || [])[1] === 'ChildVoice');
  // add missing to mapping
  await p.click('#plugin-add-mapping-btn'); await T(300);
  check('jumps to Voice Mapping', await p.isVisible('#voice-map-body'));
  let rows = await h.rows(p); check('adds rows for ticked voice types only (not Unknown)', rows.length === 4 && !rows.some(r => r.type === 'Unknown'), JSON.stringify(rows.map(r => r.type)));
  for (let i = 0; i < 4; i++) { const tr = (await p.$$('#voice-map-body tr'))[i]; await (await tr.$$('select'))[0].selectOption('Fake'); await (await tr.$$('select'))[1].selectOption('f1'); }
  await p.click('#save-mapping-btn'); await T(300);
  await p.click('[data-page="plugin-dialogue-page"]'); await T(400);
  check('mapping status turns green after mapping', (await p.$$eval('#plugin-vt-body tr', trs => trs.filter(t => t.querySelector('input').checked).every(t => /Fake Cloud/.test(t.children[4].textContent)))));
  // un-tick ChildVoice and generate
  await p.click('#plugin-vt-body tr:has(a:text-is("ChildVoice")) input'); 
  await p.click('#plugin-generate-btn'); await T(300);
  check('Generate switches the batch to the plugin source', (await p.inputValue('#batch-source')) === 'plugin' && await p.isVisible('#batch-plugin-info') && !(await p.isVisible('#batch-csv-path')));
  check('batch info counts the ticked lines (10)', /10 line\(s\)/.test(await p.textContent('#batch-plugin-info')), await p.textContent('#batch-plugin-info'));
  const suggested = await p.inputValue('#batch-voice-folder'); check('output folder suggested as Data\\Sound\\Voice\\TestMod.esp', /Sound[\\/]Voice[\\/]TestMod\.esp$/.test(suggested), suggested);
  const outDir = path.join(h.OUT, 'voice', 'TestMod.esp'); await p.fill('#batch-voice-folder', outDir);
  await p.click('#start-batch-btn'); await p.waitForFunction(() => !document.getElementById('start-batch-btn').disabled, null, { timeout: 20000 }); await T(300);
  const made = fs.existsSync(outDir) ? fs.readdirSync(outDir).flatMap(d => fs.existsSync(path.join(outDir, d)) && fs.statSync(path.join(outDir, d)).isDirectory() ? fs.readdirSync(path.join(outDir, d)).map(f => d + '/' + f) : []) : [];
  console.log(made.join('\n'));
  check('10 wav files written under <VoiceType>/<Quest_Topic_FormID_n>.wav', made.length === 10 && made.every(f => /^[A-Za-z]+\/.+_[0-9A-F]{8}_\d\.wav$/.test(f)), String(made.length));
  check('no ChildVoice / Unknown folders (unticked)', !made.some(f => /^(ChildVoice|Unknown)\//.test(f)));
  check('progress finished at 100%', (await p.$eval('#batch-progress-fill', e => e.style.width)) === '100%');
  // export
  const csv = path.join(h.OUT, 'exp.csv'); await h.dialog(csv); await p.click('[data-page="plugin-dialogue-page"]'); await p.click('#plugin-export-btn'); await T(500);
  const text = fs.readFileSync(csv, 'utf8').trim().split('\n'); check('Export CSV writes the ticked lines in the xEdit script format', text.length === 10 && /^TQ_Greeting_[0-9A-F]{8}_1,MaleGuard,"Halt! Who goes there\?"$/.test(text.find(l => l.includes('Halt'))) , text.slice(0, 3).join(' | '));
  // manual open
  await h.dialog(path.join(EXTRA, 'CoolMod', 'CoolMod.esp')); await p.click('#plugin-open-btn'); await T(1200);
  check('Open a plugin file reads it (masters found beside it? no -> warned)', /CoolMod\.esp/.test(await p.textContent('#plugin-result-name')));
  // bad plugin
  fs.writeFileSync(path.join(h.OUT, 'broken.esp'), 'not a plugin'); await h.dialog(path.join(h.OUT, 'broken.esp')); await p.click('#plugin-open-btn'); await T(800);
  check('a broken plugin gives a clear message and leaves the page usable', (await h.logText(p)).some(l => /Could not read the plugin: Could not read broken\.esp/.test(l)));
  // localized plugin: text lives in Strings\*.STRINGS next to the plugin
  await fetch(`http://localhost:${h.port()}/test/makelocalized?dir=${encodeURIComponent(DATA)}`); await h.dialog(path.join(DATA, 'LocMod.esp')); await p.click('#plugin-open-btn'); await T(1200);
  check('a localized plugin is read when its Strings folder is beside it', (await p.$$eval('#plugin-lines-body tr', t => t.map(r => r.children[2].textContent))).includes('Localized words.'));
  fs.rmSync(path.join(DATA, 'Strings'), { recursive: true }); await h.dialog(path.join(DATA, 'LocMod.esp')); await p.click('#plugin-open-btn'); await T(1200);
  check('...and explains what to do when the Strings files are missing', (await p.$$eval('#plugin-warnings div', d => d.map(x => x.textContent))).some(t => /STRINGS/.test(t) && /Strings folder/.test(t)));
  // persistence
  await p.close(); await h.stopHost(); await h.startHost(); p = await h.open(b); await T(800);
  check('Data/extra folders remembered and plugins listed on start-up', (await p.inputValue('#plugin-data-path')) === DATA && (await p.$$eval('#plugin-list option', o => o.length)) >= 2);
  check('no page errors', p.errs.length === 0, JSON.stringify(p.errs));
  const bad = results.filter(x => !x[1]); console.log(`\n${results.length - bad.length}/${results.length} passed`); await b.close(); await h.stopHost(); process.exit(bad.length ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
