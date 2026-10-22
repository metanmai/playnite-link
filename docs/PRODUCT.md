# Playnite Link

An independent Playnite extension with a user-owned personal cloud vault, not an official Playnite account system.

Each user owns a personal cloud vault connected through Rclone. No application account server, hosted identity directory or central operator is required. Cloud provider OAuth and device credential revocation belong to the user's storage provider. Devices connect to the same vault using their own cloud authorization.

Fresh profiles require explicit game linking; automatic inclusion of unambiguous provider games is opt-in. Existing profiles retain their choice. Manual games are added once by name, then selected from the cloud on other devices; shared keys stay internal. Existing playtime is migrated as a reviewed maximum baseline; new sessions are immutable and additive. Offline sessions survive restarts and replay without duplication. Outside edits and interrupted sessions require review.

Save folders are explicitly configured per game/device. While games are stopped, compare local contents and the last common revision against the personal vault. Pull a remote-only change; publish a local-only change; show local/cloud/cancel when both diverge. Never choose by device clock. Known conflicts require review and cancel blocks their launch. Other launches use local saves immediately; new remote saves are deferred during play. Keep a local backup before restore and immutable cloud revisions. Preserve directory-level consistency rather than merging arbitrary binary saves. File deletions are part of a snapshot. Empty/missing directories require explicit review before replacing existing cloud content. Concurrent offline publications form branches resolved after reconnection.

Sync portable per-game Favorite, Hidden and Notes preferences separately with conflict review. Device paths, store credentials, Playnite database and unrelated extension settings are not copied. Extension/theme installation sharing is independently opt-in; active theme selection stays local. Configuration portability beyond these fields requires provider-specific adapters.

Show connected devices with last check-in and linked game configuration. Support per-device sync switches, interval, bandwidth, display name and opt-in path visibility, plus pause/resume/removal. Provider authorization is decentralized; device removal is cooperative and cannot revoke existing cloud credentials. An optional local passcode locks the whole Playnite app: library, settings and game launching. Background synchronization continues. Startup/session/manual/expiry locks require authentication; closing the gate exits instead of exposing the app.

Stream content-addressed save files without the old 32 MiB/4096-file snapshot caps. Preserve history and backups, deduplicate identical whole files, support cancellation and surface provider quota failures without losing queued local sessions.

Validation uses synthetic saves, two device profiles, duplicate sessions, stale revisions, account isolation and a real Playnite host with a synthetic game. Physical ROG Ally, real game formats, public hosting and live third-party OAuth are separate acceptance checks. No design mock supplied.

## Current scope clarification

Extension/theme installation synchronization is optional and peer-based. Shared packages do not select another device's active theme. Playnite settings, startup mode/login behaviour, extension settings and credentials stay local. Each add-on can be marked device-only. All applications share one persistent account/settings window; navigation preserves drafts and page state.


