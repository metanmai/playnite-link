# Connection recovery verification — 0.5.4

- Google personal-Drive setup answers Rclone's internal Shared Drive question automatically. Other booleans use Yes/No buttons, enumerated provider choices use a list, and only actual text/password questions use text entry.
- Provider prompts are owned by the walkthrough. OAuth runs in the background and can be cancelled; failures are shown inline and controls recover.
- A cloud folder's stable directory ID verifies a reconnect. Matching folders may use different local remote names/configuration files. Different vaults remain blocked, including account changes behind the same remote name. The OAuth client JSON is an app identity, not an account or vault identity.
- Test connection lists the selected folder's parent to check readability and identify it. It uploads no saves. It does not claim to test write permissions. Missing default vaults are created only on first sync.
- Reset connection clears this device's active selection while retaining saved sign-ins, local history and cloud saves. The vault pin is retained to prevent mixing unrelated history.
- Saved sign-ins from cancelled/failed setup remain available in the connection picker. Custom vault-folder paths are retained when reconnecting.
- First-run startup opens the walkthrough without a nested modal call, and the application can complete startup while it stays open.

Validation: 197 automated regression checks passed. Actual isolated Playnite Desktop and Fullscreen probes passed for test/reset, preserved history, saved-sign-in reconnect, button-based prompts and correct ownership, setup resume, persisted pause, cancelled queued work and successful retry through real Rclone with a local alias. Final cancellation timings were 220 ms / 218 ms.

Real Google Drive check: the current remote and the user's newly authorized personal-client configuration were both readable and resolved to the same physical vault identity. No cloud files were uploaded by these connection checks. Credentials and folder IDs are excluded from this report.
