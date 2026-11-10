# Publishing the reviewed source snapshot

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -PublicRelease
powershell -ExecutionPolicy Bypass -File .\tests\run.ps1
```

To regenerate the README screenshots, run `powershell -ExecutionPolicy Bypass -File .\tools\CaptureScreenshots.ps1`. This temporarily shows the actual browser with an isolated profile and local fictional pages. It captures only the app windows, never the desktop. `demo.fedatather.test` maps locally to `docs/demo`; it is not a public service.

To audit and package the public source, use Python 3:

```bash
python3 tools/prepare_public_release.py --package
```

The ZIP at `build/Fedatather-source.zip` contains reviewed source files, original public assets, documentation, and screenshots. It excludes `.git`, local agent state, credentials, the personal Boss image, build caches, browser profiles, settings, exports, and binaries. A SHA-256 checksum is written alongside it.

Extract that ZIP into an empty folder and initialize a new Git repository there for GitHub. Existing local agent checkpoint refs contain the personal journal-page image; the clean ZIP deliberately carries no history or checkpoint refs. Do not upload the whole workspace or mirror all its refs.

For binary downloads, use the output from `build.ps1 -PublicRelease`, retaining the three runtime files and all accompanying license/notice files. A normal local build may embed your private Boss override and should not be used as the public binary artifact.

The audit checks candidate files against a source allowlist, common credential patterns, personal machine paths, image EXIF/XMP, and representative ignore rules. It verifies each ZIP member against the audited source. It cannot prove the absence of every possible secret or establish rights to future contributions. Re-run it after changes and inspect any newly added screenshots or assets.

## Privacy behavior

The application uses a local WebView2 profile for cookies, logins, cache, and browsing data. Settings, saved app state, export-folder preferences, and folded tab titles/URLs are stored under `%LOCALAPPDATA%\Fedatather\`, outside the source repository. Folded links and preferences are plain files, not encrypted storage. Exported images and CSVs may contain personal or third-party content.

Browsing contacts the sites opened by the user; icon lookup can download a site's declared icon. The app has no application-owned analytics or upload endpoint in the reviewed source. WebView2 and visited websites have their own network and privacy behavior. Boss mode hides the interface; pages remain loaded, and it is not a security lock.
