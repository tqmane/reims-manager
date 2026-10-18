using System.IO;
using System.Text.Json;

namespace ReimsManager;

internal static class WifiUiChecks
{
    internal static JsonElement Snapshot(string? kind=WifiForwarderState.MacNat, bool installed=true, bool configured=true,
        bool running=false, bool ready=false, string? error=null, int? pid=null) => JsonSerializer.SerializeToElement(new {
            network="bridge",network_uplink_id="aaaaaaaa-1111-4111-8111-111111111111",
            network_forwarder=new {kind,installed,configured,running,ready,pid,error,ipv4_only=true}
        });

    internal static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        if (WifiForwarderState.Read(JsonSerializer.SerializeToElement(new {})) is not null) throw new Exception("Missing forwarder state was confirmed.");
        if (WifiForwarderState.Read(JsonSerializer.SerializeToElement(new { network_forwarder=(object?)null })) is not null) throw new Exception("Null forwarder state was confirmed.");
        var none=WifiForwarderState.Read(Snapshot(kind:"none",installed:false,configured:false))!;
        if (none.Kind is not null || none.Ready) throw new Exception("Unconfigured backend 'none' was treated as Wi-Fi readiness.");
        var stopped=WifiForwarderState.Read(Snapshot())!;
        if (stopped.Ready || !stopped.Summary("stopped").Contains("起動待ち")) throw new Exception("Configured/stopped was shown connected.");
        var started=WifiForwarderState.Read(Snapshot(running:true,pid:1234))!;
        if (started.Ready || !started.Summary("running").Contains("初期化")) throw new Exception("Starting worker was shown ready.");
        var ready=WifiForwarderState.Read(Snapshot(running:true,ready:true,pid:1234))!;
        if (!ready.Summary("running").Contains("転送プロセス") || ready.Summary("running").Contains("接続済")) throw new Exception("Capture readiness was presented as LAN connectivity.");
        if (!WifiForwarderState.Read(Snapshot(installed:false,running:true,ready:true,pid:1234))!.Summary("running").Contains("準備完了")) throw new Exception("A live ready worker was hidden by an unavailable driver inventory query.");
        var failed=WifiForwarderState.Read(Snapshot(running:true,ready:true,pid:1234,error:"Authored worker failure"))!;
        if (!failed.Summary("running").Contains("エラー")) throw new Exception("A reported error was hidden behind a ready flag.");
        Reject(()=>WifiForwarderState.Read(Snapshot(ready:true)));
        foreach(string json in new[]{Snapshot().GetRawText().Replace("\"ready\":false","\"ready\":\"false\""),
            Snapshot().GetRawText().Replace("\"ipv4_only\":true","\"ipv4_only\":false")}) {
            using var document=JsonDocument.Parse(json);Reject(()=>WifiForwarderState.Read(document.RootElement));
        }
        const string id="aaaaaaaa-1111-4111-8111-111111111111";
        Reject(()=>WifiForwarderState.PrepareArguments(id,false,false));
        if (!WifiForwarderState.PrepareArguments(id,false,true).SequenceEqual(new[]{"-PhysicalAdapter",id,"-AcceptPacketFilterTerms"}) ||
            WifiForwarderState.PrepareArguments(id,true,false).Contains("-AcceptPacketFilterTerms")) throw new Exception("Driver terms consent was invented or lost.");
        foreach(string state in new[]{"running","shutdown","paused","unprepared","unknown","stopped"})
            foreach(string action in new[]{"PrepareWifi","DisableWifi"})
                if(VmActionPolicy.CanRun(action,state,true,true)!=(state=="stopped") || VmActionPolicy.CanRun(action,state,false,true) || VmActionPolicy.CanRun(action,state,true,false))
                    throw new Exception("Wi-Fi action bypassed its stopped/VM/engine policy.");
        string file=Path.Combine(folder,"wifi-prepare-result.json");
        File.WriteAllText(file,"{\"schema\":1,\"prepared\":true,\"configured\":true,\"restart_required\":false}");
        if(!WifiPrepareResult.Read(file,DateTime.UtcNow.AddSeconds(-10)).Configured) throw new Exception("Fresh Wi-Fi result was lost.");
        Reject(()=>WifiPrepareResult.Read(file,DateTime.UtcNow.AddMinutes(1)));
        File.WriteAllText(file,"{\"schema\":1,\"prepared\":false,\"configured\":false,\"restart_required\":true}");
        if(!WifiPrepareResult.Read(file,DateTime.MinValue).RestartRequired) throw new Exception("Driver restart was treated as completed configuration.");
        foreach(string json in new[]{"{\"schema\":2,\"prepared\":true,\"configured\":true,\"restart_required\":false}",
            "{\"schema\":1,\"prepared\":\"true\",\"configured\":true,\"restart_required\":false}",
            "{\"schema\":1,\"prepared\":true,\"configured\":true,\"restart_required\":true}"}) {
            File.WriteAllText(file,json);Reject(()=>WifiPrepareResult.Read(file,DateTime.MinValue));
        }
        File.WriteAllText(file,new string(' ',65537));Reject(()=>WifiPrepareResult.Read(file,DateTime.MinValue));
        File.WriteAllText(Path.Combine(folder,"wifi-ui-result.json"),"{\"missing_status_unconfirmed\":true,\"none_kind_supported\":true,\"worker_ready_not_lan\":true,\"errors_visible\":true,\"explicit_terms_only\":true,\"stopped_only\":true,\"fresh_bounded_prepare_result\":true}");
    }

    private static void Reject(Action action)
    {
        try { action();throw new Exception("Unsafe Wi-Fi state/consent/result was accepted."); }
        catch(Exception error) when(error is InvalidDataException or InvalidOperationException or KeyNotFoundException or JsonException) {}
    }
}
