# Interface — 0.3.2

Overview, Devices and Protection share navigation and consistent cards. Overview gives synchronization status and its primary action first; connection details and technical/privacy information sit below. Device cards prioritize this device, readable sync choices and linked games; IDs and removal are collapsed. Per-device timing, bandwidth and path sharing are advanced settings. Protection presents status, passcode confirmation; auto-lock settings and security scope are expandable.

Colors follow the active Playnite theme with system-color fallbacks and contrast checks for secondary text. Buttons retain keyboard focus and disabled states. Layouts wrap actions, scroll vertically and avoid horizontal scrolling.

Storage connection is a single form with executable/configuration file pickers, a vault destination and inline errors. Conflict review uses separate local/cloud cards, keeps both explicit choices neutral, and explains backups and cancellation. No option is automatically preferred by clock time.

Protection configures an app-wide Playnite lock. A compact, centered unlock window hides/disables the library and other Playnite windows, supports passcode, and offers Quit instead of a dismiss-to-library action. It does not cover the desktop with a full-screen surface. Settings state the fixed unlock duration and lock triggers explicitly. Existing passcodes remain compatible. Conflict ancestry, blob verification, playtime deduplication and device synchronization semantics are retained.

Version 0.3.2 removes Windows Hello entirely. All shared interface colours use the active Playnite theme resources, with Windows system colours as fallback.

Current behavior: game menus use Link game / Cloud sync. Full sync runs at startup, after a recorded session and on demand. Ordinary launches use local saves immediately; known conflicts require review. Idle checks exchange device, ledger and revision metadata, skip save scans when no work is pending, and back off from the configured minimum to at least 15 minutes (or the configured minimum if longer). Activity resets the delay; failed idle checks back off quietly. Pending save revisions and unapplied remote heads always trigger reconciliation. Requests made during an active sync are coalesced into a follow-up.

Account pages now share one modal window. Each tab retains its control tree, scroll position, expanded sections and unsaved form fields for the life of the window. Sync and device actions keep the window open; status updates in place. Device refresh is explicit, and saving configuration refreshes the relevant details. Closing the window discards cached pages and passcode drafts.

Dialogs are bounded by the available work area. Fullscreen review is exercised at 800×640 with its fixed Apply footer remaining visible. Per-game controls have explicit Close actions; selecting a save folder updates its page in place. Their sync status updates while the page remains open.

Version 0.5.9: one game menu action (Link game / Cloud sync), one Link game confirmation including optional save-folder selection, automatic background synchronization after linking. The linked-game page presents status and saves; history/options and troubleshooting are collapsed. Technical identity and original baseline values are confined to troubleshooting.
