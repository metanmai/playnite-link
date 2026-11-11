using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlaytimeRclone
{
    public enum SaveBadgeState { Hidden, Ready, Checking, Checked, Confirmed, Pending, Playing, Paused, Review, Waiting, Failed }

    public sealed partial class PlaytimePlugin
    {
        // Only the UI thread reads or updates the save-link index. The sync gate
        // protects live vault reads. During transfers use the last known links.
        private readonly Dictionary<Guid, Tuple<string, bool?>> badgeSaveLinks = new Dictionary<Guid, Tuple<string, bool?>>();
        private readonly ConcurrentDictionary<string, byte> badgeCloudSaves = new ConcurrentDictionary<string, byte>();
        private string badgeDevice;
        private volatile GameSyncReceiptIndex badgeStartupReceipts;
        private readonly ConcurrentDictionary<Guid, SaveBadgeState> badgeResults = new ConcurrentDictionary<Guid, SaveBadgeState>();
        private static readonly HashSet<Guid> NoBadgeChecks = new HashSet<Guid>();
        private volatile HashSet<Guid> badgeChecking = NoBadgeChecks;
        private event Action CloudBadgesChanged;
        private int badgeRefreshQueued;

        private void InitializeBadgeSaveLinks()
        {
            badgeDevice = repository.DeviceId;
            foreach (var mapping in repository.Mappings())
            {
                Guid id;
                SaveLink link;
                if (Guid.TryParse(mapping.Key, out id) && vault.State.Links.TryGetValue(mapping.Value.Key, out link) && link.HasSaves)
                    badgeSaveLinks[id] = Tuple.Create(mapping.Value.Key, link.LinkManagesSaves);
            }
        }

        private void ObserveBadgeReceipt(GameSyncReceipt receipt)
        {
            if (receipt.State == "confirmed") badgeCloudSaves.TryAdd(receipt.Game, 0);
        }

        private async void LoadBadgeReceipts()
        {
            try
            {
                await System.Threading.Tasks.Task.Run(() => {
                    var receipts = SyncReceipts.Read();
                    badgeStartupReceipts = new GameSyncReceiptIndex(receipts.Where(r => r.Device == badgeDevice));
                    foreach (var receipt in receipts) ObserveBadgeReceipt(receipt);
                });
                RefreshCloudBadges();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Cloud badge history could not be loaded.");
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name != "CloudBadge") return null;
            var badge = new CloudGameBadge(ReadSaveBadge, game => Guard(() => ShowGameSyncHistory(game)));
            badge.Loaded += (_, e) => CloudBadgesChanged += badge.Refresh;
            badge.Unloaded += (_, e) => CloudBadgesChanged -= badge.Refresh;
            return badge;
        }

        private SaveBadgeState ReadSaveBadge(Game game)
        {
            if (repository == null || config == null || devicePreferences == null || !devicePreferences.SyncSaves ||
                String.IsNullOrWhiteSpace(config.RemoteFolder))
                return SaveBadgeState.Hidden;
            var local = game.Id.ToString();
            if (syncGate.Wait(0))
            {
                try
                {
                    var mapping = repository.ReadMapping(local);
                    SaveLink link;
                    if (mapping != null && vault.State.Links.TryGetValue(mapping.Key, out link) && link.HasSaves)
                        badgeSaveLinks[game.Id] = Tuple.Create(mapping.Key, link.LinkManagesSaves);
                    else if (badgeSaveLinks.Remove(game.Id))
                    {
                        SaveBadgeState discarded;
                        badgeResults.TryRemove(game.Id, out discarded);
                    }
                }
                finally { syncGate.Release(); }
            }

            Tuple<string, bool?> saveLink;
            if (!badgeSaveLinks.TryGetValue(game.Id, out saveLink) || !badgeCloudSaves.ContainsKey(saveLink.Item1) ||
                !SaveCloudOwnership.UsesLink(game, saveLink.Item2))
                return SaveBadgeState.Hidden;
            var key = saveLink.Item1;
            if (savedConflicts.Contains(key)) return SaveBadgeState.Review;
            if (badgeChecking.Contains(game.Id)) return SaveBadgeState.Checking;
            if (IsSyncPaused) return SaveBadgeState.Paused;
            bool active, pending;
            repository.ReadGameSyncState(local, out active, out pending);
            if (game.IsRunning) return SaveBadgeState.Playing;
            if (active) return SaveBadgeState.Pending;
            SaveBadgeState last;
            if (badgeResults.TryGetValue(game.Id, out last))
                return pending && last != SaveBadgeState.Failed && last != SaveBadgeState.Waiting
                    ? SaveBadgeState.Pending : last;
            if (pending) return SaveBadgeState.Pending;
            var history = badgeStartupReceipts;
            var receipt = history == null ? null : history.Latest(badgeDevice, key);
            if (receipt == null || receipt.State == "disabled") return SaveBadgeState.Ready;
            return receipt.State == "confirmed" ? SaveBadgeState.Confirmed : SaveBadgeState.Pending;
        }

        private void BeginBadgeChecks(IEnumerable<SyncGame> games)
        {
            badgeChecking = new HashSet<Guid>(games.Where(item => {
                SaveLink link;
                return devicePreferences.SyncSaves && vault.State.Links.TryGetValue(item.Key, out link) && SaveCloudOwnership.CanSync(item.Game, link);
            }).Select(item => item.Game.Id));
            RefreshCloudBadges();
        }

        private void FinishBadgeChecks(SaveBadgeState? outcome = null)
        {
            if (outcome.HasValue)
                foreach (var id in badgeChecking) badgeResults[id] = outcome.Value;
            badgeChecking = NoBadgeChecks;
        }

        private void ResetSaveBadge(Guid game)
        {
            badgeResults[game] = SaveBadgeState.Ready;
            RefreshCloudBadges();
        }

        private void RefreshCloudBadges()
        {
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || CloudBadgesChanged == null ||
                Interlocked.Exchange(ref badgeRefreshQueued, 1) != 0) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
                Interlocked.Exchange(ref badgeRefreshQueued, 0);
                var changed = CloudBadgesChanged;
                if (changed != null) changed();
            }));
        }
    }

    public sealed class CloudGameBadge : PluginUserControl
    {
        private readonly Func<Game, SaveBadgeState> readStatus;
        private readonly Button button;
        private readonly TextBlock marker;
        private SaveBadgeState displayed;
        private Window host;

        public CloudGameBadge(Func<Game, SaveBadgeState> readStatus, Action<Game> openStatus)
        {
            this.readStatus = readStatus;
            Visibility = Visibility.Collapsed;
            var icon = new StackPanel { Orientation = Orientation.Horizontal };
            icon.Children.Add(new TextBlock {
                Text = "\uE753",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = Brushes.White
            });
            marker = new TextBlock {
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            icon.Children.Add(marker);
            button = new Button {
                Content = icon,
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 38, 48)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 180, 210, 235)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                Cursor = Cursors.Hand,
                Focusable = false
            };
            button.Click += (_, e) => {
                e.Handled = true;
                var game = GameContext;
                if (game != null && readStatus(game) != SaveBadgeState.Hidden) openStatus(game);
            };
            Content = button;
            Loaded += (_, e) => {
                host = Window.GetWindow(this);
                if (host != null) host.Activated += HostActivated;
                Refresh();
            };
            Unloaded += (_, e) => {
                if (host != null) host.Activated -= HostActivated;
                host = null;
            };
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            Refresh();
        }

        private void HostActivated(object sender, EventArgs args)
        {
            Refresh();
        }

        public void Refresh()
        {
            var state = GameContext == null ? SaveBadgeState.Hidden : readStatus(GameContext);
            if (state == displayed) return;
            displayed = state;
            Visibility = state == SaveBadgeState.Hidden ? Visibility.Collapsed : Visibility.Visible;
            if (state == SaveBadgeState.Hidden) return;

            string label;
            switch (state)
            {
                case SaveBadgeState.Checking: SetMarker("↻", Brushes.LightSkyBlue); label = "Checking this game's saves"; break;
                case SaveBadgeState.Checked: SetMarker("✓", Brushes.LightSkyBlue); label = "Launch save check finished"; break;
                case SaveBadgeState.Confirmed: SetMarker("✓", Brushes.LightGreen); label = "Last save sync confirmed"; break;
                case SaveBadgeState.Pending: SetMarker("•", Brushes.Gold); label = "Save sync pending"; break;
                case SaveBadgeState.Playing: SetMarker("•", Brushes.LightSkyBlue); label = "Playing · saves will be checked after exit"; break;
                case SaveBadgeState.Paused: SetMarker("Ⅱ", Brushes.LightGray); label = "Save sync paused"; break;
                case SaveBadgeState.Review: SetMarker("!", Brushes.Gold); label = "Save versions need review"; break;
                case SaveBadgeState.Waiting: SetMarker("!", Brushes.Gold); label = "Waiting for cloud storage"; break;
                case SaveBadgeState.Failed: SetMarker("!", Brushes.LightSalmon); label = "Could not check or sync saves"; break;
                default: SetMarker("", Brushes.White); label = "Save sync configured · ready to check"; break;
            }
            button.ToolTip = label + ". Click for this game's cloud status.";
            AutomationProperties.SetName(button, "Playnite Link · " + label);
        }

        private void SetMarker(string text, Brush color)
        {
            marker.Text = text;
            marker.Foreground = color;
            marker.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
