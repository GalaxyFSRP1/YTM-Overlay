# Optional integration test, never sends a chat message. Supply URLs of chats you
# are authorized to inspect. Real messages are filtered from the local app;
# a local row is cloned or temporarily edited only inside this test browser.
from playwright.sync_api import sync_playwright
import json, os, time

URL=os.environ.get('OVERLAY_TEST_URL','http://127.0.0.1:7511')
urls={k:os.environ.get('YTM_TEST_'+k.upper()+'_CHAT','') for k in ('twitch','kick','youtube')}
if not all(urls.values()):
 raise SystemExit('Set YTM_TEST_TWITCH_CHAT, YTM_TEST_KICK_CHAT and YTM_TEST_YOUTUBE_CHAT to authorized live chat URLs.')
checks=0

def check(ok,label):
 global checks
 if not ok:raise AssertionError(label)
 checks+=1;print('PASS',label,flush=True)

def bookmark_click(page,href):
 try:page.goto(href,wait_until='commit',timeout=3000)
 except Exception as e:
  if 'ERR_ABORTED' not in str(e):raise

with sync_playwright() as p:
 browser=p.chromium.launch(headless=True,args=['--no-sandbox'])
 context=browser.new_context(user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36')
 cdp=browser.new_browser_cdp_session();cid=cdp.send('Target.getBrowserContexts')['browserContextIds'][0]
 for origin in ['https://www.twitch.tv','https://kick.com','https://www.youtube.com']:
  cdp.send('Browser.setPermission',{'permission':{'name':'loopback-network'},'setting':'granted','origin':origin,'browserContextId':cid})
 # Do not persist or send unrelated live chat text to the app while testing.
 def filter_requests(route):
  data=json.loads(route.request.post_data or '{}')
  data['messages']=[m for m in data.get('messages',[]) if m.get('author','').startswith('YTMBetaTest_')]
  route.continue_(post_data=json.dumps(data))
 context.route('**/api/chat',filter_requests)
 setup=context.new_page();req=setup.request
 old=req.get(URL+'/api/request-state').json()
 for item in old['items']:
  if item['status'] in ('pending','ready','failed','uncertain','added'):
   req.post(URL+'/api/request-action',data={'action':'done' if item['status']=='added' else 'reject','id':item['id']})
 req.post(URL+'/api/request-action',data={'action':'clearHistory'})
 req.post(URL+'/api/request-action',data={'action':'config','enabled':True,'mode':'approval','autoQueue':False,'cooldown':0,'limit':50})
 setup.goto(URL+'/chat-connect');bookmark=setup.locator('#bookmark').get_attribute('href')
 selectors={'twitch':'[data-a-target="chat-line-message"]','kick':'#chatroom-messages [data-index]','youtube':'yt-live-chat-text-message-renderer'}
 pages={}
 for platform,url in urls.items():
  page=context.new_page();page_errors=[]
  page.on('pageerror',lambda e:page_errors.append(str(e)))
  page.on('console',lambda m:page_errors.append(m.text[:500]) if m.type=='error' else None)
  page.goto(url,wait_until='domcontentloaded',timeout=30000)
  page.wait_for_selector(selectors[platform],timeout=35000)
  matches=page.evaluate('''p=>{
   const selector=p==='twitch'?'[data-a-target="chat-line-message"]':p==='kick'?'#chatroom-messages [data-index]':'yt-live-chat-text-message-renderer';
   return [...document.querySelectorAll(selector)].filter(r=>{
    if(p==='twitch')return !!(r.getAttribute('data-a-user')&&r.querySelector('[data-a-target="chat-line-message-body"]'));
    if(p==='youtube')return !!(r.querySelector('#author-name')&&r.querySelector('#message'));
    const sep=[...r.querySelectorAll('span[aria-hidden="true"]')].find(x=>x.textContent.trim()===':');
    return !!(r.querySelector('button[title]')&&sep&&(sep.nextElementSibling||sep.parentElement?.nextElementSibling));
   }).length
  }''',platform)
  check(matches>0,platform+' live page exposes matching chat row, sender and body selectors')
  # The sites can still perform a late client-side navigation after rows appear.
  page.wait_for_timeout(1800)
  bookmark_click(page,bookmark)
  try:
   page.wait_for_function('() => Boolean(window.__ytmRequestChat)',timeout=6000)
  except Exception:
   # Reconnect once if that late navigation cleared the first bookmark instance.
   bookmark_click(page,bookmark)
   try:
    page.wait_for_function('() => Boolean(window.__ytmRequestChat)',timeout=6000)
   except Exception:
    print('Bookmark failed on',platform,'at',page.url,'errors:',page_errors[-8:],flush=True)
    raise
  pages[platform]=page
 time.sleep(3.3)
 for platform,page in pages.items():
  page.evaluate('''p=>{
   const s=p==='twitch'?'[data-a-target="chat-line-message"]':p==='kick'?'#chatroom-messages [data-index]':'yt-live-chat-text-message-renderer';
   const source=document.querySelector(s), row=p==='youtube'?source:source.cloneNode(true), author='YTMBetaTest_'+p, text='!sr Live adapter test '+p;
   if(p==='twitch'){
    row.setAttribute('data-a-user',author);row.querySelector('[data-a-target="chat-line-message-body"]').textContent=text;document.body.append(row);
   }else if(p==='youtube'){
    row.querySelector('#author-name').textContent=author;row.querySelector('#message').textContent=text;
   }else{
    row.setAttribute('data-index','999999');let name=row.querySelector('button[title]');name.title=author;name.textContent=author;
    const sep=[...row.querySelectorAll('span[aria-hidden="true"]')].find(x=>x.textContent.trim()===':');
    (sep.nextElementSibling||sep.parentElement.nextElementSibling).textContent=text;
    const host=document.createElement('div');host.id='chatroom-messages';host.append(row);document.body.append(host);
   }
  }''',platform)
 end=time.time()+15
 while time.time()<end:
  state=req.get(URL+'/api/request-state').json();authors={x['viewer'] for x in state['items']}
  if all('YTMBetaTest_'+x in authors for x in urls):break
  time.sleep(.3)
 for platform in urls:
  check('YTMBetaTest_'+platform in authors,platform+' synthetic live-page row reaches the local request inbox')
 check(all(x['viewer'].startswith('YTMBetaTest_') for x in state['items']), 'no unrelated public chat messages were saved')
 req.post(URL+'/api/request-action',data={'action':'config','enabled':False})
 browser.close()
print('ALL',checks,'LIVE CHAT PAGE CHECKS PASSED (synthetic local rows; no messages sent)')
