# Save Manager core

Source: https://github.com/Jurangren/save-manager
Pinned commit: c879d81bd864b23ab903dd2a62ecf30063bda518
Author: Jurangren. License: GNU AGPL version 3 (see LICENSE).

The Services/BackupService.cs, Services/PathHelper.cs and three Models/*.cs
files, CoreJson.cs and ToastNotificationService.cs are compiled into SaveManager.Core.dll. Link calls this backup service
for ZIP creation, metadata, CRC32, import, notes, retention and deletion.
Link provides its own UI, SHA-256 cloud graph, verified restore transaction,
device/session reporting and path guards. Upstream's cloud worker/plugin is
not loaded, so there is one synchronization owner.

Local adaptations: unique ZIP names avoid same-second overwrite; indexed source
folders avoid equal-basename collisions; bounded independent JSON avoids host
serializer changes; durable configuration commits preserve the prior committed
state on failure; imports verify source and copied CRC; SDK-only derived fields
also carry JsonIgnore; portable path substitutions respect directory boundaries;
notification labels use English Link text. These changes mean the integrated
core is tested separately rather than assumed equivalent to unchanged upstream.
See docs/ACCEPTANCE-0.6.0.md in the corresponding source.
Original Playnite Link code retains its MIT notice in ../PlayniteLink-MIT-LICENSE.
The combined distribution is AGPL-3.0; corresponding source accompanies it.
