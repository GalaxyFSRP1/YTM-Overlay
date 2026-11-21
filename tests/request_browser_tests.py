from playwright.sync_api import sync_playwright
from urllib.parse import unquote
import os, time, json
from pathlib import Path

PREVIEWS=Path(os.environ.get("OVERLAY_PREVIEWS", Path(__file__).parent / "artifacts"))
PREVIEWS.mkdir(parents=True, exist_ok=True)

URL=os.environ.get('OVERLAY_TEST_URL','http://127.0.0.1:7511')
checks=0

def check(value, label):
    global checks
    if not value: raise AssertionError(label)
    checks += 1
    print('PASS',label,flush=True)

def bookmark_click(page, href):
    try: page.goto(href,wait_until='commit',timeout=3000)
    except Exception as e:
        if 'ERR_ABORTED' not in str(e): raise

def add_message(page,platform,user,text,index=0,link=None):
    page.evaluate('''([platform,user,text,index,link])=>{
      let row=document.createElement('div'), body, name;
      if(platform==='twitch'){
        row.setAttribute('data-a-target','chat-line-message');row.setAttribute('data-a-user',user);
        body=document.createElement('span');body.setAttribute('data-a-target','chat-line-message-body');row.append(body);
        document.querySelector('#fixture').append(row);
      }else if(platform==='kick'){
        row.setAttribute('data-index',String(index));let group=document.createElement('div');
        name=document.createElement('button');name.title=user;name.textContent=user;
        let sep=document.createElement('span');sep.setAttribute('aria-hidden','true');sep.textContent=': ';
        group.append(name,sep);body=document.createElement('span');row.append(group,body);
        document.querySelector('#chatroom-messages').append(row);
      }else{
        row=document.createElement('yt-live-chat-text-message-renderer');
        name=document.createElement('span');name.id='author-name';name.textContent=user;
        body=document.createElement('span');body.id='message';row.append(name,body);document.querySelector('#fixture').append(row);
      }
      body.textContent=text;
      if(link){let a=document.createElement('a');a.href=link.href;a.textContent=link.label;body.append(a);}
    }''',[platform,user,text,index,link])

