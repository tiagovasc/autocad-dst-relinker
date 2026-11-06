using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using SheetSetRelinker;

// Synthetic-only fixtures: no customer drawings, network names, IDs or paths.
public static class CoreTests
{
    public static string LastRoot { get; private set; }
    public static int RunAll(string root)
    {
        LastRoot = Path.Combine(root, "core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(LastRoot);
        int count = 0;
        Action<string, Action> test = delegate(string name, Action body)
        {
            body(); Console.WriteLine("PASS core: " + name); count++;
        };
        test("independent known byte vectors", delegate
        {
            byte[] known = { 208, 207, 210, 73, 39, 156, 45, 52, 12 };
            Check(DstCodec.DecodeXml(known) == "<A>\u00E9\U0001F600", "Decode fixed vector.");
            Check(DstCodec.EncodeXml("<A>\u00E9\U0001F600").SequenceEqual(known), "Encode fixed vector.");
            Check(DstCodec.DecodeXml(new byte[] { 131, 134, 135 }) == "\n\t\n", "Alternative LF byte.");
        });
        test("unknown bytes and malformed UTF-8 rejected", delegate
        {
            Expect("DST_ENCODING", delegate { DstCodec.DecodeXml(new byte[] { 176 }); });
            Expect("DST_UTF8", delegate { DstCodec.DecodeXml(new byte[] { 73 }); });
            Expect("DST_ENCODING", delegate { DstCodec.EncodeXml("\r"); });
            Expect("DST_UTF8", delegate { DstCodec.EncodeXml("\uD800"); });
        });
        string path = CreateFixture(Path.Combine(LastRoot, "example.dst"));
        byte[] original = File.ReadAllBytes(path);
        DstDocument doc = DstDocument.Load(path);
        string destination = Path.Combine(LastRoot, "destination");
        Dictionary<string, string> names = Mapping(doc);
        byte[] updated = null;
        test("groups shared DWGs and exposes all source candidates", delegate
        {
            Check(doc.SheetCount == 3 && doc.SubsetCount == 2 && doc.Drawings.Count == 2, "Counts.");
            Check(doc.Drawings[0].SheetCount == 2 && doc.Drawings[1].SheetCount == 1, "Reference counts.");
            Check(doc.Drawings[0].SourceCandidates.Count == 3, "Relative, stored and UNC candidates.");
            Check(doc.Drawings[0].SourceCandidates[0] == Path.Combine(LastRoot, "Alpha & Caf\u00E9.dwg"), "Relative candidate anchored to DST.");
            Check(doc.SourceSha256.Length == 64, "Source hash.");
        });
        test("Unicode names build without modifying source", delegate
        {
            updated = doc.Build(destination, Path.Combine(destination, "new.dst"), names);
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Original file unchanged.");
            Check(!Directory.Exists(destination), "Pure build did not create destination.");
            string saved = Path.Combine(LastRoot, "reopened.dst");
            File.WriteAllBytes(saved, updated);
            DstDocument reopened = DstDocument.Load(saved);
            Check(reopened.SheetCount == 3 && reopened.SubsetCount == 2 && reopened.Drawings.Count == 2, "Reopened counts.");
            Check(reopened.Drawings.Select(x => x.FileName).SequenceEqual(names.Values), "Names preserve Unicode and punctuation.");
        });
        test("every other decoded byte and object identity preserved", delegate
        {
            string before = DstCodec.DecodeXml(original), after = DstCodec.DecodeXml(updated);
            Check(MaskPathValues(before) == MaskPathValues(after), "All non-path lexical content identical.");
            XmlDocument a = Xml(before), b = Xml(after);
            Check(a.SelectNodes("//*[@ID]").Cast<XmlElement>().Select(x => x.GetAttribute("ID")).SequenceEqual(
                  b.SelectNodes("//*[@ID]").Cast<XmlElement>().Select(x => x.GetAttribute("ID"))), "IDs and order unchanged.");
            Check(after.Contains("Keep literal Alpha &amp; Caf\u00E9.dwg here"), "Unrelated custom property not renamed.");
            Check(after.Contains("<!-- <AcSmAcDbLayoutReference ID='commented'>ignore</AcSmAcDbLayoutReference> -->"), "Comment preserved.");
            Check(after.Contains("M\u00FCnchen \U0001F30D"), "Unrelated multibyte text preserved.");
            foreach (XmlElement prop in b.SelectNodes("//AcSmSheet/AcSmAcDbLayoutReference/AcSmProp[@propname='Relative_FileName']"))
                Check(prop.InnerText.StartsWith(".\\", StringComparison.Ordinal), "Relative path anchored next to new DST.");
        });
        test("original alternative LF encoding stays byte-identical", delegate
        {
            byte[] variant = (byte[])original.Clone();
            int firstLf = Array.IndexOf(variant, (byte)131);
            Check(firstLf >= 0, "Fixture LF.");
            variant[firstLf] = 135;
            string file = Save("alternative-lf", variant);
            DstDocument loaded = DstDocument.Load(file);
            byte[] result = loaded.Build(destination, Path.Combine(destination, "lf.dst"), Mapping(loaded));
            Check(result[firstLf] == 135 && result.Count(x => x == 135) == 1, "Untouched original byte 135 retained.");
        });
        test("numeric entities and single quoted attributes are supported", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source")).Replace("&amp; Caf", "&#38; Caf")
                .Replace("propname=\"FileName\"", "propname='FileName'");
            DstDocument loaded = LoadText("entity", text);
            byte[] result = loaded.Build(destination, Path.Combine(destination, "entity.dst"), Mapping(loaded));
            Check(MaskPathValues(text) == MaskPathValues(DstCodec.DecodeXml(result)), "Numeric-entity input preserved outside edits.");
        });
        test("explicit UNC destination creates consistent paths", delegate
        {
            const string network = @"\\example.invalid\share\Relinked";
            byte[] result = doc.Build(network, network + @"\example.dst", names);
            XmlDocument built = Xml(DstCodec.DecodeXml(result));
            foreach (XmlElement reference in built.SelectNodes("//AcSmSheet/AcSmAcDbLayoutReference"))
            {
                string full = reference.SelectSingleNode("AcSmProp[@propname='FileName']").InnerText;
                string unc = reference.SelectSingleNode("AcSmProp[@propname='Unc_FileName']").InnerText;
                Check(full == unc && full.StartsWith(network + "\\", StringComparison.Ordinal), "UNC and full path agree.");
            }
        });
        test("unavailable UNC is cleared instead of retaining stale server", delegate
        {
            string local = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "RelinkerSyntheticTarget");
            byte[] result = doc.Build(local, Path.Combine(local, "test.dst"), names);
            XmlDocument built = Xml(DstCodec.DecodeXml(result));
            foreach (XmlElement prop in built.SelectNodes("//AcSmSheet/AcSmAcDbLayoutReference/AcSmProp[@propname='Unc_FileName']"))
                Check(prop.InnerText == "", "Stale source UNC removed for local destination.");
        });
        test("non-sheet template references stay untouched", delegate
        {
            string template = "<AcSmAcDbLayoutReference ID=\"template\"><AcSmProp propname=\"FileName\" vt=\"8\">Template.dwt</AcSmProp></AcSmAcDbLayoutReference>";
            string text = FixtureXml(Path.Combine(LastRoot, "source")).Replace("</AcSmSheetSet>", template + "</AcSmSheetSet>");
            DstDocument loaded = LoadText("template", text);
            string result = DstCodec.DecodeXml(loaded.Build(destination, Path.Combine(destination, "template.dst"), Mapping(loaded)));
            Check(result.Contains(template), "Template path untouched.");
        });
        test("duplicate case-insensitive target names rejected", delegate
        {
            Dictionary<string, string> bad = Mapping(doc);
            bad[doc.Drawings[0].SourcePath] = "SAME.dwg";
            bad[doc.Drawings[1].SourcePath] = "same.DWG";
            Expect("TARGET_COLLISION", delegate { doc.Build(destination, Path.Combine(destination, "collision.dst"), bad); });
        });
        test("incomplete and unknown mapping keys rejected", delegate
        {
            Dictionary<string, string> bad = Mapping(doc);
            bad.Remove(doc.Drawings[0].SourcePath);
            Expect("MAPPING", delegate { doc.Build(destination, Path.Combine(destination, "missing.dst"), bad); });
            bad.Add(@"C:\not-in-this-document.dwg", "new.dwg");
            Expect("MAPPING", delegate { doc.Build(destination, Path.Combine(destination, "unknown.dst"), bad); });
        });
        test("mapping keys are Windows case insensitive", delegate
        {
            Dictionary<string, string> mapped = names.ToDictionary(x => x.Key.ToUpperInvariant(), x => x.Value);
            Check(doc.Build(destination, Path.Combine(destination, "case.dst"), mapped).Length > 0, "Case-insensitive lookup.");
        });
        test("path traversal and invalid Windows filename rejected", delegate
        {
            foreach (string invalid in new[] { "../escape.dwg", @"folder\escape.dwg", "file:stream.dwg", "CON.dwg", "COM\u00B9.dwg", "drawing?.dwg", "bad.dwg ", "trailing.", "", "bad.txt" })
            {
                Dictionary<string, string> bad = Mapping(doc);
                bad[doc.Drawings[0].SourcePath] = invalid;
                ExpectAny(delegate { doc.Build(destination, Path.Combine(destination, "invalid.dst"), bad); });
            }
        });
        test("original output path and mismatched output folder rejected", delegate
        {
            Expect("SOURCE_OVERWRITE", delegate { doc.Build(Path.GetDirectoryName(path), path, names); });
            Expect("OUTPUT_PATH", delegate { doc.Build(destination, Path.Combine(LastRoot, "other.dst"), names); });
            Expect("OUTPUT_EXTENSION", delegate { doc.Build(destination, Path.Combine(destination, "other.txt"), names); });
            Expect("TARGET_FOLDER", delegate { doc.Build("relative", "relative\\other.dst", names); });
        });
        test("malformed XML and wrong root rejected", delegate
        {
            Expect("DST_XML", delegate { LoadText("malformed", "<AcSmDatabase><broken></AcSmDatabase>"); });
            Expect("DST_SCHEMA", delegate { LoadText("wrong-root", "<Other/>"); });
        });
        test("DTD and external entities prohibited", delegate
        {
            string text = "<!DOCTYPE AcSmDatabase [<!ENTITY external SYSTEM 'file:///C:/does-not-exist'>]>" +
                FixtureXml(Path.Combine(LastRoot, "source")).Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n", "");
            Expect("DST_XML", delegate { LoadText("dtd", text); });
        });
        test("namespace and deep XML rejected", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source")).Replace("<AcSmDatabase ", "<AcSmDatabase xmlns=\"urn:unsupported\" ");
            Expect("DST_SCHEMA", delegate { LoadText("namespace", text); });
            string deep = String.Concat(Enumerable.Repeat("<a>", 140)) + String.Concat(Enumerable.Repeat("</a>", 140));
            Expect("DST_DEPTH", delegate { LoadText("deep", deep); });
        });
        test("missing or duplicate sheet layout references rejected", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source"));
            string reference = Regex.Match(text, "<AcSmAcDbLayoutReference ID=\"reference-[^\"]+\">.*?</AcSmAcDbLayoutReference>", RegexOptions.Singleline).Value;
            Expect("DST_SCHEMA", delegate { LoadText("missing-ref", text.Replace(reference, "")); });
            Expect("DST_SCHEMA", delegate { LoadText("double-ref", text.Replace(reference, reference + reference)); });
        });
        test("duplicate sheet IDs and duplicate path properties rejected", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source"));
            Expect("DST_SCHEMA", delegate { LoadText("duplicate-id", text.Replace("ID=\"sheet-2\"", "ID=\"sheet-1\"")); });
            string property = Regex.Match(text, "<AcSmProp propname=\"FileName\"[^>]*>.*?</AcSmProp>").Value;
            Expect("DST_SCHEMA", delegate { LoadText("duplicate-property", text.Replace(property, property + property)); });
        });
        test("path CDATA, nested markup and non-string properties rejected", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source"));
            string property = Regex.Match(text, "<AcSmProp propname=\"FileName\"[^>]*>.*?</AcSmProp>").Value;
            Expect("DST_SCHEMA", delegate { LoadText("cdata", text.Replace(property, "<AcSmProp propname=\"FileName\" vt=\"8\"><![CDATA[C:\\source\\A.dwg]]></AcSmProp>")); });
            Expect("DST_SCHEMA", delegate { LoadText("nested", text.Replace(property, "<AcSmProp propname=\"FileName\" vt=\"8\"><x>C:\\source\\A.dwg</x></AcSmProp>")); });
            Expect("DST_SCHEMA", delegate { LoadText("wrong-vt", text.Replace(property, property.Replace("vt=\"8\"", "vt=\"3\""))); });
        });
        test("missing, self-closing and duplicate path schema rejected", delegate
        {
            string text = FixtureXml(Path.Combine(LastRoot, "source"));
            string property = Regex.Match(text, "<AcSmProp propname=\"Unc_FileName\"[^>]*>.*?</AcSmProp>").Value;
            Expect("DST_SCHEMA", delegate { LoadText("missing-unc", text.Replace(property, "")); });
            Expect("DST_SCHEMA", delegate { LoadText("self-closing", text.Replace(property, "<AcSmProp propname=\"Unc_FileName\" vt=\"8\"/>")); });
        });
        test("empty sheet sets and zero-size files rejected", delegate
        {
            Expect("DST_EMPTY", delegate { LoadText("no-sheets", "<AcSmDatabase><AcSmSheetSet/></AcSmDatabase>"); });
            Expect("DST_SIZE", delegate { DstDocument.Load(Save("zero", new byte[0])); });
        });
        test("64 MB file limit checked before loading", delegate
        {
            string large = Path.Combine(LastRoot, "too-large.dst");
            using (FileStream stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength((long)DstCodec.MaximumFileBytes + 1);
            Expect("DST_SIZE", delegate { DstDocument.Load(large); });
        });
        return count;
    }

    public static string CreateFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllBytes(path, DstCodec.EncodeXml(FixtureXml(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "source"))));
        return path;
    }
    private static string FixtureXml(string source)
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<AcSmDatabase ID=\"database\"><AcSmProp propname=\"Description\" vt=\"8\">M\u00FCnchen \U0001F30D</AcSmProp>" +
            "<!-- <AcSmAcDbLayoutReference ID='commented'>ignore</AcSmAcDbLayoutReference> -->" +
            "<AcSmSheetSet ID=\"set\"><AcSmProp propname=\"Custom\" vt=\"8\">Keep literal Alpha &amp; Caf\u00E9.dwg here</AcSmProp>" +
            "<AcSmSubset ID=\"subset-1\">" + Sheet("1", source, "Alpha & Caf\u00E9.dwg", "Plan \U0001F600", "1A") +
            Sheet("2", source, "Alpha & Caf\u00E9.dwg", "Section", "2A") + "</AcSmSubset>" +
            "<AcSmSubset ID=\"subset-2\">" + Sheet("3", source, "Bravo.dwg", "Elevation", "3A") +
            "</AcSmSubset></AcSmSheetSet></AcSmDatabase>\n";
    }
    private static string Sheet(string id, string folder, string file, string layout, string handle)
    {
        return "<AcSmSheet ID=\"sheet-" + id + "\"><AcSmProp propname=\"Number\" vt=\"8\">" + id + "</AcSmProp>" +
            "<AcSmProp propname=\"Title\" vt=\"8\">Synthetic " + id + "</AcSmProp>" +
            "<AcSmAcDbLayoutReference ID=\"reference-" + id + "\">" +
            Prop("FileName", Path.Combine(folder, file)) + Prop("Relative_FileName", ".\\" + file) +
            Prop("Unc_FileName", @"\\example.invalid\share\source\" + file) + Prop("Name", layout) + Prop("AcDbHandle", handle) +
            "</AcSmAcDbLayoutReference></AcSmSheet>";
    }
    private static string Prop(string name, string value) { return "<AcSmProp propname=\"" + name + "\" vt=\"8\">" + Escape(value) + "</AcSmProp>"; }
    private static string Escape(string text) { return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }
    private static string Save(string name, byte[] bytes)
    {
        string path = Path.Combine(LastRoot, name + ".dst");
        File.WriteAllBytes(path, bytes);
        return path;
    }
    private static DstDocument LoadText(string name, string text) { return DstDocument.Load(Save(name, DstCodec.EncodeXml(text))); }
    private static Dictionary<string, string> Mapping(DstDocument document)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < document.Drawings.Count; i++)
            result.Add(document.Drawings[i].SourcePath, "Renamed " + (i + 1) + " ' & # \u00E9 \U0001F600.dwg");
        return result;
    }
    private static string MaskPathValues(string text)
    {
        return Regex.Replace(text, @"(<AcSmProp\b[^>]*\bpropname=(?:""|')(?:FileName|Relative_FileName|Unc_FileName)(?:""|')[^>]*>)[^<]*(</AcSmProp>)", "$1__PATH__$2");
    }
    private static XmlDocument Xml(string text)
    {
        XmlDocument doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(text);
        return doc;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action body)
    {
        try { body(); }
        catch (DstException error) { Check(error.Code == code, "Expected " + code + ", got " + error.Code); return; }
        throw new Exception("Expected rejection: " + code);
    }
    private static void ExpectAny(Action body)
    {
        try { body(); }
        catch (DstException) { return; }
        throw new Exception("Expected a safe rejection.");
    }
}
