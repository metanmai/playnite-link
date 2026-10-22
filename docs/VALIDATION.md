# Current validation — 0.4.0

The authoritative current results are in [ACCEPTANCE.md](ACCEPTANCE.md) and evidence/acceptance-tally.json. There are 165 passing automated checks and six passing local Playnite application runs. Full live-cloud acceptance is incomplete because of the shared Google Drive OAuth project quota; the report distinguishes completed checkpoints from blocked ones.

## Historical validation notes

# Validation — 0.3.2, 2026-10-19

Passcode-only update: Windows Hello integration removed. Active Playnite theme brushes replace the fixed palette. Existing DPAPI passcode files remain compatible; obsolete Hello fields are ignored. Security (9 checks) and WPF lock (14 checks) suites passed for this release, including active-theme resource matching. The real Desktop Playnite host probe also passed; its lock-window screenshot was visually checked. Earlier sync validation below remains applicable.

# Earlier full validation — 0.3.0

## Automated checks

All **150 engine and security checks pass** against the installed Playnite/Newtonsoft assemblies and Windows .NET Framework:

| Evidence | Passed | Coverage |
|---|---:|---|
| `evidence/ledger-tests.txt` | 54 | Additive sessions, retry deduplication, migration, crash recovery, writer locks, malformed objects, real Rclone local transport, unchanged passes skip uploads |
| `evidence/vault-tests.txt` | 55 | Save ancestry/conflicts, cancellation, checkpoints, backups, unsafe paths, preference sync, destination pinning, OAuth continuation, account isolation |
| `evidence/streaming-device-tests.txt` | 15 | 64 MiB + 13-byte restore, 4100 files, deduplication, missing/corrupt blobs preserving originals, legacy migration, streaming cancellation, roster/settings/status convergence |
| `evidence/security-tests.txt` | 12 | DPAPI persistence, passcode verification, restart/expiry locks, persisted attempt throttling, configuration protection, stale verification invalidation, Hello failure handling, corrupt-state rejection, native availability |
| `evidence/app-lock-tests.txt` | 14 | Actual WPF windows hidden/disabled, password text routing, close requests exit, wrong/correct passcodes, new add-on windows, theme animation blocking, startup-dialog owner restoration, expiry, stale/fresh Hello policy, manual relock, exact restoration and gate-construction failure |

Security tests use disposable dummy passcodes. The user's existing protected passcode file is preserved during installation; no replacement passcode is assigned. Windows Hello's native availability result is **Available**. A real biometric/PIN verification has not been completed or automated. Hello success/failure policy is unit tested; completing the real prompt remains a user acceptance check.

## App-wide lock in actual Playnite

`evidence/lock-desktop-host.json` and `evidence/lock-fullscreen-host.json` both pass inside copied Playnite 10.62 profiles with a preconfigured dummy passcode. The library is hidden and disabled at startup; an unlinked game's SDK launch request is canceled. A new add-on settings window is also covered. Wrong passcodes leave the app hidden; the correct passcode restores the library and permits a real fixture game to start. The manual Lock Playnite action covers the app again. `lock-*-app-lock.png` is rendered from the actual unlock gate.

This gate runs after the extension initializes. It is not a Windows credential boundary: safe mode, extension removal, another profile, native windows outside Playnite's WPF process or same-user code/file access remain outside its enforcement. A gate close requests app shutdown; background sync and existing external game processes are not terminated by locking.

## Playnite Desktop and Fullscreen

`evidence/desktop-host.json` and `evidence/fullscreen-host.json` both pass using separate copied Playnite 10.62 applications/profiles, unique IPC pipes, a synthetic Cloud Trail game and Rclone v1.68.2 local aliases.

Each host launches two real fixture processes, receives SDK stop callbacks totaling 16 seconds, displays baseline 100 + 16 = 116 seconds, restores the cloud descendant before launch, publishes after play, preserves a backup and applies remote Favorite/Notes. A third launch is canceled at a real conflict; a reviewed cloud branch then restores successfully. Device and security windows also render successfully.

Screenshots `*-demo-library.png`, `*-conflict-*.png`, `*-settings.png`, `*-personal-account.png`, `*-devices.png` and `*-security.png` are actual WPF windows rendered by a test companion inside Playnite. Button events are invoked by the companion. Native mouse/gamepad input for copied instances is not claimed: the computer-use helper could not bind their returned process identities. Screens were visually inspected; theme text is legible.

The 20 ms UI heartbeat continues during background work, caches retain existing object timestamps and no routine progress windows appear. Warm local-alias passes: **Desktop 484 ms; Fullscreen 470 ms**. These are small synthetic-vault measurements, not WAN, large-library or multi-gigabyte throughput claims. File hashing still reads save bytes to detect changes reliably.

## Existing Rclone Google Drive

The installed Rclone configuration's `gdrive` remote was used without printing or packaging credentials. Only synthetic test data was uploaded under `PlayniteLinkTests/...` and the dedicated `PlayniteLink/playnite-link-v1` vault.

`evidence/live-cloud-tests.txt` records successful upload/download and SHA-256 comparison of a **40 MiB + 13-byte file**, plus an identical second file sharing its blob (80 MiB + 26 logical bytes, excluding a small progress file). Independent device clients created divergent edits, selected a branch, preserved the losing directory backup and converged after downloading the resolution. Real cloud device roster, remote interval/bandwidth settings, pause/resume and removal checks also completed.

That combined suite subsequently **failed on Google Drive quota throttling during its ledger phase**. Its failed status is retained. The separate `live-ledger-tests.txt` and `live-ledger-result.json` subsequently **pass**: two cloud clients exchange a shared baseline of 100 and distinct 17/23-second sessions, converge to 140 seconds and preserve that total on repeated exchanges. Its first provider attempt failed and was retried. The large-save/device stages and ledger stage therefore passed across separate runs, not one uninterrupted suite.

Google reports shared Rclone OAuth client project request quota errors intermittently. Request pacing and bounded retries are enabled; failed passes preserve local changes. Save Manager independently also displayed cloud connection failures. Provider quota availability is outside this extension's control.

## Installation in the user's profile

The extension is installed under `%APPDATA%/Playnite/Extensions/PlayniteLink`, connected to `gdrive:PlayniteLink`. Only **Playnite Link Cloud Demo** is linked. Its synthetic cloud save (`cloud-before-launch`), Favorite/Notes and remote per-device interval arrived in the real profile. Commercial saves and existing Save Manager settings were not edited.

A complete two-session cloud-backed launch/after-play acceptance test in this normal profile has not yet passed: quota errors interrupted preflight, and offline launch was declined. This is distinct from the passing isolated Desktop/Fullscreen lifecycle tests. Final installed-host evidence records the outcome. Temporary test companions are removed from the active extension directory before delivery.

## Remaining acceptance scope

Real Hello challenge completion, new provider OAuth consent, Dropbox/OneDrive live transfer, two physical PCs/ROG Ally, controller-only setup, commercial-game formats, real Syncthing peers, network partitions and long-term vault growth remain unverified. Immutable history and backups are retained without automatic garbage collection. This release is installable and extensively tested, but the outstanding live-profile/cloud quota gap prevents an unconditional everyday-use readiness claim.


