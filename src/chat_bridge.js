/* Installed as an explicitly clicked bookmark on the broadcaster's chat tab. */
(function () {
  'use strict';
  const origins = {'https://www.twitch.tv':'twitch','https://twitch.tv':'twitch',
    'https://www.youtube.com':'youtube','https://youtube.com':'youtube',
    'https://kick.com':'kick','https://www.kick.com':'kick'};
  const platform = origins[location.origin];
  if (!platform || (platform === 'youtube' && !location.pathname.startsWith('/live_chat'))) {
    alert('Open your Twitch or Kick chat, or your YouTube Live POP-OUT chat, then click the chat link bookmark.');
    return;
  }
  if (window.__ytmRequestChat && typeof window.__ytmRequestChat.stop === 'function') {
    window.__ytmRequestChat.stop(); return;
  }
  const key = '__CHAT_KEY__';
  const endpoint = 'http://127.0.0.1:__BRIDGE_PORT__/api/chat';
  const session = (globalThis.crypto && crypto.randomUUID) ? crypto.randomUUID() : Date.now() + '-' + Math.random().toString(36).slice(2);
  const parts = location.pathname.split('/').filter(Boolean);
  let channel = location.pathname.replace(/\/+$/, '').slice(0, 100) || 'live chat';
  if (platform === 'twitch') channel = (parts[0] === 'popout' ? parts[1] : parts[0]) || 'live chat';
  if (platform === 'kick') channel = parts[0] || 'live chat';
  if (platform === 'youtube') channel = 'live chat ' + (new URLSearchParams(location.search).get('v') || 'pop-out');
  let stopped = false, sending = false, warned = false, connected = false, counter = 0;
  const pending = [], seen = new Set(), seenOrder = [];
  const started = Date.now();
  let observer, scanTimer, sendTimer, scanSoon = 0;
  function notice(value, ms) {
    let e = document.getElementById('ytm-request-chat-notice');
    if (!e) {
      e = document.createElement('div'); e.id = 'ytm-request-chat-notice';
      Object.assign(e.style,{position:'fixed',right:'16px',top:'16px',zIndex:'2147483647',maxWidth:'360px',
        padding:'12px 15px',borderRadius:'10px',background:'#171820',color:'#fff',border:'1px solid #a682ff',
        boxShadow:'0 5px 24px #0008',font:'14px/1.4 Arial,sans-serif'});
      document.body.appendChild(e);
    }
    e.textContent = value;
    setTimeout(function(){ if(e.isConnected && e.textContent === value) e.remove(); }, ms || 6500);
  }
  function getText(e) { return e ? (e.textContent || '').replace(/[\u200b-\u200d\ufeff]/g,'').replace(/\s+/g,' ').trim() : ''; }
  function videoLink(href) {
    try {
      let u = new URL(href);
      const allowed = h => ['youtube.com','www.youtube.com','music.youtube.com','m.youtube.com','youtu.be'].includes(h);
      if (u.protocol !== 'https:' || !allowed(u.hostname)) return '';
      // YouTube chat can wrap external links in a local /redirect URL.
      if (u.hostname.endsWith('youtube.com') && u.pathname === '/redirect') {
        u = new URL(u.searchParams.get('q') || u.searchParams.get('url') || '');
      }
      return u.protocol === 'https:' && allowed(u.hostname) ? u.href : '';
    } catch (_) { return ''; }
  }
  function message(row) {
    let author = '', body = null;
    if (platform === 'twitch') {
      author = row.getAttribute('data-a-user') || getText(row.querySelector('[data-a-target="chat-message-username"], .chat-author__display-name'));
      body = row.querySelector('[data-a-target="chat-line-message-body"]');
    } else if (platform === 'youtube') {
      author = getText(row.querySelector('#author-name'));
      body = row.querySelector('#message');
    } else {
      let name = row.querySelector('button[title]');
      author = name ? (name.getAttribute('title') || getText(name)) : '';
      const sep = Array.from(row.querySelectorAll('span[aria-hidden="true"]')).find(x => getText(x) === ':');
      body = sep && (sep.nextElementSibling || (sep.parentElement && sep.parentElement.nextElementSibling));
    }
    let text = getText(body);
    // Chat pages sometimes abbreviate a displayed YouTube link. Preserve its
    // real href only when the message is an !sr command with one link.
    if (body && /^!(?:sr|songrequest)\s+/i.test(text)) {
      const links = body.querySelectorAll('a[href]');
      if (links.length === 1) {
        const href = videoLink(links[0].href || '');
        const shown = getText(links[0]);
        if (href && shown && text.includes(shown)) text = text.replace(shown, href);
      }
    }
    return {author:author.trim(), text:text};
  }
  function remember(signature) {
    if (seen.has(signature)) return false;
    seen.add(signature); seenOrder.push(signature);
    if (seenOrder.length > 5000) seen.delete(seenOrder.shift());
    return true;
  }
  function scan() {
    scanSoon = 0;
    if (stopped) return;
    const selector = platform === 'twitch' ? '[data-a-target="chat-line-message"]' :
      platform === 'youtube' ? 'yt-live-chat-text-message-renderer' : '#chatroom-messages [data-index]';
    for (const row of document.querySelectorAll(selector)) {
      const m = message(row);
      if (!m.author || !/^!(?:sr|songrequest)\s+\S/i.test(m.text)) continue;
      // Never replay the history visible while the bookmark starts. An identical
      // message from the same viewer is de-duplicated for this link session.
      const signature = m.author.toLowerCase() + '\u0000' + m.text;
      if (!remember(signature) || Date.now() - started < 2500) continue;
      if (pending.length >= 100) { pending.shift(); notice('YTM Overlay chat buffer full: some requests may have been missed.',12000); }
      pending.push({id:session + '-' + (++counter),author:m.author.slice(0,80),text:m.text.slice(0,400)});
    }
    if (pending.length) send();
  }
  function scheduleScan() { if (!scanSoon && !stopped) scanSoon = setTimeout(scan,250); }
  async function send(stop) {
    if ((!stop && stopped) || sending) return;
    sending = true;
    const batch = stop ? [] : pending.slice(0,20);
    try {
      const r = await fetch(endpoint,{method:'POST',mode:'cors',credentials:'omit',referrerPolicy:'no-referrer',
        targetAddressSpace:'loopback',headers:{'Content-Type':'text/plain'},
        body:JSON.stringify({key:key,platform:platform,session:session,channel:channel,stop:!!stop,messages:batch})});
      if (!r.ok) throw new Error('link rejected');
      if (batch.length) pending.splice(0,batch.length);
      if (!connected && !stop) { notice('YTM Overlay chat link active. Wait 3 seconds, then test !sr in chat.'); connected = true; }
      warned = false;
    } catch (_) {
      if (!warned && !stop) {
        notice('YTM Overlay chat cannot reach the app. Allow local-network / Apps on device permission for this site, and check the app is running.',14000);
        warned = true;
      }
    } finally { sending = false; }
  }
  window.__ytmRequestChat = {stop:function(){
    if (stopped) return;
    stopped = true; clearInterval(scanTimer); clearInterval(sendTimer); clearTimeout(scanSoon);
    if (observer) observer.disconnect(); delete window.__ytmRequestChat;
    send(true); notice('YTM Overlay chat link stopped.');
  }};
  // The first request happens immediately in the user's click, allowing browsers
  // to display their device/loopback permission prompt.
  send(); scan();
  observer = new MutationObserver(scheduleScan);
  observer.observe(document.body,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['data-index']});
  scanTimer = setInterval(scan,1000);
  sendTimer = setInterval(send,3000);
})();
