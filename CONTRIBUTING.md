# Contributing

Thanks for helping test this beta. Please reproduce issues with the newest release and provide your OS, browser, OBS version, relevant setup steps and expected/actual behavior. Never include private bookmark code, pairing keys, cookies or stream keys.

Run `dotnet run --project tests/OverlayTests.csproj -c Release` before opening a pull request. Browser-fixture and optional live tests are documented in [TESTING.md](TESTING.md). Keep chat adapters scoped to exact trusted origins; treat all viewer text as untrusted. Player-queue changes must preserve explicit opt-in, durable job claims and the uncertain/no-automatic-retry behavior.

Live platform pages and undocumented APIs can change without notice. Do not record or submit real viewers' chat content in tests; use synthetic fixtures and monitor only chats you are authorized to inspect.
