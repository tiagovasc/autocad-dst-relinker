using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using SheetSetRelinker;

public static class OperationTests
{
    public static string LastRoot { get; private set; }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Pass one temporary test-output folder.");
            Console.WriteLine("Operation tests passed: " + RunAll(args[0]));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    public static int RunAll(string root)
    {
        LastRoot = Path.Combine(root, "ops-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(LastRoot);
        int count = 0;
        Test("links-only success and original preservation", delegate(Fixture f, RunLog log)
        {
            f.PutTargets();
            byte[] original = File.ReadAllBytes(f.Input);
            int last = -1;
            string output = Engine.Run(f.Job, log, delegate(int p, string s) { last = p; }, CancellationToken.None);
            Check(File.Exists(output) && last == 100, "Output/progress");
            Check(original.SequenceEqual(File.ReadAllBytes(f.Input)), "Original unchanged");
            var read = DstDocument.Load(output);
            Check(read.SheetCount == 3 && read.Drawings.Count == 2, "Preserved references");
            Check(read.Drawings.All(d => d.FileName.EndsWith("_new.dwg")), "Mapped names");
            Check(File.ReadAllText(log.SupportPath).Contains("\"success\""), "Support success");
        }); count++;
        Test("copy mode verifies unique drawings", delegate(Fixture f, RunLog log)
        {
            f.Job.CopyDrawings = true;
            string output = Engine.Run(f.Job, log, null, CancellationToken.None);
            Check(File.Exists(output), "DST created");
            Check(Directory.GetFiles(f.Destination, "*.dwg").Length == 2, "Copied unique sources once");
            foreach (var d in f.Job.Document.Drawings) Check(File.ReadAllBytes(Engine.ResolveSource(f.Job, d)).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Destination, f.Job.Mapping[d.SourcePath]))), "Byte-identical DWG copy");
        }); count++;
        Test("missing target", delegate(Fixture f, RunLog log)
        {
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Missing files rejected");
            Check(!File.Exists(Engine.OutputPath(f.Job)), "No output");
        }); count++;
        Test("duplicate target names", delegate(Fixture f, RunLog log)
        {
            foreach (var d in f.Job.Document.Drawings) f.Job.Mapping[d.SourcePath] = "same.dwg";
            f.Job.CopyDrawings = true;
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Collision rejected");
        }); count++;
        Test("copy cannot overwrite", delegate(Fixture f, RunLog log)
        {
            f.PutTargets(); f.Job.CopyDrawings = true;
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Existing targets rejected");
        }); count++;
        Test("output cannot overwrite", delegate(Fixture f, RunLog log)
        {
            f.PutTargets();
            string output = Engine.OutputPath(f.Job);
            File.WriteAllText(output, "keep existing output");
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Existing DST rejected");
            Check(File.ReadAllText(output) == "keep existing output", "Existing output unchanged");
        }); count++;
        Test("wrong DWG signature", delegate(Fixture f, RunLog log)
        {
            f.PutTargets();
            File.WriteAllText(Path.Combine(f.Destination, f.Job.Mapping[f.Job.Document.Drawings[0].SourcePath]), "not a drawing");
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Invalid header rejected");
        }); count++;
        Test("locked drawing", delegate(Fixture f, RunLog log)
        {
            f.Job.CopyDrawings = true;
            using (var locked = new FileStream(Engine.ResolveSource(f.Job, f.Job.Document.Drawings[0]), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Locked file rejected");
        }); count++;
        Test("drawing lock marker", delegate(Fixture f, RunLog log)
        {
            f.PutTargets();
            File.WriteAllText(Path.ChangeExtension(Path.Combine(f.Destination, f.Job.Mapping[f.Job.Document.Drawings[0].SourcePath]), ".dwl2"), "test lock");
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Lock marker rejected");
        }); count++;
        Test("cancel during copy", delegate(Fixture f, RunLog log)
        {
            f.Job.CopyDrawings = true;
            using (var cancellation = new CancellationTokenSource())
            {
                bool cancelled = false;
                try { Engine.Run(f.Job, log, delegate(int p, string s) { if (p >= 10) cancellation.Cancel(); }, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation reported");
                Check(!File.Exists(Engine.OutputPath(f.Job)), "No DST published");
                Check(File.ReadAllText(log.SupportPath).Contains("cancelled"), "Cancellation diagnostic");
                Check(log.ReadText().Contains("RECOVERY"), "Recovery location logged");
            }
        }); count++;
        Test("changed original DST", delegate(Fixture f, RunLog log)
        {
            f.PutTargets();
            using (var stream = new FileStream(f.Input, FileMode.Append)) stream.WriteByte(0);
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Stale document rejected");
        }); count++;
        Test("distinct byte-identical sources still require selection", delegate(Fixture f, RunLog log)
        {
            f.Job.CopyDrawings = true;
            var d = f.Job.Document.Drawings[0];
            File.Copy(Path.Combine(f.Source, d.FileName), Path.Combine(Path.GetDirectoryName(f.Input), d.FileName));
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Ambiguous source rejected");
            f.Job.SourceOverrides[d.SourcePath] = Path.Combine(f.Source, d.FileName);
            Check(Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Explicit source accepted");
        }); count++;
        Test("two paths to one physical source are deduplicated", delegate(Fixture f, RunLog log)
        {
            f.Job.CopyDrawings = true;
            var drawing = f.Job.Document.Drawings[0];
            string stored = Path.Combine(f.Source, drawing.FileName);
            string alias = Path.Combine(Path.GetDirectoryName(f.Input), drawing.FileName);
            if (!CreateHardLink(alias, stored, IntPtr.Zero))
                throw new IOException("Synthetic hardlink test could not be prepared. Windows error " + Marshal.GetLastWin32Error());
            Check(Engine.ResolveSource(f.Job, drawing) == alias, "Preferred relative alias retained.");
            Check(Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Actual same-file aliases accepted.");
            string output = Engine.Run(f.Job, log, null, CancellationToken.None);
            Check(File.Exists(output), "Copy operation succeeds through the verified alias.");
        }); count++;
        Test("support report omits private values", delegate(Fixture f, RunLog log)
        {
            Engine.Validate(f.Job, log, CancellationToken.None);
            string support = File.ReadAllText(log.SupportPath);
            Check(!support.Contains(f.Destination) && !support.Contains("private-fixture") && !support.Contains("A_new.dwg"), "Support privacy");
            Check(log.ReadText().Contains(f.Destination), "Detailed local log has actual path");
        }); count++;
        Test("original path cannot be output", delegate(Fixture f, RunLog log)
        {
            f.Job.DestinationFolder = Path.GetDirectoryName(f.Input);
            f.Job.OutputName = Path.GetFileName(f.Input);
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Original output rejected");
        }); count++;
        Test("invalid and reserved output name", delegate(Fixture f, RunLog log)
        {
            f.Job.OutputName = "NUL.dst";
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Reserved name rejected");
            f.Job.OutputName = "..\\outside.dst";
            Check(!Engine.Validate(f.Job, log, CancellationToken.None).IsValid, "Directory traversal rejected");
        }); count++;
        Test("error support status and retry reset", delegate(Fixture f, RunLog log)
        {
            log.Error(new DstException("DST_ENCODING", "private-fixture error details"));
            string failed = File.ReadAllText(log.SupportPath);
            Check(failed.Contains("\"status\": \"failed\"") && failed.Contains("DST_ENCODING"), "Error status and safe code");
            Check(!failed.Contains("private-fixture"), "No private error message");
            log.WriteSupport("validating");
            string retry = File.ReadAllText(log.SupportPath);
            Check(retry.Contains("\"error_type\": \"\"") && retry.Contains("\"last_error_code\": \"\""), "Retry resets stale error");
        }); count++;
        return count;
    }
    private static void Test(string name, Action<Fixture, RunLog> body)
    {
        string root = Path.Combine(LastRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
        var fixture = new Fixture(root);
        using (RunLog log = RunLog.Create(Path.Combine(root, "logs")))
        {
            try { body(fixture, log); Console.WriteLine("PASS " + name); }
            catch (Exception error) { throw new Exception("Operation test failed: " + name + "; fixture=" + root, error); }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);
    private sealed class Fixture
    {
        public string Source, Destination, Input;
        public Job Job;
        public Fixture(string root)
        {
            Source = Path.Combine(root, "private-fixture source");
            Destination = Path.Combine(root, "destination & Unicode é");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination);
            Input = Path.Combine(root, "private-fixture.dst");
            foreach (string name in new[] { "A.dwg", "B.dwg" }) File.WriteAllBytes(Path.Combine(Source, name), Encoding.ASCII.GetBytes("AC1032 synthetic test data; not a real drawing"));
            var xml = new StringBuilder("<AcSmDatabase ID=\"db\"><AcSmSheetSet ID=\"set\"><AcSmSubset ID=\"sub\">");
            int index = 0;
            foreach (string name in new[] { "A.dwg", "A.dwg", "B.dwg" })
            {
                index++;
                xml.Append("<AcSmSheet ID=\"s" + index + "\"><AcSmProp propname=\"Number\" vt=\"8\">" + index + "</AcSmProp><AcSmAcDbLayoutReference ID=\"r" + index + "\">");
                xml.Append(Prop("FileName", Path.Combine(Source, name)));
                xml.Append(Prop("Relative_FileName", ".\\" + name));
                xml.Append(Prop("Unc_FileName", ""));
                xml.Append(Prop("Name", "Layout" + index));
                xml.Append(Prop("AcDbHandle", "1" + index));
                xml.Append("</AcSmAcDbLayoutReference></AcSmSheet>");
            }
            xml.Append("</AcSmSubset></AcSmSheetSet></AcSmDatabase>");
            File.WriteAllBytes(Input, DstCodec.EncodeXml(xml.ToString()));
            Job = new Job { Document = DstDocument.Load(Input), InputPath = Input, DestinationFolder = Destination, OutputName = "Updated.dst" };
            foreach (Drawing drawing in Job.Document.Drawings) Job.Mapping.Add(drawing.SourcePath, Path.GetFileNameWithoutExtension(drawing.FileName) + "_new.dwg");
        }
        public void PutTargets()
        {
            foreach (Drawing drawing in Job.Document.Drawings) File.Copy(Path.Combine(Source, drawing.FileName), Path.Combine(Destination, Job.Mapping[drawing.SourcePath]));
        }
        private static string Prop(string name, string value) { return "<AcSmProp propname=\"" + name + "\" vt=\"8\">" + SecurityElement.Escape(value) + "</AcSmProp>"; }
    }
}
