using System;
using System.Windows;
using System.Windows.Controls;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private void ShowSyncSettings() { OpenAccountPage("Sync settings"); }

        private void BuildSyncSettings(Window window)
        {
            var panel = Page(window, "Sync settings", "Choose what this device syncs. Changes save as you make them.", "Sync settings");
            var feedback = Label(panel, "Your existing history stays available when you turn a category off.", 13, true);

            var games = Card(panel, "Game data");
            SyncSwitch(games, "Game saves and backups", () => devicePreferences.SyncSaves,
                enabled => SaveDeviceSyncSetting(p => p.SyncSaves = enabled), feedback);
            SyncSwitch(games, "Playtime and session history", () => devicePreferences.SyncPlaytime,
                enabled => SaveDeviceSyncSetting(p => p.SyncPlaytime = enabled), feedback);
            SyncSwitch(games, "Favorites, hidden status and game notes", () => devicePreferences.SyncPreferences,
                enabled => SaveDeviceSyncSetting(p => p.SyncPreferences = enabled), feedback);
            SyncSwitch(games, "Achievements", () => onboarding.SyncAchievements,
                enabled => SaveOnboardingSyncSetting(o => o.SyncAchievements = enabled), feedback);
            Label(games, "Achievement sync uses a compatible add-on installed separately on each device.", 13, true);

            var optional = Card(panel, "Add-ons and services");
            if (setup != null)
                SyncSwitch(optional, "Extension and theme installation files", () => setup.Options.Enabled, SaveSetupSharing, feedback);
            else
                Label(optional, "Extension and theme sharing is unavailable on this device.", 13, true);
            SyncSwitch(optional, "Update online HowLongToBeat progress", () => onboarding.OnlineHltb,
                enabled => SaveOnboardingSyncSetting(o => o.OnlineHltb = enabled), feedback);
            Label(optional, "Online HowLongToBeat updates require playtime sync and a separate sign-in.", 13, true);
            Button(optional, "Sign in to HowLongToBeat…", ShowHltbSignIn);
            activityStatusText = Label(optional, activityStatus, 13, true);

            var options = Card(panel, "Device and library options");
            SyncSwitch(options, "Sync automatically in the background", () => devicePreferences.AutoSync,
                enabled => SaveDeviceSyncSetting(p => p.AutoSync = enabled), feedback);
            Label(options, "Pre-launch checks follow each game's save settings while save sync is on.", 13, true);
            SyncSwitch(options, "Automatically include store games", () => onboarding.AutomaticGames,
                enabled => SaveOnboardingSyncSetting(o => o.AutomaticGames = enabled), feedback);
            SyncSwitch(options, "Share save-folder paths with my other devices", () => devicePreferences.SharePaths,
                enabled => SaveDeviceSyncSetting(p => p.SharePaths = enabled), feedback);
            Label(options, "Device check-ins and linked game lists support the vault's connected-device features.", 13, true);
        }

        private void SyncSwitch(Panel panel, string label, Func<bool> current, Action<bool> save, TextBlock feedback)
        {
            var toggle = Toggle(panel, label, current());
            toggle.Click += (_, e) =>
            {
                var value = toggle.IsChecked == true;
                if (value == current()) return;
                if (!EnsureAccess() || !syncGate.Wait(0))
                {
                    toggle.IsChecked = current();
                    feedback.Text = "Wait for the current sync to finish before changing settings.";
                    return;
                }

                try
                {
                    save(value);
                    feedback.Text = "Saved. Changes apply to the next sync on this device.";
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Could not save sync settings.");
                    toggle.IsChecked = current();
                    feedback.Text = "Could not save this setting. " + ex.GetBaseException().Message;
                }
                finally { syncGate.Release(); }
            };
        }

        private void SaveDeviceSyncSetting(Action<DevicePreferences> change)
        {
            var next = devicePreferences.Clone();
            change(next);
            var id = repository.DeviceId;
            // Persist the same device settings event that the next sync publishes.
            devices.SetSettings(id, id, next);
            devicePreferences = next;
            accountPages.Remove("Devices");
        }

        private void LoadLocalSyncSettings()
        {
            try
            {
                var id = repository.DeviceId;
                var settings = devices.Heads(id, "settings");
                if (settings.Count == 1 && settings[0].Author == id)
                    devicePreferences = devicePreferences.ApplyRequested(settings[0], id);
            }
            catch (Exception ex) { logger.Warn(ex, "Could not read pending device settings."); }
        }

        private void SaveOnboardingSyncSetting(Action<OnboardingOptions> change)
        {
            var previous = onboarding;
            onboarding = previous.Copy();
            change(onboarding);
            try { SaveOnboarding(); }
            catch { onboarding = previous; throw; }
            RefreshOverviewPage();
        }

        private void SaveSetupSharing(bool enabled)
        {
            var previous = setup.Options.Enabled;
            setup.Options.Enabled = enabled;
            try { setup.SaveOptions(); }
            catch { setup.Options.Enabled = previous; throw; }
            nextSetupCapture = DateTime.MinValue;
            setupStatus = enabled ? "Enabled. Check for changes to publish and discover add-ons." : "Add-on sync is off on this device.";
            RebuildSetupPage();
        }
    }
}
