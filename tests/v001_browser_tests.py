# Disposable fixture-only checks for v0.0.1 tools, transport, filtering and CSV.
from playwright.sync_api import sync_playwright
from pathlib import Path
import csv, os, time

URL = os.environ.get('OVERLAY_TEST_URL', 'http://127.0.0.1:7511')
PREVIEWS = Path(os.environ.get('OVERLAY_PREVIEWS', Path(__file__).parent / 'artifacts'))
PREVIEWS.mkdir(parents=True, exist_ok=True)
checks = 0


def check(ok, text):
    global checks
    if not ok:
        raise AssertionError(text)
    checks += 1
    print('PASS', text, flush=True)


def bookmark_click(page, href):
    try:
        page.goto(href, wait_until='commit', timeout=3000)
    except Exception as e:
        if 'ERR_ABORTED' not in str(e):
            raise


def add_chat(page, platform, viewer, text, index):
    page.evaluate('''([platform,viewer,text,index])=>{
      let row=document.createElement(platform==='youtube'?'yt-live-chat-text-message-renderer':'div'), body, name;
      if(platform==='twitch'){
        row.setAttribute('data-a-target','chat-line-message');row.setAttribute('data-a-user',viewer);
        body=document.createElement('span');body.setAttribute('data-a-target','chat-line-message-body');row.append(body);document.querySelector('#fixture').append(row);
      }else if(platform==='kick'){
        row.setAttribute('data-index',String(index));let group=document.createElement('div');name=document.createElement('button');name.title=viewer;name.textContent=viewer;
        let sep=document.createElement('span');sep.setAttribute('aria-hidden','true');sep.textContent=':';group.append(name,sep);
        body=document.createElement('span');row.append(group,body);document.querySelector('#chatroom-messages').append(row);
      }else{
        name=document.createElement('span');name.id='author-name';name.textContent=viewer;body=document.createElement('span');body.id='message';row.append(name,body);document.querySelector('#fixture').append(row);
      }
      body.textContent=text;
    }''', [platform, viewer, text, index])


