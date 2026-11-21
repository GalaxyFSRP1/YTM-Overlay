from playwright.sync_api import sync_playwright
import time, os
from pathlib import Path

PREVIEWS=Path(os.environ.get("OVERLAY_PREVIEWS", Path(__file__).parent / "artifacts"))
PREVIEWS.mkdir(parents=True, exist_ok=True)
URL='http://127.0.0.1:7190'
checks=0

def check(value,label):
 global checks
 if not value: raise AssertionError(label)
 checks+=1; print('PASS',label)

with sync_playwright() as p:
 b=p.chromium.launch(headless=True,args=['--no-sandbox'])
 page=b.new_page(viewport={'width':1100,'height':240}); errors=[]
 page.on('pageerror',lambda e:errors.append(str(e)))
 state=page.request.get(URL+'/api/state').json()
 check(state['version']=='0.0.1' and state['now']['title']=='No Surprises','published Windows beta EXE returns v0.0.1 live data')
 check(state['lyrics']['available'] and state['lyrics']['synced'],'published EXE fetched synchronized lyrics')
 check(state['queue']['source']=='browser' and state['queue']['items'][0]['title']=='Creep','published EXE exposes direct queue')
 page.goto(URL+'/',wait_until='load')
 page.wait_for_function("document.querySelector('#qhead-title').textContent==='Up next' && document.querySelector('.lyr-line.active')")
 check(page.locator('#lyrics').is_visible() and page.locator('#queue').is_visible(),'published EXE serves combined overlay with lyrics and queue')
 first=page.locator('#tpos').inner_text();time.sleep(2.2);second=page.locator('#tpos').inner_text()
 check(first!=second,'published EXE overlay timer advances')
 page.screenshot(path=str(PREVIEWS / 'beta-wine-combined.png'),omit_background=True)
 page.set_viewport_size({'width':800,'height':180});page.goto(URL+'/lyrics',wait_until='load')
 page.wait_for_function("document.querySelector('.lyr-line.active')")
 check(page.locator('#lyrics').is_visible() and not page.locator('#card').is_visible(),'published EXE standalone lyrics render')
 page.set_viewport_size({'width':650,'height':350});page.goto(URL+'/queue',wait_until='load')
 page.wait_for_function("document.querySelector('#qrows').textContent.includes('Creep')")
 check(page.locator('#queue').is_visible() and page.locator('#qhead-title').inner_text()=='Up next','published EXE standalone actual queue renders')
 page.goto(URL+'/queue?demo=1',wait_until='load')
 page.wait_for_function("document.querySelector('#qrows').textContent.includes('Creep')")
 check(page.locator('#queue').is_visible(),'queue sample renders')
 check(not errors,'published EXE page has no JS exceptions')
 b.close()
print('ALL',checks,'PUBLISHED EXE SMOKE CHECKS PASSED')
