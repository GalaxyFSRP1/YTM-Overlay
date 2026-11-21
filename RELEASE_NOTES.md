# v0.0.1 beta

A balanced update to the unofficial Windows YouTube Music / OBS overlay. The x64 executable is self-contained and unsigned; no separate .NET install is needed. This is still a beta: test with your own setup before streaming.

## New in this beta

- **Player & tools:** previous, play/pause and next for the linked YouTube Music browser, quick OBS-source links, copy song text and a diagnostic summary that excludes song titles, viewer names, request content, channel names and pairing keys.
- **Now-playing-only OBS source** at `/now-playing`, plus an anonymous requests source that hides viewer names on stream. Request overlay URLs can set a row limit and accent color.
- **Request moderation:** independent Twitch/YouTube Live/Kick intake switches and an optional blocked-phrase filter for viewer names and request text.
- **Request organization:** search the dashboard, move waiting approved requests earlier or later, and export local request history to CSV (including spreadsheet-formula protection).
- Existing approval/automatic request handling, lyrics, queue sources, timing controls and opt-in experimental player insertion remain available.

## Upgrading and important limits

Exit the old copy via its tray icon before starting this EXE. Existing settings, request history and saved lyrics are reused; back up `%APPDATA%\YTM-Overlay` first if desired. **Recreate the music bookmark** from v0.0.1 to enable the new playback buttons. The controls click the connected YouTube Music page, not Windows media controls; a click acknowledgment is not a playback guarantee. They may be unavailable during ads, queue insertion or site changes. Check the player before retrying an uncertain action.

Blocked phrases are literal text checks, not comprehensive moderation: they do not identify the song behind a video URL. Reordering affects only waiting local requests, not the actual player queue after insertion. Anonymous mode hides the requester-name field on the OBS source only, not names included in song text; the local inbox/API and exported CSV still contain names. Keep CSV files private. Automatic mode can display unreviewed request text.

Player insertion is off by default and uses undocumented YouTube Music interfaces. It can fail or be uncertain; inspect the player before retrying. Music broadcast rights remain the streamer's responsibility.

## Verification

Automated core, browser-fixture and Wine checks passed. A disposable signed-out YouTube Music browser was tested for queue insertion, play/pause and next. Previous was tested in a browser fixture. Chat page selectors were exercised with synthetic local rows, not actual viewer messages. Real Windows, OBS, signed-in accounts, and live end-to-end viewer traffic remain unverified.

Attach `YTM-Overlay-v0.0.1-beta.exe`, `SHA256SUMS.txt` and `NOTICES.txt` to the `v0.0.1` release and mark it as a **pre-release**. See `PUBLISHING.md` for updating the existing repository safely.
