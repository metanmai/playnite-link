using System;
using System.Linq;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    public static class SaveCloudOwnership
    {
        private static readonly Guid Steam = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        private static readonly Guid Epic = new Guid("00000002-dbd1-46c6-b5d0-b1ba559d10e4");

        public static bool UsesLink(Game game, bool? localChoice)
        {
            if (localChoice.HasValue) return localChoice.Value;
            return !HasStoreLauncher(game);
        }

        public static bool HasStoreLauncher(Game game)
        {
            if (game.PluginId == Steam || game.PluginId == Epic) return true;
            return game.GameActions != null && game.GameActions.Any(action => action != null && action.Path != null &&
                (action.Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ||
                 action.Path.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase)));
        }

        public static bool CanSync(Game game, SaveLink link)
        {
            return game != null && link != null && link.HasSaves && UsesLink(game, link.LinkManagesSaves);
        }
    }
}
