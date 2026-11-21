# Optional network integration test: this uses a signed-out real YouTube Music tab,
# never posts to a public chat, and changes only that disposable browser's queue.
from playwright.sync_api import sync_playwright
import os,time
URL=os.environ.get('OVERLAY_TEST_URL','http://127.0.0.1:7511')
checks=0

def check(ok,label):
 global checks
 if not ok: raise AssertionError(label)
 checks+=1;print('PASS',label,flush=True)

def click_bookmark(page,bookmark):
 try:page.goto(bookmark,wait_until='commit',timeout=3000)
 except Exception as e:
  if 'ERR_ABORTED' not in str(e):raise

with sync_playwright() as p:
 browser=p.chromium.launch(headless=True,args=['--no-sandbox','--autoplay-policy=no-user-gesture-required'])
 context=browser.new_context(user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36')
 cdp=browser.new_browser_cdp_session();cid=cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
 cdp.send('Browser.setPermission',{'permission':{'name':'loopback-network'},'setting':'granted','origin':'https://music.youtube.com','browserContextId':cid})
 setup=context.new_page();req=setup.request
 # This points only at the explicitly disposable test server.
 old=req.get(URL+'/api/request-state').json()
 for item in old['items']:
  if item['status'] in ('pending','ready','failed','uncertain','added'):
   req.post(URL+'/api/request-action',data={'action':'done' if item['status']=='added' else 'reject','id':item['id']})
 req.post(URL+'/api/request-action',data={'action':'clearHistory'})
 req.post(URL+'/api/request-action',data={'action':'config','enabled':True,'mode':'automatic','autoQueue':False,'cooldown':0,'limit':50})
 setup.goto(URL+'/connect');bookmark=setup.locator('#bookmark').get_attribute('href')
 music=context.new_page();errors=[];music.on('pageerror',lambda e:errors.append(str(e)))
 music.goto('https://music.youtube.com/watch?v=fJ9rUzIMcZQ',wait_until='domcontentloaded',timeout=30000)
 music.wait_for_selector('ytmusic-player-queue-item[selected]',timeout=30000)
 music.wait_for_function("() => document.querySelector('ytmusic-player-queue')?.getState?.().queue.items.length > 0",timeout=20000)
 click_bookmark(music,bookmark)
 music.wait_for_function("() => document.querySelector('#ytm-overlay-bridge-notice')?.textContent.includes('linked')",timeout=12000)
 check(True,'new music bookmark links to a real signed-out YouTube Music tab')
 def count_video(v):
  return music.evaluate('''id=>{const s=document.querySelector('ytmusic-player-queue').getState().queue;
   return s.items.filter(x=>(x.playlistPanelVideoRenderer||x.playlistPanelVideoWrapperRenderer?.primaryRenderer?.playlistPanelVideoRenderer)?.videoId===id).length}''',v)
 def wait_item(song,timeout=35):
  end=time.time()+timeout
  while time.time()<end:
   state=req.get(URL+'/api/request-state').json();items=[x for x in state['items'] if x['song']==song]
   if items and items[0]['status'] in ('added','failed','uncertain'):return items[0]
   time.sleep(.3)
  raise AssertionError('Timed out waiting for '+song)
 direct='https://music.youtube.com/watch?v=dQw4w9WgXcQ'
 before=count_video('dQw4w9WgXcQ')
 check(req.post(URL+'/api/request-action',data={'action':'manual','song':direct}).ok,'direct video request accepted into the local list')
 time.sleep(2)
 state=req.get(URL+'/api/request-state').json();item=next(x for x in state['items'] if x['song']==direct)
 check(item['status']=='ready' and count_video('dQw4w9WgXcQ')==before,'player queue is unchanged until insertion is explicitly enabled')
 check(req.post(URL+'/api/request-action',data={'action':'config','autoQueue':True}).ok,'experimental queue insertion enabled for the test')
 item=wait_item(direct)
 check(item['status']=='added','direct video receives a positive queue confirmation: '+str(item.get('note')))
 check(count_video('dQw4w9WgXcQ')>before,'direct video is actually present in the real player queue')
 song='daft punk harder better faster stronger'
 check(req.post(URL+'/api/request-action',data={'action':'manual','song':song}).ok,'plain-text song request accepted')
 item=wait_item(song)
 check(item['status']=='added' and len(item['videoId'])==11 and bool(item['resolvedTitle']),'text search resolves a real first result and inserts it: '+str(item.get('note')))
 check(count_video(item['videoId'])>=1,'resolved search result is present in the real player queue')
 check(not errors,'no injected music-link JavaScript errors: '+repr(errors))
 req.post(URL+'/api/request-action',data={'action':'config','autoQueue':False,'enabled':False})
 browser.close()
print('ALL',checks,'LIVE REQUEST / YOUTUBE MUSIC CHECKS PASSED')
