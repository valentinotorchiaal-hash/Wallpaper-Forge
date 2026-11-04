/*
 * Puente con la app de escritorio (WebView2 postMessage).
 * Fuera de WebView2 (abriendo index.html en un navegador) usa datos de ejemplo,
 * para poder diseñar y probar la interfaz sin Windows.
 */
(function () {
  'use strict';
  var pending = new Map();
  var nextId = 1;
  var handlers = {};
  var host = window.chrome && window.chrome.webview;

  function on(event, fn) { (handlers[event] = handlers[event] || []).push(fn); }
  function emit(event, data) { (handlers[event] || []).forEach(function (fn) { fn(data); }); }

  function call(method, params) {
    if (!host) return Mock.call(method, params || {});
    var id = nextId++;
    return new Promise(function (resolve, reject) {
      pending.set(id, { resolve: resolve, reject: reject });
      host.postMessage({ id: id, method: method, params: params || {} });
    });
  }

  if (host) {
    host.addEventListener('message', function (e) {
      var msg = e.data;
      if (msg && msg.event) return emit(msg.event, msg.data);
      var p = msg && pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error)); else p.resolve(msg.result);
    });
  }

  // ---------------- Modo demostración ----------------
  var Mock = (function () {
    function svg(c1, c2, label) {
      var s = '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">' +
        '<stop offset="0" stop-color="' + c1 + '"/><stop offset="1" stop-color="' + c2 + '"/></linearGradient></defs>' +
        '<rect width="640" height="360" fill="url(#g)"/><text x="32" y="330" font-family="sans-serif" font-size="28" fill="rgba(255,255,255,.75)">' + label + '</text></svg>';
      return 'data:image/svg+xml,' + encodeURIComponent(s);
    }
    var lib = [
      { id: 'a1', title: 'Ciudad de neón', type: 'video', preview: svg('#ff2a6d', '#05d9e8', 'video'), tags: ['Sci-Fi', 'Ciudad'], source: 'Local', hasProperties: false, size: 48e6, supported: true },
      { id: 'a2', title: 'Reloj minimalista', type: 'web', preview: svg('#141e30', '#243b55', 'web'), tags: ['Reloj', 'Minimalista'], source: 'Local', hasProperties: true, size: 2e5, supported: true },
      { id: 'a3', title: 'Bosque al atardecer', type: 'scene', preview: svg('#f12711', '#f5af19', 'escena'), tags: ['Naturaleza'], source: 'SteamWorkshop', hasProperties: true, size: 32e6, supported: true },
      { id: 'a4', title: 'Lluvia en la ventana', type: 'video', preview: svg('#0f2027', '#2c5364', 'video'), tags: ['Relajante'], source: 'SteamWorkshop', hasProperties: false, size: 120e6, supported: true },
      { id: 'a5', title: 'Ondas de audio', type: 'web', preview: svg('#8e2de2', '#4a00e0', 'web'), tags: ['Abstracto', 'Audio'], source: 'SteamWorkshop', hasProperties: true, size: 1e6, supported: true },
      { id: 'a6', title: 'Galaxia espiral', type: 'scene', preview: svg('#000428', '#004e92', 'escena'), tags: ['Espacio'], source: 'WallpaperEngineProjects', hasProperties: false, size: 12e6, supported: true },
    ];
    var state = {
      version: '0.1.0',
      paused: false,
      monitors: [
        { id: '\\\\.\\DISPLAY1', index: 1, primary: true, x: 0, y: 0, width: 2560, height: 1440, scale: 1.25, wallpaperId: 'a1', wallpaperTitle: 'Ciudad de neón', covered: false },
        { id: '\\\\.\\DISPLAY2', index: 2, primary: false, x: 2560, y: 180, width: 1920, height: 1080, scale: 1, wallpaperId: 'a3', wallpaperTitle: 'Bosque al atardecer', covered: true },
      ],
      settings: {
        pauseMode: 'Fullscreen', muted: false, startWithWindows: true, startMinimized: true, scanSteamWorkshop: true, fpsLimit: 60,
        wallpaperEngineInstallPath: '', detectedWallpaperEngine: 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\wallpaper_engine',
        libraryFolders: ['D:\\Wallpapers'], defaultLibrary: 'C:\\Users\\vos\\Documents\\Wallpaper Forge\\wallpapers',
        pauseIgnoreProcesses: ['NVIDIA Share', 'nvcontainer', 'GameBar'],
      },
    };
    var props = [
      { key: 'schemecolor', type: 'color', label: 'Color del esquema', value: '0.48 0.3 1' },
      { key: 'showclock', type: 'bool', label: 'Mostrar reloj', value: true },
      { key: 'clocksize', type: 'slider', label: 'Tamaño del reloj', value: 60, min: 10, max: 200, step: 1, condition: 'showclock.value == true' },
      { key: 'format', type: 'combo', label: 'Formato', value: '24', options: [{ label: '24 horas', value: '24' }, { label: '12 horas', value: '12' }], condition: 'showclock.value == true' },
      { key: 'greeting', type: 'textinput', label: 'Saludo', value: 'Hola' },
      { key: 'bg', type: 'file', label: 'Imagen de fondo', value: '', fileType: 'image' },
      { key: 'info', type: 'text', label: 'Las propiedades se aplican al instante.' },
    ];
    function details(id) {
      var w = lib.find(function (x) { return x.id === id; });
      return Object.assign({}, w, {
        description: 'Wallpaper de ejemplo para el modo demostración de la interfaz.',
        folder: 'C:\\wallpapers\\' + id, file: w.type === 'video' ? 'video.mp4' : (w.type === 'web' ? 'index.html' : 'scene.json'),
        workshopId: w.source === 'SteamWorkshop' ? '123456789' : null, rating: 'Everyone',
        playback: { volume: 50, rate: 1, fit: 'cover' },
        properties: w.hasProperties ? props : [],
      });
    }
    function delay(v) { return new Promise(function (r) { setTimeout(function () { r(v); }, 60); }); }
    return {
      call: function (method, p) {
        switch (method) {
          case 'getState': return delay(JSON.parse(JSON.stringify(state)));
          case 'getLibrary': return delay(lib.slice());
          case 'getWallpaper': return delay(details(p.id));
          case 'apply':
            state.monitors.forEach(function (m) {
              if (!p.monitor || p.monitor === 'all' || p.monitor === m.id) {
                m.wallpaperId = p.id;
                m.wallpaperTitle = lib.find(function (x) { return x.id === p.id; }).title;
              }
            });
            return delay(JSON.parse(JSON.stringify(state)));
          case 'clear':
            state.monitors.forEach(function (m) { if (m.id === p.monitor) { m.wallpaperId = null; m.wallpaperTitle = null; } });
            return delay(JSON.parse(JSON.stringify(state)));
          case 'setPaused': state.paused = p.paused; return delay(JSON.parse(JSON.stringify(state)));
          case 'setMuted': state.settings.muted = p.muted; return delay(JSON.parse(JSON.stringify(state)));
          case 'setSettings': Object.assign(state.settings, p); return delay(JSON.parse(JSON.stringify(state)));
          case 'setProperty':
            props.forEach(function (x) { if (x.key === p.key) x.value = p.value; });
            return delay(true);
          case 'resetProperties': return delay(details(p.id));
          case 'pickFile': return delay('C:\\Users\\vos\\Pictures\\fondo.jpg');
          case 'pickFolder': return delay('C:\\Users\\vos\\Pictures');
          default: return delay(true);
        }
      },
    };
  })();

  window.Bridge = { call: call, on: on, isDemo: !host };
})();
