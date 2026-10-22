# Save synchronization acceptance — 0.5.11

## Interface

The conflict dialog identifies This device and the named cloud device, with version dates and file counts. Actions are Keep local saves, Use cloud saves and Decide later. Closing the window also defers the choice. An empty local folder is explicitly labelled. Identical local heads are not repeated as cloud choices. Other versions stay in cloud history; replaced local files stay in a backup. Binary game saves are selected as whole-folder versions, not guessed byte-level merges.

The linked-game page shows Choose saves only when a conflict needs review. Its real button opens the same dialog; Check for changes also permits an explicit conflict choice.

## Bug fixed

An explicitly selected empty save was being emitted in the legacy format. Converting that empty object separately on two devices could create redundant sibling revisions and produce a false conflict after the other device recreated saves. Empty saves now use the streamed format, and legacy empty objects need no conversion branch. The delete/recreate/pull sequence passes without a choice. A dedicated regression checks that legacy-empty conversion adds no branch.

## Verification

Two actual isolated Playnite instances run the extension with GameActivity and HowLongToBeat installed: Desktop A and Fullscreen B. Tests interact with the real WPF conflict buttons and capture rendered dialogs. Each step starts with startup sync paused to isolate the intended operation. Offline changes are simulated by editing fixture files without syncing. No personal game-save folders are touched.

The local Rclone run passed 30 core scenarios, 19 additional scenarios and 11 game-page/transfer scenarios (60 total). It covers bidirectional text and binary saves; offline divergent changes; choosing local or cloud; deciding later; closing the dialog; subsequent convergence; invalid-connection failure and retry; confirming or cancelling an empty save; deletion and recreation; background deferral; identical edits without a prompt; an actual throttled upload stopped in progress, resumed, and downloaded by B; and repeated syncs across process restarts. Every step verifies the expected file set and bytes, one game mapping, two sessions and 16 seconds. Every restore verifies that exactly one new local backup contains the replaced bytes. Cancel/failure permits durable queuing of the local edit but cannot accept a different save version.

Additional passing assertions: 64 vault/connection, 16 streaming/device and 58 ledger/game-picker. These include 64 MiB streaming saves, over 4,096 files, missing/corrupted blobs, unsafe paths, failed restore safety, multihead resolution ancestry and a game starting during restore staging.

Evidence: saves-0511-local-tally.json, saves-0511-local-extra-tally.json and saves-0511-local-transfer-tally.json. The real Google Drive run passed the same 30 core scenarios plus 11 game-page/transfer scenarios (41 total) in its own unique test folder. The stopped 2 MiB upload did not publish a manifest to B; B retained its prior saves before resume, then received the resumed file with the exact expected bytes. See saves-0511-cloud-tally.json and saves-0511-cloud-transfer-tally.json. Combined: 101 passing actual-instance steps and 138 passing focused assertions.

## Reproduce

Run tests/Run-SaveAcceptance.ps1 for an isolated local fixture. To use a separate real cloud fixture, provide CloudRemote with a unique test-only folder and RcloneConfig with an already-authorized private config path. Reports contain no tokens. The script first provisions two linked clients, then runs the save scenarios. The cloud fixture is created below its test prefix; it does not replace the user's normal vault.

These checks use two instances on one Windows computer. Physical handheld hardware, arbitrary game-specific save formats, and every possible provider outage are not exhaustively reproduced. Disk/format failures are exercised by focused safety tests; a stopped live upload and retry are exercised through the running extension.
