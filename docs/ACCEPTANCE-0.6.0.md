# Save Manager replacement acceptance — 0.6.0

Date: 23 October 2026. Target: Windows 11, Playnite 10.63, .NET Framework 4.8.

The replacement reuses Jurangren Save Manager 2.0.3's backup service, models,
portable-path helper and Windows notification service at commit
`c879d81bd864b23ab903dd2a62ecf30063bda518`. The combined distribution is AGPL-3.0
and includes its corresponding source. Integration changes are listed in
`vendor/SaveManager/UPSTREAM.md`; reused code is not assumed correct without tests.

## Feature inventory

Inventory checked against upstream README, SaveManagerSettings,
SaveManagerViewModel commands and plugin menus, rather than screenshots alone.

| Upstream capability | Link replacement and verification |
| --- | --- |
| Multiple folders and individual files | Ordered source paths, stable IDs, per-source type declarations; core ZIP and three-source restore tests |
| User/game/emulator portable paths | Reused path helper with directory-boundary fixes; moved-installation and emulator tests |
| Add, edit, remove and open paths | Save paths window, explicit import review; actual Desktop/Fullscreen window checks |
| Manual and quick backups, notes | Reused ZIP/CRC core; same-second uniqueness, persisted notes/import/reload tests; single/batch quick backup menus |
| Last nine backup shortcuts | Restore backup game submenu; host check |
| Safe restore and recovery | Pre-restore ZIP plus validated transactional restore; single-root and multi-root process-kill/two-restart tests |
| Exclude files/folders on restore | Local protection, neighboring-prefix tests, raw shared-data preservation; actual host restore |
| Force restore ignoring exclusions | Explicit one-time action, retains exclusion preferences; host restores excluded settings and checks preferences |
| Import/export path configuration | Own and native configurations, explicit local game/path review; parser and host tests |
| External ZIP import/export | Native mapping/CRC plus plain ZIP normalization; traversal/duplicate/undeclared-entry rejection |
| Global configuration/backup bundles | All configurations, including unlinked/orphan backup groups; SHA manifest, original notes/date/automatic flag, atomic replacement with previous bundle |
| Game matching | Explicit local game selection; native IDs never implicitly match arbitrary names; migration and bundle UI checks |
| Local backup root and game overrides | Copy and SHA verify before updating paths; old files preserved; host relocation checks |
| Auto backup at game exit | Serialized stopped-game backup followed by sync; per-game opt-out and failure reporting |
| Retention, zero unlimited | Reused core, only automatic backups pruned; named manual backups retained |
| Confirmation and notifications | Per-game confirmation, optional Windows notification and Playnite error notification; notification service reused |
| Sidebar and startup sync | Settings retained/imported; actual host sidebar toggle and settings checks |
| Reinstall recovery offer | Installed-state tracking, notification and opt-out; host checks |
| Live/latest save version and version tracking | Immutable SHA-256 revisions with ancestry; CRC retained for native archives; conflicts never decided by clock |
| Cloud connect/test and prelaunch sync | Drive, OneDrive, WebDAV, R2/S3 and pCloud plus Dropbox; backend continuation tests, actual WebDAV/S3 configuration tests, live Drive transfer |
| Pull/push/config sync | Full sync exchanges validated metadata and required blobs; legacy cloud recovery is read-only and separately reviewed |
| Backup download/re-upload/availability verification | Library downloads never restore implicitly; original note/date/automatic flag checked in host; re-upload queues selected archive; verification freshly downloads and hashes current saves and active backup objects |
| Delete backups and empty-folder cleanup | Native service deletion, shared catalog tombstones; immutable older history retained |
| Delete local data | Verified recovery export required before reset, no live-save deletion, cloud paused; host reset checks |
| Delete cloud data | Explicit exact-destination confirmation, purge confined to selected `playnite-link-v1`; real Rclone alias scope test preserves sibling files |
| Device sessions and cloud status (additional request) | Named devices, start/end/duration history, per-device revision/check receipt, unknown/behind/playing/pending/stale states cannot claim current sync |

## Verification evidence

