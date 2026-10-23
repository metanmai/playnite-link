# Playnite Link 0.6.2

Personal-cloud save, playtime and preference sync for Playnite. **Your storage account owns the vault. There is no Playnite Link account server, hosted user directory, subscription, or central save database.**

This is an independent extension, not an official Playnite product. Version 0.6.1 targets Windows 10/11, Playnite 10 and .NET Framework 4.8. It incorporates Jurangren's Save Manager 2.0.3 backup core under AGPL-3.0. Corresponding source accompanies the package. An existing Rclone installation is reused, or an official pinned, checksum-verified runtime is prepared in the background.

## Install and connect

1. Open `release/PlayniteLink_8ca18482-3ebe-4bf7-b229-a259b2094767_0_6_1.pext` in Playnite and restart when prompted. Upgrade every device to 0.6.0 before using multiple save sources.
2. Open **main menu → Playnite Link → Personal cloud account & settings**.
3. Select **Connect**, choose an existing authorized storage connection, and connect. The default vault folder is **PlayniteLink**. Existing Rclone and Save Manager configurations are discovered automatically. Advanced configuration remains available for a different vault path.
4. New connections support Google Drive, Dropbox, OneDrive, pCloud, WebDAV and Cloudflare R2. Browser providers use their sign-in pages; WebDAV and R2 ask for endpoint/access credentials locally. Google Drive requires your own Desktop OAuth client JSON to avoid Rclone's shared project quota. R2 vault destinations include the bucket name. New connections use a unique local configuration file. Credentials are never shared with other devices.
5. On the second device, authorize the same storage account independently and select the **same folder in that account**. Remote names can differ between devices. The first successful connection pins each local profile to its destination to prevent accidentally mixing personal accounts. To change vaults, use **Switch vault / start fresh**; previous local profiles and cloud history are retained.

