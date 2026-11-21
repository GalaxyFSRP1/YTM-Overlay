YTM Overlay v0.0.1 beta — Windows x64

Run YTM-Overlay-v0.0.1-beta.exe after extracting the project ZIP.
No .NET installation is needed. The executable is unsigned; Windows may show
an unknown-publisher warning. Review the top-level README before streaming.
Exit the older app from its tray menu first, and recreate the music bookmark.

To verify in PowerShell:
  Get-FileHash .\YTM-Overlay-v0.0.1-beta.exe -Algorithm SHA256
Compare with SHA256SUMS.txt in this folder.

Keep NOTICES.txt with the EXE; it contains project and bundled runtime
licenses and notices. For GitHub, attach the EXE, SHA256SUMS.txt and
NOTICES.txt to a NEW release tagged v0.0.1 and mark it as a prerelease.
UPDATE-from-v0.0.0.patch helps update the existing source repository; see
the top-level PUBLISHING.md before using it.
