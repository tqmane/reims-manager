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
            if (e.Args.Length > 0 && e.Args[0] == "--control") {
                Shutdown(await ManagerControl.RunClient(e.Args)); return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--backend-check") {
                string folder = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(folder);
                await SelfChecks.CheckBackend(); await BackendPipeChecks.Run(folder); Shutdown(0); return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--ssh-config-check") {
                await SshConfigChecks.Run(Path.GetFullPath(e.Args[1])); Shutdown(0); return;
            }
            if (e.Args.Length == 3 && e.Args[0] == "--preferences-probe") {
                await PreferencesChecks.Probe(Path.GetFullPath(e.Args[1]), e.Args[2]); Shutdown(0); return;
            }
            if (e.Args.Length == 1 && e.Args[0] == "--console-fixture") {
                var fixtureWindow = new Window {
                    Title = "Reims isolated native console test", Width = 640, Height = 420,
                    Content = new System.Windows.Controls.TextBlock {
                        Text = "Native console test fixture — not a macOS guest", Margin = new Thickness(32), FontSize = 20
                    }
                };
                MainWindow = fixtureWindow; fixtureWindow.Show(); return;
            }
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
                await PreferencesChecks.Run(folder);
                await window.RenderChecks(folder);
                await window.CheckThemeRendering(folder);
                await window.CheckNativeConsoleLifecycle(folder);
                string fixture=Path.Combine(folder,"fixture.reimsvm");
                if (File.Exists(fixture)) File.Delete(fixture);
                var entry=VmLibrary.Create(fixture,Path.Combine(folder,"fixture.data"));
                if (VmLibrary.Open(fixture)!=entry) throw new Exception("VM library roundtrip failed");
                File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new {fluent_theme=true,argument_roundtrip=true,vm_library_roundtrip=true,qmp_correlation=true,qmp_error_handling=true,pid_identity=true,settings_roundtrip=true,settings_process_restart=true,settings_multi_instance_merge=true,settings_backup_recovery=true,unavailable_vm_visible=true,settings_profile_import=true,profile_containment=true,engine_setup=true,action_policy=true,workspace_navigation=true,unsaved_edits=true,vm_context_invalidation=true,exact_memory_sync=true,safe_vm_creation=true,corrupt_settings_backup=true,backend_output_bounds=true,read_helper_cancellation=true,compact_layout=true,dark_theme_render=true,console_focus_layout=true,settings_dedicated_page=true,settings_cancel_preserves_data=true,settings_save_sequence=true,native_console_lifecycle=true,pages_rendered=Directory.GetFiles(folder,"*.png").Length}));
                window.Close(); Shutdown(0); return;
            }
            window.Show();
            var control = new ManagerControlServer(window.Control);
            window.Closed += (_, _) => control.Dispose();
        } catch (Exception error) {
            if (e.Args.Length==3 && e.Args[0]=="--preferences-probe") {
                File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[1]), "probe-error.txt"), error.ToString());
            } else if (e.Args.Length==2 && (e.Args[0]=="--self-test" || e.Args[0]=="--ssh-config-check" || e.Args[0]=="--backend-check")) {
                string folder=Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "error.txt"), error.ToString());
            } else { MessageBox.Show(error.Message,"Reims Manager"); }
            Shutdown(1);
        }
    }
}
