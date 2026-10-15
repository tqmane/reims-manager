using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ReimsManager;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try {
            if (e.Args.Length >= 2 && e.Args[0] == "--echo-args") {
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(e.Args.Skip(2))); Shutdown(0); return;
            }
            if (e.Args.Length==2 && e.Args[0]=="--keyboard-probe") {
                var probe=new KeyboardProbe(Path.GetFullPath(e.Args[1])); MainWindow=probe; probe.Show(); return;
            }
            if (e.Args.Length==3 && e.Args[0]=="--clipboard-check") {
                string folder=Path.GetFullPath(e.Args[2]); Directory.CreateDirectory(folder);
                const string marker="Reims 日本語 clipboard\n\"'$(literal); & < > _\r\n";
                string previous=await GuestClipboard.Transfer(e.Args[1],null);
                try {
                    await GuestClipboard.Transfer(e.Args[1],marker);
                    if (await GuestClipboard.Transfer(e.Args[1],null)!=marker) throw new Exception("Guest clipboard roundtrip failed");
                    File.WriteAllText(Path.Combine(folder,"clipboard-result.json"),"{\"unicode_roundtrip\":true,\"shell_literals\":true}");
                } finally { if (await GuestClipboard.Transfer(e.Args[1],null)==marker) await GuestClipboard.Transfer(e.Args[1],previous); }
                Shutdown(0); return;
            }
            var window = new MainWindow(e.Args.Length==2 && e.Args[0]=="--self-test"); MainWindow = window;
            if (e.Args.Length == 0 || e.Args[0] != "--self-test") {
                string? root=null, qemu=null, vm=null;
                for (int i=0;i<e.Args.Length;i++) {
                    string argument=e.Args[i];
                    if (argument is "--root" or "--qemu" or "--open") {
                        if (++i>=e.Args.Length) throw new ArgumentException("起動オプションの値がありません。");
                        if (argument=="--root") root=e.Args[i];
                        else if (argument=="--qemu") qemu=e.Args[i];
                        else vm=e.Args[i];
                    } else if (e.Args.Length==1 && Path.GetExtension(argument).Equals(".reimsvm",StringComparison.OrdinalIgnoreCase)) vm=argument;
                    else throw new ArgumentException("対応していない起動オプションです。");
                }
                if (root is not null) window.ConfigureEngine(root,qemu);
                else if (qemu is not null) throw new ArgumentException("--qemu には --root も指定してください。");
                if (vm is not null) window.Loaded += (_,_) => window.OpenVmFile(vm);
            }
            if (e.Args.Length == 2 && e.Args[0] == "--self-test") {
                string folder = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(folder);
                string[] values = ["", "日本語 path", "quote\"value", "trailing\\", "$(do-nothing)&literal", "comma,path"];
                string output = Path.Combine(folder, "argv.json");
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
                foreach (string arg in new[] {"--echo-args",output}.Concat(values)) info.ArgumentList.Add(arg);
                using var child = Process.Start(info)!; await child.WaitForExitAsync();
                if (child.ExitCode != 0 || !values.SequenceEqual(JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!)) throw new Exception("Argument roundtrip failed");
                window.Opacity = 0; window.ShowInTaskbar = false; window.Show();
                await SelfChecks.Run(folder);
                await window.RenderChecks(folder);
                string fixture=Path.Combine(folder,"fixture.reimsvm");
                if (File.Exists(fixture)) File.Delete(fixture);
                var entry=VmLibrary.Create(fixture,Path.Combine(folder,"fixture.data"));
                if (VmLibrary.Open(fixture)!=entry) throw new Exception("VM library roundtrip failed");
                File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new {fluent_theme=true,argument_roundtrip=true,vm_library_roundtrip=true,qmp_correlation=true,qmp_error_handling=true,pid_identity=true,settings_roundtrip=true,profile_containment=true,pages_rendered=window.Tabs.Items.Count+window.Devices.Items.Count-1}));
                window.Close(); Shutdown(0); return;
            }
            window.Show();
        } catch (Exception error) {
            if (e.Args.Length==2 && e.Args[0]=="--self-test") {
                string folder=Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "error.txt"), error.ToString());
            } else { MessageBox.Show(error.Message,"Reims Manager"); }
            Shutdown(1);
        }
    }
}
