using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SheetSetRelinker;

public static class UiTests
{
    public static int RunAll(string root)
    {
        string folder = Path.Combine(root, "ui"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source"); Directory.CreateDirectory(source);
        string target = Path.Combine(folder, "target"); Directory.CreateDirectory(target);
        string input = Path.Combine(source, "Example.dst");
        var xml = new StringBuilder("<AcSmDatabase ID=\"db\"><AcSmSheetSet ID=\"set\"><AcSmSubset ID=\"subset\">");
        var names = new[] { "Site_R01.dwg", "Plans_R01.dwg", "Details_R01.dwg" };
        foreach (string name in names) File.WriteAllText(Path.Combine(source, name), "AC1032 synthetic fixture, not a real DWG");
        for (int i = 0; i < 42; i++) {
            string name = names[i % 3];
            xml.Append("<AcSmSheet ID=\"s" + i + "\"><AcSmAcDbLayoutReference ID=\"r" + i + "\">");
            xml.Append(Prop("FileName", Path.Combine(source, name))).Append(Prop("Relative_FileName", ".\\" + name)).Append(Prop("Unc_FileName", ""));
            xml.Append(Prop("Name", "Layout " + i)).Append(Prop("AcDbHandle", (i + 16).ToString("X"))).Append("</AcSmAcDbLayoutReference></AcSmSheet>");
        }
        xml.Append("</AcSmSubset></AcSmSheetSet></AcSmDatabase>");
        File.WriteAllBytes(input, DstCodec.EncodeXml(xml.ToString()));
        using (var form = new MainForm()) {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Show(); Application.DoEvents();
            form.AcceptFiles(new[] { "wrong.txt" });
            Check(form.StatusText.Contains("Drop one"), "invalid drop gives useful feedback");
            form.AcceptFiles(new[] { input }); Wait(form);
            Check(form.StatusText.Contains("Choose the destination"), "DST drag/drop handler loads");
            form.ApplyReplacement("_R01", "_R02");
            form.ConfigureForTest(target, "Example-relinked.dst", false);
            Button validate = Descendants(form).OfType<Button>().Single(b => b.Text == "Validate");
            Button create = Descendants(form).OfType<Button>().Single(b => b.Text == "Create updated DST");
            Console.WriteLine("UI Validate enabled=" + validate.Enabled + " visible=" + validate.Visible + " selectable=" + validate.CanSelect + " bounds=" + validate.Bounds + " busy=" + form.IsBusy);
            form.RenderTo(Path.Combine(root, "ui-before-validation.png"));
            validate.PerformClick(); Wait(form);
            Check(!create.Enabled && form.StatusText.Contains("not found"), "missing targets block creation: " + form.StatusText + "; create_enabled=" + create.Enabled);
            form.ConfigureForTest(target, "Example-relinked.dst", true);
            validate.PerformClick(); Wait(form);
            Check(create.Enabled, "copy mode passes and enables creation");
            form.ApplyReplacement("_R02", "_R03");
            Check(!create.Enabled, "editing invalidates checks");
            validate.PerformClick(); Wait(form); create.PerformClick(); Wait(form);
            Check(form.LastOutput != null && File.Exists(form.LastOutput), "real Create button writes output");
            Check(DstDocument.Load(form.LastOutput).SheetCount == 42 && Directory.GetFiles(target, "*.dwg").Length == 3, "42 sheets and 3 copied drawings");
            Check(form.StatusText.StartsWith("Created "), "success visible");
            validate.PerformClick(); Wait(form);
            Check(!create.Enabled, "repeat run blocks overwrite");
            string links = Path.Combine(folder, "links"); Directory.CreateDirectory(links);
            foreach (string name in names) File.Copy(Path.Combine(source, name), Path.Combine(links, name.Replace("_R01", "_R03")));
            form.ConfigureForTest(links, "Links.dst", false); validate.PerformClick(); Wait(form); create.PerformClick(); Wait(form);
            Check(form.LastOutput == Path.Combine(links, "Links.dst") && File.Exists(form.LastOutput), "links-only button workflow");
            // Render the actual WinForms controls, with generic display paths only.
            form.ApplyReplacement("_R03", "_R02"); form.ConfigureForTest(@"D:\Projects\Example\Revision 02", "Example-relinked.dst", false);
            form.RenderTo(Path.Combine(root, "ui.png"));
            form.Size = new Size(850, 640); Application.DoEvents();
            Check(form.RectangleToClient(create.RectangleToScreen(create.ClientRectangle)).Bottom <= form.ClientSize.Height, "small-screen footer remains accessible");
            form.RenderTo(Path.Combine(root, "ui-small.png"));
            form.Close();
        }
        Console.WriteLine("PASS 11 native UI component checks (drop, mapping, validation, copy, links-only, overwrite, small screen, render)");
        return 11;
    }
    private static string Prop(string name, string value) { return "<AcSmProp propname=\"" + name + "\" vt=\"8\">" + SecurityElement.Escape(value) + "</AcSmProp>"; }
    private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
    private static void Wait(MainForm form) { var clock = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(10); if (clock.ElapsedMilliseconds > 30000) throw new TimeoutException("UI operation did not finish."); } while (form.IsBusy); Application.DoEvents(); }
    private static void Check(bool ok, string description) { if (!ok) throw new Exception("UI: " + description); }
}
