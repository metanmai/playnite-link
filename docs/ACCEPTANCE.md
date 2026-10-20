# Acceptance report — Playnite Link 0.5.1

The previous 45-second launch path queued a full-vault network synchronization behind existing work. Ordinary launches now proceed from local saves without waiting for network work. Known cached conflicts, unfinished sessions, app protection and the final local save replacement remain safeguards. Cloud saves discovered during play are deferred until the game stops. Users switching devices can explicitly synchronize first when they need the newest remote save before starting.

Device, ledger, GameActivity and save-revision metadata use one filtered Rclone download. Blob payloads and unrelated folders are excluded. Game data converges before optional HLTB/add-on work. The GameActivity retry waits for the current cycle and avoids an unconditional second full synchronization.

## Actual two-device timing

Real Playnite 10.62 Desktop and Fullscreen instances, with GameActivity 3.7 and HowLongToBeat 3.13 enabled. The Google Drive run uses the existing authorized personal Rclone connection under a unique synthetic prefix. Launch occurs while background sync is already in progress. Each peer performs five repeated syncs; the first peer is restarted and verifies convergence without gameplay events.

| Run | Launch ms | Warm metadata ms | Sessions | Seconds |
|---|---:|---:|---:|---:|
| launch-final-local-A | 470 | 170 | 3 | 68 |
| launch-final-local-B | 519 | 184 | 4 | 76 |
| launch-final-local-Verify | 0 | 187 | 4 | 76 |
| launch-final-cloud-A | 571 | 4099 | 3 | 68 |
| launch-final-cloud-B | 464 | 4049 | 4 | 76 |
| launch-final-cloud-Verify | 0 | 3898 | 4 | 76 |

All measured launches and warm metadata checks are below the 10,000 ms target. A zero launch value on Verify means no game was launched during that phase. Cloud checks are real WAN measurements; local-alias results are labeled separately. These timings do not promise that arbitrary large save transfers, offline networks, quota failures, user conflict decisions or addon installations finish in ten seconds.

## Automated regression checks

| Group | Passed | Failed |
|---|---:|---:|
| Ledger, transport and filtered metadata batch | 55 | 0 |
| Save conflicts, backups, preferences and OAuth | 55 | 0 |
| Streaming, devices and launch-during-staging safety | 16 | 0 |
| Passcode persistence and throttling | 9 | 0 |
| Application protection | 15 | 0 |
| Add-on peers, conflicts and rollback | 18 | 0 |
| GameActivity identity and duplicate protection | 18 | 0 |
| HLTB progress and identity checks | 11 | 0 |
| Pinned runtime integrity | 4 | 0 |

**201 checks passed.** Save staging is tested with a launch-time veto after remote files have staged: local contents remain unchanged, no backup/swap occurs, and the temporary stage is cleaned up. The actual Desktop and Fullscreen save host tests cover cloud/local conflict choices, canceled launches, backups, preferences, stable UI navigation and no global progress dialogs.

The 0.5 session/online HLTB acceptance is retained in [the release notes](../CHANGELOG.md). This change does not alter online HLTB progress semantics. Physical ROG Ally hardware is not claimed; the two actual Windows profiles exercise Desktop/Fullscreen modes. Save directories, addon sharing and online HLTB remain explicit local configuration choices.

