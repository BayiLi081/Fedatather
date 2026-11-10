# Third-party components and original assets

Fedatather's source code, icon, public Boss graphic, screenshot tools, and fictional demo content are provided under the repository's MIT license. The original public Boss graphic is reproducible with `tools/New-BossImage.ps1`.

The WebView2 SDK is downloaded from Microsoft's NuGet package during the build. It is not part of the source snapshot and is covered by its own license. Binary builds include the package's unmodified `LICENSE.txt` and `NOTICE.txt` as `WEBVIEW2-LICENSE.txt` and `WEBVIEW2-NOTICE.txt`. Retain these files when distributing the executable and DLLs. The separately installed WebView2 Runtime and Windows components have their own terms.

A personal `assets/boss_here.jpg` override is excluded from the public snapshot and is not covered by this project's MIT license. Public builds always embed `assets/boss_public.jpg` instead. Demo screenshots use the public image and fictional, locally served content; they contain no third-party publications or signed-in websites.
