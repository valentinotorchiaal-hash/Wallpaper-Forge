const { chromium } = require('playwright');
const http = require('http'), fs = require('fs'), path = require('path');
/*
 * Tests de la interfaz en modo demostración: miniaturas estáticas de GIF y video, reproducción al pasar
 * el mouse, vista previa de video en el panel, "Quitar de la biblioteca" y bloqueo del modo demo.
 * Uso: node tests/runtime/ui-test.js <carpeta con anim.gif y clip.webm>
 *   ffmpeg -f lavfi -i testsrc=size=160x90:rate=10 -t 1 anim.gif
 *   ffmpeg -f lavfi -i testsrc=size=320x180:rate=30 -t 3 -c:v libvpx-vp9 clip.webm
 */
const MEDIA = path.resolve(process.argv[2] || 'media');
const UI = path.resolve(__dirname, '../../src/WallpaperForge/wwwroot/ui/index.html');

const srv = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  const name = (u.searchParams.get('path') || '').split(/[\\/]/).pop();
  const f = path.join(MEDIA, name);
  if (!fs.existsSync(f)) { res.writeHead(404); return res.end(); }
  const type = f.endsWith('.gif') ? 'image/gif' : 'video/webm';
  const len = fs.statSync(f).size;
  const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
  if (m) {
    const a = +m[1], b = m[2] ? +m[2] : len - 1;
    res.writeHead(206, { 'Content-Type': type, 'Access-Control-Allow-Origin': '*', 'Accept-Ranges': 'bytes',
      'Content-Range': 'bytes ' + a + '-' + b + '/' + len, 'Content-Length': b - a + 1 });
    return fs.createReadStream(f, { start: a, end: b }).pipe(res);
  }
  res.writeHead(200, { 'Content-Type': type, 'Access-Control-Allow-Origin': '*', 'Accept-Ranges': 'bytes', 'Content-Length': len });
  fs.createReadStream(f).pipe(res);
});

srv.listen(0, async () => {
  const base = 'http://127.0.0.1:' + srv.address().port + '/media';
  const b = await chromium.launch();
  let ok = true;
  const check = (c, m) => { console.log((c ? 'OK  ' : 'FAIL') + ' ' + m); if (!c) ok = false; };
  const errs = [];

  let p = await b.newPage();
  await p.goto('file://' + UI);
  await p.waitForTimeout(300);
  check((await p.textContent('body')).includes('Abrí Wallpaper Forge desde su ícono'), 'sin ?demo=1 no muestra datos de ejemplo');
  await p.close();

  p = await b.newPage({ viewport: { width: 1280, height: 820 } });
  p.on('pageerror', e => errs.push(e.message));
  const extra = [
    { id: 'g1', title: 'GIF animado', type: 'image', preview: base + '?path=C%3A%5Cverdes%5Canim.gif', tags: ['verdes'], source: 'Local', size: 714, supported: true },
    { id: 'v1', title: 'Video sin preview', type: 'video', preview: '', video: base + '?path=C%3A%5Cverdes%5Cclip.webm', tags: ['verdes'], source: 'Local', size: 42723, supported: true },
  ];
  await p.addInitScript('window.__WF_DEMO_EXTRA = ' + JSON.stringify(extra) + ';');
  await p.goto('file://' + UI + '?demo=1');
  await p.waitForSelector('.card');
  const isOurs = c => /GIF animado|Video sin preview/.test(c.textContent);
  await p.waitForFunction(() => [...document.querySelectorAll('.card')]
    .filter(c => /GIF animado|Video sin preview/.test(c.textContent))
    .every(c => { const i = c.querySelector('.thumb-media img'); return i && i.src.startsWith('data:image/jpeg'); }),
    null, { timeout: 15000 }).catch(() => {});
  const thumbs = await p.$$eval('.card', cs => cs.filter(c => /GIF animado|Video sin preview/.test(c.textContent))
    .map(c => { const i = c.querySelector('.thumb-media img'); return i ? i.src.slice(0, 22) : 'none'; }));
  check(thumbs.length === 2 && thumbs.every(t => t === 'data:image/jpeg;base64'), 'GIF y video muestran un cuadro estático ' + thumbs);
  check(await p.$$eval('.grid img', is => is.filter(i => /\.gif|\.webm/.test(decodeURIComponent(i.src))).length) === 0,
    'sin GIF animados en la grilla sin hover');
  check(await p.$$eval('.grid video', v => v.length) === 0, 'ningún <video> vivo en la grilla');

  let videoCard = null, gifCard = null;
  for (const c of await p.$$('.card')) {
    const t = await c.textContent();
    if (t.includes('Video sin preview')) videoCard = c;
    if (t.includes('GIF animado')) gifCard = c;
  }
  await videoCard.hover(); await p.waitForTimeout(500);
  check(await videoCard.$$eval('video.live', v => v.length) === 1, 'al pasar el mouse el video se reproduce');
  await p.mouse.move(5, 400); await p.waitForTimeout(200);
  check(await videoCard.$$eval('video.live', v => v.length) === 0, 'al salir el video se descarta');
  await gifCard.hover(); await p.waitForTimeout(300);
  check(await gifCard.$$eval('img.live', v => v.length) === 1, 'al pasar el mouse el GIF se anima');
  await p.mouse.move(5, 400); await p.waitForTimeout(200);

  await videoCard.click();
  await p.waitForSelector('.details .btn.danger');
  check(await p.$$eval('.details .big-preview video', v => v.length) === 1, 'el panel muestra el video antes de aplicarlo');
  p.once('dialog', d => d.accept());
  const before = await p.$$eval('.card', c => c.length);
  await p.click('.details .btn.danger'); await p.waitForTimeout(400);
  check(await p.$$eval('.card', c => c.length) === before - 1, 'quitar de la biblioteca');
  // Configuración completa (antes se cortaba si no había wallpapers quitados)
  await p.click('#btnSettings');
  await p.waitForSelector('.folders li', { state: 'attached' });
  check(await p.$$eval('.modal-body h3', h => h.map(x => x.textContent).includes('Diagnóstico')), 'Configuración se muestra completa');
  check(/v\d+\.\d+\.\d+/.test(await p.textContent('#appVersion')), 'la versión se ve en la ventana');
  check(errs.length === 0, 'sin errores JS ' + errs.join(';'));
  await b.close(); srv.close(); process.exit(ok ? 0 : 1);
});
