using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;

public sealed class HostProbe : GenericPlugin
{
    public override Guid Id { get { return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95"); } }
    public HostProbe(IPlayniteAPI api) : base(api) { }

    const string Saves = "Game saves and backups";
    const string Playtime = "Playtime and session history";
    const string Preferences = "Favorites, hidden status and game notes";
    const string Achievements = "Achievements";
    const string Addons = "Extension and theme installation files";
    const string Hltb = "Update online HowLongToBeat progress";
    const string Automatic = "Sync automatically in the background";
    const string IncludeGames = "Automatically include store games";
    const string Paths = "Share save-folder paths with my other devices";

    PlaytimePlugin subject;
    string root;
    int checks;

    object Field(string name)
    { return typeof(PlaytimePlugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(subject); }

    void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }

    IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T) yield return (T)child;
            foreach (var element in Elements<T>(child)) yield return element;
        }
    }

    CheckBox Toggle(Window window, string label)
    { return Elements<CheckBox>(window).Single(box => ((TextBlock)box.Content).Text == label); }

    void Set(Window window, string label, bool enabled)
    {
        var toggle = Toggle(window, label);
        if (toggle.IsChecked != enabled)
            typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(toggle, null);
        Check(toggle.IsChecked == enabled, label + " did not keep its new value");
    }

    void Capture(Window window)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "sync-settings.png"))) png.Save(file);
    }

    Task Settings(Action<Window> test)
    {
        var completion = new TaskCompletionSource<bool>();
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            Window window = null;
            try
            {
                window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Playnite Link" && w.IsVisible);
                window.UpdateLayout();
                test(window);
                completion.SetResult(true);
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (window != null) window.Close(); }
        }));
        subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(item => item.Description == "Sync settings").Action(null);
        return completion.Task;
    }

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(); }
            catch (Exception ex) { Finish(new { passed = false, error = ex.ToString(), checks }); }
        }));
    }

    void Finish(object result)
    {
        File.WriteAllText(Path.Combine(root, "host-result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
        Application.Current.Shutdown();
    }

    string Cloud(string area) { return Path.Combine(root, "Cloud", "personal", "playnite-link-v1", area, "objects"); }
    string Cached(string data, string area) { return Path.Combine(data, "cloud-cache", "metadata", area, "objects"); }
    int Objects(string directory) { return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0; }

    async Task Run()
    {
        root = PlayniteApi.Paths.ConfigurationPath;
        Check(File.Exists(Path.Combine(root, "PROBE-ONLY")), "Isolated profile required");
        subject = (PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p => p.Id == new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
        Check(subject.IsSyncPaused, "Fixture must start paused");
        if (File.Exists(Path.Combine(root, "verify-sync-settings-restart")))
        {
            await Settings(window =>
            {
                foreach (var label in new[] { Saves, Playtime, Achievements, Addons, Hltb, Automatic })
                    Check(Toggle(window, label).IsChecked == false, label + " changed after restart");
                foreach (var label in new[] { Preferences, IncludeGames, Paths })
                    Check(Toggle(window, label).IsChecked == true, label + " changed after restart");
            });
            Finish(new { passed = true, phase = "restart", checks });
            return;
        }

        await Settings(window =>
        {
            Capture(window);
            foreach (var label in new[] { Saves, Playtime, Preferences, Achievements, Automatic })
                Check(Toggle(window, label).IsChecked == true, label + " did not preserve its existing default");
            foreach (var label in new[] { Addons, Hltb, IncludeGames, Paths })
                Check(Toggle(window, label).IsChecked == false, label + " was enabled without a choice");
            var gate = (SemaphoreSlim)Field("syncGate");
            Check(gate.Wait(0), "Could not reserve test transfer gate");
            try
            {
                typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Toggle(window, Saves), null);
                Check(Toggle(window, Saves).IsChecked == true, "A setting changed during a transfer");
            }
            finally { gate.Release(); }
            foreach (var label in new[] { Saves, Playtime, Preferences, Achievements, Automatic }) Set(window, label, false);
            foreach (var label in new[] { Addons, Hltb, IncludeGames, Paths })
            { Set(window, label, true); Set(window, label, false); }
        });
        Check(subject.IsSyncPaused, "Saving switches resumed transfers");

        var repo = (Repository)Field("repository");
        var mapping = repo.Mappings().Single();
        var game = new Game { Id = new Guid(mapping.Key), Name = "Sync settings fixture", Notes = "Local fixture note", Favorite = true, Playtime = 100 };
        PlayniteApi.Database.Games.Add(game);
        var saveFolder = Path.Combine(root, "fixture-saves");
        Directory.CreateDirectory(saveFolder);
        File.WriteAllText(Path.Combine(saveFolder, "slot.txt"), "Local fixture save");
        subject.PersonalVault.State.Links[mapping.Value.Key] = new SaveLink
        {
            Folder = saveFolder, CheckBeforeLaunch = true,
            Sources = new List<SaveSource> { new SaveSource { Id = "main", Path = saveFolder, IsDirectory = true } }
        };
        subject.PersonalVault.Save();
        var starting = new OnGameStartingEventArgs();
        typeof(OnGameStartingEventArgs).GetField("<Game>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(starting, game);
        subject.OnGameStarting(starting);
        Check(!starting.CancelStartup && !subject.IsLaunchPreflight(game.Id), "Disabled saves still triggered launch sync");
        ((HashSet<Guid>)Field("launchReservations")).Remove(game.Id);

        var data = Path.Combine(PlayniteApi.Paths.ExtensionsDataPath, subject.Id.ToString());
        var peer = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.AddMinutes(-5);
        var session = new Entry { Id = Guid.NewGuid().ToString("N"), Kind = "session", Device = peer, Game = mapping.Value.Key,
            Seconds = 30, StartedUtc = time.ToString("O"), EndedUtc = time.AddSeconds(30).ToString("O") };
        var cachedSession = new Entry { Id = Guid.NewGuid().ToString("N"), Kind = "session", Device = peer, Game = mapping.Value.Key,
            Seconds = 20, StartedUtc = time.AddMinutes(-1).ToString("O"), EndedUtc = time.AddSeconds(-40).ToString("O") };
        repo.Merge(new[] { cachedSession });
        File.WriteAllText(Path.Combine(Cloud("ledger"), session.Hash() + ".json"), session.Canonical());

        var localAchievement = new AchievementRecord { Game = mapping.Value.Key, Scope = Identity.Hash("fixture"), Id = "LOCAL", Device = repo.DeviceId, Name = "Local achievement", Description = "" };
        var remoteAchievement = new AchievementRecord { Game = mapping.Value.Key, Scope = localAchievement.Scope, Id = "REMOTE", Device = peer, Name = "Remote achievement", Description = "", Unlocked = true };
        new AchievementStore(data).Observe(localAchievement);
        File.WriteAllText(Path.Combine(Cloud("achievements"), remoteAchievement.Hash() + ".json"), remoteAchievement.Canonical());

        using (var archive = new MemoryStream(new byte[] { 1, 2, 3 }))
            new BackupCatalog(Path.Combine(data, "backup-objects")).Add(new BackupRecord
            {
                BackupId = Guid.NewGuid().ToString("N"), Game = mapping.Value.Key, Device = repo.DeviceId,
                Archive = subject.SaveBlobs.Put(archive).ToString(), Note = "Backup transfer fixture", CreatedUtc = time.ToString("O")
            });

        Check(await subject.SyncPersonalAsync(true, null), "Manual sync with categories off failed");
        Check(Objects(Cloud("ledger")) == 1 && Objects(Cached(data, "ledger")) == 0, "Disabled playtime transferred records");
        Check(PlayniteApi.Database.Games.Get(game.Id).Playtime == 100, "Disabled playtime projected cached history");
        Check(Objects(Cloud("revisions")) == 0 && Objects(Cloud("backups")) == 0, "Disabled saves/preferences published records");
        Check(Objects(Cloud("achievements")) == 1 && Objects(Cached(data, "achievements")) == 0, "Disabled achievements transferred records");
        Check(Objects(Cloud("setup")) == 0, "Disabled add-on sharing published packages");

        await Settings(window => Set(window, Saves, true));
        Check(await subject.SyncPersonalAsync(true, null), "Saves-only sync failed");
        var saveReceipt = new GameSyncReceipts(Cloud("receipts")).Latest(repo.DeviceId, mapping.Value.Key);
        Check(saveReceipt != null && saveReceipt.State == "confirmed" && saveReceipt.Sessions == Identity.Hash(""), "Saves-only sync depended on playtime confirmation");
        Check(Objects(Cloud("revisions")) == 1 && Objects(Cloud("backups")) == 1, "Saves-only sync did not publish saves and backups independently");
        Check(PlayniteApi.Database.Games.Get(game.Id).Playtime == 100 && Objects(Cached(data, "ledger")) == 0, "Saves-only sync pulled or applied playtime");

        await Settings(window =>
        {
            foreach (var label in new[] { Playtime, Preferences, Achievements, Addons }) Set(window, label, true);
        });
        Check(await subject.SyncPersonalAsync(false, null), "Device-only background check failed");
        Check(Objects(Cloud("ledger")) == 1 && Objects(Cached(data, "ledger")) == 0 && Objects(Cloud("revisions")) == 1, "Manual-only mode transferred game data in the background");
        Check(await subject.SyncPersonalAsync(true, null), "Manual sync with categories on failed");
        Check(PlayniteApi.Database.Games.Get(game.Id).Playtime == 150, "Re-enabled playtime did not receive and project both sessions");
        Check(subject.PersonalVault.Heads(mapping.Value.Key, "save").Count == 1, "Re-enabled saves did not publish the fixture");
        Check(Objects(Cloud("backups")) == 1, "Re-enabled backups did not publish the fixture");
        Check(subject.PersonalVault.Heads(mapping.Value.Key, "preferences").Count == 1, "Re-enabled preferences did not publish the fixture");
        Check(Objects(Cloud("achievements")) == 2 && new AchievementStore(data).ForGame(mapping.Value.Key).Count == 2, "Re-enabled achievements did not transfer both ways");
        Check(Objects(Cloud("setup")) > 0, "Re-enabled add-on sharing did not publish packages");

        subject.StopTransfer();
        await Settings(window =>
        {
            foreach (var label in new[] { Saves, Playtime, Achievements, Addons }) Set(window, label, false);
            foreach (var label in new[] { IncludeGames, Paths }) Set(window, label, true);
        });
        File.WriteAllText(Path.Combine(root, "verify-sync-settings-restart"), "");
        Finish(new { passed = true, phase = "edit", checks, mode = PlayniteApi.ApplicationInfo.Mode.ToString() });
    }
}
