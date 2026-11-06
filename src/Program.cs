using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("Sheet Set Relinker")]
[assembly: AssemblyDescription("Offline AutoCAD DST drawing path and filename updater")]
[assembly: AssemblyCompany("Sheet Set Relinker contributors")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#if NET10_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
#endif

namespace SheetSetRelinker
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [STAThread]
        private static void Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { ReportCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { ReportCrash(e.ExceptionObject as Exception); };
            if (args.Length == 2 && args[0] == "--self-test") { Environment.ExitCode = DiagnosticRun.Execute(args[1]); return; }
            try { Application.Run(new MainForm(args.Length == 1 ? args[0] : null)); }
            catch (Exception e) { ReportCrash(e); }
        }
        private static void ReportCrash(Exception e)
        {
            string location = "";
            try { using (RunLog log = RunLog.Create()) { log.Error(e ?? new Exception("Unknown fatal error")); location = "\n\nLog: " + log.LogPath; } } catch { }
            MessageBox.Show("The operation could not finish. Your original files have not been replaced." + location + "\n\n" + (e == null ? "Unknown error" : e.Message), "Sheet Set Relinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
