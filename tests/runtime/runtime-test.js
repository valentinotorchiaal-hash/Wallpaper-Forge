const { chromium } = require('playwright');
const fs = require('fs');
/*
 * Tests del runtime web (shim de compatibilidad, escenas y video) en Chromium.
 * Uso:
 *   WF_EXPORT_SCENE=<carpeta> dotnet test tests/WallpaperForge.Tests   (genera una escena de prueba)
 *   node tests/runtime/runtime-test.js <carpeta>
 * Requiere: npm i playwright (o Playwright global).
 */
const path = require('path');
const SCENE_DIR = path.resolve(process.argv[2] || 'scene-out');
const RT = path.resolve(__dirname, '../../src/WallpaperForge/wwwroot/runtime');
const FIX = path.resolve(__dirname, 'fixtures');
const shim = fs.readFileSync(RT + '/we-shim.js', 'utf8');
function initScript(init) { return 'window.__WF_INIT = ' + JSON.stringify(init) + ';\n' + shim; }
(async () => {
  const b = await chromium.launch({ args: ['--allow-file-access-from-files', '--autoplay-policy=no-user-gesture-required'] });
  let ok = true; const check = (c, m) => { console.log((c ? 'OK  ' : 'FAIL') + ' ' + m); if (!c) ok = false; };

  // 1) Shim de compatibilidad
  let p = await b.newPage();
  const errs = []; p.on('pageerror', e => errs.push(e.message));
  await p.addInitScript(initScript({ user: { speed: { type: 'slider', value: 7 } }, general: { fps: 30 },
    directories: { imgs: { mode: 'fetchall', files: ['C:/a.jpg', 'C:/b.jpg'] } } }));
  await p.goto('file://' + FIX + '/shim/index.html');
  await p.waitForTimeout(200);
  let log = await p.evaluate(() => log);
  check(log.some(l => l[0] === 'general' && l[1].includes('"fps":30')), 'applyGeneralProperties con fps');
  check(log.some(l => l[0] === 'user' && l[1].includes('"value":7')), 'applyUserProperties inicial');
  check(log.some(l => l[0] === 'dir' && l[2] === 2), 'userDirectoryFilesAddedOrChanged');
  check(log.some(l => l[0] === 'random' && /C:\/[ab]\.jpg/.test(l[2])), 'wallpaperRequestRandomFileForProperty');
  await p.evaluate(() => { __wf.applyUser({ speed: { type: 'slider', value: 9 } }); __wf.setPaused(true); });
  log = await p.evaluate(() => log);
  check(log.some(l => l[0] === 'user' && l[1].includes('"value":9')), 'cambio de propiedad en vivo');
  check(log.some(l => l[0] === 'paused' && l[1] === true), 'setPaused(true)');
  check(log.filter(l => l[0] === 'user').length === 2, 'applyUserProperties no se duplica');
  check(errs.length === 0, 'sin errores JS en shim ' + errs.join(';'));
  await p.close();

  // 2) Renderizador de escenas
  p = await b.newPage({ viewport: { width: 960, height: 540 } });
  p.on('pageerror', e => errs.push(e.message));
  await p.addInitScript(initScript({ user: { showlogo: { type: 'bool', value: true } }, playback: { volume: 50 } }));
  const data = 'file://' + SCENE_DIR + '/scene-data.js';
  await p.goto('file://' + RT + '/scene.html?data=' + encodeURIComponent(data));
  await p.waitForTimeout(500);
  const px = (x, y) => p.evaluate(([x, y]) => Array.from(document.getElementById('c').getContext('2d').getImageData(x, y, 1, 1).data), [x, y]);
  let bg = await px(200, 200), logo = await px(1060, 490);
  check(bg[1] === 200 && bg[0] === 0, 'capa de fondo dibujada ' + bg);
  check(Math.abs(logo[0] - 128) <= 1 && logo[1] === 200, 'capa hija (alpha 0.5) con mezcla aditiva, posición y padre ' + logo);
  // 64x32 escalado x2 y rotado 90° → 64 de ancho x 128 de alto alrededor de (1060,490)
  let inside = await px(1060, 490 + 60), outside = await px(1060 + 40, 490);
  check(inside[0] > 100 && outside[0] === 0, 'rotación de 90° aplicada ' + inside + ' / ' + outside);
    await p.evaluate(() => __wf.applyUser({ showlogo: { type: 'bool', value: false } }));
  await p.waitForTimeout(100);
  logo = await px(1060, 490);
  check(logo[0] === 0, 'visibilidad ligada a propiedad del usuario ' + logo);
  check(errs.length === 0, 'sin errores JS en escena ' + errs.join(';'));

  // 3) Video: la página carga y aplica escalado
  p = await b.newPage();
  await p.addInitScript(initScript({ playback: { volume: 30, rate: 1.5, fit: 'contain' } }));
  await p.goto('file://' + RT + '/video.html?src=' + encodeURIComponent('file:///no-existe.mp4'));
  await p.waitForTimeout(300);
  const v = await p.evaluate(() => { const v = document.getElementById('v'); return [v.style.objectFit, v.playbackRate, v.volume, document.getElementById('err').style.display]; });
  check(v[0] === 'contain' && v[1] === 1.5 && Math.abs(v[2] - 0.3) < 1e-6, 'opciones de reproducción ' + v);
  check(v[3] === 'block', 'muestra error si el video no existe');
  // 4) Imagen / GIF: escalado
  p = await b.newPage();
  await p.addInitScript(initScript({ playback: { fit: 'contain' } }));
  await p.goto('file://' + RT + '/image.html?src=' + encodeURIComponent('data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw=='));
  await p.waitForTimeout(200);
  const img = await p.evaluate(() => { const i = document.getElementById('i'); return [i.style.objectFit, i.complete && i.naturalWidth]; });
  check(img[0] === 'contain' && img[1] === 1, 'imagen cargada con escalado ' + img);
  await p.evaluate(() => __wf.applyPlayback({ fit: 'stretch' }));
  check(await p.evaluate(() => document.getElementById('i').style.objectFit) === 'fill', 'cambio de escalado en vivo');
  await b.close();
  process.exit(ok ? 0 : 1);
})();