with sync_playwright() as p:
    browser=p.chromium.launch(headless=True,args=['--no-sandbox'])
    context=browser.new_context(viewport={'width':1200,'height':950})
    cdp=browser.new_browser_cdp_session();cid=cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
    origins=['https://www.twitch.tv','https://kick.com','https://www.youtube.com']
    for origin in origins:
        cdp.send('Browser.setPermission',{'permission':{'name':'loopback-network'},'setting':'granted','origin':origin,'browserContextId':cid})
    html='<html><head><title>Local chat fixture</title></head><body><div id="fixture"></div><div id="chatroom-messages"></div></body></html>'
    for pattern in ['https://www.twitch.tv/**','https://kick.com/**','https://www.youtube.com/**']:
        context.route(pattern,lambda r:r.fulfill(status=200,content_type='text/html',body=html))
    dashboard=context.new_page();errors=[]
    # Isolate repeat runs against the disposable test server.
    old=dashboard.request.get(URL+'/api/request-state').json()
    for item in old['items']:
        action = 'done' if item['status']=='added' else 'reject'
        if item['status'] in ('pending','ready','failed','uncertain','added'):
            dashboard.request.post(URL+'/api/request-action',data={'action':action,'id':item['id']})
    dashboard.request.post(URL+'/api/request-action',data={'action':'clearHistory'})
    dashboard.request.post(URL+'/api/request-action',data={'action':'config','enabled':False,'mode':'approval','autoQueue':False,'cooldown':60,'limit':50})
    dashboard.on('pageerror',lambda e:errors.append(str(e)))
    dashboard.on('dialog',lambda d:d.accept())
    dashboard.goto(URL+'/requests',wait_until='load')
    dashboard.wait_for_function("document.querySelector('#mode').value === 'approval'")
    check(not dashboard.locator('#enabled').is_checked() and not dashboard.locator('#autoQueue').is_checked(), 'dashboard defaults to paused approval without player insertion')
    dashboard.locator('#enabled').check();dashboard.locator('#save').click()
    dashboard.wait_for_function("document.querySelector('#toast').textContent.includes('saved')")
    check(dashboard.request.get(URL+'/api/request-state').json()['settings']['enabled'], 'dashboard saves request settings')
    setup=context.new_page();setup.goto(URL+'/chat-connect');href=setup.locator('#bookmark').get_attribute('href')
    check(href.startswith('javascript:') and '__CHAT_KEY__' not in unquote(href),'chat guide creates a paired bookmark')
    pages={}
    for platform,url in [('twitch','https://www.twitch.tv/popout/creator/chat'),('kick','https://kick.com/creator'),('youtube','https://www.youtube.com/live_chat?is_popout=1&v=abc')]:
        page=context.new_page();page.on('pageerror',lambda e:errors.append(str(e)));page.goto(url)
        add_message(page,platform,'OldViewer','!sr Old History '+platform)
        bookmark_click(page,href)
        page.wait_for_function('Boolean(window.__ytmRequestChat)')
        pages[platform]=page
    time.sleep(3.3)
    check(len(dashboard.request.get(URL+'/api/request-state').json()['items'])==0, 'visible chat history is ignored at connect')
    add_message(pages['twitch'],'twitch','Alice','!sr First Twitch Song')
    add_message(pages['kick'],'kick','Bob','!songrequest First Kick Song',1)
    add_message(pages['youtube'],'youtube','Cara','!sr First YouTube Song')
    deadline=time.time()+12
    while time.time()<deadline:
        state=dashboard.request.get(URL+'/api/request-state').json()
        if len(state['items'])>=3:break
        time.sleep(.25)
    check(len(state['items'])==3 and {i['platform'] for i in state['items']}=={'twitch','kick','youtube'}, 'all three DOM adapters deliver new chat commands to the inbox')
    dashboard.wait_for_function("document.querySelector('#pendingCount').textContent === '3 pending'")
    check(dashboard.locator('#sources .dot.on').count()==3, 'dashboard displays three live chat heartbeats')
    overlay=context.new_page();overlay.set_viewport_size({'width':650,'height':400});overlay.goto(URL+'/request-overlay')
    check(overlay.locator('#panel').is_hidden(), 'OBS request source hides pending and empty lists')
    dashboard.locator('#pending .item').filter(has_text='First Twitch Song').locator('button[data-action="approve"]').click()
    overlay.wait_for_function("document.querySelector('#items').textContent.includes('First Twitch Song')",timeout=6000)
    check(overlay.locator('#panel').is_visible() and not overlay.locator('#items').inner_text().count('First Kick Song'), 'approved request appears in OBS without pending content')
    check(overlay.locator('#items').inner_text().count('Alice')==1, 'OBS request source identifies the viewer')
    dangerous='<img src=x onerror=window.pwned=true>'
    add_message(pages['twitch'],'twitch','ScriptTester','!sr '+dangerous,2)
    dashboard.wait_for_function("document.querySelector('#pending').textContent.includes('<img src=x')",timeout=9000)
    check(dashboard.locator('#pending img').count()==0 and dashboard.evaluate('window.pwned') is None, 'dashboard treats untrusted request text as text, not HTML')
    dashboard.locator('#pending .item').filter(has_text='<img src=x').locator('button[data-action="approve"]').click()
    overlay.wait_for_function("document.querySelector('#items').textContent.includes('<img src=x')",timeout=6000)
    check(overlay.locator('#items img').count()==0 and overlay.evaluate('window.pwned') is None, 'OBS source does not execute request markup')
    dashboard.locator('#mode').select_option('automatic');dashboard.locator('#save').click()
    dashboard.wait_for_function("document.querySelector('#toast').textContent.includes('saved')")
    add_message(pages['youtube'],'youtube','AutoViewer','!sr Automatic Viewer Song')
    deadline=time.time()+10
    while time.time()<deadline:
        state=dashboard.request.get(URL+'/api/request-state').json()
        found=[x for x in state['items'] if x['viewer']=='AutoViewer']
        if found:break
        time.sleep(.25)
    check(found and found[0]['status']=='ready', 'automatic mode accepts a new browser chat request')
    check(not state['settings']['autoQueue'], 'automatic acceptance alone does not pretend to modify the player')
    # Some YouTube chat messages wrap links in /redirect and render a shortened
    # label; zero-width formatting characters must not corrupt the command.
    add_message(pages['youtube'],'youtube','LinkViewer','!sr \u200b',link={
        'href':'https://www.youtube.com/redirect?q=https%3A%2F%2Fmusic.youtube.com%2Fwatch%3Fv%3DfJ9rUzIMcZQ',
        'label':'YouTube video…'})
    # A YouTube redirect to a non-YouTube URL must not be followed or submitted.
    add_message(pages['youtube'],'youtube','ExternalLinkViewer','!sr ',link={
        'href':'https://www.youtube.com/redirect?q=https%3A%2F%2Fevil.example%2Fwatch%3Fv%3DfJ9rUzIMcZQ',
        'label':'A visible song title'})
    deadline=time.time()+10
    while time.time()<deadline:
        state=dashboard.request.get(URL+'/api/request-state').json()
        by_viewer={x['viewer']:x for x in state['items']}
        if 'LinkViewer' in by_viewer and 'ExternalLinkViewer' in by_viewer: break
        time.sleep(.25)
    check(by_viewer.get('LinkViewer',{}).get('videoId')=='fJ9rUzIMcZQ', 'YouTube redirect links and zero-width chat formatting resolve to the video')
    check(by_viewer.get('ExternalLinkViewer',{}).get('song')=='A visible song title' and not by_viewer['ExternalLinkViewer']['videoId'], 'external redirect target is ignored in favor of visible song text')
    dashboard.screenshot(path=str(PREVIEWS / 'beta-requests-dashboard.png'),full_page=True)
    overlay.screenshot(path=str(PREVIEWS / 'beta-request-overlay.png'),omit_background=True)
    crowded={'total':12,'items':[{'song':'Song '+str(i),'viewer':'V','platform':'twitch','status':'ready'} for i in range(8)]}
    overlay.route('**/api/request-overlay',lambda r:r.fulfill(status=200,content_type='application/json',body=json.dumps(crowded)))
    overlay.reload();overlay.wait_for_function("document.querySelectorAll('#items .item').length > 0")
    last=overlay.locator('#items .item').last.bounding_box()
    check(overlay.locator('#count').inner_text()=='12' and last['y']+last['height'] <= 400, 'request OBS source fits its height and keeps the total count')
    dashboard.set_viewport_size({'width':390,'height':844})
    check(dashboard.evaluate('document.documentElement.scrollWidth <= window.innerWidth+1'), 'request dashboard adapts to narrow windows')
    bookmark_click(pages['kick'],href)
    pages['kick'].wait_for_function('!window.__ytmRequestChat')
    check(True, 'clicking chat bookmark again stops that link')
    check(not errors,'no request page or chat adapter JavaScript exceptions: '+repr(errors))
    browser.close()
print('ALL',checks,'REQUEST BROWSER CHECKS PASSED')