The extension creates only `playnite-link-v1` under the chosen destination, containing immutable ledger, revision and device metadata plus content-addressed save blobs. Use a dedicated personal vault folder. Users who want encryption at rest can select an [Rclone crypt remote](https://rclone.org/crypt/); a normal remote does not encrypt save contents at rest.

## Link a game on both devices

1. Right-click a stopped game → **Playnite Link → Link game…**.
2. If the game is already in your cloud, select it by name and device. Otherwise choose **Add a new game to cloud**; its name is filled in for you. You never need to type shared keys or enter playtime totals.
3. To include saves, choose this game's dedicated save folder in the same screen. This is a one-time choice on each device; paths can differ. Leave this optional if another tool already handles saves.
4. Click **Link game**. Playtime and sessions sync in the background automatically. On your second device, link the local game to that same cloud entry. Existing local playtime is retained for review rather than added twice to shared history.
5. Play as usual. You only need to make a choice when devices have different save versions. A new empty save folder restores the cloud version after linking.

**Cloud sync…** opens a simple status page for a linked game. **History and options** holds session/save history, unlinking and troubleshooting. You can also include saves later from this page. Existing device pause and automatic-sync choices are respected. Optional automatic selection of store games remains controlled by **Automatically include library games**.

Only explicitly linked games are managed. Favorite, Hidden and Notes sync for linked games. Configure save roots separately for each game/device. Playnite store extensions keep their own storefront logins; this extension does not copy their credentials, install paths or the Playnite database. Optional add-on synchronization separately transfers extension/theme packages, with settings staying local. Disable Save Manager / Steam Cloud / Syncthing management of the same live save directory before enabling this extension for it. Unrelated Save Manager configuration is not changed automatically.

## Save Manager replacement

Right-click a linked game → **Playnite Link → Save manager…**. Add folders or individual files, keep selected files/folders local during restores, create named ZIP backups, edit notes, import/export backups and per-game configurations, and set automatic backup retention. `{GameDir}`, `{EmulatorDir}` and environment variables follow local installations. Global settings include custom backup locations, sidebar access and startup checking. Reinstalling a game offers recovery; quick/batch backups are available from game context menus.

**Sessions & device sync status…** shows start/end times, duration and named devices, plus the last confirmed revision and session digest from each device. Unknown, stale, paused, playing and divergent devices do not receive a current-sync claim. Confirmation describes the last completed check, not proof that an offline device has no unsent progress. **Verify cloud save & backup files** downloads current referenced blobs afresh and checks SHA-256.

Use **main menu → Playnite Link → Save Manager migration & portability** to recover the old cloud, map each old game, and copy/verify existing backups. Notes, timestamps, automatic/manual flags, path exclusions and backup preferences are retained. Unmatched/missing historical records remain in original configuration archives and are reported. Link pauses while the old extension is loaded; verified retirement archives that extension after Playnite exits. Old data and authorization files remain recoverable. Add-on sharing cannot silently reinstall the retired extension on that device.

ZIP imports add history without replacing live saves. Global packages include configurations and backups, including backups belonging to unlinked games. Imported settings and save destinations require review. Native Save Manager ZIPs and ordinary one-folder ZIPs are supported. Backup layout order/type must match configured destinations when restoring; source IDs/types must match across devices for automatic multi-source sync. Layout mismatches stop before replacement. Local restore exclusions do not remove those files from snapshots published for peers that have not excluded them.

Automatic retention deletes old local automatic ZIPs and removes their entries from the shared active backup list. Immutable cloud history is retained; this is not automatic cloud-space reclamation. Data maintenance provides local reset with a verified recovery bundle and explicit erasure of the selected cloud protocol area. Cloud erasure includes sessions and other Link data; keep peers paused and export needed data first. Symlinks/junctions remain unsupported. The internal `__pl_roots` snapshot namespace is reserved. Backup notes are bounded to 100,000 characters.

## Normal use and conflicts

- Startup and after-play synchronization run on background workers. Idle metadata checks back off from the configured minimum to 15 minutes (or the minimum if longer), skipping save scans when no work is pending. File scanning, hashing and Rclone processes stay off Playnite's UI thread. A serialized queue prevents overlapping sync passes. Persistent download caches skip existing immutable objects.
- Save-linked games check cloud saves before launching by default. You can disable this per game in Save paths & options. A failed check offers an explicit offline choice. Known conflicts and unfinished sessions still require review. Startup/after-play sync runs in the background; wait for upload completion before switching devices.
- If one side changed since the common version, it syncs automatically. If both changed, choose **Use this device**, a **cloud version**, or **Cancel**. Cancel keeps both versions separate and does not start the game. Device clocks do not decide the winner.
- Binary saves are resolved as whole-directory versions. The extension does not guess how to merge game-specific save formats. A restore stages files, preserves the old directory beside the save folder as `.pl-backup-<id>`, and then switches directories. Keep these backups until you are satisfied with the chosen version.
- **History and options → Save history** restores a cached historical version as a new revision. The old revisions remain intact. Sync first to fetch history from other devices.
- Offline sessions and snapshots are durable and retry without counting playtime twice. A crashed/unfinished play session requires **History and options → Troubleshooting → Review an interrupted session** with verified seconds. Wall-clock sleep or crash time is not guessed. If another tool or a manual edit changes playtime, **Restore playtime from cloud history** requires explicit review.

Personal drive sync is eventually consistent. Devices playing offline at the same time can create branches; those branches are detected after reconnection. No extension can guarantee a global lock or immediate visibility while devices are disconnected. A cloud upload must finish before another device can download it. Leave Playnite open long enough for the background status to complete after play.

## Privacy, recovery and limits

- **Connected devices** shows devices connected to your vault, last check-in, status and linked games. Edit each device's name, background interval, transfer bandwidth and saves/playtime/preferences switches; pause, resume or remove a device. Changes apply on its next check-in. Conflicting settings require review. Save paths stay local unless that device explicitly enables path sharing. Removed devices leave the roster; historical graph records remain for safe convergence.
- **Passcode** now locks **Playnite as a whole**. The library, settings and add-on windows are hidden/disabled, and launches of all games (including unlinked games and shortcut/IPC launch requests) are canceled until unlock. Enable it under Protection with a local 6–128 character passcode. It locks at startup, Windows session lock, manual **Lock Playnite now**, or expiry (1–60 minutes since unlock). Closing the unlock screen exits Playnite; Escape does not dismiss it. Background sync and already-running games continue; relocking avoids activating the lock window over another foreground application.
- Existing 0.2 passcodes automatically apply to the whole app after upgrading; no passcode reset is needed. Failed attempts remain throttled; the PBKDF2-SHA256 verifier remains protected with Windows DPAPI. A Windows lock arriving during verification invalidates that attempt's late result. Damaged protection settings keep the app locked, and an unlock-screen failure closes the app instead of bypassing protection.
- **App-lock scope:** this is enforced by the running extension after it initializes, not by Windows credentials. It does not encrypt files or prevent bypass through disabling/uninstalling the extension, Playnite safe mode, another profile, or code/file access under the same Windows account. It covers Playnite's WPF windows; arbitrary native windows belonging to other software are outside its control. No passcode is invented during installation.

- The user's provider grants and revokes storage access. To remove a lost device, revoke its authorization with the provider. This cannot erase copies it already downloaded. Rclone credentials stay local and are never put into vault records. Protect `personal-rclone.conf` like any other OAuth credential file; Rclone's password obscuring alone is not encryption.
- Pause cloud sync in the account panel without deleting local history. Local data lives in Playnite's `ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767`. State writes keep `.bak` files. Back up that directory before manual repair.
- Do not delete individual ancestor objects from the vault: missing history fails closed. This version preserves history without automatic pruning or compaction. Identical files share SHA-256 blobs across paths and revisions; changed files need a new full-file blob. Local history, cache and restore backups also consume disk space.
- **The 32 MiB save limit and 4096-file limit are removed.** Saves stream through 128 KiB buffers, with SHA-256 validation before restore. There is no configured total save-byte cap; free disk space, provider quotas and filesystem limits still apply. Metadata has a defensive 64 MiB per-object bound, separate from raw save payloads. Older inline snapshots migrate while retaining ancestry. Keep all devices on 0.6.0 or newer for multi-source saves. Large-library longevity and multi-gigabyte saves still require broader acceptance testing.
- **Stop current transfer** cancels Rclone and streaming work while preserving queued local changes. Provider throttling leaves changes queued for retry; it cannot guarantee fast WAN synchronization. Google Drive's shared Rclone OAuth client was intermittently quota-limited during live validation.
- Junctions/symlinks, unsafe Windows filenames, drive roots and shared system directories are rejected. Empty folders are not represented in snapshots. File ACLs, alternate streams and timestamps are not copied; ordinary game-save bytes and relative paths are.
- Setup and conflict review use native Playnite dialogs. Gamepad-only enrollment and physical ROG Ally controls need hardware acceptance. There is no cloud worker running while Playnite is closed.

## Syncthing option

The extension's conflict resolution remains necessary even with Syncthing: [Syncthing preserves file conflicts](https://docs.syncthing.net/users/syncing.html#conflicting-changes) but cannot combine playtime or understand save semantics.

For peer-to-peer transport, configure a Syncthing folder **only for the immutable vault**, then an Rclone alias on each device:

```ini
[personal]
type = alias
remote = C:/Users/you/Syncthing/PlayniteVault
```

Select `personal:profile` as the extension's remote destination. Syncthing transports the generated object files; the extension manages the live game directory. Do not synchronize live Playnite databases, local extension state, OAuth files, or the same live saves with both tools. This alias path is exercised with a local filesystem backend; a real Syncthing peer pair has not been acceptance-tested.

## Build and test

The [21 October source audit](docs/AUDIT-REPORT-2026-10-21.md) records the original findings. The [22 October closure report](docs/AUDIT-CLOSURE-2026-10-22.md) closes the fresh Google enrollment/WAN and abrupt-termination recovery gates. Recommendation: **conditionally ready for Windows 11 / Playnite 10 with Google Drive**, subject to the report's platform/provider/scale limits. Recovery has durable journals and actual process-kill tests. Older acceptance reports are historical.

Run the local regression gate with `pwsh -NoProfile -ExecutionPolicy Bypass -File ./tests/Run-Regression.ps1`. Supply `-RcloneExe` if Rclone is installed elsewhere; `-IncludeRuntimeDownload` also exercises the pinned runtime download. Every run uses new local Windows fixtures and stops on failure. `build.ps1` and `package.ps1` accept `-BuildDirectory`; packaging also accepts `-ReleaseDirectory` for isolated candidate output.

No .NET SDK installation is required on a machine with the .NET Framework compiler and Playnite installed:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File ./build.ps1
./build/Tests.exe "$env:LOCALAPPDATA/PlayniteLink-ledger-tests" "C:/path/to/rclone.exe"
./build/VaultTests.exe "$env:LOCALAPPDATA/PlayniteLink-vault-tests" "C:/path/to/rclone.exe"
./build/StreamingTests.exe "$env:LOCALAPPDATA/PlayniteLink-streaming-tests"
./build/SecurityTests.exe "$env:LOCALAPPDATA/PlayniteLink-security-tests"
./build/ApplicationLockTests.exe "$env:LOCALAPPDATA/PlayniteLink-app-lock-tests"
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tests/Run-HostProbe.ps1 -Mode Desktop -LockProbe
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tests/Run-HostProbe.ps1 -Mode Fullscreen -LockProbe
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tests/Run-HostProbe.ps1 -Mode Desktop
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tests/Run-HostProbe.ps1 -Mode Fullscreen
pwsh -NoProfile -ExecutionPolicy Bypass -File ./package.ps1
```

Use a local Windows test directory, not a WSL UNC path: Windows file-lock and rename guarantees are required. Host tests copy the installed Playnite program to an isolated profile, use a unique IPC pipe, create a synthetic **Cloud Trail** game, and use a dummy personal vault via an Rclone local alias. They do not modify the real library or upload real saves. Configure `rcloneExe` in the host script if it is not available in Save Manager's usual tools directory. `-KeepOpen` retains the test instance for inspection.

See [validation evidence](docs/VALIDATION.md), [product behavior](docs/PRODUCT.md), and [implementation notes](docs/TECH.md). The tested ledger, transport and host harness were adapted from the user's earlier PlaytimeRclone project; see [third-party notices](THIRD-PARTY-NOTICES.md).

## Native sessions and HowLongToBeat

Link records and synchronizes dated sessions without add-ons. Its Sessions & device sync status window shows device names and save confirmations. Optional GameActivity history is adopted and displayed when available; adapter errors do not block native totals. Existing detailed GameActivity sessions are adopted once, identical copied history is deduplicated, and new sessions retain their ledger identity across devices. Playnite's total, play count and last activity are projected without replaying game-start/game-stop events. GameActivity receives only missing session rows. Partial aggregate-only history cannot supply missing individual session dates. Conflicting edits require review rather than silently inventing another session.

Enable **Update my online HowLongToBeat progress** in Overview and choose Sign in to HowLongToBeat directly in Link. No HLTB add-on is required. Existing HLTB metadata/profile entries receive the merged absolute progress value. The update loads the current online entry, preserves greater online progress and changes only progress time; lists, scores and completion times are retained. HLTB has no numeric session-count field equivalent to GameActivity's session list; its Playnite display reads the shared Playnite count. Credentials remain local. Missing mappings/profile entries are reported for review rather than creating potentially duplicate online entries.

Upload receipts suppress repeated writes, survive restart, and recheck online progress after ten minutes. Failed updates remain retryable with backoff. HLTB offers no conditional write transaction: simultaneous external edits can temporarily race; a fresh read and subsequent convergence repair stale progress. Link never deliberately lowers online time; reducing it remains an explicit HLTB edit.

Reproduce the isolated two-device test with `tests/Run-ActivityPeers.ps1`. `tests/NativeHltbTests.cs` tests the independent HLTB protocol with fixtures. `tests/Run-HltbLive.ps1` is the historical optional-adapter probe, not certification of the new native client; private account fixtures stay outside the repository. See the current acceptance report for the real cloud and online two-device results.

Game menus offer Link game for new games and Cloud sync for linked games. Full sync runs in the background at startup, after a recorded session and on demand. Save-linked games check cloud saves before launch unless their per-game check is disabled. Known save conflicts require review; files are never replaced while a game is starting or running. Idle checks exchange device, ledger and revision metadata, skip save scans when no work is pending, and back off from the configured minimum to at least 15 minutes (or the configured minimum if longer). Activity resets the delay; failed idle checks back off quietly. Pending save revisions and unapplied remote heads always trigger reconciliation. Requests made during an active sync are coalesced into a follow-up.

## Optional extension and theme sync

Open **Playnite Link → Add-ons** and enable sharing on each participating device. Sharing is off by default. Every device is an equal peer: independent installations merge, conflicting package edits require a choice, and removals become shared only after a previously tracked installation disappears. Use **Device-only add-ons** to exclude individual packages from both sending and receiving.

Use **Check for changes**, review the available versions, then **Apply selected changes and restart**. Verified files are staged before exit; the restart helper verifies again, backs up replaced code, and rolls back earlier replacements if an installation fails. Running games must stop before applying. Built-in add-ons and Playnite Link itself are excluded. Ordinary package files are supported; junctions and symlinks are rejected. Unchanged package fingerprints skip rehashing; automatic discovery runs at most once every five minutes during background checks.

Only installation files under Extensions and Themes are shared. Playnite settings, extension data/settings, cloud credentials, passcodes, startup preferences and the active theme are not copied. A synced theme becomes available without being selected. Both devices must independently authorize the same Rclone vault. Unsupported or differing package directory layouts require review.

Current audit results and limits: [closure report](docs/AUDIT-CLOSURE-2026-10-22.md). Tests use synthetic games/packages and include fresh Google browser enrollment, live cloud upload/download and process-kill recovery. Earlier versioned results remain in the [acceptance report](docs/ACCEPTANCE.md). Physical ROG Ally controls remain unverified.


## Launch checks (0.6.0)

Save-linked games check and sync before launch by default. A failed or unavailable connection requires an explicit offline choice; the per-game setting can disable this check. Newer cloud saves discovered during play are deferred until the game stops. Known conflicts, unfinished sessions, app protection and an in-progress final save replacement remain explicit safeguards.

Ledger, session, save-revision and device metadata are downloaded in one filtered Rclone operation. Save blobs and add-on files are excluded from that download. Add-on work runs after game-data convergence, and GameActivity retry checks wait for the active cycle rather than automatically queueing another full sync. Launch timing is tested while cloud work is already in progress, against a ten-second target.


