from playwright.sync_api import sync_playwright
import copy, json, time, os
from pathlib import Path

PREVIEWS=Path(os.environ.get("OVERLAY_PREVIEWS", Path(__file__).parent / "artifacts"))
PREVIEWS.mkdir(parents=True, exist_ok=True)

URL = 'http://127.0.0.1:7511'
checks = 0

def check(x, name):
    global checks
    if not x:
        raise AssertionError(name)
    checks += 1
    print('PASS', name)

with sync_playwright() as p:
    b = p.chromium.launch(headless=True, args=['--no-sandbox'])
    page = b.new_page(viewport={'width':1100, 'height':240}, device_scale_factor=1)
    errors = []
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.goto(URL+'/', wait_until='load')
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Second test line')", timeout=5000)
    check(page.locator('#lyrics').is_visible(), 'real server -> main overlay draws the lyrics card')
    check(page.locator('.lyr-line.active').inner_text() == 'Second test line', 'synced line matches playback position')
    check(page.locator('#queue').is_visible(), 'queue still renders alongside lyrics')
    main_box = page.locator('#lyrics').bounding_box()
    check(main_box['x'] >= -1 and main_box['x'] + main_box['width'] <= 1101, 'main lyric card fits 1100px OBS source')
    page.screenshot(path=str(PREVIEWS / 'v13-combined.png'), omit_background=True)

    resp = page.request.post(URL+'/api/settings', data=json.dumps({'showLyrics':False}), headers={'Content-Type':'application/json'})
    check(resp.ok, 'settings update accepted')
    page.wait_for_function("document.querySelector('#lyrics').classList.contains('hide')", timeout=5000)
    check(not page.locator('#lyrics').is_visible(), 'main overlay respects lyrics off setting')
    page.set_viewport_size({'width':800, 'height':180})
    page.goto(URL+'/lyrics', wait_until='load')
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Second test line')", timeout=5000)
    check(page.locator('#lyrics').is_visible(), 'standalone lyrics still visible while main setting is off')
    check(not page.locator('#card').is_visible() and not page.locator('#queue').is_visible(), 'standalone source only shows lyrics')
    box = page.locator('#lyrics').bounding_box()
    check(abs(box['width'] - 800) < 2 and abs(box['height'] - 180) < 2, 'standalone lyrics fill 800 x 180 source')
    page.screenshot(path=str(PREVIEWS / 'v13-lyrics-only.png'), omit_background=True)

    page.goto(URL+'/lyrics?demo=1', wait_until='load')
    page.wait_for_function("document.querySelector('#lyr-src').textContent === 'SAMPLE'")
    check(page.locator('#lyrics').is_visible() and page.locator('#lyr-body').inner_text().strip() != '', 'sample test is visible without depending on automatic lookup')

    # Intercept only the state, while using the actual served HTML and JS.
    state = {
        'now': {'title':'A very very very long song title that must scroll without multiplying endlessly',
                'artist':'Test Artist','album':'','trackId':'long-1','position':8,'duration':100,
                'rate':1,'playing':False,'sourceLabel':'YouTube Music','art':''},
        'queue':{'items':[{'title':'Queue test','artist':'Artist','videoId':'q1','thumb':''}]},
        'lyrics':{'available':True,'synced':True,'instrumental':False,'reason':'synced','source':'Test',
                  'lines':[{'t':0,'x':'First line'}, {'t':5,'x':'Middle line'}, {'t':15,'x':'Last line'}]},
        'settings':{'showLyrics':True,'showQueue':True,'queueCount':4,'lyricsLines':2,'lyricsKaraoke':True,
                    'lyricsHideInstrumental':False,'lyricsOffsetSeconds':0,'showArt':True,'showLabel':True,
                    'showProgress':True,'showTimes':True,'titleMarquee':True,'scale':1,'placeholder':'Nothing playing',
                    'theme':'glass','align':'center','label':'My custom label'}
    }
    page.route('**/api/state', lambda route: route.fulfill(status=200, content_type='application/json', body=json.dumps(state)))
    page.set_viewport_size({'width':1100,'height':240})
    page.goto(URL+'/', wait_until='load')
    page.wait_for_function("document.querySelector('.lyr-line.active')?.textContent === 'Middle line'")
    check(page.locator('#title').get_attribute('class').find('scroll') >= 0, 'long title activates marquee')
    check('My custom label' in page.locator('#label').inner_text(), 'custom label setting is respected')
    t1 = page.locator('#titletext').inner_text()
    time.sleep(2)
    t2 = page.locator('#titletext').inner_text()
    check(t1 == t2 and len(t2) < 200, 'long title does not double on every poll')
    check(page.locator('#tpos').inner_text() == '0:08', 'paused playback progress displays actual position')
    state['settings']['hideWhenPaused'] = True
    page.wait_for_function("getComputedStyle(document.querySelector('#stage')).opacity === '0'")
    check(True, 'optional hide-when-paused hides the overlay')
    state['settings']['hideWhenPaused'] = False
    page.wait_for_function("getComputedStyle(document.querySelector('#stage')).opacity === '1'")
    check(True, 'overlay returns when hide-when-paused is disabled')

    state['settings']['lyricsOffsetSeconds'] = 10
    page.wait_for_function("document.querySelector('.lyr-line.active')?.textContent === 'Last line'", timeout=5000)
    check(True, 'positive timing adjustment advances highlighted line')
    state['settings']['lyricsKaraoke'] = False
    page.wait_for_function("document.querySelectorAll('.lyr-line.active').length === 0 && !document.querySelector('#lyrics').classList.contains('lyr-synced')")
    check(True, 'karaoke highlight can be turned off')
    state['settings']['lyricsKaraoke'] = True
    state['settings']['lyricsOffsetSeconds'] = 0
    state['lyrics']['lines'][1]['x'] = 'Edited middle line'
    page.wait_for_function("document.querySelector('.lyr-line.active')?.textContent === 'Edited middle line'")
    check(True, 'editing a middle lyric line updates the overlay immediately')
    state['settings']['lyricsLines'] = 1
    page.wait_for_function("document.querySelectorAll('#lyr-body .lyr-line').length === 1")
    check(True, 'changing displayed lyric count updates existing track')
    state['settings']['lyricsLines'] = 3
    page.wait_for_function("document.querySelectorAll('#lyr-body .lyr-line').length === 3")
    check(True, 'three lyric lines can be displayed')

    state['lyrics'] = {'available':False,'synced':False,'instrumental':False,'reason':'no-match','lines':[]}
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('No lyrics found')")
    check(page.locator('#lyrics').is_visible(), 'no-match leaves a visible explanation')
    state['lyrics']['reason'] = 'error'
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Could not reach')")
    check(page.locator('#lyrics').is_visible(), 'connection error leaves a visible explanation')
    state['lyrics'] = {'available':False,'instrumental':True,'reason':'instrumental','lines':[]}
    state['settings']['lyricsHideInstrumental'] = True
    page.wait_for_function("document.querySelector('#lyrics').classList.contains('hide')")
    check(not page.locator('#lyrics').is_visible(), 'instrumental hide option works in main overlay')

    # Lyrics-only forces its own state even when the main checkbox is false.
    separate = copy.deepcopy(state)
    separate['now'] = None
    separate['lyrics'] = {'available':False,'reason':'no-title','lines':[]}
    separate['settings']['showLyrics'] = True
    separate['settings']['placeholder'] = ''
    page.route('**/api/lyrics-state', lambda route: route.fulfill(status=200, content_type='application/json', body=json.dumps(separate)))
    page.set_viewport_size({'width':800,'height':180})
    page.goto(URL+'/lyrics', wait_until='load')
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Waiting for a song')")
    check(page.locator('#lyrics').is_visible(), 'standalone lyrics stays visible while no song is detected')
    separate['now'] = copy.deepcopy(state['now'])
    separate['lyrics'] = {'available':False,'instrumental':True,'reason':'instrumental','lines':[]}
    page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Instrumental track')")
    check(page.locator('#lyrics').is_visible(), 'standalone source explains instrumental even when main card hides it')

    # Ensure layout doesn't clip cards over several common OBS source sizes.
    page.unroute('**/api/lyrics-state')
    state['settings']['lyricsHideInstrumental'] = False
    state['settings']['lyricsLines'] = 2
    state['lyrics'] = {'available':True,'synced':True,'instrumental':False,'source':'Test','reason':'synced',
                        'lines':[{'t':0,'x':'One line'}, {'t':5,'x':'Second line'}, {'t':15,'x':'Third line'}]}
    for w,h in [(1100,240),(900,260),(600,600),(1600,600)]:
        page.set_viewport_size({'width':w,'height':h})
        page.goto(URL+'/', wait_until='load')
        page.wait_for_function("document.querySelector('#lyr-body').textContent.includes('Second line')")
        for selector in ('#card','#lyrics','#queue'):
            r = page.locator(selector).bounding_box()
            check(r is not None and r['x'] >= -1 and r['y'] >= -1 and r['x']+r['width'] <= w+1 and r['y']+r['height'] <= h+1,
                  f'{selector} fits viewport {w}x{h}')
    check(not errors, 'no browser JavaScript exceptions: '+str(errors))
    b.close()

print('ALL', checks, 'BROWSER CHECKS PASSED')
