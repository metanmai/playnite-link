using System;
using System.Windows.Controls;
using StartPage.SDK;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin:IStartPageExtension
    {
        IStartPageExtension AchievementWidgets{get{return achievementEngine as IStartPageExtension;}}
        public StartPageExtensionArgs GetAvailableStartPageViews(){return AchievementWidgets==null?null:AchievementWidgets.GetAvailableStartPageViews();}
        public object GetStartPageView(string id,Guid instance){return AchievementWidgets==null?null:AchievementWidgets.GetStartPageView(id,instance);}
        public Control GetStartPageViewSettings(string id,Guid instance){return AchievementWidgets==null?null:AchievementWidgets.GetStartPageViewSettings(id,instance);}
        public void OnViewRemoved(string id,Guid instance){if(AchievementWidgets!=null)AchievementWidgets.OnViewRemoved(id,instance);}
    }
}
