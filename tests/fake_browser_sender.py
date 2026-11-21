import json, time, urllib.request, sys
from pathlib import Path
settings=Path('/home/user/.cache/ytm-wine/drive_c/users/user/AppData/Roaming/YTM-Overlay/settings.json')
key=json.loads(settings.read_text())['BrowserBridgeKey']
start=time.monotonic()
initial=float(sys.argv[1]) if len(sys.argv)>1 else 30
while True:
    data={'key':key,'title':'No Surprises','artist':'Radiohead','album':'OK Computer','videoId':'test-no-surprises',
          'position':initial+time.monotonic()-start,'duration':239,'rate':1,'playing':True,'ad':False,
          'queueKnown':True,'items':[{'title':'Creep','artist':'Radiohead','videoId':'creep','thumb':''}],
          'sentAt':int(time.time()*1000)}
    request=urllib.request.Request('http://127.0.0.1:7190/api/bridge',data=json.dumps(data).encode(),
          headers={'Origin':'https://music.youtube.com','Content-Type':'text/plain'},method='POST')
    try: urllib.request.urlopen(request,timeout=2).read()
    except Exception as e: print(type(e).__name__,flush=True)
    time.sleep(.7)
