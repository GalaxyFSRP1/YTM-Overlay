from playwright.sync_api import sync_playwright
import copy, json, time, os
from pathlib import Path

PREVIEWS=Path(os.environ.get("OVERLAY_PREVIEWS", Path(__file__).parent / "artifacts"))
PREVIEWS.mkdir(parents=True, exist_ok=True)

URL='http://127.0.0.1:7511'
checks=0

def check(ok, name):
    global checks
    if not ok: raise AssertionError(name)
    checks += 1
    print('PASS',name,flush=True)

def seconds(s):
    return sum(int(x)*60**i for i,x in enumerate(reversed(s.lstrip('-').split(':'))))

with sync_playwright() as p:
    b=p.chromium.launch(headless=True,args=['--no-sandbox'])
    page=b.new_page(viewport={'width':1100,'height':240})
    errors=[]
    page.on('pageerror',lambda e:errors.append(str(e)))
    state={
      'now':{'title':'Test clock','artist':'Artist','album':'Album','trackId':'t1','position':20,'duration':100,
             'rate':1,'playing':True,'clockRevision':1,'clockOffset':0,'art':'','sourceLabel':'Browser'},
      'queue':{'source':'browser','items':[{'title':'Real first','artist':'Example','videoId':'a','thumb':''},
                                          {'title':'Real second','artist':'Example','videoId':'b','thumb':''}]},
      'lyrics':{'available':True,'synced':True,'instrumental':False,'source':'Test',
                'lines':[{'t':0,'x':'First words'},{'t':19,'x':'Clock is running'},{'t':40,'x':'Next lyric'}]},
      'settings':{'theme':'glass','showQueue':True,'queueCount':4,'showLyrics':True,'lyricsLines':2,
                  'lyricsKaraoke':True,'lyricsOffsetSeconds':0,'showProgress':True,'showTimes':True,
                  'showArt':True,'showLabel':True,'showQueueArtist':True,'showQueueArt':True,
                  'showQueueNumbers':True,'titleMarquee':True,'scale':1,'placeholder':'Nothing playing'}
    }
    page.route('**/api/state',lambda r:r.fulfill(status=200,content_type='application/json',body=json.dumps(state)))
    page.goto(URL+'/',wait_until='load')
    page.wait_for_function("document.querySelector('#qhead-title').textContent === 'Up next'")
    check(page.locator('#qrows .qtitle').first.inner_text()=='Real first','live queue is labeled Up next and shows actual items')
    values=[]
    for _ in range(11):
        values.append(seconds(page.locator('#tpos').inner_text())); time.sleep(.3)
    check(values==sorted(values) and values[-1]>=values[0]+2,'frozen repeated API positions do not pull the browser timer backward')
    state['now'].update({'position':5,'clockRevision':2})
    page.wait_for_function("document.querySelector('#tpos').textContent === '0:05'",timeout=4000)
    check(True,'clock revision permits a real backward seek')
    state['now'].update({'position':12,'playing':False,'clockRevision':3})
    page.wait_for_function("document.querySelector('#tpos').textContent === '0:12'",timeout=4000)
    time.sleep(1.2)
    check(page.locator('#tpos').inner_text()=='0:12','paused timer freezes in the OBS page')
    state['now'].update({'playing':True,'rate':2,'clockRevision':4})
    page.wait_for_function("!document.body.classList.contains('paused')",timeout=3000)
    time.sleep(1.7)
    check(seconds(page.locator('#tpos').inner_text())>=15,'OBS timer respects playback rate')
    state['now'].update({'position':8,'rate':1,'clockOffset':-4})
    page.wait_for_function("document.querySelector('#tpos').textContent === '0:08'",timeout=3000)
    check(True,'changing manual clock offset immediately reanchors the timer')
    state['settings']['showRemainingTime']=True
    page.wait_for_function("document.querySelector('#tdur').textContent.startsWith('-')")
    check(True,'remaining-time display option works')
    state['settings']['transparentBackground']=True
    state['settings']['reduceMotion']=True
    state['settings']['lyricsAlign']='center'
    page.wait_for_function("document.body.classList.contains('clean') && document.body.classList.contains('reduce-motion') && document.body.classList.contains('lyrics-center')")
    check(page.locator('#card').evaluate("e=>getComputedStyle(e).backgroundImage")=='none','transparent panels option removes background')
    check(page.locator('#lyr-body').evaluate("e=>getComputedStyle(e).textAlign")=='center','lyric alignment option is applied')
    check(page.locator('#card').evaluate("e=>getComputedStyle(e).animationName")=='none','reduce motion disables card animation')
    state['settings']['transparentBackground']=False
    state['settings']['reduceMotion']=False
    page.screenshot(path=str(PREVIEWS / 'beta-main-overlay.png'),omit_background=True)

    # Dedicated queue is independent of the main showQueue setting.
    page.unroute('**/api/state')
    resp=page.request.post(URL+'/api/settings',data=json.dumps({'showQueue':False,'queueCount':0}),headers={'Content-Type':'application/json'})
    check(resp.ok,'test server accepts local settings change')
    page.set_viewport_size({'width':650,'height':350})
    page.goto(URL+'/queue',wait_until='load')
    page.wait_for_function("document.querySelector('#qrows').textContent.includes('Suggested test track')",timeout=6000)
    box=page.locator('#queue').bounding_box()
    check(page.locator('#queue').is_visible() and not page.locator('#card').is_visible() and not page.locator('#lyrics').is_visible(),
          'queue-only URL displays only its own source while main queue is off')
    check(abs(box['width']-650)<2 and abs(box['height']-350)<2,'queue-only source fills its OBS rectangle')
    check(page.locator('#qhead-title').inner_text()=='Suggested next','fallback suggestions are honestly labeled')
    page.screenshot(path=str(PREVIEWS / 'beta-queue-only.png'),omit_background=True)

    queue_state=copy.deepcopy(state)
    queue_state['now']=None
    queue_state['queue']={'source':'suggestions','items':[]}
    queue_state['settings']['showQueue']=True
    page.route('**/api/queue-state',lambda r:r.fulfill(status=200,content_type='application/json',body=json.dumps(queue_state)))
    page.goto(URL+'/queue',wait_until='load')
    page.wait_for_function("document.querySelector('#qrows').textContent.includes('Waiting for a song')")
    check(page.locator('#queue').is_visible(),'queue-only source stays visible while idle')
    queue_state['now']=copy.deepcopy(state['now'])
    queue_state['queue']={'source':'browser','items':[]}
    page.wait_for_function("document.querySelector('#qrows').textContent.includes('Your queue is empty')")
    check(page.locator('#qhead-title').inner_text()=='Up next','known empty real queue is distinguished from suggestions')
    queue_state['queue']['items']=[{'title':'Queue row '+str(i),'artist':'Artist','videoId':str(i),'thumb':''} for i in range(8)]
    queue_state['settings']['queueCount']=8
    page.wait_for_function("document.querySelectorAll('#qrows .qrow').length === 8")
    last=page.locator('#qrows .qrow').last.bounding_box()
    check(last['y']+last['height']<=351,'eight queue rows fit a 650x350 standalone source')
    check(not errors,'no browser exceptions: '+str(errors))
    b.close()
print('ALL',checks,'CLOCK BROWSER CHECKS PASSED')
