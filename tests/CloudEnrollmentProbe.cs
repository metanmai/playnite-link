using System;
using System.IO;
using System.Collections.Generic;
using PlaytimeRclone;
class CloudEnrollmentProbe
{
    static int Main(string[] a)
    {
        try
        {
            var credentials=new Dictionary<string,string>();bool personal=false;
            foreach(var line in File.ReadLines(a[1]))
            {
                var trimmed=line.Trim();if(trimmed.StartsWith("[")){personal=trimmed=="[personal]";continue;}
                var split=trimmed.IndexOf('=');if(!personal||split<0)continue;
                var key=trimmed.Substring(0,split).Trim();
                if(key=="client_id"||key=="client_secret")credentials[key]=trimmed.Substring(split+1).Trim();
            }
            if(credentials.Count!=2)throw new Exception("Personal OAuth client fields are unavailable.");
            if(File.Exists(a[2]))throw new Exception("Use a new isolated enrollment config.");
            CloudAccount.Configure(a[0],a[2],"enrollment","drive",q=>{
                Console.WriteLine("Provider question: "+q.Name);
                if(q.Name=="config_is_local"||q.Name=="config_refresh_token")return "true";
                if(!String.IsNullOrEmpty(q.Default))return q.Default;
                throw new Exception("Provider needs interactive selection: "+q.Name);
            },(message,work)=>{Console.WriteLine(message);work();},credentials);
            File.WriteAllText(a[3],"{\"passed\":true,\"freshBrowserEnrollment\":true,\"provider\":\"Google Drive\"}");
            Console.WriteLine("PASS fresh Google Drive browser enrollment");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex.Message);return 1;}
    }
}
