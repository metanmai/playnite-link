# Acceptance report — Playnite Link 0.5.0

Windows 11, real Playnite 10.62 Desktop and Fullscreen applications, installed GameActivity 3.7 and HowLongToBeat 3.13. Separate profiles, libraries, game IDs, device identities and IPC pipes simulate two equal devices. A synthetic executable is launched through Playnite; no personal saves are used.

| Automated group | Passed | Failed |
|---|---:|---:|
| Ledger and transport | 54 | 0 |
| Save conflicts, backups, preferences and OAuth | 55 | 0 |
| Streaming, large saves and devices | 15 | 0 |
| Passcode persistence and throttling | 9 | 0 |
| WPF app protection | 15 | 0 |
| Add-on peers, conflicts and rollback | 18 | 0 |
| GameActivity history and duplicate protection | 18 | 0 |
| HLTB absolute uploads, retries and identity checks | 11 | 0 |
| Pinned Rclone download and integrity | 4 | 0 |

**199 automated checks passed.**

## Actual two-device runs

| Run | Playnite seconds | GameActivity sessions | Online HLTB verified |
|---|---:|---:|---|
| activity-final-local-A | 68 | 3 | False |
| activity-final-local-B | 76 | 4 | False |
| activity-final-local-Verify | 76 | 4 | False |
| activity-final-cloud-A | 68 | 3 | False |
| activity-final-cloud-B | 76 | 4 | False |
| activity-final-cloud-Verify | 76 | 4 | False |
| activity-online-A | 319856 | 3 | True |
| activity-online-B | 319864 | 4 | True |
| activity-online-Verify | 319864 | 4 | True |

Each peer performs five repeated syncs. Peer A is restarted and syncs five more times with zero game-start/game-stop callbacks. All runs assert exact Playnite playtime/count, native GameActivity row count/summed seconds and HowLongToBeat Playnite-model playtime/count. Existing provider games link through the actual Connect button without manual identities.

The cloud run uses the existing personal Google OAuth/Rclone connection in a unique synthetic Drive subfolder. Both add-ons are enabled on both peers. The separate online HLTB run uses the same real account independently authorized in two isolated profiles, with real profile submissions from the normal synchronization path. Two real sessions increase the existing online entry by 16 seconds; repeated syncs and restart retain the absolute merged value. A guarded restoration returns the profile to its original progress; that restoration is verified online. Lists, reviews and completion fields are also compared before/after live no-op submissions. No online entry is duplicated or invented. Encrypted HLTB cookies are copied only to private local acceptance labs; they are not cloud-synced or packaged.

## UI and regression checks

The Desktop and Fullscreen save/conflict host probes pass again: two real game stops, local/cloud conflict choices, canceled launch, backups, preferences, stable navigation and retained draft state. Warm metadata checks measured 615 ms (Desktop) and 635 ms (Fullscreen) using a local Rclone alias, with no idle save scan or global progress window. These are not WAN latency claims.

Actual GameActivity Desktop detail and Playnite Link session-history windows were rendered and visually inspected. Native GameActivity detailed/chart views require resources absent from the Fullscreen theme; Link provides a theme-aware session history using actual native data in both modes. Tests do not patch third-party styles or claim native Fullscreen chart compatibility. Screenshots and machine-readable results are in evidence/.

## Boundaries

- Fresh connections auto-link unambiguous provider identities. Manual games/ambiguous duplicates need a reviewed shared identity; save folders need a local selection. Settings and sign-ins stay local.
- GameActivity copied histories are deduplicated by stable identity. Ambiguous timestamps, edited/deleted tracked sessions and elapsed-time disagreements require review. A problematic game does not block other games. Aggregate-only historical counts cannot supply missing session dates.
- Online HLTB updates are opt-in and require its local sign-in plus an existing, correctly mapped profile entry. New entries are not automatically created, avoiding duplicate creation races. HLTB has no numeric session-count field equivalent to the GameActivity session list.
- HLTB updates set progress absolutely, preserve greater online progress and alter no other fields. Durable receipts and periodic fresh reads repair stale progress after retries; HLTB offers no conditional-write guarantee against simultaneous external writers.
- New Google accounts still need a personal Desktop OAuth client to avoid shared-project quota. The user-specific client/credentials are never distributed. Rclone runtime acquisition is automatic, pinned and checksum-verified.
- Physical ROG Ally controls were not tested. Two actual Windows Playnite profiles exercise Desktop and Fullscreen behavior; this does not claim a second physical machine.

## Harness correction

An initial cloud fixture selected the first listed remote rather than its fixture alias. It was stopped, its synthetic device registration retired, and an audit verified zero synthetic ledger objects published in the personal root and no personal save modification. The picker now explicitly selects and asserts the fixture alias before Connect. See evidence/activity-harness-retirement.json. Subsequent cloud runs use unique isolated prefixes.

Earlier save, device, add-on and OAuth cloud acceptance remains recorded in [the 0.4 report](ACCEPTANCE-0.4.md).
