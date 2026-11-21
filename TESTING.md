# Testing

## What has been checked for v0.0.1 beta

- **167 core checks**: clock/lyrics, HTTP behavior and browser-link security, request validation and moderation, all three chat origins, platform switches, filters and reordering, player-control sessions, acknowledgment/restart safety and disk-failure handling.
- **39 overlay browser checks**, **18 clock/queue browser checks**, **19 request dashboard/chat-adapter checks**, and **27 v0.0.1 browser-fixture checks**. These cover playback buttons, artwork fallback, compact and anonymous OBS sources, moderation controls, CSV export, untrusted-text rendering and responsive layouts.
- **10 live signed-out YouTube Music queue checks** and **6 live player-control checks**: a disposable browser actually inserted direct-link and text requests after explicitly enabling insertion, toggled play/pause, and advanced to the next track. Previous was exercised with a fixture, not a live account. These checks were also run against the Windows EXE under Wine.
- Live Twitch, YouTube Live and Kick page selectors were checked in the preceding beta, and synthetic rows inserted *only into the disposable browser DOM* reached the local inbox. No message was posted to a public chat and unrelated messages were filtered from the test server. Live sites and bookmark execution can be intermittent; this is a beta integration, not a reliability guarantee.
- **9 Windows EXE smoke checks under Wine**, including lyrics, timer, queue and overlay pages. The final v0.0.1 EXE also passed the 27 new browser fixtures and 19 request fixtures under Wine. Wine cannot validate the Windows media-session APIs.

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
python tests/v001_browser_tests.py
```

The fixture tests change request settings and requests in the server they target. Run them only against the disposable test server, not your streaming app. Screenshots go to `tests/artifacts/` by default; set `OVERLAY_PREVIEWS` to choose another folder.

## Optional online checks

`tests/live_request_music_tests.py` uses a signed-out disposable YouTube Music browser and **changes its queue**. `tests/live_player_controls.py` changes playback state and advances to the next track; it may click YouTube Music's ordinary Skip button if an ad offers one. Point them only at the disposable test server or a disposable test instance, never your live setup. `tests/live_browser_tests.py` plays a real public track and may wait through an advertisement. These tests depend on external sites, permissions and network conditions.

`tests/live_chat_page_tests.py` requires explicit `YTM_TEST_TWITCH_CHAT`, `YTM_TEST_KICK_CHAT` and `YTM_TEST_YOUTUBE_CHAT` URLs for live chats you are authorized to inspect. It filters unrelated messages before they reach the test server, and modifies/clones one browser-local row per platform rather than posting to chat. It can fail if a site changes or the chat is inactive. It does not prove real viewer messages or signed-in accounts work.

`tests/wine_exe_smoke.py` and `tests/fake_browser_sender.py` are developer smoke tests for the original Linux/Wine setup; they assume the app is listening on port 7190 and the Wine prefix is under the path shown in the script. They are not Windows installation steps.
