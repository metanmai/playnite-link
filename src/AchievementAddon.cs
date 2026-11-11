using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Playnite.SDK.Plugins;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private const string AchievementAddonType = "PlayniteAchievements.PlayniteAchievementsPlugin";
        private readonly ConcurrentDictionary<Guid, long> customDataWrites = new ConcurrentDictionary<Guid, long>();
        private readonly List<Tuple<object, EventInfo, Delegate>> achievementSubscriptions = new List<Tuple<object, EventInfo, Delegate>>();
        private readonly HashSet<Guid> changedAchievementGames = new HashSet<Guid>();
        private Timer achievementChangeTimer;
        private object achievementCustomDataStore;

        private Plugin FindAchievementAddon()
        {
            return PlayniteApi.Addons.Plugins.FirstOrDefault(plugin => plugin.GetType().FullName == AchievementAddonType);
        }

        private void StartAchievementChanges()
        {
            var addon = FindAchievementAddon();
            if (addon == null) return;

            try
            {
                var cache = PluginActivityStore.Member(addon, "CacheManager");
                WatchAchievementChanges(cache, "GameCacheUpdated", "GameId", (string id) =>
                {
                    Guid game;
                    if (Guid.TryParse(id, out game)) AchievementDataChanged(game);
                });

                var store = PluginActivityStore.Member(addon, "GameCustomDataStore");
                if (store == null) return;
                var type = store.GetType();
                var load = type.GetMethod("LoadOrDefault", new[] { typeof(Guid) });
                var packageSignature = new[] { typeof(Guid), typeof(string) };
                if (load == null || type.GetMethod("Save", new[] { typeof(Guid), load.ReturnType }) == null ||
                    type.GetMethod("HasPortableData", new[] { typeof(Guid) }) == null ||
                    type.GetMethod("ExportPortablePackage", packageSignature) == null ||
                    type.GetMethod("ImportReplacePortable", packageSignature) == null)
                    return;

                // The change counter protects incoming edits against concurrent local changes.
                if (WatchAchievementChanges(store, "CustomDataChanged", "PlayniteGameId", (Guid game) =>
                {
                    customDataWrites.AddOrUpdate(game, 1, (_, version) => checked(version + 1));
                    AchievementDataChanged(game);
                }))
                    achievementCustomDataStore = store;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not watch the optional Playnite Achievements add-on.");
            }
        }

        private long CustomDataVersion(Guid game)
        {
            long version;
            return customDataWrites.TryGetValue(game, out version) ? version : 0;
        }

        private bool WatchAchievementChanges<T>(object target, string eventName, string gameProperty, Action<T> changed)
        {
            if (target == null) return false;
            var info = target.GetType().GetEvent(eventName);
            if (info == null) return false;
            var args = info.EventHandlerType.GetMethod("Invoke").GetParameters();
            if (args.Length != 2) return false;
            var property = args[1].ParameterType.GetProperty(gameProperty);
            if (property == null || property.PropertyType != typeof(T)) return false;

            var parameters = args.Select(arg => Expression.Parameter(arg.ParameterType, arg.Name)).ToArray();
            var handler = Expression.Lambda(info.EventHandlerType,
                Expression.Invoke(Expression.Constant(changed), Expression.Property(parameters[1], property)), parameters).Compile();
            info.AddEventHandler(target, handler);
            achievementSubscriptions.Add(Tuple.Create(target, info, handler));
            return true;
        }

        private void AchievementDataChanged(Guid game)
        {
            lock (changedAchievementGames)
            {
                if (closing || !onboarding.SyncAchievements) return;
                changedAchievementGames.Add(game);
                if (achievementChangeTimer == null)
                    achievementChangeTimer = new Timer(_ => QueueChangedAchievements(), null, 1500, Timeout.Infinite);
                else
                    achievementChangeTimer.Change(1500, Timeout.Infinite);
            }
        }

        private void QueueChangedAchievements()
        {
            string[] games;
            lock (changedAchievementGames)
            {
                if (closing || repository == null || !onboarding.SyncAchievements) return;
                games = changedAchievementGames.Select(game => game.ToString()).ToArray();
                changedAchievementGames.Clear();
            }

            try
            {
                if (!games.Any(game => repository.ReadMapping(game) != null)) return;
                repository.QueueGameSyncs(games);
                QueuePersonalSync();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not queue changed achievements for sync.");
            }
        }

        private void StopAchievementChanges()
        {
            lock (changedAchievementGames)
            {
                if (achievementChangeTimer != null) achievementChangeTimer.Dispose();
            }

            foreach (var subscription in achievementSubscriptions)
                subscription.Item2.RemoveEventHandler(subscription.Item1, subscription.Item3);
            achievementSubscriptions.Clear();
        }
    }
}
