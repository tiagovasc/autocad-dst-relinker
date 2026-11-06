using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SheetSetRelinker
{
    // Opt-in packaged-binary check. It operates only on its own synthetic files.
    internal static class DiagnosticRun
    {
        public static int Execute(string requestedDirectory)
        {
            string root = Path.GetFullPath(requestedDirectory);
            Directory.CreateDirectory(root);
            string run = Path.Combine(root, "self-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run);
            string reportPath = Path.Combine(run, "result.txt");
            string source = Path.Combine(run, "source"), target = Path.Combine(run, "target");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            string dst = Path.Combine(source, "Example.dst");
            string[] names = { "Site_R01.dwg", "Plans_R01.dwg", "Details_R01.dwg" };
            foreach (string name in names) File.WriteAllText(Path.Combine(source, name), "AC1032 synthetic self-test fixture; not a CAD drawing");
            var xml = new StringBuilder("<AcSmDatabase ID=\"db\"><AcSmSheetSet ID=\"set\"><AcSmSubset ID=\"subset\">");
            for (int i = 0; i < 42; i++) {
                string name = names[i % names.Length];
                xml.Append("<AcSmSheet ID=\"s" + i + "\"><AcSmAcDbLayoutReference ID=\"r" + i + "\">")
                    .Append(Prop("FileName", Path.Combine(source, name))).Append(Prop("Relative_FileName", ".\\" + name)).Append(Prop("Unc_FileName", ""))
                    .Append(Prop("Name", "Layout " + i)).Append(Prop("AcDbHandle", (i + 16).ToString("X"))).Append("</AcSmAcDbLayoutReference></AcSmSheet>");
            }
            xml.Append("</AcSmSubset></AcSmSheetSet></AcSmDatabase>");
            File.WriteAllBytes(dst, DstCodec.EncodeXml(xml.ToString()));
            string originalHash = Hash(dst);
            int exitCode = 1;
            using (var form = new MainForm()) {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
                form.Shown += async delegate {
                    try {
                        form.AcceptFiles(new[] { dst });
                        await WaitForIdle(form);
                        form.ApplyReplacement("_R01", "_R02");
                        form.ConfigureForTest(target, "Example-relinked.dst", false);
                        if (await form.ValidateJobAsync()) throw new Exception("Missing drawings were not rejected.");
                        form.ConfigureForTest(target, "Example-relinked.dst", true);
                        if (!await form.ValidateJobAsync()) throw new Exception("Copy validation: " + form.StatusText);
                        if (!await form.CreateJobAsync()) throw new Exception("Copy workflow: " + form.StatusText);
                        if (DstDocument.Load(form.LastOutput).SheetCount != 42 || Directory.GetFiles(target, "*.dwg").Length != 3) throw new Exception("Output counts differ.");
                        if (Hash(dst) != originalHash) throw new Exception("Original DST changed.");
                        foreach (string name in names) if (Hash(Path.Combine(source, name)) != Hash(Path.Combine(target, name.Replace("_R01", "_R02")))) throw new Exception("Copied drawing differs.");
                        if (await form.ValidateJobAsync()) throw new Exception("Existing output was not protected.");
                        form.ConfigureForTest(target, "Example-links-only.dst", false);
                        if (!await form.ValidateJobAsync() || !await form.CreateJobAsync()) throw new Exception("Links-only workflow: " + form.StatusText);
                        form.ConfigureForTest(@"D:\Projects\Example\Revision 02", "Example-relinked.dst", false);
                        form.RenderTo(Path.Combine(run, "ui.png"));
                        File.WriteAllText(reportPath, "PASS packaged executable\r\nRuntime: " + Environment.Version + "\r\nProcess: " + Process.GetCurrentProcess().MainModule.FileName + "\r\nMissing files rejected; copy mode and links-only succeeded; 42 sheets and 3 drawings; source hashes preserved; existing output blocked; native window rendered.\r\nSynthetic test only: no AutoCAD or DWG layout validation.\r\n");
                        exitCode = 0;
                    } catch (Exception e) { File.WriteAllText(reportPath, "FAIL\r\n" + e); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            return exitCode;
        }
        private static async Task WaitForIdle(MainForm form) { var clock = Stopwatch.StartNew(); while (form.IsBusy) { if (clock.ElapsedMilliseconds > 30000) throw new TimeoutException("Loading timed out."); await Task.Delay(20); } }
        private static string Prop(string name, string value) { return "<AcSmProp propname=\"" + name + "\" vt=\"8\">" + SecurityElement.Escape(value) + "</AcSmProp>"; }
        private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)); }
    }
}
