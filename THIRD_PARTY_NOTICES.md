# Third-party notices

The Windows executable is self-contained and includes Microsoft .NET 10 runtime and Windows Desktop components. Their license and runtime third-party notices from the .NET 10.0.15 packages used for the packaged build are included in `third-party/dotnet/` and combined in `release/NOTICES.txt`; distribute that file with the executable. The Windows SDK targeting pack is provided by Microsoft under the [Windows SDK license](https://aka.ms/WinSDKLicenseURL). A later rebuild may use different runtime patch versions; the GitHub workflows run `tools/make_notices.py` against the restored packages to produce notices for that build. Review them before publishing.

The optional queue insertion uses undocumented YouTube Music page interfaces. Publicly available reverse-engineering examples, including [Pear Desktop](https://github.com/pear-devs/pear-desktop), informed the investigation. This project does not bundle Pear Desktop. YouTube Music may change or remove these interfaces at any time.

Lyrics and suggestion lookups can contact LRCLIB, lyrics.ovh and YouTube Music; browser chat links run on Twitch, YouTube Live and Kick. These are external services, not bundled components, and their own terms and privacy policies apply. Names and trademarks belong to their respective owners. This project is not endorsed by or affiliated with those services.
