# Decentralized implementation contract

## Launch scheduling (0.5.1)

Ordinary starts do not acquire the network semaphore or enqueue a second full synchronization. A UI-owned launch reservation closes the interval before Playnite reports IsRunning; background capture/reconciliation excludes starting and running games. A cached unresolved conflict still uses the explicit resolution path. Passcode, unfinished-session and restart guards retain priority.

Downloads and verified restore staging occur without a launch reservation. Immediately before final replacement, the restorer atomically checks the UI-owned busy state and reserves the local save replacement. A launch during staging vetoes replacement and retains original files. Once replacement begins, a launch waits asynchronously for that short local transaction, with a ten-second retry bound. The reservation lasts through durable checkpoint commit. Network work is outside that critical section.

One filtered Rclone pull retrieves devices, ledger, activity and revision object areas into separate caches; each protocol still validates its own records. Blobs, nested paths and unrelated areas are excluded. Ledger publication uses those already-downloaded caches rather than pulling twice. Add-on work follows game convergence; optional HLTB/add-on work is omitted from explicit conflict launch completion. Native GameActivity retry waits for the active cycle and queues follow-up only while its writer remains deferred.

## Session integration (0.5.0)

Standard version-1 baselines/sessions remain in the legacy ledger area. Version-2 historical session/count metadata uses a separate activity area, so old readers do not consume unsupported records. Both areas merge before projection. Historical totals contribute MAX(baseline, detailed history sum), then unique post-enrollment sessions are added. Counts similarly use the larger historical count plus new sessions. Canonical version-1 hashes remain unchanged.

GameActivity native rows use durable per-device bindings to ledger IDs. Projection inserts only missing rows through the installed database API, waits for its native writer, and never replays SDK lifecycle events. Exact timestamp/content adoption handles a crash before receipt persistence. Ambiguous copied history, edits and deletions require review. Failed games are isolated from healthy synchronization.

Online HLTB is a separate local opt-in. Its installed API loads and validates the existing submission and game identity, patches General.Progress only, submits and verifies. The desired value is absolute; an already greater online value is retained. Receipts are local and durable, failures retry with backoff, and ten-minute verification repairs stale external progress. The external API provides no compare-and-swap; transient concurrent writer races remain possible. Missing profile entries are not created automatically. HLTB authentication remains managed by the installed HLTB add-on.

Reuse the local PlaytimeRclone ledger, transport, extension UI and test harness under its MIT license. C# 5/.NET Framework plugin builds against the installed Playnite SDK. Rclone supplies per-user provider OAuth and transport. Credentials remain in local Rclone configuration. No account server is required. This supersedes the initial server design following the user's explicit decentralization requirement.

Immutable content-addressed JSON revisions form a DAG. Resolution references all observed heads; unseen concurrent branches survive. Content and ancestry establish the common version; clocks do not select winners. Version 2 saves map relative paths to SHA-256 blob IDs and Int64 lengths. Raw payloads stream with 128 KiB buffers and deduplicate by entire-file content. Blobs upload before their manifests. Restore verifies every staged blob before swapping directories, preserves the old folder, and rolls back on failure. Checkpoints advance only after success. Local revisions persist before network work. Version 1 inline snapshots migrate with explicit migration ancestry so delayed old descendants do not invent false conflicts.

Rclone copies directories with immutable objects and never deletes vault history. Rclone crypt remotes can encrypt the vault. Syncthing may transport an immutable local vault through a Rclone local alias; it must not also synchronize live saves or the live Playnite database. Personal vaults use separate remote folders. Switching a populated local ledger to another vault is blocked.

Cloud OAuth is delegated to the installed Rclone provider implementation. Configuration can reuse an existing remote or launch Rclone's provider configuration flow. Playnite store extensions retain their own storefront authentication; store credentials are not copied between devices.

Device profiles, settings and status are separate immutable DAGs. Settings conflicts block game data until resolved; concurrent removal takes priority over unseen resume. Profiles check in at most every 15 minutes unless configuration changes. Device controls are cooperative, not cryptographic credential revocation. Provider access controls remain authoritative.

Synchronization is serialized on background workers. UI dispatcher work is limited to reading/updating Playnite models and dialogs. Persistent metadata caches avoid redundant downloads; required missing blobs are fetched by manifest. Cancellation interrupts both Rclone and streaming loops. Rclone uses request pacing and bounded retries, with sanitized errors. There is no central online device service.

App-wide protection optionally requires PBKDF2-SHA256 (600,000 iterations, random 32-byte salt). ApplicationLock hides/disables all Playnite WPF windows and covers new ones before rendering. Routed input is filtered outside the unlock gate; raw input is left to normal WPF routing so passcode typing works. An independent SDK OnGameStarting guard precedes enrollment and launch permissions, canceling all game launches while locked. The gate cannot be dismissed to reveal the library: closing requests app exit. Expiry is polled every 250 ms; while locked, a rendering hook covers newly created windows. The gate does not activate when Playnite is in the background.

The verifier and attempt-throttling state use DPAPI CurrentUser. Existing verifiers remain compatible. UI lock-state reads do not wait on expensive KDF work. A generation counter invalidates passcode results after a newer Windows/manual lock. Failed protection loading keeps the app locked; failed gate creation requests shutdown. Background synchronization remains available. This is extension-enforced app privacy, not file encryption or defense against safe mode, extension removal or code running as the same Windows user.

## Add-on package synchronization (0.4.0)

Opt-in setup events use an isolated immutable history and component identity hashes. Files are SHA-256 blobs. Every device can publish; independent package components merge. Same-component branches require an explicit choice, and identical branches receive a common resolution. Device-only exclusions block publication and application. Checkpoints advance only after the restart helper writes an atomic success receipt. The helper verifies both staged bytes and unchanged installed bytes, backs up replacements, rolls back partial changes, and never edits ExtensionsData or application configuration. Settings synchronization is excluded. Active local themes cannot be removed through shared tombstones until a different theme is selected locally.

