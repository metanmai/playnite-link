using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    public sealed class CloudQuestion
    {
        public string Name,Help,Default;
        public bool Secret,Required;
        public bool Boolean;
        public List<CloudChoice> Choices=new List<CloudChoice>();
    }
    public sealed class CloudChoice {public string Value {get;set;} public string Label {get;set;} public override string ToString(){return Label;}}
    public static class CloudAccount
    {
        public static JObject Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json))return new JObject { {"State",""} };
            var result=JsonSafety.Object(json);
            if(result["State"]!=null&&result["State"].Type!=JTokenType.String)throw new InvalidDataException("Unexpected Rclone configuration state.");
            return result;
        }

        public static Uri WebDavEndpoint(string value)
        {
            Uri endpoint;
            if(!Uri.TryCreate(value,UriKind.Absolute,out endpoint)||endpoint.Scheme!=Uri.UriSchemeHttps||
                String.IsNullOrWhiteSpace(endpoint.Host)||endpoint.UserInfo.Length!=0||endpoint.Fragment.Length!=0)
                throw new ArgumentException("Enter a complete HTTPS WebDAV endpoint without credentials in the URL. HTTP exposes credentials and sync data. Existing Rclone connections can still be selected in Advanced.");
            return endpoint;
        }

        public static void Configure(string exe,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null,Func<bool> cancelled=null)
        {
            if(provider=="webdav")
            {
                string endpoint;
                WebDavEndpoint(initial!=null&&initial.TryGetValue("url",out endpoint)?endpoint:null);
            }
            var destination=Path.GetFullPath(config);
            if(File.Exists(destination))throw new IOException("Choose a new configuration file for this enrollment.");
            var parent=Path.GetDirectoryName(destination);
            var stage=Path.Combine(parent,".enroll-"+Guid.NewGuid().ToString("N"));
            CheckCancelled(cancelled);
            Directory.CreateDirectory(stage);
            try
            {
                var pending=Path.Combine(stage,"rclone.conf");
                ConfigureProtocol(exe,(info,input)=>Run(info,input,cancelled),pending,remote,provider,ask,progress,initial,cancelled);
                CheckCancelled(cancelled);
                File.Move(pending,destination);
            }
            finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
        }

        public static void ConfigureProtocol(string exe,Func<ProcessStartInfo,string,string> run,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null,Func<bool> cancelled=null)
        {
            var args=new List<string>{"config","create",remote,provider,"config_is_local","true","--config",config,"--non-interactive"};
            var allowed=new HashSet<string>(new[]{"client_id","client_secret"});
            if(provider=="webdav")allowed.UnionWith(new[]{"url","vendor","user","pass"});
            if(provider=="s3")allowed.UnionWith(new[]{"provider","access_key_id","secret_access_key","endpoint","env_auth","acl","no_check_bucket"});
            var protectedFields=new HashSet<string>(new[]{"client_secret","pass","access_key_id","secret_access_key"});
            var pendingFields=new HashSet<string>();
            if(initial!=null)foreach(var pair in initial)
            {
                if(!allowed.Contains(pair.Key))throw new ArgumentException("Unsupported provider credential field.");
                if(protectedFields.Contains(pair.Key))pendingFields.Add(pair.Key);
                else{args.Add(pair.Key);args.Add(pair.Value);}
            }
            // Rclone asks backend settings before its usual provider setup.
            // Feed credential fields through the same continuation channel.
            if(pendingFields.Count>0)args.Add("--all");
            string state=null,answer=null;
            for(int step=0;step<60;step++)
            {
                CheckCancelled(cancelled);
                var info=Start(exe,args);
                if(state!=null)
                {
                    info.EnvironmentVariables["RCLONE_STATE"]=state;
                    // Rclone treats an empty env override as absent. A literal
                    // empty result is safe and keeps optional answers working.
                    if(answer.Length==0)info.Arguments+=" --result=";
                    else info.EnvironmentVariables["RCLONE_RESULT"]=answer;
                }
                string response=null;progress("Connecting your cloud storage…",()=>response=run(info,null));
                CheckCancelled(cancelled);
                var result=Parse(response);state=(string)result["State"];
                if(String.IsNullOrEmpty(state))
                {
                    if(pendingFields.Count!=0)throw new IOException("Rclone did not accept the required provider credential fields.");
                    return;
                }
                var option=result["Option"] as JObject;if(option==null)throw new IOException("Rclone returned a continuation without a question.");
                var name=(string)option["Name"];
                var password=(bool?)option["IsPassword"]??false;
                var secret=password||((bool?)option["Sensitive"]??false)||protectedFields.Contains(name);
                var fallback=(string)option["DefaultStr"]??(string)option["Default"]??"";
                var backendQuestion=state.StartsWith("*all-",StringComparison.Ordinal);
                if(backendQuestion)
                {
                    if(!String.IsNullOrWhiteSpace((string)result["Error"]))throw new IOException("Rclone rejected a storage setting. Review the supplied credentials and endpoint.");
                    string supplied;
                    answer=initial!=null&&initial.TryGetValue(name,out supplied)?supplied:fallback;
                    if(pendingFields.Remove(name)&&password)
                    {
                        // The native --all config-set continuation stores
                        // password answers as provided. Obscure using stdin.
                        if(answer.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Rclone passwords must be on a single line.");
                        answer=run(Start(exe,new List<string>{"obscure","-"},true),answer).Trim();
                        if(answer.Length==0)throw new IOException("Rclone could not prepare the WebDAV password.");
                    }
                }
                else
                {
                    var question=new CloudQuestion{Name=name,Help=(secret?"":((string)result["Error"]??""))+"\n"+((string)option["Help"]??""),Default=secret?"":fallback,Secret=secret,Required=(bool?)option["Required"]??false};
                    question.Boolean=String.Equals((string)option["Type"],"bool",StringComparison.OrdinalIgnoreCase);
                    var examples=option["Examples"] as JArray;
                    if(examples!=null&&!secret)foreach(var example in examples){var value=(string)example["Value"];if(value!=null)question.Choices.Add(new CloudChoice{Value=value,Label=(string)example["Help"]??value});}
                    if(question.Choices.Count>0&&question.Choices.All(c=>c.Value.Equals("true",StringComparison.OrdinalIgnoreCase)||c.Value.Equals("false",StringComparison.OrdinalIgnoreCase)))question.Boolean=true;
                    // Personal Drive setup does not require a Shared Drive.
                    answer=provider=="drive"&&name=="config_change_team_drive"?"false":ask(question);
                    if(answer==null)throw new OperationCanceledException("Cloud connection cancelled. Existing account settings were kept.");
                    if(question.Required&&String.IsNullOrWhiteSpace(answer))throw new ArgumentException("This provider requires a value for "+name+".");
                }
                args=new List<string>{"config","update",remote,"config_is_local","true","--config",config,"--non-interactive","--continue"};
            }
            throw new IOException("Too many cloud configuration steps. Configure this provider using rclone config.");
        }

        private static ProcessStartInfo Start(string exe,List<string> args,bool stdin=false)
        {
            var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(RcloneTransport.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=stdin,RedirectStandardOutput=true,RedirectStandardError=true};
            info.EnvironmentVariables.Remove("RCLONE_STATE");
            info.EnvironmentVariables.Remove("RCLONE_RESULT");
            return info;
        }

        private static void CheckCancelled(Func<bool> cancelled)
        {if(cancelled!=null&&cancelled())throw new OperationCanceledException("Connection cancelled. Your previous connection is unchanged.");}

        private static string Run(ProcessStartInfo info,string input,Func<bool> cancelled)
        {
            CheckCancelled(cancelled);
            using(var process=Process.Start(info))
            {
                var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                try
                {
                    if(input!=null)
                    {
                        var bytes=new UTF8Encoding(false).GetBytes(input+"\n");
                        try{process.StandardInput.BaseStream.Write(bytes,0,bytes.Length);process.StandardInput.Close();}
                        finally{Array.Clear(bytes,0,bytes.Length);}
                    }
                    var watch=Stopwatch.StartNew();
                    while(!process.WaitForExit(250))
                    {
                        CheckCancelled(cancelled);
                        if(watch.Elapsed.TotalSeconds>240)throw new IOException("Cloud authorization timed out. Retry connecting.");
                    }
                    CheckCancelled(cancelled);
                    System.Threading.Tasks.Task.WaitAll(output,errors);
                    if(process.ExitCode!=0)throw new IOException("Cloud configuration failed (Rclone exit "+process.ExitCode+").");
                    return output.Result;
                }
                finally
                {
                    if(!process.HasExited)try{process.Kill();}catch(InvalidOperationException){}
                    process.WaitForExit();
                }
            }
        }
    }
}
