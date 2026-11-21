from playwright.sync_api import sync_playwright, TimeoutError as PwTimeout
import time, os

URL = os.environ.get('OVERLAY_TEST_URL', 'http://127.0.0.1:7511')
checks = 0

def check(value, label):
    global checks
    if not value: raise AssertionError(label)
    checks += 1
    print('PASS', label, flush=True)

def js_bookmark(page, bookmark):
    try:
        page.goto(bookmark, wait_until='commit', timeout=3000)
    except Exception as e:
        # A javascript: bookmark is executed in the current document; there is
        # no navigation response, so Playwright normally reports ERR_ABORTED.
        if 'ERR_ABORTED' not in str(e): raise

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, args=['--no-sandbox','--autoplay-policy=no-user-gesture-required'])
    context = browser.new_context(user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36')
    cdp = browser.new_browser_cdp_session()
    context_id = cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
    cdp.send('Browser.setPermission', {'permission':{'name':'loopback-network'}, 'setting':'granted',
                                      'origin':'https://music.youtube.com','browserContextId':context_id})
    setup = context.new_page()
    setup.goto(URL+'/connect',wait_until='load')
    bookmark = setup.locator('#bookmark').get_attribute('href')
    check(bookmark.startswith('javascript:'), 'embedded setup provides a bookmarklet')
    page = context.new_page()
    page.goto('https://music.youtube.com/watch?v=fJ9rUzIMcZQ',wait_until='domcontentloaded',timeout=30000)
    page.wait_for_selector('ytmusic-player-queue-item[selected]',timeout=30000)
    js_bookmark(page,bookmark)
    page.wait_for_function("() => Boolean(window.__ytmOverlayBridge)",timeout=5000)
    page.wait_for_function("() => document.querySelector('#ytm-overlay-bridge-notice')?.textContent.includes('linked')",timeout=10000)
    check(True, 'bookmark executes on the live YouTube Music page and links to localhost')
    def getstate(): return setup.request.get(URL+'/api/queue-state').json()
    for _ in range(25):
        state = getstate()
        if state['queue']['source']=='browser': break
        time.sleep(.2)
    check(state['queue']['source']=='browser', 'real page reports an actual queue rather than public suggestions')
    for _ in range(20):
        dom = page.evaluate("""() => {let a=[...document.querySelectorAll('ytmusic-player-queue ytmusic-player-queue-item')];
            let i=a.findIndex(x=>x.hasAttribute('selected')); return a.slice(i+1,i+5).map(x=>x.querySelector('.song-title')?.textContent.trim())}""")
        state = getstate()
        actual = [x['title'] for x in state['queue']['items'][:4]]
        if len(actual)>=2 and actual==dom: break
        time.sleep(.3)
    check(len(actual)>=2 and actual==dom, 'first four queue entries match the live page in order: '+repr((actual,dom)))
    if page.evaluate("() => !!document.querySelector('#movie_player.ad-showing')"):
        now = state['now']
        check(not now['playing'] and now['sourceLabel'].endswith('(ad)'), 'advertisement does not run the song timer')
    # Allow the ad to finish. No ad is skipped or blocked.
    page.wait_for_function("""() => {let v=document.querySelector('video.html5-main-video');
       return v && !document.querySelector('#movie_player.ad-showing') && Number.isFinite(v.duration) && v.duration>60 && v.currentTime>1 && !v.paused}""",timeout=70000)
    time.sleep(1.1)
    video_time = page.evaluate("() => document.querySelector('video.html5-main-video').currentTime")
    state = getstate()
    check(state['now']['playing'] and abs(state['now']['position'] - video_time) < 1.0,
          'live browser clock agrees with the real playing video to within one second')
    page.evaluate("() => document.querySelector('video.html5-main-video').currentTime = 48")
    time.sleep(1.5)
    video_time = page.evaluate("() => document.querySelector('video.html5-main-video').currentTime")
    state = getstate()
    check(video_time >= 47 and abs(state['now']['position'] - video_time) < 1.2,
          'forward seek in the real player is reflected by the overlay clock')
    page.evaluate("() => document.querySelector('video.html5-main-video').pause()")
    time.sleep(1.2)
    paused = getstate()['now']; time.sleep(1.2); paused2 = getstate()['now']
    check(not paused['playing'] and not paused2['playing'] and abs(paused['position']-paused2['position']) < .15,
          'real player pause freezes the overlay clock')
    page.evaluate("() => document.querySelector('video.html5-main-video').play()")
    time.sleep(1.3)
    check(getstate()['now']['playing'], 'real player resume restarts the overlay clock')
    page.evaluate("() => document.querySelector('video.html5-main-video').currentTime = 15")
    time.sleep(1.5)
    video_time = page.evaluate("() => document.querySelector('video.html5-main-video').currentTime")
    state = getstate()
    check(video_time < 25 and abs(state['now']['position'] - video_time) < 1.2,
          'backward seek is honored rather than suppressed as jitter')
    js_bookmark(page, bookmark)
    page.wait_for_function("() => !window.__ytmOverlayBridge",timeout=5000)
    for _ in range(15):
        if getstate()['queue']['source'] != 'browser': break
        time.sleep(.2)
    check(getstate()['queue']['source'] != 'browser', 'clicking bookmark again disconnects the live browser')
    browser.close()
print('ALL',checks,'LIVE YOUTUBE MUSIC CHECKS PASSED')
