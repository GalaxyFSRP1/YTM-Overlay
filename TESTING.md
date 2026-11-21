# Testing

## What has been checked for v0.0.0 beta

- **130 core checks**: clock/lyrics, HTTP behavior and browser-link security, request validation and moderation, all three chat origins, cooldown/duplicate limits, player command leases, acknowledgment/restart safety and disk-failure handling.
- **39 overlay browser checks**, **18 clock/queue browser checks**, and **19 request dashboard/chat-adapter browser-fixture checks**, including untrusted-text rendering, safe YouTube redirect-link handling, and responsive OBS layouts.
- **10 live signed-out YouTube Music checks**: with a disposable browser, accepted direct-link and text requests were actually inserted into the browser's current queue after explicitly enabling insertion. The test did not use a viewer's account.
- Live Twitch, YouTube Live and Kick page selectors were checked, and synthetic rows inserted *only into the disposable browser DOM* reached the local inbox. No message was posted to a public chat and unrelated messages were filtered from the test server. Live sites and bookmark execution were occasionally intermittent; this is a beta integration, not a reliability guarantee.
- **9 Windows EXE smoke checks under Wine**, including lyrics, timer, queue and overlay pages. The final Windows EXE and the signed-out YouTube Music insertion flow were also exercised together under Wine. Wine cannot validate the Windows media-session APIs.

**Not verified:** real Windows 10/11, real OBS, signed-in YouTube Music accounts, a user's own live streams or actual end-to-end viewer chat traffic. Platform page layouts, permissions, localization, ad behavior and account restrictions can differ.

## Portable checks

```bash
dotnet run --project tests/OverlayTests.csproj -c Release
```

The test harness redirects settings into a temporary test directory and uses mocked media sessions/lyrics. It does not modify the installed app's settings. To run local browser fixtures, install Python and Playwright:

```bash
python -m pip install -r tests/requirements.txt
python -m playwright install chromium
# In one terminal, start the DISPOSABLE test server:
dotnet run --project tests/OverlayTests.csproj -c Release -- serve 7511
# In another terminal:
python tests/browser_tests.py
python tests/browser_clock_tests.py
python tests/request_browser_tests.py
```

The fixture tests change request settings and requests in the server they target. Run them only against the disposable test server, not your streaming app. Screenshots go to `tests/artifacts/` by default; set `OVERLAY_PREVIEWS` to choose another folder.

## Optional online checks

`tests/live_request_music_tests.py` uses a signed-out disposable YouTube Music browser and **changes its queue**. Point it only at the disposable test server and a browser profile you can discard. `tests/live_browser_tests.py` plays a real public track and may wait through an advertisement. These tests depend on external sites and network conditions.

`tests/live_chat_page_tests.py` requires explicit `YTM_TEST_TWITCH_CHAT`, `YTM_TEST_KICK_CHAT` and `YTM_TEST_YOUTUBE_CHAT` URLs for live chats you are authorized to inspect. It filters unrelated messages before they reach the test server, and modifies/clones one browser-local row per platform rather than posting to chat. It can fail if a site changes or the chat is inactive. It does not prove real viewer messages or signed-in accounts work.

`tests/wine_exe_smoke.py` and `tests/fake_browser_sender.py` are developer smoke tests for the original Linux/Wine setup; they assume the app is listening on port 7190 and the Wine prefix is under the path shown in the script. They are not Windows installation steps.
