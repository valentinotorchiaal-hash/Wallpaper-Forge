(function () {
  'use strict';
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  var state = null;          // estado de la app (monitores, ajustes)
  var library = [];          // wallpapers
  var selected = null;       // detalles del wallpaper seleccionado
  var target = 'all';        // monitor destino de "Aplicar"
  var filters = { text: '', type: '', source: '', sort: 'recent' };

  var TYPE_LABEL = { video: 'Video', web: 'Web', scene: 'Escena', image: 'Imagen', application: 'Aplicación', unknown: '?' };
  var SOURCE_LABEL = { Local: 'Local', SteamWorkshop: 'Workshop', WallpaperEngineProjects: 'Wallpaper Engine' };

  var ICONS = {
    pause: '<svg viewBox="0 0 24 24"><path d="M7 5h3v14H7zM14 5h3v14h-3z"/></svg>',
    play: '<svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>',
    sound: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9H4Zm12.5 3a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4Z"/></svg>',
    muted: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 4V5L8 9H4Zm12.6-.4L15.2 10l2 2-2 2 1.4 1.4 2-2 2 2 1.4-1.4-2-2 2-2-1.4-1.4-2 2-2-2Z"/></svg>',
  };

  // ---------------- Utilidades ----------------
  function el(tag, attrs, children) {
    var e = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      var v = attrs[k];
      if (v == null || v === false) return;
      if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else if (k === 'html') e.innerHTML = v;
      else if (k.indexOf('on') === 0) e.addEventListener(k.slice(2), v);
      else e.setAttribute(k, v === true ? '' : v);
    });
    (children || []).forEach(function (c) { if (c != null) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return e;
  }
  function stripHtml(s) { var d = document.createElement('div'); d.innerHTML = s || ''; return d.textContent || ''; }
  function fmtSize(b) {
    if (!b) return '';
    var u = ['B', 'KB', 'MB', 'GB']; var i = 0;
    while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return b.toFixed(i ? 1 : 0) + ' ' + u[i];
  }
  function toast(msg, kind) {
    var t = el('div', { class: 'toast ' + (kind || '') , text: msg });
    $('#toasts').appendChild(t);
    setTimeout(function () { t.classList.add('out'); setTimeout(function () { t.remove(); }, 300); }, kind === 'error' ? 7000 : 3500);
  }
  function call(method, params) {
    return Bridge.call(method, params).catch(function (e) { toast(e.message || String(e), 'error'); throw e; });
  }
  function debounce(fn, ms) {
    var t; return function () { var a = arguments, self = this; clearTimeout(t); t = setTimeout(function () { fn.apply(self, a); }, ms); };
  }

  // Colores de Wallpaper Engine: "r g b" con floats 0..1
  function weToHex(v) {
    var p = String(v || '0 0 0').trim().split(/\s+/).map(Number);
    return '#' + [0, 1, 2].map(function (i) {
      var n = Math.round(Math.max(0, Math.min(1, p[i] || 0)) * 255);
      return ('0' + n.toString(16)).slice(-2);
    }).join('');
  }
  function hexToWe(h) {
    return [1, 3, 5].map(function (i) { return +(parseInt(h.substr(i, 2), 16) / 255).toFixed(5); }).join(' ');
  }

  // ---------------- Estado ----------------
  function setState(s) {
    if (!s) return;
    state = s;
    if (target !== 'all' && !state.monitors.some(function (m) { return m.id === target; })) target = 'all';
    renderMonitors();
    renderTopButtons();
    var key = state.monitors.map(function (m) { return m.wallpaperId; }).join('|');
    if (key !== lastActiveKey) { lastActiveKey = key; renderGrid(); }
    if (selected) renderDetails();
  }
  var lastActiveKey = null;

  function renderTopButtons() {
    var pb = $('#btnPause');
    pb.innerHTML = state.paused ? ICONS.play : ICONS.pause;
    pb.title = state.paused ? 'Reanudar' : 'Pausar todo';
    pb.classList.toggle('on', state.paused);
    var mb = $('#btnMute');
    mb.innerHTML = state.settings.muted ? ICONS.muted : ICONS.sound;
    mb.title = state.settings.muted ? 'Activar sonido' : 'Silenciar';
    mb.classList.toggle('on', state.settings.muted);
  }

  // ---------------- Monitores ----------------
  function renderMonitors() {
    var box = $('#monitors');
    box.innerHTML = '';
    var mons = state.monitors;
    if (!mons.length) { box.appendChild(el('p', { class: 'muted', text: 'No se detectaron monitores.' })); return; }

    var minX = Math.min.apply(null, mons.map(function (m) { return m.x; }));
    var minY = Math.min.apply(null, mons.map(function (m) { return m.y; }));
    var maxX = Math.max.apply(null, mons.map(function (m) { return m.x + m.width; }));
    var maxY = Math.max.apply(null, mons.map(function (m) { return m.y + m.height; }));
    var scale = Math.min(320 / (maxX - minX), 84 / (maxY - minY));

    var map = el('div', { class: 'monitor-map', style: 'width:' + Math.ceil((maxX - minX) * scale) + 'px;height:' + Math.ceil((maxY - minY) * scale) + 'px' });
    mons.forEach(function (m) {
      var w = library.find(function (x) { return x.id === m.wallpaperId; });
      var node = el('button', {
        class: 'monitor' + (target === m.id ? ' active' : '') + (m.covered ? ' covered' : ''),
        style: 'left:' + (m.x - minX) * scale + 'px;top:' + (m.y - minY) * scale + 'px;width:' + (m.width * scale - 4) + 'px;height:' + (m.height * scale - 4) + 'px',
        title: 'Monitor ' + m.index + ' — ' + m.width + '×' + m.height + (m.wallpaperTitle ? '\n' + m.wallpaperTitle : ''),
        onclick: function () { target = target === m.id ? 'all' : m.id; renderMonitors(); if (selected) renderDetails(); },
      }, [el('span', { class: 'num', text: String(m.index) })]);
      if (w && w.preview) node.style.backgroundImage = 'url("' + w.preview + '")';
      map.appendChild(node);
    });

    var list = el('div', { class: 'monitor-list' });
    list.appendChild(el('button', {
      class: 'monitor-row' + (target === 'all' ? ' active' : ''),
      onclick: function () { target = 'all'; renderMonitors(); if (selected) renderDetails(); },
    }, [el('strong', { text: 'Todos los monitores' }), el('span', { class: 'muted', text: mons.length + ' detectados' })]));

    mons.forEach(function (m) {
      var status = state.paused ? 'En pausa' : (state.batteryPause ? 'En pausa (batería)' : (m.covered ? 'En pausa (pantalla completa)' : (m.wallpaperId ? 'Reproduciendo' : 'Sin wallpaper')));
      var row = el('div', { class: 'monitor-row' + (target === m.id ? ' active' : ''), role: 'button', tabindex: '0',
        onclick: function () { target = m.id; renderMonitors(); if (selected) renderDetails(); } }, [
        el('strong', { text: 'Monitor ' + m.index + (m.primary ? ' · principal' : '') }),
        el('span', { class: 'muted', text: m.width + '×' + m.height + ' · ' + Math.round(m.scale * 100) + '%' }),
        el('span', { class: 'wp-title', text: m.wallpaperTitle || '—' }),
        el('span', { class: 'status ' + (m.covered || state.paused || state.batteryPause ? 'paused' : (m.wallpaperId ? 'playing' : '')), text: status }),
        m.wallpaperId ? el('button', { class: 'link', text: 'Quitar', onclick: function (e) {
          e.stopPropagation();
          call('clear', { monitor: m.id }).then(setState);
        } }) : null,
      ]);
      list.appendChild(row);
    });

    box.appendChild(map);
    box.appendChild(list);
  }

  // ---------------- Miniaturas ----------------
  // En la grilla todo es estático (un cuadro del GIF o del video): sólo se mueve al pasar el mouse.
  // Así cientos de GIF y videos no consumen CPU ni memoria. Los cuadros se guardan en memoria.
  var thumbCache = new Map();
  var thumbObserver = 'IntersectionObserver' in window ? new IntersectionObserver(function (entries) {
    entries.forEach(function (en) {
      if (en.isIntersecting) { thumbObserver.unobserve(en.target); loadThumb(en.target); }
    });
  }, { rootMargin: '300px' }) : null;

  function thumbFor(w) {
    var box = el('div', { class: 'thumb-media' });
    box._w = w;
    var cached = thumbCache.get(w.id);
    if (cached) box.appendChild(el('img', { src: cached, alt: '' }));
    else if (!w.preview && !w.video) box.appendChild(el('div', { class: 'noimg', text: TYPE_LABEL[w.type] }));
    else if (thumbObserver) thumbObserver.observe(box);
    else loadThumb(box);
    return box;
  }

  function isAnimated(url) {
    var path = decodeURIComponent(url || '').split('path=').pop();
    return /\.(gif|webp)$/i.test(path);
  }

  function loadThumb(box) {
    var w = box._w;
    var src = w.preview || w.video;
    var isVideo = !w.preview && !!w.video;
    if (!isVideo && !isAnimated(src)) {
      // Imagen fija: se usa directo.
      box.appendChild(el('img', { src: src, alt: '', loading: 'lazy' }));
      return;
    }
    capture(src, isVideo).then(function (dataUrl) {
      if (dataUrl) { thumbCache.set(w.id, dataUrl); box.innerHTML = ''; box.appendChild(el('img', { src: dataUrl, alt: '' })); }
      else { box.innerHTML = ''; box.appendChild(el('div', { class: 'noimg', text: TYPE_LABEL[w.type] })); }
    });
  }

  // Toma un cuadro de un GIF o video y lo devuelve como JPEG (data URL). El elemento original se descarta.
  function capture(src, isVideo) {
    return new Promise(function (resolve) {
      var done = false;
      function finish(media, mw, mh) {
        if (done) return; done = true;
        try {
          var scale = Math.min(1, 480 / (mw || 480));
          var c = document.createElement('canvas');
          c.width = Math.max(1, Math.round((mw || 480) * scale));
          c.height = Math.max(1, Math.round((mh || 270) * scale));
          c.getContext('2d').drawImage(media, 0, 0, c.width, c.height);
          resolve(c.toDataURL('image/jpeg', 0.82));
        } catch (e) { resolve(null); }
        if (isVideo) { media.removeAttribute('src'); media.load(); }
      }
      setTimeout(function () { if (!done) { done = true; resolve(null); } }, 15000);
      if (isVideo) {
        var v = document.createElement('video');
        v.crossOrigin = 'anonymous'; v.muted = true; v.preload = 'auto'; v.playsInline = true;
        v.addEventListener('loadedmetadata', function () { v.currentTime = Math.min(1, (v.duration || 2) / 3); }, { once: true });
        v.addEventListener('seeked', function () {
          // Se espera a que el cuadro esté realmente dibujado (si no, sale negro).
          // (finish ignora la segunda llamada; el respaldo por tiempo evita quedar sin miniatura).
          if (v.requestVideoFrameCallback) {
            v.requestVideoFrameCallback(function () { finish(v, v.videoWidth, v.videoHeight); });
            v.play().catch(function () {});
          }
          setTimeout(function () { finish(v, v.videoWidth, v.videoHeight); }, 600);
        }, { once: true });
        v.addEventListener('error', function () { if (!done) { done = true; resolve(null); } }, { once: true });
        v.src = src;
      } else {
        var img = new Image();
        img.crossOrigin = 'anonymous';
        img.onload = function () { finish(img, img.naturalWidth, img.naturalHeight); };
        img.onerror = function () { if (!done) { done = true; resolve(null); } };
        img.src = src;
      }
    });
  }

  // Al pasar el mouse: el GIF o el video se reproducen; al salir, se descartan.
  function hoverPlay(card, w) {
    var live = null;
    card.addEventListener('mouseenter', function () {
      var media = card.querySelector('.thumb-media');
      if (!media || live) return;
      if (w.video && !w.preview) {
        live = el('video', { class: 'live', src: w.video, muted: true, autoplay: true, loop: true, playsinline: true });
        live.muted = true;
      } else if (w.preview && isAnimated(w.preview)) {
        live = el('img', { class: 'live', src: w.preview, alt: '' });
      }
      if (live) media.appendChild(live);
    });
    card.addEventListener('mouseleave', function () {
      if (!live) return;
      if (live.tagName === 'VIDEO') { live.pause(); live.removeAttribute('src'); live.load(); }
      live.remove(); live = null;
    });
  }

  // ---------------- Biblioteca ----------------
  function filtered() {
    var q = filters.text.trim().toLowerCase();
    var items = library.filter(function (w) {
      if (filters.type && w.type !== filters.type) return false;
      if (filters.source && w.source !== filters.source) return false;
      if (!q) return true;
      return w.title.toLowerCase().indexOf(q) >= 0 ||
        (w.tags || []).some(function (t) { return t.toLowerCase().indexOf(q) >= 0; });
    });
    if (filters.sort === 'name') items.sort(function (a, b) { return a.title.localeCompare(b.title, 'es'); });
    else if (filters.sort === 'size') items.sort(function (a, b) { return (b.size || 0) - (a.size || 0); });
    return items;
  }

  function renderGrid() {
    var grid = $('#grid');
    var items = filtered();
    $('#count').textContent = items.length + (items.length === 1 ? ' wallpaper' : ' wallpapers');
    grid.innerHTML = '';
    if (!items.length) {
      grid.appendChild(el('div', { class: 'empty' }, [
        el('h3', { text: library.length ? 'Nada coincide con la búsqueda' : 'Tu biblioteca está vacía' }),
        el('p', { class: 'muted', text: library.length ? 'Probá con otro nombre, etiqueta o filtro.' :
          'Arrastrá acá videos, imágenes o GIF (o pegalos con Ctrl+V), o agregá la carpeta donde los guardás. Si tenés Wallpaper Engine, los wallpapers del Workshop aparecen solos.' }),
        library.length ? null : el('button', { class: 'btn primary', text: 'Importar videos, imágenes o GIF…', onclick: function () { doImport('media'); } }),
      ]));
      return;
    }
    var activeIds = state ? state.monitors.map(function (m) { return m.wallpaperId; }) : [];
    items.forEach(function (w) {
      var card = el('article', {
        class: 'card' + (selected && selected.id === w.id ? ' selected' : '') + (w.supported ? '' : ' unsupported'),
        tabindex: '0',
        onclick: function () { select(w.id); },
        ondblclick: function () { if (w.supported) apply(w.id); },
        onkeydown: function (e) { if (e.key === 'Enter') select(w.id); },
      }, [
        el('div', { class: 'thumb' }, [
          thumbFor(w),
          el('span', { class: 'badge type-' + w.type, text: TYPE_LABEL[w.type] || w.type }),
          activeIds.indexOf(w.id) >= 0 ? el('span', { class: 'badge active', text: 'En uso' }) : null,
          w.supported ? el('button', { class: 'quick-apply', text: 'Aplicar', onclick: function (e) { e.stopPropagation(); apply(w.id); } })
                      : el('span', { class: 'badge warn', text: 'No soportado aún' }),
        ]),
        el('div', { class: 'meta' }, [
          el('h3', { text: w.title, title: w.title }),
          el('span', { class: 'muted', text: [SOURCE_LABEL[w.source] || w.source, fmtSize(w.size)].filter(Boolean).join(' · ') }),
        ]),
      ]);
      hoverPlay(card, w);
      grid.appendChild(card);
    });
  }

  function select(id) {
    call('getWallpaper', { id: id }).then(function (d) {
      selected = d;
      $$('.card.selected').forEach(function (c) { c.classList.remove('selected'); });
      renderGrid();
      renderDetails();
    });
  }

  function apply(id) {
    var label = target === 'all' ? 'todos los monitores' : 'el monitor ' + monitorIndex(target);
    return call('apply', { id: id, monitor: target }).then(function (s) {
      setState(s);
      toast('Aplicado en ' + label);
    });
  }

  function monitorIndex(id) {
    var m = state.monitors.find(function (x) { return x.id === id; });
    return m ? m.index : '?';
  }

  // ---------------- Panel de detalles ----------------
  function renderDetails() {
    var box = $('#details');
    var d = selected;
    if (!d) { box.hidden = true; return; }
    box.hidden = false;
    box.innerHTML = '';

    var head = el('div', { class: 'details-head' }, [
      el('button', { class: 'icon-btn close', title: 'Cerrar', text: '✕', onclick: function () { selected = null; renderDetails(); renderGrid(); } }),
    ]);
    var preview = el('div', { class: 'big-preview' }, [
      d.preview ? (d.previewIsVideo ? el('video', { src: d.preview, autoplay: true, loop: true, muted: true, playsinline: true }) : el('img', { src: d.preview, alt: '' }))
                : el('div', { class: 'noimg', text: TYPE_LABEL[d.type] }),
    ]);

    var targetLabel = target === 'all' ? 'todos los monitores' : 'monitor ' + monitorIndex(target);
    var applyBtn = el('button', {
      class: 'btn primary wide', text: 'Aplicar en ' + targetLabel,
      disabled: ['video', 'web', 'scene', 'image'].indexOf(d.type) < 0,
      onclick: function () { apply(d.id); },
    });

    var info = el('div', { class: 'info' }, [
      el('h2', { text: d.title }),
      el('div', { class: 'tags' }, [el('span', { class: 'badge type-' + d.type, text: TYPE_LABEL[d.type] })]
        .concat((d.tags || []).map(function (t) { return el('span', { class: 'tag', text: t }); }))),
      d.description ? el('p', { class: 'desc', text: stripHtml(d.description) }) : null,
      d.type === 'scene' ? el('p', { class: 'note', text: 'Escenas: soporte parcial. Se muestran las capas de imagen, texturas animadas y sonidos; los efectos, partículas y textos todavía no.' }) : null,
      ['video', 'web', 'scene', 'image'].indexOf(d.type) < 0 ? el('p', { class: 'note warn', text: 'Este tipo de wallpaper todavía no está soportado.' }) : null,
      applyBtn,
      el('div', { class: 'row-actions' }, [
        el('button', { class: 'btn ghost', text: d.looseFile ? 'Mostrar archivo' : 'Abrir carpeta', onclick: function () { call('openFolder', { id: d.id }); } }),
        el('button', { class: 'btn ghost danger', text: 'Quitar de la biblioteca', title: 'No borra el archivo del disco', onclick: function () {
          if (!confirm('¿Quitar "' + d.title + '" de la biblioteca?\n\nNo se borra del disco. Se puede volver a mostrar desde Configuración → Biblioteca.')) return;
          var id = d.id;
          call('hideWallpaper', { id: id }).then(function (s) {
            library = library.filter(function (x) { return x.id !== id; });
            selected = null; renderDetails(); lastActiveKey = null; setState(s);
            toast('Quitado de la biblioteca');
          });
        } }),
        d.properties.length ? el('button', { class: 'btn ghost', text: 'Restablecer', onclick: function () {
          call('resetProperties', { id: d.id }).then(function (nd) { selected = nd; renderDetails(); toast('Propiedades restablecidas'); });
        } }) : null,
      ]),
      el('dl', { class: 'facts' }, [
        el('dt', { text: 'Archivo' }), el('dd', { text: d.file || '—' }),
        el('dt', { text: 'Tamaño' }), el('dd', { text: fmtSize(d.size) || '—' }),
        el('dt', { text: 'Origen' }), el('dd', { text: SOURCE_LABEL[d.source] || d.source }),
        d.workshopId ? el('dt', { text: 'Workshop' }) : null, d.workshopId ? el('dd', { text: d.workshopId }) : null,
      ]),
    ]);

    box.appendChild(head);
    box.appendChild(preview);
    box.appendChild(info);
    box.appendChild(renderPlayback(d));
    if (d.properties.length) box.appendChild(renderProperties(d));
  }

  function renderPlayback(d) {
    var pb = d.playback;
    var send = debounce(function () { call('setPlayback', Object.assign({ id: d.id }, pb)); }, 150);
    var sec = el('section', { class: 'props' }, [el('h3', { text: 'Reproducción' })]);
    if (d.type === 'image') {
      var fitSel = el('select', { onchange: function () { pb.fit = fitSel.value; send(); } }, [
        el('option', { value: 'cover', text: 'Rellenar (recorta)' }),
        el('option', { value: 'contain', text: 'Ajustar (con bordes)' }),
        el('option', { value: 'stretch', text: 'Estirar' }),
      ]);
      fitSel.value = pb.fit;
      sec.appendChild(field('Escalado', fitSel));
      return sec;
    }
    if (d.type !== 'web') {
      sec.appendChild(field('Volumen', sliderInput(pb.volume, 0, 100, 1, false, function (v) { pb.volume = v; send(); })));
    } else {
      sec.appendChild(el('p', { class: 'muted small', text: 'El volumen de los wallpapers web lo controla el propio wallpaper. Podés silenciarlos con el botón de sonido de arriba.' }));
    }
    if (d.type === 'video') {
      sec.appendChild(field('Velocidad', sliderInput(pb.rate, 0.25, 2, 0.05, true, function (v) { pb.rate = v; send(); })));
      var sel = el('select', { onchange: function () { pb.fit = sel.value; send(); } }, [
        el('option', { value: 'cover', text: 'Rellenar (recorta)' }),
        el('option', { value: 'contain', text: 'Ajustar (con bordes)' }),
        el('option', { value: 'stretch', text: 'Estirar' }),
      ]);
      sel.value = pb.fit;
      sec.appendChild(field('Escalado', sel));
    }
    return sec;
  }

  function renderProperties(d) {
    var sec = el('section', { class: 'props' }, [el('h3', { text: 'Propiedades' })]);
    var values = {};
    d.properties.forEach(function (p) { values[p.key] = { value: p.value }; });
    var rows = [];

    function send(p, v) {
      values[p.key] = { value: v };
      p.value = v;
      updateConditions();
      return call('setProperty', { id: d.id, key: p.key, value: v });
    }
    var sendDebounced = {};

    d.properties.forEach(function (p) {
      var control;
      var label = stripHtml(p.label) || p.key;
      switch (p.type) {
        case 'slider':
          sendDebounced[p.key] = debounce(function (v) { send(p, v); }, 120);
          var step = p.step || (p.fraction ? Math.pow(10, -(p.precision || 2)) : 1);
          control = sliderInput(Number(p.value), p.min == null ? 0 : p.min, p.max == null ? 100 : p.max, step, !!p.fraction, function (v) { sendDebounced[p.key](v); });
          break;
        case 'bool':
          control = toggle(!!p.value, function (v) { send(p, v); });
          break;
        case 'color':
          control = el('input', { type: 'color', value: weToHex(p.value), oninput: debounce(function (e) { send(p, hexToWe(e.target.value)); }, 80) });
          break;
        case 'combo':
          control = el('select', { onchange: function (e) {
            var opt = p.options[e.target.selectedIndex];
            send(p, opt ? opt.value : e.target.value);
          } }, p.options.map(function (o) { return el('option', { text: stripHtml(o.label) }); }));
          var idx = p.options.findIndex(function (o) { return String(o.value) === String(p.value); });
          control.selectedIndex = idx < 0 ? 0 : idx;
          break;
        case 'textinput':
          control = el('input', { type: 'text', value: p.value == null ? '' : String(p.value), oninput: debounce(function (e) { send(p, e.target.value); }, 300) });
          break;
        case 'file':
        case 'directory':
          var name = el('span', { class: 'file-name', text: p.value ? String(p.value).split(/[\\/]/).pop() : 'Ninguno', title: p.value || '' });
          control = el('div', { class: 'file-pick' }, [
            name,
            el('button', { class: 'btn ghost small', text: 'Elegir…', onclick: function () {
              var req = p.type === 'file' ? call('pickFile', { kind: p.fileType || 'image' }) : call('pickFolder', { title: label });
              req.then(function (path) {
                if (!path) return;
                name.textContent = path.split(/[\\/]/).pop(); name.title = path;
                send(p, path);
              });
            } }),
            p.value ? el('button', { class: 'btn ghost small', text: 'Quitar', onclick: function () { name.textContent = 'Ninguno'; send(p, ''); } }) : null,
          ]);
          break;
        case 'text':
          var r = el('p', { class: 'prop-text', html: sanitizeLabel(p.label) });
          rows.push({ p: p, row: r });
          sec.appendChild(r);
          return;
        case 'group':
          var g = el('h4', { class: 'prop-group', text: label });
          rows.push({ p: p, row: g });
          sec.appendChild(g);
          return;
        default:
          control = el('span', { class: 'muted', text: 'Tipo no soportado: ' + p.type });
      }
      var row = field(label, control);
      rows.push({ p: p, row: row });
      sec.appendChild(row);
    });

    function updateConditions() {
      rows.forEach(function (r) {
        if (!r.p.condition) return;
        r.row.hidden = !evalCondition(r.p.condition, values);
      });
    }
    updateConditions();
    return sec;
  }

  // Condiciones de Wallpaper Engine: expresiones JS como "showclock.value == true && style.value == 2"
  function evalCondition(expr, values) {
    try {
      var keys = Object.keys(values).filter(function (k) { return /^[A-Za-z_$][\w$]*$/.test(k); });
      var fn = new Function(keys.join(','), '"use strict"; return (' + expr + ');');
      return !!fn.apply(null, keys.map(function (k) { return values[k]; }));
    } catch (e) {
      return true;
    }
  }

  // Los textos de Wallpaper Engine pueden traer HTML simple (<b>, <br>, <a>...). Se permite un subconjunto.
  function sanitizeLabel(html) {
    var tpl = document.createElement('template');
    tpl.innerHTML = html || '';
    var allowed = { B: 1, I: 1, U: 1, BR: 1, STRONG: 1, EM: 1, SPAN: 1, SMALL: 1, A: 1, P: 1, H1: 1, H2: 1, H3: 1, H4: 1 };
    (function clean(node) {
      Array.prototype.slice.call(node.childNodes).forEach(function (c) {
        if (c.nodeType === 1) {
          if (!allowed[c.tagName]) { c.replaceWith(document.createTextNode(c.textContent)); return; }
          Array.prototype.slice.call(c.attributes).forEach(function (a) {
            if (!(c.tagName === 'A' && a.name === 'href' && /^https?:/i.test(a.value))) c.removeAttribute(a.name);
          });
          if (c.tagName === 'A') c.setAttribute('target', '_blank');
          clean(c);
        }
      });
    })(tpl.content);
    var div = document.createElement('div');
    div.appendChild(tpl.content);
    return div.innerHTML;
  }

  function field(label, control) {
    return el('div', { class: 'field' }, [el('label', { text: label }), control]);
  }

  function sliderInput(value, min, max, step, fraction, onchange) {
    var out = el('output', { text: fmtNum(value, fraction, step) });
    var input = el('input', { type: 'range', min: min, max: max, step: step, value: value, oninput: function () {
      var v = fraction ? parseFloat(input.value) : Math.round(parseFloat(input.value));
      out.textContent = fmtNum(v, fraction, step);
      onchange(v);
    } });
    return el('div', { class: 'slider' }, [input, out]);
  }
  function fmtNum(v, fraction, step) {
    if (!fraction) return String(Math.round(v));
    var decimals = Math.min(4, Math.max(1, (String(step).split('.')[1] || '').length));
    return Number(v).toFixed(decimals);
  }
  function toggle(value, onchange) {
    var input = el('input', { type: 'checkbox', onchange: function () { onchange(input.checked); } });
    input.checked = value;
    return el('label', { class: 'switch' }, [input, el('span', { class: 'track' })]);
  }

  // ---------------- Importar ----------------
  function doImport(kind) {
    var map = { media: 'importMedia', video: 'importMedia', web: 'importWeb', folder: 'addLibraryFolder', paste: 'pasteFiles' };
    if (kind === 'rescan') return refreshLibrary(true).then(function () { toast('Biblioteca actualizada'); });
    return call(map[kind]).then(function (res) {
      if (res == null) return;
      if (kind === 'folder') setState(res);
      return refreshLibrary(false).then(function () {
        if (typeof res === 'string' && res) select(res);
        toast(kind === 'folder' ? 'Carpeta agregada' : 'Agregado a la biblioteca');
      });
    });
  }

  function refreshLibrary(rescan) {
    return call('getLibrary', { rescan: !!rescan }).then(function (items) {
      library = items;
      renderGrid();
      renderMonitors();
    });
  }

  // ---------------- Configuración ----------------
  function openSettings() {
    var s = state.settings;
    var body = $('#settingsBody');
    body.innerHTML = '';
    function save(patch) { return call('setSettings', patch).then(setState); }

    var pause = el('div', { class: 'radio-group' }, [
      ['Fullscreen', 'Pausar el monitor que tenga una app en pantalla completa'],
      ['FullscreenOrMaximized', 'Pausar también con ventanas maximizadas'],
      ['Never', 'No pausar nunca'],
    ].map(function (o) {
      var input = el('input', { type: 'radio', name: 'pause', value: o[0], onchange: function () { save({ pauseMode: o[0] }); } });
      input.checked = s.pauseMode === o[0];
      return el('label', { class: 'radio' }, [input, el('span', { text: o[1] })]);
    }));

    var ignore = el('textarea', { rows: '3', placeholder: 'Un proceso por línea, sin .exe' });
    ignore.value = (s.pauseIgnoreProcesses || []).join('\n');
    ignore.addEventListener('change', function () { save({ pauseIgnoreProcesses: ignore.value.split(/\r?\n/) }); });

    var fps = el('input', { type: 'number', min: '5', max: '240', value: s.fpsLimit, onchange: function (e) { save({ fpsLimit: parseInt(e.target.value, 10) || 60 }); } });

    var weInput = el('input', { type: 'text', value: s.wallpaperEngineInstallPath || '', placeholder: s.detectedWallpaperEngine || 'No detectado' });
    weInput.addEventListener('change', function () { save({ wallpaperEngineInstallPath: weInput.value.trim() }); });

    var folders = el('ul', { class: 'folders' }, [el('li', {}, [el('span', { text: s.defaultLibrary }), el('span', { class: 'muted', text: 'biblioteca propia' })])]
      .concat(s.libraryFolders.map(function (f) {
        return el('li', {}, [el('span', { text: f }), el('button', { class: 'link', text: 'Quitar', onclick: function () {
          call('removeLibraryFolder', { folder: f }).then(function (st) { setState(st); refreshLibrary(false); openSettings(); });
        } })]);
      })));

    [
      el('h3', { text: 'Rendimiento' }),
      pause,
      el('div', { class: 'field inline' }, [el('label', { text: 'Pausar cuando la PC usa batería' }), toggle(s.pauseOnBattery, function (v) { save({ pauseOnBattery: v }); })]),
      field('Procesos que no pausan', ignore),
      field('Límite de FPS (para wallpapers web)', fps),
      el('h3', { text: 'Inicio' }),
      el('div', { class: 'field inline' }, [el('label', { text: 'Iniciar con Windows' }), toggle(s.startWithWindows, function (v) { save({ startWithWindows: v }); })]),
      el('div', { class: 'field inline' }, [el('label', { text: 'Iniciar minimizado en la bandeja' }), toggle(s.startMinimized, function (v) { save({ startMinimized: v }); })]),
      el('h3', { text: 'Biblioteca' }),
      folders,
      el('button', { class: 'btn ghost', text: 'Agregar carpeta…', onclick: function () { doImport('folder').then(openSettings); } }),
      el('div', { class: 'field inline' }, [el('label', { text: 'Incluir wallpapers de Steam Workshop (Wallpaper Engine)' }),
        toggle(s.scanSteamWorkshop, function (v) { save({ scanSteamWorkshop: v }).then(function () { refreshLibrary(false); }); })]),
      el('div', { class: 'field inline' }, [el('label', { text: 'Mostrar los wallpapers de ejemplo de Wallpaper Engine' }),
        toggle(s.includeWallpaperEngineSamples, function (v) { save({ includeWallpaperEngineSamples: v }).then(function () { refreshLibrary(false); }); })]),
      s.hiddenCount ? el('button', { class: 'btn ghost small', text: 'Volver a mostrar los wallpapers quitados (' + s.hiddenCount + ')', onclick: function () {
        call('unhideAll').then(function (st) { setState(st); refreshLibrary(false); openSettings(); toast('Se volvieron a mostrar'); });
      } }) : null,
      field('Carpeta de Wallpaper Engine (para assets de escenas)', weInput),
      el('h3', { text: 'Otros programas de fondos' }),
      otherAppsSection(),
      el('h3', { text: 'Diagnóstico' }),
      el('div', { class: 'row-actions' }, [
        el('button', { class: 'btn ghost', text: 'Abrir carpeta de logs', onclick: function () { call('openLogs'); } }),
      ]),
      el('p', { class: 'muted small', text: 'Wallpaper Forge ' + (state.version || '') + ' · proyecto independiente, no afiliado a Wallpaper Engine ni a Valve.' }),
    ].forEach(function (n) { body.appendChild(n); });

    $('#settingsModal').hidden = false;
  }

  // ---------------- Otros programas de fondos (Wallpaper Engine, Lively) ----------------
  function otherAppsSection() {
    var box = el('div', { class: 'other-apps' }, [el('p', { class: 'muted small', text: 'Buscando…' })]);
    function render(r) {
      box.innerHTML = '';
      var active = r.autostart.filter(function (a) { return a.enabled; });
      if (!r.running.length && !active.length) {
        box.appendChild(el('p', { class: 'muted small', text: 'No hay otros programas de fondos abiertos ni configurados para arrancar con Windows.' }));
      } else {
        var list = el('ul', { class: 'folders' });
        r.running.forEach(function (x) { list.appendChild(el('li', {}, [el('span', { text: x.app + ' — abierto (' + x.process + ')' }), el('span', { class: 'status-dot warn', text: 'en ejecución' })])); });
        active.forEach(function (x) { list.appendChild(el('li', {}, [el('span', { text: x.app + ' — ' + x.description }), el('span', { class: 'muted', text: x.needsAdmin ? 'requiere admin' : '' })])); });
        box.appendChild(list);
        box.appendChild(el('button', { class: 'btn primary', text: 'Cerrar y desactivar su inicio automático', onclick: function (e) {
          e.target.disabled = true; e.target.textContent = 'Aplicando…';
          call('disableOtherApps').then(function (res) {
            var parts = [];
            if (res.closed.length) parts.push('Cerrado: ' + res.closed.join(', '));
            if (res.disabled.length) parts.push('Inicio desactivado: ' + res.disabled.length);
            if (res.imported) parts.push('Wallpapers importados: ' + res.imported);
            if (res.elevationDeclined) parts.push('No se aceptaron los permisos de administrador');
            toast(parts.join(' · ') || 'No había nada para cambiar', res.errors.length || res.elevationDeclined ? 'warn' : '');
            load();
          }).catch(load);
        } }));
      }
      if (r.disabledByUs.length) {
        box.appendChild(el('p', { class: 'muted small', text: 'Desactivado por Wallpaper Forge: ' + r.disabledByUs.map(function (x) { return x.app + ' (' + x.description + ')'; }).join('; ') }));
        box.appendChild(el('button', { class: 'btn ghost', text: 'Restaurar inicio automático', onclick: function () {
          call('restoreOtherApps').then(function (res) { toast('Restaurado: ' + res.restored.length + (res.elevationDeclined ? ' (faltaron permisos de admin)' : '')); load(); });
        } }));
      }
      box.appendChild(el('div', { class: 'field inline' }, [el('label', { text: 'Cerrarlos automáticamente si se vuelven a abrir' }),
        toggle(r.guard, function (v) { call('setSettings', { closeOtherWallpaperApps: v }).then(setState); })]));
      box.appendChild(el('button', { class: 'btn ghost small', text: 'Copiar los wallpapers elegidos en Wallpaper Engine', onclick: function () {
        call('importFromWallpaperEngine').then(function (n) { toast(n ? 'Importados ' + n + ' wallpapers' : 'No había nada para importar (o los monitores ya tienen wallpaper)'); });
      } }));
    }
    function load() { call('detectOtherApps').then(render); }
    load();
    return box;
  }

  // ---------------- Eventos ----------------
  function bind() {
    $('#search').addEventListener('input', debounce(function (e) { filters.text = e.target.value; renderGrid(); }, 120));
    $$('#typeFilter .chip').forEach(function (c) {
      c.addEventListener('click', function () {
        $$('#typeFilter .chip').forEach(function (x) { x.classList.remove('active'); });
        c.classList.add('active');
        filters.type = c.dataset.type;
        renderGrid();
      });
    });
    $('#sourceFilter').addEventListener('change', function (e) { filters.source = e.target.value; renderGrid(); });
    $('#sort').addEventListener('change', function (e) { filters.sort = e.target.value; renderGrid(); });

    var menu = $('#importMenu');
    $('#btnImport').addEventListener('click', function (e) { e.stopPropagation(); menu.hidden = !menu.hidden; });
    document.addEventListener('click', function () { menu.hidden = true; });
    $$('#importMenu [data-import]').forEach(function (b) {
      b.addEventListener('click', function () { menu.hidden = true; doImport(b.dataset.import); });
    });

    $('#btnPause').addEventListener('click', function () { call('setPaused', { paused: !state.paused }).then(setState); });
    $('#btnMute').addEventListener('click', function () { call('setMuted', { muted: !state.settings.muted }).then(setState); });
    $('#btnSettings').addEventListener('click', openSettings);
    $$('#settingsModal [data-close]').forEach(function (b) { b.addEventListener('click', function () { $('#settingsModal').hidden = true; }); });
    $('#settingsModal').addEventListener('click', function (e) { if (e.target.id === 'settingsModal') e.target.hidden = true; });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { $('#settingsModal').hidden = true; menu.hidden = true; }
      if (e.key === '/' && document.activeElement.tagName !== 'INPUT' && document.activeElement.tagName !== 'TEXTAREA') { e.preventDefault(); $('#search').focus(); }
    });

    // Arrastrar archivos o carpetas a la ventana.
    var drop = $('#dropOverlay');
    var dragDepth = 0;
    window.addEventListener('dragenter', function (e) {
      if (!e.dataTransfer || Array.prototype.indexOf.call(e.dataTransfer.types, 'Files') < 0) return;
      dragDepth++; drop.hidden = false;
    });
    window.addEventListener('dragleave', function () { if (--dragDepth <= 0) { dragDepth = 0; drop.hidden = true; } });
    window.addEventListener('dragover', function (e) { e.preventDefault(); });
    window.addEventListener('drop', function (e) {
      e.preventDefault(); dragDepth = 0; drop.hidden = true;
      var files = e.dataTransfer && e.dataTransfer.files;
      if (!files || !files.length) return;
      Bridge.sendFiles('importPaths', files).then(function (res) {
        return refreshLibrary(false).then(function () { if (res) select(res); toast('Agregado a la biblioteca'); });
      }).catch(function (err) { toast(err.message || String(err), 'error'); });
    });
    // Ctrl+V con archivos copiados en el Explorador.
    document.addEventListener('keydown', function (e) {
      var tag = document.activeElement && document.activeElement.tagName;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v' && tag !== 'INPUT' && tag !== 'TEXTAREA') {
        e.preventDefault();
        call('pasteFiles').then(function (res) {
          if (res == null) { toast('No hay archivos copiados. Copiá videos, imágenes o GIF en el Explorador y volvé a pegar.'); return; }
          refreshLibrary(false).then(function () { if (res) select(res); toast('Agregado a la biblioteca'); });
        });
      }
    });
    // Al volver a mostrar la ventana (desde la bandeja) se refresca todo.
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'visible') { call('getState').then(function (st) { lastActiveKey = null; setState(st); }); refreshLibrary(false); }
    });

    Bridge.on('state', setState);
    Bridge.on('warnings', function (w) {
      toast('"' + w.title + '": ' + w.warnings.length + ' elementos de la escena no se pueden mostrar todavía.', 'warn');
    });
  }

  function start() {
    bind();
    Promise.all([call('getState'), call('getLibrary')]).then(function (r) {
      library = r[1];
      setState(r[0]);
      if (Bridge.isDemo) toast('Modo demostración: datos de ejemplo (fuera de la app).');
    });
  }

  start();
})();
