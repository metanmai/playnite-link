using System;
using Playnite.SDK;
using Playnite.SDK.Plugins;
public sealed class FixtureAddon:GenericPlugin
{
    public override Guid Id{get{return new Guid("758ab3a0-7335-48bb-8099-feb10b420ea4");}}
    public FixtureAddon(IPlayniteAPI api):base(api){}
}
