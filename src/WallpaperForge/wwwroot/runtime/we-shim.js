/*
 * Wallpaper Forge — capa de compatibilidad con la API web de Wallpaper Engine.
 * Se inyecta antes que cualquier script de la página.
 *
 * Implementa:
 *   window.wallpaperPropertyListener = {
 *     applyUserProperties(props), applyGeneralProperties(props), setPaused(bool),
 *     userDirectoryFilesAddedOrChanged(prop, files), userDirectoryFilesRemoved(prop, files)
 *   }
 *   window.wallpaperRequestRandomFileForProperty(prop, callback)
 *   window.wallpaperRegisterAudioListener(cb)          (sin datos de audio en la v0.1)
 *   window.wallpaperRegisterMedia*Listener(cb)         (sin datos en la v0.1)
 *
 * Y expone window.__wf para que la app hable con la página.
 */
(function () {
  'use strict';
  if (window.top !== window) return;          // sólo el documento principal
  if (window.__wf) return;

  var init = window.__WF_INIT || {};
  var state = {
    user: init.user || {},
    general: init.general || {},
    directories: init.directories || {},
    playback: init.playback || { volume: 50, rate: 1, fit: 'cover' },
    paused: false,
  };
  var listener = null;
  var appliedTo = null;
  var audioListeners = [];
  var mediaListeners = { status: [], properties: [], thumbnail: [], playback: [], timeline: [] };
  var pausedByUs = new Set();

  function clone(o) { return JSON.parse(JSON.stringify(o)); }
  function safe(fn) {
    try { fn(); } catch (e) { console.error('[Wallpaper Forge] error en el wallpaper:', e); }
  }

  function applyAll(l) {
    if (!l || appliedTo === l) return;
    appliedTo = l;
    if (typeof l.applyGeneralProperties === 'function') safe(function () { l.applyGeneralProperties(clone(state.general)); });
    if (typeof l.applyUserProperties === 'function') safe(function () { l.applyUserProperties(clone(state.user)); });
    if (typeof l.userDirectoryFilesAddedOrChanged === 'function') {
      Object.keys(state.directories).forEach(function (key) {
        var d = state.directories[key];
        if (d && d.mode !== 'ondemand' && d.files && d.files.length)
          safe(function () { l.userDirectoryFilesAddedOrChanged(key, d.files.slice()); });
      });
    }
    if (state.paused && typeof l.setPaused === 'function') safe(function () { l.setPaused(true); });
  }

  function whenLoaded(fn) {
    if (document.readyState === 'complete') setTimeout(fn, 0);
    else window.addEventListener('load', function () { setTimeout(fn, 0); }, { once: true });
  }

  Object.defineProperty(window, 'wallpaperPropertyListener', {
    configurable: true,
    enumerable: true,
    get: function () { return listener; },
    set: function (v) {
      listener = v;
      appliedTo = null;
      whenLoaded(function () { applyAll(listener); });
    },
  });

  window.wallpaperRequestRandomFileForProperty = function (prop, callback) {
    var d = state.directories[prop];
    var files = d && d.files ? d.files : [];
    var file = files.length ? files[Math.floor(Math.random() * files.length)] : '';
    setTimeout(function () { safe(function () { callback(prop, file); }); }, 0);
  };

  window.wallpaperRegisterAudioListener = function (cb) {
    if (typeof cb === 'function') audioListeners.push(cb);
  };
  ['Status', 'Properties', 'Thumbnail', 'Playback', 'Timeline'].forEach(function (n) {
    window['wallpaperRegisterMedia' + n + 'Listener'] = function (cb) {
      if (typeof cb === 'function') mediaListeners[n.toLowerCase()].push(cb);
    };
  });

  function setMediaPaused(paused) {
    var media = document.querySelectorAll('video, audio');
    media.forEach(function (m) {
      if (paused) {
        if (!m.paused) { pausedByUs.add(m); m.pause(); }
      } else if (pausedByUs.has(m)) {
        var p = m.play();
        if (p && p.catch) p.catch(function () {});
      }
    });
    if (!paused) pausedByUs.clear();
  }

  window.__wf = {
    version: 1,
    state: state,
    applyUser: function (changed) {
      Object.keys(changed || {}).forEach(function (k) { state.user[k] = changed[k]; });
      window.dispatchEvent(new CustomEvent('wf-user-properties', { detail: clone(changed) }));
      if (listener && typeof listener.applyUserProperties === 'function')
        safe(function () { listener.applyUserProperties(clone(changed)); });
    },
    applyGeneral: function (changed) {
      Object.keys(changed || {}).forEach(function (k) { state.general[k] = changed[k]; });
      if (listener && typeof listener.applyGeneralProperties === 'function')
        safe(function () { listener.applyGeneralProperties(clone(changed)); });
    },
    applyPlayback: function (o) {
      state.playback = Object.assign({}, state.playback, o || {});
      window.dispatchEvent(new CustomEvent('wf-playback', { detail: clone(state.playback) }));
    },
    setPaused: function (paused) {
      paused = !!paused;
      if (state.paused === paused) return;
      state.paused = paused;
      setMediaPaused(paused);
      window.dispatchEvent(new CustomEvent('wf-paused', { detail: paused }));
      if (listener && typeof listener.setPaused === 'function') safe(function () { listener.setPaused(paused); });
    },
  };
})();
