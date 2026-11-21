# Optional live integration test. Uses a disposable signed-out YouTube Music
# profile and changes only its playback/queue position; it never posts to chat.
from playwright.sync_api import sync_playwright
import os, time

URL = os.environ.get('OVERLAY_TEST_URL', 'http://127.0.0.1:7511')
checks = 0


def check(value, label):
    global checks
    if not value: raise AssertionError(label)
    checks += 1; print('PASS', label, flush=True)


def bookmark_click(page, href):
    try: page.goto(href, wait_until='commit', timeout=3000)
    except Exception as e:
        if 'ERR_ABORTED' not in str(e): raise


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, args=['--no-sandbox', '--autoplay-policy=no-user-gesture-required', '--mute-audio'])
    context = browser.new_context(user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36')
    cdp = browser.new_browser_cdp_session(); cid = cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
    cdp.send('Browser.setPermission', {'permission': {'name': 'loopback-network'}, 'setting': 'granted', 'origin': 'https://music.youtube.com', 'browserContextId': cid})
    setup = context.new_page(); setup.goto(URL + '/connect'); bookmark = setup.locator('#bookmark').get_attribute('href')
    music = context.new_page(); errors=[]; music.on('pageerror', lambda e: errors.append(str(e)))
    music.goto('https://music.youtube.com/watch?v=fJ9rUzIMcZQ', wait_until='domcontentloaded', timeout=30000)
    music.wait_for_selector('ytmusic-player-queue-item[selected]', timeout=35000)
    bookmark_click(music, bookmark)
    music.wait_for_function("() => document.querySelector('#ytm-overlay-bridge-notice')?.textContent.includes('linked')", timeout=12000)
    check(True, 'updated bookmark links to a real signed-out YouTube Music page')
    # The signed-out player can show preroll ads. Wait naturally and, if its
    # standard Skip button is offered, click it just as a viewer would. Never
    # dispatch a transport action while an ad is playing.
    deadline = time.monotonic() + 90
    status = {}
    while time.monotonic() < deadline:
        status = setup.request.get(URL + '/api/player-status').json()
        if status.get('available'):
            break
        skip = music.locator('.ytp-ad-skip-button-modern, .ytp-ad-skip-button').first
        if skip.count() and skip.is_visible():
            try: skip.click(timeout=2000)
            except Exception: pass
        time.sleep(.5)
    if not status.get('available'):
        diagnostics = music.evaluate('''()=>({ad:!!document.querySelector('#movie_player.ad-showing'),
          playerBar:!!document.querySelector('ytmusic-player-bar'),
          playButton:!!document.querySelector('ytmusic-player-bar #play-pause-button button'),
          videoPaused:document.querySelector('video.html5-main-video')?.paused})''')
        raise AssertionError('Real player controls did not become available: '+repr(diagnostics))
    player = context.new_page(); player.goto(URL + '/player')
    player.wait_for_function("() => !document.querySelector('#toggle').disabled", timeout=15000)
    check(True, 'real music page reports playback controls available')
    music.wait_for_selector('video.html5-main-video', timeout=10000)
    before = music.evaluate("() => document.querySelector('video.html5-main-video').paused")
    player.locator('#toggle').click()
    music.wait_for_function("before => document.querySelector('video.html5-main-video').paused !== before", arg=before, timeout=12000)
    player.wait_for_function("() => document.querySelector('#controlStatus').textContent.includes('Click sent') && !document.querySelector('#toggle').disabled", timeout=12000)
    check(True, 'play/pause actually changes the real disposable player state')
    player.locator('#toggle').click()
    music.wait_for_function("before => document.querySelector('video.html5-main-video').paused === before", arg=before, timeout=12000)
    player.wait_for_function("() => !document.querySelector('#next').disabled", timeout=12000)
    check(True, 'second play/pause restores the original player state')
    selected = music.evaluate("() => document.querySelector('ytmusic-player-queue-item[selected]')?.data?.navigationEndpoint?.watchEndpoint?.videoId || ''")
    player.locator('#next').click()
    music.wait_for_function("id => !!document.querySelector('ytmusic-player-queue-item[selected]')?.data?.navigationEndpoint?.watchEndpoint?.videoId && document.querySelector('ytmusic-player-queue-item[selected]').data.navigationEndpoint.watchEndpoint.videoId !== id", arg=selected, timeout=15000)
    check(True, 'next advances to another track in the real disposable player')
    check(not errors, 'no injected music-link JavaScript errors: ' + repr(errors))
    browser.close()
print('ALL', checks, 'LIVE PLAYER CONTROL CHECKS PASSED')