`docs/evidence/save-manager-060/` contains sanitized results, suite transcripts and
the machine-readable tally. Tests use isolated profiles and destinations. No live
game save was replaced by acceptance testing.

- Regression gate: 13 compiled suites plus setup and retirement helper tests.
  The individual case counts and exit codes are in `tally.json`; no failing
  suite is accepted. This includes 42 replacement cases and real process kills
  between multiple save roots followed by two recovery runs.
- Actual Playnite Desktop and Fullscreen: 47 replacement assertions each,
  covering native ZIP restore, exclusions, force restore, named sessions,
  receipts, global bundles, migration replay, custom roots, download metadata,
  reinstall offers, sidebar and reset without touching live progress.
- Existing Desktop and Fullscreen runtime/visual/performance probes: actual
  synthetic game launch/stop, prelaunch cloud restore, conflict cancel/local/cloud,
  interrupted restore, running-game rejection and session projection passed.
- Live Google Drive: multi-source restore, ZIP SHA round trip, offline branching,
  cancellation preservation, explicit resolution, 79 seconds of converged session
  history and device receipts passed against a dedicated synthetic vault.
- Nine distinct available legacy Dispatch ZIPs: source SHA unchanged, native
  import and byte-for-byte isolated restore round trips passed.
- Long Windows fixture paths: the 42 replacement cases rerun successfully after
  shortening atomic temporary filenames. Earlier failed transcripts are retained
  privately; they are not counted as passes.

## Migration status and missing data

The user's old catalog references `Backup_20261018_171913.zip` (CRC `0913AB88`).
That ZIP was absent from local backups, the legacy cloud folder and its trashed
listing. Nine available archives were recovered read-only and verified. An
additional unmatched/zero-ID legacy record is preserved in original configuration;
it must not be silently discarded or treated as a recoverable ZIP.

The migration flow copies original configuration/settings and all available
archives, verifies hashes and fresh persisted import records, and is idempotent.
Missing records are displayed and block retirement unless explicitly acknowledged.
The retirement helper waits for Playnite to exit, rechecks source/configuration/
assembly/recovery hashes, then moves only the verified old extension outside
`Extensions`. Original data and recovery copies remain. Coexistence pauses Link's
save control until the old extension is retired and Playnite restarts.

This build has not been installed into the user's normal profile, the normal
profile has not been migrated, and the old extension has not been removed by these
tests. No new GitHub release is implied by producing a local package.

## Deliberate differences and limits

- The old synchronization worker is replaced by Link's conflict-aware immutable
  vault, rather than running two controllers over the same saves. A named ZIP is
  a checkpoint; the current save revision is the equivalent of upstream Latest.
- Paths/settings stay local unless explicitly reviewed. Reordered, removed or
  type-changed sources fail before replacement; an individually empty source
  requires review even if another source still has files.
- Exclusions apply to restores and comparisons. Existing unexcluded peer data
  remains in shared history; missing local excluded files cannot delete it.
- Backup deletion/retention removes active library entries and local ZIPs, not
  historical content-addressed cloud objects. Whole-vault erasure is separate.
  Re-upload retries missing objects; corruption of an existing immutable object
  is reported by verification rather than overwritten silently.
- English UI is supplied. Upstream's Simplified Chinese localization and exact
  screen layout are not reproduced. Bulk quick backup is supported; backup
  deletion in the manager operates on one selected archive at a time.
- Notes are bounded at 100,000 characters; metadata at 1 MiB. `__pl_roots` is a
  reserved primary-save namespace. Symlinks/junctions, Windows ACL restoration
  and preservation of filesystem timestamps are outside the save-byte contract.
- All six provider protocols are tested; live authentication/transfer acceptance
  for this release covers Google Drive only. Other real provider accounts,
  physical Ally/gamepad input, Windows 10 and an actual commercial-game launch
  after restore are not claimed. Dispatch save bytes were tested in private
  destinations, not launched in the game.
- A successful receipt means up to date at the last confirmed check. Offline
  devices can have unsent changes; no UI can establish their present state.

The matrix verifies the listed cases. It is not a claim to have exercised every
possible game, disk failure, provider outage or input combination.