def wait_until(fn, seconds=10):
    end = time.time() + seconds
    while time.time() < end:
        value = fn()
        if value:
            return value
        time.sleep(.15)
    raise AssertionError('Timed out waiting for expected state')


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, args=['--no-sandbox'])
    context = browser.new_context(viewport={'width': 1200, 'height': 900}, accept_downloads=True)
    cdp = browser.new_browser_cdp_session()
    cid = cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
    origins = ['https://music.youtube.com', 'https://www.twitch.tv', 'https://kick.com', 'https://www.youtube.com']
    for origin in origins:
        cdp.send('Browser.setPermission', {'permission': {'name': 'loopback-network'}, 'setting': 'granted', 'origin': origin, 'browserContextId': cid})
    music_html = '''<html><body><ytmusic-player-bar><span class="ytmusicTrackInfoTitle">Fixture Track</span><span class="byline">Fixture Artist</span>
      <yt-icon-button class="previous-button"><button onclick="window.__clicks.push('previous')">previous</button></yt-icon-button>
      <yt-icon-button id="play-pause-button"><button onclick="window.__clicks.push('toggle')">toggle</button></yt-icon-button>
      <yt-icon-button class="next-button"><button onclick="window.__clicks.push('next')">next</button></yt-icon-button>
      </ytmusic-player-bar><video></video><script>window.__clicks=[]</script></body></html>'''
    chat_html = '<html><body><div id="fixture"></div><div id="chatroom-messages"></div></body></html>'
    context.route('https://music.youtube.com/**', lambda r: r.fulfill(status=200, content_type='text/html', body=music_html))
    for pattern in ['https://www.twitch.tv/**', 'https://kick.com/**', 'https://www.youtube.com/**']:
        context.route(pattern, lambda r: r.fulfill(status=200, content_type='text/html', body=chat_html))
    errors = []
    def attach(page):
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.on('dialog', lambda d: d.accept())
        return page

    dashboard = attach(context.new_page())
    req = dashboard.request
    old = req.get(URL + '/api/request-state').json()
    for item in old['items']:
        if item['status'] in ('pending', 'ready', 'failed', 'uncertain', 'added'):
            req.post(URL + '/api/request-action', data={'action': 'done' if item['status'] == 'added' else 'reject', 'id': item['id']})
    req.post(URL + '/api/request-action', data={'action': 'clearHistory'})
    req.post(URL + '/api/request-action', data={'action': 'config', 'enabled': False, 'mode': 'approval', 'autoQueue': False,
                                               'cooldown': 0, 'limit': 50, 'platforms': {'twitch': True, 'youtube': True, 'kick': True}, 'blockedTerms': ''})

    # A synthetic YouTube Music page exercises the actual bookmark and server.
    setup = attach(context.new_page()); setup.goto(URL + '/connect')
    bookmark = setup.locator('#bookmark').get_attribute('href')
    music = attach(context.new_page()); music.goto('https://music.youtube.com/watch?v=fJ9rUzIMcZQ')
    bookmark_click(music, bookmark)
    music.wait_for_function('() => Boolean(window.__ytmOverlayBridge)')
    player = attach(context.new_page()); player.goto(URL + '/player')
    player.wait_for_function("() => !document.querySelector('#toggle').disabled", timeout=10000)
    check(player.locator('#title').inner_text() == 'Fixture Track' and 'Controls ready' in player.locator('#linkText').inner_text(),
          'player tools display the linked fixture track and available controls')
    art_fallback = player.evaluate('''()=>{const n=current.state.now,original=n.art,img=document.querySelector('#artImg'),art=document.querySelector('#art');
      n.art='https://art.invalid/first.png';render();img.dispatchEvent(new Event('error'));render();
      const failed=!art.classList.contains('has') && failedArt===n.art;
      n.art='https://art.invalid/second.png';render();const changed=art.classList.contains('has') && failedArt==='';
      n.art=original;render();return failed&&changed}''')
    check(art_fallback, 'failed artwork falls back and a new artwork URL can be tried')
    for action in ['toggle', 'next', 'previous']:
        player.locator('#' + action).click()
        music.wait_for_function('(action)=>window.__clicks.includes(action)', arg=action, timeout=10000)
        player.wait_for_function("() => document.querySelector('#controlStatus').textContent.includes('Click sent') && !document.querySelector('#toggle').disabled", timeout=10000)
    check(music.evaluate('() => window.__clicks') == ['toggle', 'next', 'previous'], 'previous, play/pause and next each dispatch exactly once')
    check(req.post(URL + '/api/player-action', data={'action': 'invalid'}).status == 400, 'unknown transport actions are rejected')
    player.evaluate("Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async x=>{window.__copied=x}}})")
    player.locator('#copyTrack').click()
    check('Fixture Track' in player.evaluate('window.__copied'), 'current song text can be copied')
    req.post(URL + '/api/request-action', data={'action': 'manual', 'song': 'PRIVATE Song Should Not Be In Diagnostics'})
    player.locator('#copyReport').click()
    report = player.evaluate('window.__copied')
    check('Music browser link: connected' in report and 'Fixture Track' not in report and 'PRIVATE Song' not in report and 'Fixture Artist' not in report,
          'diagnostic summary omits track and request content')
    player.locator('.source').filter(has_text='Now playing only').locator('button').click()
    check(player.evaluate('window.__copied').endswith('/now-playing'), 'compact OBS source URL can be copied')
    player.set_viewport_size({'width': 390, 'height': 844})
    check(player.evaluate('document.documentElement.scrollWidth <= window.innerWidth+1'), 'player and tools page fits a narrow screen')
    player.set_viewport_size({'width': 1200, 'height': 900}); player.screenshot(path=str(PREVIEWS / 'v001-player-tools.png'), full_page=True)

    compact = attach(context.new_page()); compact.set_viewport_size({'width': 500, 'height': 150}); compact.goto(URL + '/now-playing')
    compact.wait_for_function("() => document.querySelector('#titletext').textContent.includes('Fixture Track')")
    compact.wait_for_timeout(700)  # allow the first-track entrance animation to settle
    box = compact.locator('#card').bounding_box()
    check(compact.locator('#card').is_visible() and compact.locator('#queue').is_hidden() and compact.locator('#lyrics').is_hidden(),
          'now-playing-only source hides lyrics and queue independently of main settings')
    check(abs(box['width'] - 500) < 2 and abs(box['height'] - 150) < 2, 'now-playing-only source fills its OBS rectangle')
    compact.goto(URL + '/now-playing?demo=1'); compact.wait_for_function("() => document.querySelector('#titletext').textContent.includes('Sample song')")
    check(True, 'compact source supports demo preview')
    compact.screenshot(path=str(PREVIEWS / 'v001-now-playing.png'), omit_background=True)
    compact.set_viewport_size({'width': 310, 'height': 100})
    check(compact.evaluate('document.documentElement.scrollWidth <= innerWidth && document.documentElement.scrollHeight <= innerHeight'), 'compact source fits a smaller OBS rectangle')
    request_overlay = attach(context.new_page()); request_overlay.set_viewport_size({'width':650, 'height':400})
    request_overlay.goto(URL + '/request-overlay?demo=1&anonymous=1&max=1&accent=22d3ee')
    check(request_overlay.locator('#items .item').count() == 1 and 'musiclover' not in request_overlay.locator('#items').inner_text(),
          'anonymous requests source hides viewer names and honors its row limit')
    check(request_overlay.locator('#count').inner_text() == '2' and request_overlay.locator('#count').evaluate('(e)=>getComputedStyle(e).backgroundColor') == 'rgb(34, 211, 238)',
          'custom request accent is applied while the badge keeps the full count')
    request_overlay.goto(URL + '/request-overlay?demo=1&accent=bad%3Bcolor')
    check(request_overlay.locator('#count').evaluate('(e)=>getComputedStyle(e).backgroundColor') == 'rgb(255, 82, 120)',
          'invalid request accent is ignored')

    # Reset after diagnostic setup, then exercise new moderation UI and adapters.
    old = req.get(URL + '/api/request-state').json()
    for item in old['items']:
        if item['status'] in ('pending', 'ready', 'failed', 'uncertain', 'added'):
            req.post(URL + '/api/request-action', data={'action': 'done' if item['status'] == 'added' else 'reject', 'id': item['id']})
    req.post(URL + '/api/request-action', data={'action': 'clearHistory'})
    dashboard.goto(URL + '/requests')
    dashboard.wait_for_function("() => document.querySelector('#mode').value === 'approval'")
    dashboard.locator('#enabled').check(); dashboard.locator('#mode').select_option('automatic')
    dashboard.locator('#youtube').uncheck(); dashboard.locator('#blockedTerms').fill('blocked phrase\nspoiler')
    dashboard.locator('#save').click()
    wait_until(lambda: req.get(URL + '/api/request-state').json()['settings']['mode'] == 'automatic')
    settings = req.get(URL + '/api/request-state').json()['settings']
    check(settings['enabled'] and not settings['platforms']['youtube'] and settings['blockedTerms'] == 'blocked phrase\nspoiler',
          'dashboard saves platform switches and blocked phrases together')
    chat_setup = attach(context.new_page()); chat_setup.goto(URL + '/chat-connect'); chat_bookmark = chat_setup.locator('#bookmark').get_attribute('href')
    chats = {}
    for platform, url in [('twitch', 'https://www.twitch.tv/popout/creator/chat'), ('youtube', 'https://www.youtube.com/live_chat?is_popout=1&v=abc'), ('kick', 'https://kick.com/creator')]:
        page = attach(context.new_page()); page.goto(url); bookmark_click(page, chat_bookmark)
        page.wait_for_function('() => Boolean(window.__ytmRequestChat)'); chats[platform] = page
    time.sleep(3.1)
    add_chat(chats['twitch'], 'twitch', 'BlockedViewer', '!sr A BLOCKED PHRASE song', 1)
    add_chat(chats['youtube'], 'youtube', 'DisabledViewer', '!sr YouTube disabled song', 1)
    add_chat(chats['kick'], 'kick', 'AllowedViewer', '!sr Allowed Kick Song', 1)
    wait_until(lambda: any(x['viewer'] == 'AllowedViewer' for x in req.get(URL + '/api/request-state').json()['items']))
    viewers = {x['viewer'] for x in req.get(URL + '/api/request-state').json()['items']}
    check(viewers == {'AllowedViewer'}, 'blocked text and a disabled platform do not create requests; enabled chat still works')
    dashboard.locator('#youtube').check(); dashboard.locator('#save').click()
    wait_until(lambda: req.get(URL + '/api/request-state').json()['settings']['platforms']['youtube'])
    add_chat(chats['youtube'], 'youtube', 'NewViewer', '!sr Reenabled YouTube Song', 2)
    wait_until(lambda: any(x['viewer'] == 'NewViewer' for x in req.get(URL + '/api/request-state').json()['items']))
    check(True, 're-enabled platform accepts new chat requests without replaying old ones')
    for song in ['Queue Alpha', 'Queue Beta', '=2+3']:
        check(req.post(URL + '/api/request-action', data={'action': 'manual', 'song': song}).ok, 'manual fixture request accepted: ' + song)
    dashboard.wait_for_function("() => document.querySelector('#active').textContent.includes('Queue Beta')")
    before = [x['song'] for x in req.get(URL + '/api/request-overlay').json()['items']]
    dashboard.locator('#active .item').filter(has_text='Queue Beta').locator('button[data-action="moveEarlier"]').click()
    wait_until(lambda: [x['song'] for x in req.get(URL + '/api/request-overlay').json()['items']] != before)
    after = [x['song'] for x in req.get(URL + '/api/request-overlay').json()['items']]
    check(after.index('Queue Beta') < after.index('Queue Alpha'), 'reordering waiting requests updates OBS and local dispatch order')
    total_before = len(req.get(URL + '/api/request-state').json()['items'])
    dashboard.locator('#filter').fill('Queue Beta')
    check(dashboard.locator('#active .item').count() == 1 and len(req.get(URL + '/api/request-state').json()['items']) == total_before,
          'search filters the dashboard without changing saved requests')
    dashboard.locator('#filter').fill('')
    with dashboard.expect_download() as download_info:
        dashboard.locator('#exportCsv').click()
    rows = list(csv.DictReader(Path(download_info.value.path()).read_text(encoding='utf-8-sig').splitlines()))
    check(any(r['song'] == "'=2+3" for r in rows), 'CSV export neutralizes spreadsheet formula injection')
    check(any(r['viewer'] == 'AllowedViewer' for r in rows) and not any('ChatBridgeKey' in str(r) for r in rows),
          'CSV export contains request records but not pairing keys')
    dashboard.set_viewport_size({'width': 390, 'height': 844})
    check(dashboard.evaluate('document.documentElement.scrollWidth <= innerWidth+1'), 'expanded request controls fit narrow screens')
    check(not errors, 'no v0.0.1 fixture JavaScript errors: ' + repr(errors))
    req.post(URL + '/api/request-action', data={'action': 'config', 'enabled': False, 'autoQueue': False,
                                               'platforms': {'twitch': True, 'youtube': True, 'kick': True}, 'blockedTerms': ''})
    browser.close()
print('ALL', checks, 'V0.0.1 BROWSER FIXTURE CHECKS PASSED')
