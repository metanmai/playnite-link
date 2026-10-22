# Playnite Link 0.5.5: sync status feedback

Sync views now show a theme-colored animated activity bar, a named phase, elapsed time, and game counts when a total is known. Completion, pause, cancellation, failure and review are distinct states. No estimated percentage is presented for cloud operations whose size is unknown. The unchanged-metadata path now reports a terminal outcome.

GameActivity capture and library projection yield between batches of four games so the dispatcher can draw progress and handle input. Existing history and duplicate-prevention receipts remain in use.

New setups default automatic game selection off. Existing selections and explicit preferences are preserved; onboarding explains the difference between playtime selection and save folder selection.

Validation: 201 assertions passed across ledger, vault, streaming/device, security, application lock, setup, activity and HLTB suites. Setup tests were rerun with a shorter local test path after the initial test directory exceeded the .NET Framework path length limit. Desktop and Fullscreen isolated Playnite walkthroughs verified visible indeterminate progress, elapsed time, responsive UI, pause hiding progress, resume, completion hiding progress, queued cancellation and duplicate-free repeated sync. These fixtures use real Rclone with local alias storage, not Google Drive.
