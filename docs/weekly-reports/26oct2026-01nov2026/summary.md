26 Oct 2026

Tasks
- Built and deployed release/2026.10.26.01 to Azure dev: bcp-api-dev and bcp-web-dev both on 2026.10.26.01; API settings synced (18); /health/version reports ready

Bug fixes
- None

Investigated, follow up pending
- None

28 Oct 2026

Tasks
- Scanned every local and GitHub branch for the injected malware (auto-run VS Code task, fake font script, api.js in npm start, karma.conf.js tail); all local branches clean
- Cleaned 9 infected GitHub branches (main, release/2026.10.16.01, 10.19.01, 10.19.02, 10.23.01, 10.26.01, merged/oct15-all, feature/workspace-clause-limit, security/critical-fixes-2026-10-21) with no-force merges that keep the real local files; all 29 GitHub branches rescan clean

Bug fixes
- None

Investigated, follow up pending
- Force pushes cannot be blocked on the free GitHub plan; relying on revoked tokens, 2FA and a pre-pull scan instead
- Old history (Aug to 26 Oct) still holds the forged commits; do not check out or build those commits
