using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ReimsManager;

internal static class SelfChecks
{
    internal static async Task Run(string folder)
    {
        await CheckQmp(false);
        await CheckQmp(true);
        string runtimeDirectory=Path.Combine(folder,"identity-check","state");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(Path.Combine(runtimeDirectory,"config.json"),"{}");
        using var self=Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(runtimeDirectory,"runtime.json"),JsonSerializer.Serialize(new {
            pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks+1,executable=Environment.ProcessPath,qmp_port=4444
        }));
        try {
            await LiveVmStatus.Read(Path.GetDirectoryName(runtimeDirectory)!);
            throw new Exception("A reused PID was accepted");
        } catch (InvalidDataException) { }

        string? previous=Environment.GetEnvironmentVariable("REIMS_MANAGER_HOME");
        Environment.SetEnvironmentVariable("REIMS_MANAGER_HOME",Path.Combine(folder,"settings-check"));
        try {
            var settings=new ManagerSettings { EngineRoot=Path.Combine(folder,"日本語 engine"),VirtualMachines=["example.reimsvm"] };
            settings.Save();
            var saved=ManagerSettings.Load(out var warning);
            if (warning is not null || saved.EngineRoot!=settings.EngineRoot || saved.VirtualMachines.Single()!="example.reimsvm")
                throw new Exception("Settings roundtrip failed");
        } finally { Environment.SetEnvironmentVariable("REIMS_MANAGER_HOME",previous); }

        string escaped=Path.Combine(folder,"escaped.reimsvm");
        File.WriteAllText(escaped,"{\"schema\":1,\"name\":\"Escaped\",\"directory\":\"../outside\"}");
        try { VmLibrary.Open(escaped); throw new Exception("A VM path outside its package was accepted"); }
        catch (InvalidDataException) { }
    }

    private static async Task CheckQmp(bool error)
    {
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        Task server=Task.Run(async ()=> {
            using var socket=await listener.AcceptTcpClientAsync();
            using var stream=socket.GetStream();
            using var reader=new StreamReader(stream,Encoding.UTF8,false,1024,true);
            using var writer=new StreamWriter(stream,new UTF8Encoding(false),1024,true) {AutoFlush=true};
            await writer.WriteLineAsync("{\"QMP\":{}}");
            foreach (string command in new[]{"qmp_capabilities","query-status"}) {
                using var request=JsonDocument.Parse(await reader.ReadLineAsync() ?? throw new Exception("Missing QMP request"));
                if(request.RootElement.GetProperty("execute").GetString()!=command) throw new Exception("QMP handshake order changed");
                string id=request.RootElement.GetProperty("id").GetString()!;
                await writer.WriteLineAsync("{\"event\":\"RESUME\"}");
                object response=error && command=="query-status"
                    ? new {error=new {desc="Expected rejection"},id}
                    : (object)new {@return=new {status="running"},id};
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
        });
        try {
            try {
                string result=await LiveVmStatus.Query(port);
                if(error || result!="running") throw new Exception("QMP response validation failed");
            } catch (IOException) when(error) { }
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        } finally { listener.Stop(); }
    }
}
