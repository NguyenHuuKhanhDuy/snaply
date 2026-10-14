# Releasing Snaply

Releases are built by `.github/workflows/release.yml` (Velopack + GitHub Releases, Windows x64).

## Cut a release

1. Make sure `main` is green (`dotnet test Snaply.sln`).
2. Tag and push (the version comes from the tag, `v` stripped):
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. The workflow runs the tests, publishes `win-x64` self-contained, packs with `vpk` (installer, full package, delta against the previous release), uploads a GitHub release named `Snaply v0.2.0`, then checks that `releases/latest/download/releases.win.json` lists the version. A red check means installed apps would not see the update.
4. Run the workflow manually (Actions → Release → Run workflow) for a dry run: it builds a throwaway `0.0.1-ci.N` package and publishes nothing.

Use semantic versions (`MAJOR.MINOR.PATCH`). Pre-release suffixes are not offered to users.

## How users get updates

- Installed copies (from `SnaplyApp-win-Setup.exe`) check the GitHub release feed ~10 s after start, at most once an hour, and again every 30 minutes while the app keeps running in the tray, when **Settings → Updates → Check automatically** is on. A banner offers **Update now**, **Release notes** and **Later**. Nothing downloads or restarts without a click.
- After downloading, the user chooses **Restart to update** (blocked while a capture overlay is open; asks for confirmation if the editor is open — edits are saved automatically) or **Install on next launch**.
- **Settings → Updates → Check for updates** always checks manually and reports the result.
- Builds that were not installed with the installer (Rider, `dotnet run`, unzipped publish) show "Updates are only available in the installed version".
- The only network request is the public GitHub release list/feed. No screenshots, OCR text or personal data are sent. Turning the setting off means no request until the button is pressed.

## Important details

- **Pack id is `SnaplyApp`, not `Snaply`.** Velopack installs to `%LOCALAPPDATA%\<pack id>`; Snaply stores screenshots and its database in `%LOCALAPPDATA%\Snaply`. Using different folders guarantees an update or uninstall can never touch user data. Do not change the pack id.
- `vpk` and the `Velopack` NuGet package must be the same version (`VELOPACK_VERSION` in the workflow and `Directory.Packages.props`). The workflow fails early on a mismatch.
- The feed repository is `Snaply:Update:RepoUrl` in `appsettings.json` (empty disables updates).

## Rollback

Installed apps never downgrade. To roll back a bad release, publish a fixed higher version (for example `v0.2.1`). If a release must disappear immediately, delete its assets/release on GitHub so the feed stops offering it; users who already updated keep that version until the fix ships.

## Known limitations

- Windows x64 only. macOS/Linux packaging is future work.
- Installers are **not code-signed**, so Windows SmartScreen may warn on first install. Signing (SignPath, Azure Trusted Signing or a certificate) is a follow-up.

## The repository (or the release repository) must be public

The in-app updater and the workflow's "Verify release is visible to the updater" step read the release feed **anonymously**. For a private repository GitHub answers 404 to anonymous requests, so installed apps would never see an update (and the verify step fails with an explanatory error). Either make the repository public, or publish the release assets to a separate public repository and point `Snaply:Update:RepoUrl` and `REPO_URL` in the workflow at it. Embedding an access token in the app is not an option: anything shipped in the app can be extracted.

`vpk download` in the workflow uses the job's `GITHUB_TOKEN` so it is not subject to GitHub's anonymous rate limit (a 403 "rate limit exceeded" is what the first run showed).
