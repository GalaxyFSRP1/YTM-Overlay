/* This whole script is embedded in a user-clicked bookmark; no extension or file is required. */
(function () {
  'use strict';
  if (location.origin !== 'https://music.youtube.com') {
    alert('Open music.youtube.com first, then click the YTM Live Sync bookmark.');
    return;
  }
  if (window.__ytmOverlayBridge && typeof window.__ytmOverlayBridge.stop === 'function') {
    window.__ytmOverlayBridge.stop();
    return;
  }
  const key = '__BRIDGE_KEY__';
  const endpoint = 'http://127.0.0.1:__BRIDGE_PORT__/api/bridge';
  const session = (globalThis.crypto && crypto.randomUUID) ? crypto.randomUUID() : Date.now() + '-' + Math.random().toString(36).slice(2);
  let timer = 0, stopped = false, busy = false, announced = false, warned = false, commandBusy = false, controlBusy = false;
  function message(text, duration) {
    let node = document.getElementById('ytm-overlay-bridge-notice');
    if (!node) {
      node = document.createElement('div');
      node.id = 'ytm-overlay-bridge-notice';
      Object.assign(node.style, {position:'fixed',top:'16px',right:'16px',zIndex:'2147483647',
        maxWidth:'360px',padding:'13px 16px',borderRadius:'10px',background:'#171820',color:'#fff',
        font:'14px/1.4 Arial,sans-serif',boxShadow:'0 5px 24px #0008',border:'1px solid #ff2d55'});
      document.body.appendChild(node);
    }
    node.textContent = text;
    const current = text;
    setTimeout(function(){ if(node.isConnected && node.textContent === current) node.remove(); }, duration || 6000);
  }
  function text(el, selector) {
    const x = el && el.querySelector(selector);
    return x ? (x.getAttribute('title') || x.textContent || '').trim() : '';
  }
  function image(el) {
    const x = el && el.querySelector('img.image, img.ytmusicTrackInfoThumbnail, img#img, img');
    return x ? (x.currentSrc || x.src || '') : '';
  }
  function id(el) {
    try { return el.data.navigationEndpoint.watchEndpoint.videoId || ''; } catch (_) { return ''; }
  }
  function seconds(s) {
    const parts = (s || '').trim().split(':');
    if (parts.length < 2 || parts.length > 3 || parts.some(x => !/^\d+$/.test(x))) return 0;
    return parts.reduce((a, n) => a * 60 + Number(n), 0);
  }
  function snapshot() {
    const bar = document.querySelector('ytmusic-player-bar') || document.querySelector('ytmusic-miniplayer');
    const rows = Array.from(document.querySelectorAll('ytmusic-player-queue ytmusic-player-queue-item'));
    const selected = rows.findIndex(r => r.hasAttribute('selected'));
    const current = selected >= 0 ? rows[selected] : null;
    const video = document.querySelector('video.html5-main-video') || document.querySelector('video');
    const movie = document.querySelector('#movie_player');
    const ad = !!((bar && bar.hasAttribute('is-advertisement')) || (movie && movie.classList.contains('ad-showing')));
    const meta = !ad && navigator.mediaSession ? navigator.mediaSession.metadata : null;
    const selectedTitle = text(current, '.song-title, .ytmusicTrackInfoTitle');
    const barTitle = text(bar, '.ytmusicTrackInfoTitle, .title');
    const title = (meta && meta.title) || barTitle || selectedTitle;
    if (!title) return null;
    let artist = (meta && meta.artist) || text(current, '.byline') || text(bar, '.ytmusicTrackInfoBylineItem, .byline');
    artist = (artist || '').split(' • ')[0].trim();
    let cover = (meta && meta.artwork && meta.artwork.length && meta.artwork[meta.artwork.length-1].src) || image(bar) || image(current);
    const queue = selected < 0 ? [] : rows.slice(selected + 1, selected + 21).map(r => ({
      title:text(r, '.song-title, .ytmusicTrackInfoTitle'), artist:text(r, '.byline, .ytmusicTrackInfoBylineItem'),
      videoId:id(r), thumb:image(r)
    })).filter(r => r.title);
    const duration = !ad && video && Number.isFinite(video.duration) && video.duration > 0
      ? video.duration : seconds(text(current, '.duration'));
    return { key:key, title:title, artist:artist, album:(meta && meta.album) || '',
      videoId:id(current) || new URLSearchParams(location.search).get('v') || '', art:cover || '',
      position:!ad && video && Number.isFinite(video.currentTime) ? video.currentTime : 0,
      duration:duration || 0, rate:video && video.playbackRate > 0 ? video.playbackRate : 1,
      playing:!!(!ad && video && !video.paused && !video.ended && video.readyState >= 3), ad:ad,
      queueKnown:selected >= 0, items:queue, sentAt:Date.now() };
  }
  async function post(value, suffix) {
    const r = await fetch(endpoint + (suffix || ''), {method:'POST',mode:'cors',credentials:'omit',referrerPolicy:'no-referrer',
      targetAddressSpace:'loopback',headers:{'Content-Type':'text/plain'},body:JSON.stringify(value)});
    if (!r.ok) throw new Error('local link rejected');
    return r.json();
  }
  function queueParts() {
    try {
      const q = document.querySelector('ytmusic-player-queue');
      const app = document.querySelector('ytmusic-app');
      const state = q && q.getState && q.getState().queue;
      return q && app && app.networkManager && typeof app.networkManager.fetch === 'function' &&
        typeof q.dispatch === 'function' && state && Array.isArray(state.items) && state.items.length
        ? {q:q, app:app, state:state} : null;
    } catch (_) { return null; }
  }
  function videoOf(item) {
    const p = item && (item.playlistPanelVideoRenderer ||
      (item.playlistPanelVideoWrapperRenderer && item.playlistPanelVideoWrapperRenderer.primaryRenderer &&
       item.playlistPanelVideoWrapperRenderer.primaryRenderer.playlistPanelVideoRenderer));
    return p && p.videoId || '';
  }
  function runsText(value) { return value && value.runs ? value.runs.map(r => r.text || '').join('') : ''; }
  function withTimeout(promise, ms) {
    return Promise.race([promise, new Promise((_,reject) => setTimeout(() => reject(new Error('timeout')),ms))]);
  }
  async function resolveSearch(app, query) {
    const result = await withTimeout(app.networkManager.fetch('/search', {query:query,
      params:'EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D'}),15000);
    const stack = [result]; let visited = 0;
    while (stack.length && visited++ < 30000) {
      const node = stack.pop();
      if (!node || typeof node !== 'object') continue;
      const row = node.musicResponsiveListItemRenderer;
      if (row) {
        let v = '';
        try { v = row.overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchEndpoint.videoId; } catch (_) {}
        if (/^[A-Za-z0-9_-]{11}$/.test(v)) {
          let title = '';
          try { title = runsText(row.flexColumns[0].musicResponsiveListItemFlexColumnRenderer.text); } catch (_) {}
          return {videoId:v, title:title};
        }
      }
      // Reverse push preserves the search response's original result order.
      const values = Array.isArray(node) ? node : Object.values(node);
      for (let i = values.length - 1; i >= 0; --i) if (values[i] && typeof values[i] === 'object') stack.push(values[i]);
    }
    throw new Error('no result');
  }
  async function execute(command) {
    let touched = false, result = 'failed', videoId = '', title = '', reason = '';
    try {
      if (!command || !/^[a-f0-9]{32}$/.test(command.id || '') || !/^[a-f0-9]{32}$/.test(command.claim || '')) return;
      let parts = queueParts();
      if (!parts) throw new Error('Start a song in YouTube Music first.');
      videoId = command.videoId || '';
      if (!videoId) {
        const found = await resolveSearch(parts.app, String(command.query || '').slice(0,160));
        videoId = found.videoId; title = found.title;
      }
      if (!/^[A-Za-z0-9_-]{11}$/.test(videoId)) throw new Error('No playable video was found.');
      parts = queueParts();
      if (!parts || stopped) throw new Error('The music link stopped or the player is not ready.');
      const reply = await withTimeout(parts.app.networkManager.fetch('/music/get_queue', {
        videoIds:[videoId], queueContextParams:parts.state.queueContextParams, queueInsertPosition:'INSERT_AT_END'
      }),15000);
      const additions = Array.isArray(reply && reply.queueDatas)
        ? reply.queueDatas.map(x => x && x.content).filter(x => videoOf(x) === videoId).slice(0,1) : [];
      if (!additions.length) throw new Error('YouTube Music did not return that song for this player.');
      parts = queueParts();
      if (!parts || stopped) throw new Error('The music link stopped or the player is not ready.');
      const before = parts.state.items.filter(x => videoOf(x) === videoId).length;
      const payload = {items:additions, index:parts.state.items.length, nextQueueItemId:parts.state.nextQueueItemId,
        shouldAssignIds:true, shuffleEnabled:false};
      touched = true;
      parts.q.dispatch({type:'ADD_ITEMS', payload:payload});
      const after = parts.q.getState().queue.items.filter(x => videoOf(x) === videoId).length;
      result = after > before ? 'added' : 'uncertain';
      if (!title) {
        const first = additions[0].playlistPanelVideoRenderer;
        title = first ? runsText(first.title) : '';
      }
    } catch (e) {
      result = touched ? 'uncertain' : 'failed';
      const safeReasons = ['Start a song in YouTube Music first.', 'No playable video was found.',
        'The music link stopped or the player is not ready.', 'YouTube Music did not return that song for this player.'];
      reason = e && safeReasons.includes(e.message) ? e.message : 'Search or queue lookup failed. Try a direct YouTube video link.';
    }
    const ack = {key:key,id:command.id,claim:command.claim,result:result,videoId:videoId,title:title,reason:reason};
    let sent = false;
    for (let attempt = 0; attempt < 3 && !sent; ++attempt) {
      try { await post(ack,'/ack'); sent = true; } catch (_) { await new Promise(r => setTimeout(r,1000)); }
    }
    if (!sent) message('Request result could not be confirmed. Check the YouTube Music queue before retrying.',12000);
  }
  function controlButton(action) {
    const bar = document.querySelector('ytmusic-player-bar');
    const selectors = {toggle:'#play-pause-button button', previous:'.previous-button button', next:'.next-button button'};
    const button = bar && selectors[action] && bar.querySelector(selectors[action]);
    return button && !button.disabled && !button.closest('[disabled],[hidden]') ? button : null;
  }
  // Other buttons can be disabled by YouTube Music (for example at the start
  // of a queue); advertise availability based on the central play/pause control.
  function controlsReady() { return !!controlButton('toggle'); }
  async function executeControl(command) {
    if (!command || !/^[a-f0-9]{32}$/.test(command.id || '') || !['toggle','previous','next'].includes(command.action)) return;
    let result = 'failed';
    try {
      const movie = document.querySelector('#movie_player');
      const bar = document.querySelector('ytmusic-player-bar');
      const ad = !!((bar && bar.hasAttribute('is-advertisement')) || (movie && movie.classList.contains('ad-showing')));
      const button = !stopped && !ad && controlButton(command.action);
      if (button) { button.click(); result = 'clicked'; }
    } catch (_) {}
    const ack = {key:key,session:session,id:command.id,result:result};
    let sent = false;
    for (let attempt = 0; attempt < 3 && !sent; ++attempt) {
      try { await post(ack,'/control-ack'); sent = true; } catch (_) { await new Promise(r => setTimeout(r,1000)); }
    }
    if (!sent) message('Player control result could not be confirmed. Check the player before repeating it.',12000);
  }
  async function send() {
    if (stopped || busy) return;
    busy = true;
    try {
      const data = snapshot();
      if (data) {
        data.session = session;
        data.canQueue = !commandBusy && !controlBusy && !!queueParts();
        data.canControl = !commandBusy && !controlBusy && !data.ad && controlsReady();
      }
      const answer = await post(data || {key:key,hello:true});
      if (answer && answer.transport && !controlBusy) {
        controlBusy = true;
        executeControl(answer.transport).catch(function(){}).finally(function(){ controlBusy = false; });
      } else if (answer && answer.command && !commandBusy) {
        commandBusy = true;
        execute(answer.command).catch(function(){}).finally(function(){ commandBusy = false; });
      }
      if (!announced) {
        message(data ? 'YTM Overlay: browser linked. Keep this tab open.' : 'YTM Overlay linked. Start a song to share the timer and queue.');
        announced = true;
      }
      warned = false;
    } catch (_) {
      if (!warned) {
        message('YTM Overlay could not connect. Allow “Apps on device” / local network access for YouTube Music, and make sure the overlay app is running.', 12000);
        warned = true;
      }
    } finally { busy = false; }
  }
  window.__ytmOverlayBridge = { stop:function(){
    if (stopped) return;
    stopped = true; clearInterval(timer); delete window.__ytmOverlayBridge;
    post({key:key,stop:true}).catch(function(){});
    message('YTM Overlay browser link stopped. Windows media detection is still available.');
  }};
  // The first request runs inside the bookmark click, so Chrome can prompt for
  // "Apps on device" permission before subsequent background updates.
  send();
  timer = setInterval(send, 700);
})();
