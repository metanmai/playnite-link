using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace PlaytimeRclone
{
    // Protocol based on Lacro59's MIT-licensed HowLongToBeat client. Keep the
    // fresh edit document intact: reviews, lists and completion times are not ours.
    public sealed class NativeHltbProgress : IHltbProgress
    {
        readonly Func<string,JObject,Task<string>> request;
        readonly long account, game, submission;
        public NativeHltbProgress(Func<string,JObject,Task<string>> request,long account,long game,long submission)
        {if(account<=0||game<=0||submission<=0)throw new ArgumentException("Invalid HLTB identity.");this.request=request;this.account=account;this.game=game;this.submission=submission;}
        public static JObject Edit(string html)
        {
            var match=Regex.Match(html,"<script\\b[^>]*\\bid=[\"']__NEXT_DATA__[\"'][^>]*>(.*?)</script>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
            if(!match.Success)throw new IOException("HLTB edit data is unavailable. Progress remains queued.");
            var edit=JsonSafety.Object(match.Groups[1].Value).SelectToken("props.pageProps.editData") as JObject;
            if(edit==null)throw new IOException("HLTB edit format changed. Progress remains queued.");return edit;
        }
        void Validate(JObject edit)
        {if((long?)edit["userId"]!=account||(long?)edit["gameId"]!=game||(long?)edit["submissionId"]!=submission)throw new IOException("HLTB account or game entry changed; upload stopped.");}
        public async Task<object> Read(){var edit=Edit(await request("/submit/edit/"+submission,null));Validate(edit);return edit;}
        public ulong Seconds(object value)
        {
            var edit=(JObject)value;Validate(edit);var progress=edit.SelectToken("general.progress") as JObject;
            if(progress==null)throw new IOException("HLTB progress format changed.");
            return checked(((ulong?)progress["hours"]??0)*3600+((ulong?)progress["minutes"]??0)*60+((ulong?)progress["seconds"]??0));
        }
        public async Task<bool> Write(object value,ulong seconds)
        {
            var edit=(JObject)((JObject)value).DeepClone();Validate(edit);
            if(seconds/3600>Int32.MaxValue)throw new IOException("HLTB progress exceeds its supported range.");
            var progress=edit.SelectToken("general.progress") as JObject;if(progress==null)throw new IOException("HLTB progress format changed.");
            progress["hours"]=(int)(seconds/3600);progress["minutes"]=(int)(seconds/60%60);progress["seconds"]=(int)(seconds%60);
            var response=JsonSafety.Object(await request("/api/submit",edit));
            if(response["error"]!=null&&response["error"].Type!=JTokenType.Null&&response["error"].ToString()!="false")throw new IOException("HLTB rejected the update. Progress remains queued.");
            return true; // HltbProgress always verifies by reading the entry again.
        }
        public static JObject Match(JArray entries,string title)
        {
            // No fuzzy title matching: remasters, DLC and replays may have separate entries.
            var matches=entries.OfType<JObject>().Where(e=>String.Equals(((string)e["custom_title"]??(string)e["game_name"]??"").Trim(),title.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length==1?matches[0]:null;
        }
    }
    public sealed partial class PlaytimePlugin
    {
        private void ShowHltbSignIn()
        {
            using(var view=PlayniteApi.WebViews.CreateView(960,760))
            {view.Navigate("https://howlongtobeat.com/login");view.OpenDialog();}
            hltbRetryAfter=DateTime.MinValue;QueueSync();
        }
        private async Task SyncNativeHltb()
        {
            // Playnite owns the persistent browser cookie jar. No passwords, cookies
            // or account identifiers enter Link's cloud, setup bundles or diagnostics.
            var cookies=UI(()=>{using(var view=PlayniteApi.WebViews.CreateOffscreenView())return view.GetCookies().Where(c=>c.Domain.TrimStart('.').Equals("howlongtobeat.com",StringComparison.OrdinalIgnoreCase)).ToArray();});
            if(cookies.Length==0){activityStatus="Sessions are synchronized. Sign in to HowLongToBeat in Link to upload online progress.";hltbRetryAfter=DateTime.UtcNow.AddMinutes(5);return;}
            var jar=new CookieContainer();
            foreach(var cookie in cookies){try{jar.Add(new Cookie(cookie.Name,cookie.Value,String.IsNullOrEmpty(cookie.Path)?"/":cookie.Path,cookie.Domain){Secure=true});}catch(CookieException){}}
            Func<string,JObject,Task<string>> request=(path,body)=>Task.Run(()=>{
                if(!path.StartsWith("/api/",StringComparison.Ordinal)&&!Regex.IsMatch(path,"^/submit/edit/[0-9]+$"))throw new IOException("Invalid HLTB request.");
                var req=(HttpWebRequest)WebRequest.Create("https://howlongtobeat.com"+path);req.CookieContainer=jar;req.Timeout=20000;req.ReadWriteTimeout=20000;req.AllowAutoRedirect=false;req.UserAgent="Mozilla/5.0 PlayniteLink";req.Accept="application/json, text/html";req.Referer="https://howlongtobeat.com/";
                if(body!=null){req.Method="POST";req.ContentType="application/json";req.Headers["Origin"]="https://howlongtobeat.com";var bytes=Encoding.UTF8.GetBytes(body.ToString(Formatting.None));req.ContentLength=bytes.Length;using(var output=req.GetRequestStream())output.Write(bytes,0,bytes.Length);}
                using(var response=(HttpWebResponse)req.GetResponse())
                {if((int)response.StatusCode<200||(int)response.StatusCode>=300)throw new IOException("HLTB requires sign-in or retry.");using(var reader=new StreamReader(response.GetResponseStream())){var text=reader.ReadToEnd();if(text.Length>16*1024*1024)throw new IOException("HLTB response exceeds the supported size.");return text;}}
            });
            var user=JsonSafety.Object(await request("/api/user",null));var account=(long?)user.SelectToken("data[0].user_id")??0;
            if(account<=0){activityStatus="Sign in to HowLongToBeat in Link. Session history stays available without sign-in.";hltbRetryAfter=DateTime.UtcNow.AddMinutes(5);return;}
            var query=new JObject{{"user_id",account},{"lists",new JArray("playing","backlog","replays","custom","custom2","custom3","completed","retired")},{"set_playstyle","comp_all"},{"name",""},{"platform",""},{"storefront",""},{"sortBy",""},{"sortFlip",0},{"view",""},{"random",0},{"limit",5000},{"currentUserHome",true}};
            var catalog=JsonSafety.Object(await request("/api/user/"+account+"/games/list",query));var entries=catalog.SelectToken("data.gamesList") as JArray;if(entries==null)throw new IOException("HLTB library format changed. Progress remains queued.");
            if(hltbProgress==null)hltbProgress=new HltbProgress(Root);int pending=0;
            foreach(var pair in repository.Snapshot().Mappings)
            {
                if(closing||transferCancelled)return;
                var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null||game.IsRunning||game.IsLaunching)continue;
                var snapshot=repository.SnapshotGame(pair.Key);var map=snapshot.Mappings[pair.Key];var total=new Ledger(snapshot.Entries).Total(map.Key);
                if(game.Playtime!=map.Expected||game.Playtime!=total){pending++;continue;}
                var entry=NativeHltbProgress.Match(entries,game.Name);if(entry==null){pending++;continue;}
                var gameId=(long?)entry["game_id"]??0;var submission=(long?)entry["id"]??0;
                await hltbProgress.Update(Identity.Hash(account+":"+gameId+":"+submission),total,new NativeHltbProgress(request,account,gameId,submission),DateTime.UtcNow);
            }
            activityStatus="Link session history and online HLTB progress are up to date."+(pending>0?" "+pending+" game(s) need a unique matching HLTB profile entry or playtime review.":"");
        }
    }
}
