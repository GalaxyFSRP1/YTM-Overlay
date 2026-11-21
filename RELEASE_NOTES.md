# v0.0.0 beta — first public beta

This is the first public beta of the unofficial YouTube Music OBS overlay. The Windows x64 executable is self-contained and unsigned; no separate .NET runtime is needed.

## What's included

- Now playing, a smoother browser-linked playback clock, synchronized/plain lyrics, a live lyric preview, and locally saved manual lyrics.
- A browser-linked **Up next** queue, with clearly labeled public suggestions when the real queue is unavailable. Separate OBS sources for lyrics, music queue and viewer requests.
- A local viewer-request dashboard for **Twitch, YouTube Live and Kick**. Viewers can use `!sr` with a song name or single YouTube video URL. Choose **approval** or **automatic acceptance**. Requests start paused.
- Duplicate and cooldown checks, an open-request limit, manual adding, moderation/history controls, and per-chat link status.
- An **opt-in experimental** feature to insert accepted requests into a linked YouTube Music browser queue. Text searches choose the first result; the wrong match is possible. Acceptance alone does not change the player.
- A move to the .NET 10 LTS runtime for this self-contained build.

## Before going live

Read the setup and privacy instructions in the README. Keep each chat tab open, allow local-network permission for the sites you trust, and use YouTube Live's pop-out chat. Each site can change its page layout. No bot replies are sent. Music broadcast rights are the streamer's responsibility.

Player insertion is off by default. It uses undocumented YouTube Music page interfaces and may fail. If a result is uncertain, check the actual player queue before retrying. Marking a request done does not remove it from the player. In automatic mode, unreviewed request text and names can appear on the requests overlay.

## Verification and limitations

Automated core, browser-fixture and Wine checks passed. A real **signed-out** YouTube Music browser queue was tested for direct-link and text-search insertion. Three live chat page layouts were exercised with synthetic browser-local rows, not actual viewer messages. Real Windows, OBS, signed-in YouTube Music, and live end-to-end viewer traffic remain unverified. Please report issues with steps to reproduce; do **not** include bookmark codes, pairing keys, cookies or stream keys.

Attach `YTM-Overlay-v0.0.0-beta.exe`, `SHA256SUMS.txt` and `NOTICES.txt` to the GitHub release and mark it as a **pre-release**.
