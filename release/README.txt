YTM Overlay v0.0.0 beta — Windows x64

Run YTM-Overlay-v0.0.0-beta.exe after extracting the project ZIP.
No .NET installation is needed. The executable is unsigned; Windows may show
an unknown-publisher warning. Review the top-level README before streaming.

To verify in PowerShell:
  Get-FileHash .\YTM-Overlay-v0.0.0-beta.exe -Algorithm SHA256
Compare with SHA256SUMS.txt in this folder.

Keep NOTICES.txt with the EXE; it contains the project and bundled runtime
licenses and third-party notices. For GitHub, attach the EXE, SHA256SUMS.txt
and NOTICES.txt to a release tagged v0.0.0 and mark it as a prerelease. See
the top-level PUBLISHING.md and release notes.
