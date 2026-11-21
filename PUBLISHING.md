# Publish the first GitHub beta

No repository or release is created by this package. Review the code, README, license, screenshots and release notes before making them public. Replace the default MIT license if it does not reflect the permissions you intend to grant.

## 1. Create and upload the repository

1. Extract the ZIP. The extracted project folder is the repository root: it contains `src/`, `tests/`, `.github/` and this file.
2. Create an empty repository on GitHub under your own account or organization.
3. In the project folder, initialize Git, commit the source and connect it to **your own** repository URL:

```bash
git init
git add .
git commit -m "Initial v0.0.0 beta"
git branch -M main
git remote add origin <your-repository-url>
git push -u origin main
```

Replace `<your-repository-url>` with the actual HTTPS or SSH URL. Do not upload local settings, request history, bookmarks, pairing keys, cookies or stream keys. `.gitignore` intentionally excludes the large `.exe`: publish it as a release asset, not a Git commit. The ZIP still includes that EXE under `release/` for you to attach.

GitHub Actions runs the portable tests, local browser fixtures and a Windows build on pushes and pull requests. If Actions are disabled, enable them in the repository settings.

## 2. Publish the prerelease manually

1. Open **Releases → Draft a new release**.
2. Create the tag **`v0.0.0`** against your intended commit. Use a title such as **`v0.0.0 beta`**.
3. Paste the contents of `RELEASE_NOTES.md` into the description. Attach the extracted `release/YTM-Overlay-v0.0.0-beta.exe` and `release/SHA256SUMS.txt`.
4. Check **Set as a pre-release**. Verify the tag and assets, then publish. The release must remain labeled beta; do not mark it as the latest stable release.

To verify the packaged Windows executable before uploading, in PowerShell run:

```powershell
Get-FileHash .\release\YTM-Overlay-v0.0.0-beta.exe -Algorithm SHA256
```

Compare the result with `release/SHA256SUMS.txt`.

## Optional: build and publish via Actions instead

The separate **Publish v0.0.0 beta** workflow is manual-only. In GitHub **Actions**, select that workflow and run it on the intended source ref. It rebuilds the Windows EXE, computes a checksum, gathers notices for the runtime packages used in that build, and creates a GitHub **pre-release** tagged `v0.0.0` using `RELEASE_NOTES.md`. Grant Actions permission to write repository contents if required. Do not also publish the same tag manually; the workflow will fail if that release already exists. The workflow's rebuilt EXE may have a different checksum from the EXE included in this ZIP.

For a later version, deliberately update the application version, documentation, release notes and the manually scoped release workflow; the initial `v0.0.0` tag should not be reused.
