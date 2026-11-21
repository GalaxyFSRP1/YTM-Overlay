# Update GitHub to v0.0.1 beta

This ZIP does **not** publish anything automatically. Review the source, README, release notes and license before publishing. The package includes an EXE for a GitHub release; `.gitignore` keeps large executables out of the source commit.

## Update the existing repository

1. Extract the v0.0.1 ZIP to a **new folder**. Keep your existing Git repository and its `.git` folder; do not run `git init` again. Back up any work you have not committed.
2. In Command Prompt, change to your **existing** local repository (the folder where your successful v0.0.0 push was made). Confirm it is clean, and update it:

```cmd
git status
git pull --ff-only origin main
```

If you have uncommitted changes, save or commit them first. If the fast-forward pull fails, resolve that separately; do not force-push.

3. The included `release/UPDATE-from-v0.0.0.patch` was prepared against commit `682ea2acf8df372dee6a336c35a445f53de908aa` on the existing repository's public `main` branch. Replace the example path below with the **actual path** to the patch inside the freshly extracted v0.0.1 folder:

```cmd
git apply --check --3way "C:\path\to\YTM-Overlay-v0.0.1-beta\release\UPDATE-from-v0.0.0.patch"
git apply --3way "C:\path\to\YTM-Overlay-v0.0.1-beta\release\UPDATE-from-v0.0.0.patch"
git status
git diff --cached --stat
```

Only run the second `git apply` if the check succeeds. If it fails, the repository may have changed; stop and resolve the differences instead of overwriting your work. `--3way` may stage the changes. Review them before committing. The patch does not contain the large EXE; the ZIP does.

4. Commit and push the reviewed changes:

```cmd
git add .
git commit -m "Update to v0.0.1 beta"
git push origin main
```

Never upload runtime settings, request history, bookmark codes, pairing keys, cookies or stream keys. The EXE and the generated patch are ignored by Git; `release/NOTICES.txt` and the checksum are tracked.

## Publish the prerelease

1. On GitHub, choose **Releases → Draft a new release**.
2. Create a **new** tag `v0.0.1` against the intended commit. Do not overwrite your v0.0.0 release or tag. Use the title `v0.0.1 beta`.
3. Paste `RELEASE_NOTES.md` into the description. Attach these three files from the **newly extracted v0.0.1 ZIP**:
   - `release/YTM-Overlay-v0.0.1-beta.exe`
   - `release/SHA256SUMS.txt`
   - `release/NOTICES.txt`
4. Select **Set as a pre-release**, review the assets and publish. The notices must accompany the EXE if redistributed.

Verify the packaged executable in PowerShell:

```powershell
Get-FileHash .\release\YTM-Overlay-v0.0.1-beta.exe -Algorithm SHA256
```

Compare it with `release/SHA256SUMS.txt`.

## Optional: use the manual release workflow

The **Publish v0.0.1 beta** Actions workflow is manual-only. Run it on the intended source ref to rebuild the EXE, compute its checksum, generate notices for that build's runtime and create a `v0.0.1` pre-release. Grant Actions permission to write repository contents if needed. Do not publish the same tag manually as well. A rebuilt EXE can have a different checksum from the packaged one.

If starting a different, empty repository, initialize Git in the extracted project folder, commit the source and add your own remote URL. If the remote already has commits, integrate its history rather than using a force-push.
