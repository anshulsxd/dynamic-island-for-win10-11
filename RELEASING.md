# Publishing an update (so Island tells your users)

One-time setup
1. Put this project in a GitHub repo.
2. Open `AppInfo.cs` and set `GitHubRepo` to your repo, e.g. `"janedoe/Island"`. Rebuild and hand out that build once.

Every update after that
1. Change the code, commit and push.
2. Create a tag and push it:  `git tag v1.1.0` then `git push origin v1.1.0`
   (or: GitHub > Releases > Draft a new release > tag `v1.1.0` > Publish).
3. The included GitHub Action builds **Island.exe** and attaches it to that Release automatically
   (it also stamps the tag's number into the app, so the version always matches).
   If you build by hand instead, attach a file named exactly **Island.exe** to the Release.

What users see
- Within a few hours (or at once via tray icon > "Check for updates") the island pops out with
  "Update available". The **Version** tab then shows a green **Install update** button.
- One click: Island downloads the new Island.exe, swaps itself, and restarts. Nothing to open on GitHub.
- If you attach a file digest on the release (GitHub does this automatically for new uploads),
  Island verifies it before installing.

Notes
- Only the newest *published, non-pre-release* Release counts. Tags must look like v1.2.3.
- Island must live in a normal writable folder (not Program Files) to replace itself.
- Updates are only downloaded from github.com over HTTPS.
