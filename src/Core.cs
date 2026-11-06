// Sheet Set Relinker: offline DST inspection and narrowly scoped path editing.
// The observed byte mapping is documented by Bedz01/dst-codec (MIT):
// https://github.com/Bedz01/dst-codec
// This is not the Autodesk API. Native AutoCAD validation is a separate check.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SheetSetRelinker
{
    public sealed class DstException : Exception
    {
        public string Code { get; private set; }
        public DstException(string code, string message) : base(message) { Code = code; }
        public DstException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    }

    public sealed class Drawing
    {
        private readonly List<string> candidates = new List<string>();
        public string SourcePath { get; internal set; }
        public string FileName { get; internal set; }
        public int SheetCount { get; internal set; }
        public IReadOnlyList<string> SourceCandidates { get { return candidates.AsReadOnly(); } }
        internal void AddCandidate(string path)
        {
            if (!String.IsNullOrEmpty(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                candidates.Add(path);
        }
    }

    public static class DstCodec
    {
        public const int MaximumFileBytes = 64 * 1024 * 1024;
        private static readonly int[] Permute = { 13, 12, 15, 14, 9, 8, 11, 10, 5, 4, 7, 6, 1, 0, 3, 2 };
        private static readonly int[] Bases = { 127, 111, 159, 143, 191, 175, 223, 207, -1, 239, 31, -1, 63, 47, 95, 79 };
        private static readonly int[] DecodeTable = MakeDecodeTable();
        private static readonly int[] EncodeTable = MakeEncodeTable();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static int[] MakeDecodeTable()
        {
            int[] table = Enumerable.Repeat(-1, 256).ToArray();
            for (int i = 0; i < 256; i++)
                if (Bases[i >> 4] >= 0) table[i] = Bases[i >> 4] + Permute[i & 15];
            table[131] = 10; table[135] = 10; table[134] = 9;
            return table;
        }
        private static int[] MakeEncodeTable()
        {
            int[] table = Enumerable.Repeat(-1, 256).ToArray();
            for (int i = 0; i < 256; i++)
                if (DecodeTable[i] >= 0 && table[DecodeTable[i]] < 0) table[DecodeTable[i]] = i;
            table[10] = 131; table[9] = 134;
            return table;
        }
        public static byte[] DecodeBytes(byte[] encoded)
        {
            if (encoded == null) throw new ArgumentNullException("encoded");
            if (encoded.Length == 0 || encoded.Length > MaximumFileBytes)
                throw new DstException("DST_SIZE", "The DST must be nonempty and no larger than 64 MB.");
            byte[] decoded = new byte[encoded.Length];
            for (int i = 0; i < encoded.Length; i++)
            {
                int value = DecodeTable[encoded[i]];
                if (value < 0)
                    throw new DstException("DST_ENCODING", String.Format("Unsupported DST byte 0x{0:X2} at offset {1}. No changes were made.", encoded[i], i));
                decoded[i] = (byte)value;
            }
            try { StrictUtf8.GetCharCount(decoded); }
            catch (DecoderFallbackException ex) { throw new DstException("DST_UTF8", "Decoded DST text is not valid UTF-8.", ex); }
            return decoded;
        }
        public static string DecodeXml(byte[] encoded) { return StrictUtf8.GetString(DecodeBytes(encoded)); }
        // Useful for synthetic fixtures. Build() never re-encodes untouched source bytes.
        public static byte[] EncodeXml(string xml)
        {
            if (xml == null) throw new ArgumentNullException("xml");
            byte[] plain;
            try { plain = StrictUtf8.GetBytes(xml); }
            catch (EncoderFallbackException ex) { throw new DstException("DST_UTF8", "Text contains an invalid Unicode surrogate.", ex); }
            return EncodeBytes(plain);
        }
        internal static byte[] EncodeBytes(byte[] plain)
        {
            byte[] result = new byte[plain.Length];
            for (int i = 0; i < plain.Length; i++)
            {
                int value = EncodeTable[plain[i]];
                if (value < 0)
                    throw new DstException("DST_ENCODING", String.Format("Text contains an unsupported byte 0x{0:X2} at offset {1}.", plain[i], i));
                result[i] = (byte)value;
            }
            return result;
        }
    }

    public sealed class DstDocument
    {
        private const int MaximumElements = 250000;
        private const int MaximumDepth = 128;
        private static readonly string[] PathProperties = { "FileName", "Relative_FileName", "Unc_FileName" };
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly byte[] sourceBytes;
        private readonly byte[] xmlBytes;
        private readonly XmlDocument xmlDocument;
        private readonly List<LayoutRecord> layouts = new List<LayoutRecord>();
        private readonly ReadOnlyCollection<Drawing> drawings;
        public IReadOnlyList<Drawing> Drawings { get { return drawings; } }
        public string SourceFilePath { get; private set; }
        public string SourceSha256 { get; private set; }
        public long SourceFileBytes { get { return sourceBytes.Length; } }
        public int SheetCount { get; private set; }
        public int SubsetCount { get; private set; }

        private sealed class RawSpan
        {
            public int ContentStart;
            public int ContentEnd;
            public bool SelfClosing;
        }
        private sealed class FieldSpan
        {
            public string Name;
            public int CharStart;
            public int CharEnd;
            public int ByteStart;
            public int ByteLength;
        }
        private sealed class LayoutRecord
        {
            public string Id;
            public string SourcePath;
            public readonly List<FieldSpan> Fields = new List<FieldSpan>();
        }
        private sealed class Edit
        {
            public int Start;
            public int Length;
            public byte[] NewXml;
        }

        public static DstDocument Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new DstException("DST_PATH", "Choose a DST file.");
            string full = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(full), ".dst", StringComparison.OrdinalIgnoreCase))
                throw new DstException("DST_EXTENSION", "Choose a file with the .dst extension.");
            byte[] data;
            // Check before allocating. Read the same handle so a growing file cannot bypass the limit.
            using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > DstCodec.MaximumFileBytes)
                    throw new DstException("DST_SIZE", "The DST must be nonempty and no larger than 64 MB.");
                data = new byte[(int)stream.Length];
                int read = 0;
                while (read < data.Length)
                {
                    int count = stream.Read(data, read, data.Length - read);
                    if (count == 0) throw new DstException("DST_CHANGED", "The source DST changed while it was being read.");
                    read += count;
                }
                if (stream.ReadByte() != -1) throw new DstException("DST_CHANGED", "The source DST changed while it was being read.");
            }
            return new DstDocument(full, data);
        }

        private DstDocument(string path, byte[] bytes)
        {
            SourceFilePath = path;
            sourceBytes = bytes;
            SourceSha256 = Hash(bytes);
            xmlBytes = DstCodec.DecodeBytes(bytes);
            string xmlText = StrictUtf8.GetString(xmlBytes);
            xmlDocument = ParseXml(xmlBytes);
            if (xmlDocument.DocumentElement == null || xmlDocument.DocumentElement.Name != "AcSmDatabase")
                throw new DstException("DST_SCHEMA", "This file is not a supported AutoCAD sheet-set database.");
            XmlNodeList sheetSets = xmlDocument.GetElementsByTagName("AcSmSheetSet");
            if (sheetSets.Count != 1)
                throw new DstException("DST_SCHEMA", "Exactly one sheet-set object is required.");
            Dictionary<XmlElement, RawSpan> raw = IndexRawElements(xmlText, xmlDocument);
            XmlNodeList sheets = xmlDocument.GetElementsByTagName("AcSmSheet");
            SheetCount = sheets.Count;
            SubsetCount = xmlDocument.GetElementsByTagName("AcSmSubset").Count;
            if (SheetCount == 0) throw new DstException("DST_EMPTY", "The DST contains no sheets to relink.");
            HashSet<string> sheetIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> layoutIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Drawing> grouped = new Dictionary<string, Drawing>(StringComparer.OrdinalIgnoreCase);
            List<Drawing> drawingOrder = new List<Drawing>();
            string sourceFolder = Path.GetDirectoryName(path);
            foreach (XmlElement sheet in sheets)
            {
                if (sheet.ParentNode.Name != "AcSmSheetSet" && sheet.ParentNode.Name != "AcSmSubset")
                    throw new DstException("DST_SCHEMA", "A sheet is outside the supported sheet-set/subset hierarchy.");
                string sheetId = RequireId(sheet);
                if (!sheetIds.Add(sheetId)) throw new DstException("DST_SCHEMA", "Duplicate sheet IDs are not supported.");
                List<XmlElement> references = Children(sheet, "AcSmAcDbLayoutReference");
                if (references.Count != 1)
                    throw new DstException("DST_SCHEMA", "Each sheet must contain exactly one direct layout reference.");
                XmlElement reference = references[0];
                string layoutId = RequireId(reference);
                if (!layoutIds.Add(layoutId)) throw new DstException("DST_SCHEMA", "Duplicate sheet layout-reference IDs are not supported.");
                Dictionary<string, XmlElement> properties = Properties(reference);
                foreach (string required in PathProperties.Concat(new[] { "Name", "AcDbHandle" }))
                    if (!properties.ContainsKey(required))
                        throw new DstException("DST_SCHEMA", "A layout reference is missing the " + required + " property.");
                if (String.IsNullOrEmpty(properties["Name"].InnerText) || String.IsNullOrEmpty(properties["AcDbHandle"].InnerText))
                    throw new DstException("DST_SCHEMA", "A layout reference has no layout name or handle.");
                string stored = properties["FileName"].InnerText;
                if (String.IsNullOrWhiteSpace(stored))
                    throw new DstException("DST_SCHEMA", "A sheet layout has no DWG filename.");
                string sourcePath = ResolveStoredPath(stored, sourceFolder);
                string fileName = Path.GetFileName(sourcePath);
                ValidateDrawingName(fileName);
                LayoutRecord record = new LayoutRecord { Id = layoutId, SourcePath = sourcePath };
                foreach (string propertyName in PathProperties)
                {
                    XmlElement property = properties[propertyName];
                    RequireTextOnly(property);
                    RawSpan span = raw[property];
                    if (span.SelfClosing)
                        throw new DstException("DST_SCHEMA", "Self-closing path properties are not supported. Use a DST saved by the classic Sheet Set Manager.");
                    record.Fields.Add(new FieldSpan { Name = propertyName, CharStart = span.ContentStart, CharEnd = span.ContentEnd });
                }
                layouts.Add(record);
                Drawing drawing;
                if (!grouped.TryGetValue(sourcePath, out drawing))
                {
                    drawing = new Drawing { SourcePath = sourcePath, FileName = fileName, SheetCount = 0 };
                    grouped.Add(sourcePath, drawing);
                    drawingOrder.Add(drawing);
                }
                drawing.SheetCount++;
                string relative = properties["Relative_FileName"].InnerText;
                if (!String.IsNullOrWhiteSpace(relative)) drawing.AddCandidate(ResolveStoredPath(relative, sourceFolder));
                drawing.AddCandidate(sourcePath);
                string unc = properties["Unc_FileName"].InnerText;
                if (!String.IsNullOrWhiteSpace(unc)) drawing.AddCandidate(ResolveStoredPath(unc, sourceFolder));
            }
            List<FieldSpan> spans = layouts.SelectMany(x => x.Fields).OrderBy(x => x.CharStart).ToList();
            int lastCharacter = 0, lastByte = 0;
            foreach (FieldSpan span in spans)
            {
                span.ByteStart = ByteOffset(xmlText, span.CharStart, ref lastCharacter, ref lastByte);
                int end = ByteOffset(xmlText, span.CharEnd, ref lastCharacter, ref lastByte);
                span.ByteLength = end - span.ByteStart;
            }
            drawings = drawingOrder.AsReadOnly();
        }

        public byte[] Build(string targetFolder, string outputDstPath, IDictionary<string, string> mapping)
        {
            string folder = NormalizeAbsoluteDirectory(targetFolder);
            if (String.IsNullOrWhiteSpace(outputDstPath) || !IsFullyQualified(outputDstPath))
                throw new DstException("OUTPUT_PATH", "Choose a full path for the new DST.");
            string dstPath = Path.GetFullPath(outputDstPath);
            if (!String.Equals(NormalizeAbsoluteDirectory(Path.GetDirectoryName(dstPath)), folder, StringComparison.OrdinalIgnoreCase))
                throw new DstException("OUTPUT_PATH", "The new DST must be in the selected destination folder with the DWGs.");
            if (!String.Equals(Path.GetExtension(dstPath), ".dst", StringComparison.OrdinalIgnoreCase))
                throw new DstException("OUTPUT_EXTENSION", "The output filename must end in .dst.");
            ValidateWindowsFileName(Path.GetFileName(dstPath));
            if (String.Equals(dstPath, SourceFilePath, StringComparison.OrdinalIgnoreCase))
                throw new DstException("SOURCE_OVERWRITE", "The output DST must be a separate file. The original DST is preserved.");
            if (mapping == null) throw new DstException("MAPPING", "A filename mapping is required.");
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in mapping)
            {
                if (String.IsNullOrEmpty(pair.Key) || names.ContainsKey(pair.Key))
                    throw new DstException("MAPPING", "The mapping contains an empty or duplicate source path.");
                names.Add(pair.Key, pair.Value);
            }
            if (names.Count != drawings.Count || drawings.Any(x => !names.ContainsKey(x.SourcePath)))
                throw new DstException("MAPPING", "Provide exactly one target filename for every listed DWG.");
            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, string>> replacements = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Drawing drawing in drawings)
            {
                string name = names[drawing.SourcePath];
                ValidateDrawingName(name);
                if (!targets.Add(name))
                    throw new DstException("TARGET_COLLISION", "Two drawings would have the same destination filename: " + name);
                string absolute = Path.GetFullPath(Path.Combine(folder, name));
                replacements.Add(drawing.SourcePath, new Dictionary<string, string>(StringComparer.Ordinal) {
                    { "FileName", absolute },
                    { "Relative_FileName", ".\\" + name },
                    { "Unc_FileName", ResolveUniversalName(absolute) }
                });
            }
            List<Edit> edits = new List<Edit>();
            Dictionary<string, Dictionary<string, string>> expectedByReference = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (LayoutRecord layout in layouts)
            {
                Dictionary<string, string> values = replacements[layout.SourcePath];
                expectedByReference.Add(layout.Id, values);
                foreach (FieldSpan field in layout.Fields)
                    edits.Add(new Edit { Start = field.ByteStart, Length = field.ByteLength, NewXml = StrictUtf8.GetBytes(EscapeText(values[field.Name])) });
            }
            edits.Sort((a, b) => a.Start.CompareTo(b.Start));
            long size = sourceBytes.Length + edits.Sum(x => (long)x.NewXml.Length - x.Length);
            if (size > DstCodec.MaximumFileBytes) throw new DstException("DST_SIZE", "The relinked DST would exceed 64 MB.");
            byte[] encoded, expectedXml;
            using (MemoryStream output = new MemoryStream((int)size))
            using (MemoryStream expected = new MemoryStream((int)size))
            {
                int position = 0;
                foreach (Edit edit in edits)
                {
                    if (edit.Start < position) throw new DstException("VERIFY_SPANS", "Overlapping XML edit spans were detected.");
                    int unchanged = edit.Start - position;
                    // Copy the original ENCODED bytes, including alternative LF representations.
                    output.Write(sourceBytes, position, unchanged);
                    expected.Write(xmlBytes, position, unchanged);
                    byte[] replacement = DstCodec.EncodeBytes(edit.NewXml);
                    output.Write(replacement, 0, replacement.Length);
                    expected.Write(edit.NewXml, 0, edit.NewXml.Length);
                    position = edit.Start + edit.Length;
                }
                output.Write(sourceBytes, position, sourceBytes.Length - position);
                expected.Write(xmlBytes, position, xmlBytes.Length - position);
                encoded = output.ToArray();
                expectedXml = expected.ToArray();
            }
            byte[] finalXml = DstCodec.DecodeBytes(encoded);
            if (!finalXml.SequenceEqual(expectedXml))
                throw new DstException("VERIFY_ENCODING", "The output failed its encoding verification.");
            XmlDocument finalDocument = ParseXml(finalXml);
            VerifyStructure(xmlDocument, finalDocument, expectedByReference);
            return encoded;
        }

        private static XmlDocument ParseXml(byte[] bytes)
        {
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    IgnoreComments = false, IgnoreWhitespace = false,
                    MaxCharactersInDocument = DstCodec.MaximumFileBytes
                };
                using (MemoryStream input = new MemoryStream(bytes, false))
                using (XmlReader reader = XmlReader.Create(input, settings))
                {
                    int count = 0;
                    while (reader.Read())
                    {
                        if (reader.Depth > MaximumDepth) throw new DstException("DST_DEPTH", "The DST nesting is too deep.");
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (++count > MaximumElements) throw new DstException("DST_ELEMENTS", "The DST contains too many XML elements.");
                            if (reader.NamespaceURI.Length != 0 || reader.Prefix.Length != 0)
                                throw new DstException("DST_SCHEMA", "Namespaced DST schemas are not supported.");
                        }
                    }
                }
                XmlDocument document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
                using (MemoryStream input = new MemoryStream(bytes, false))
                using (XmlReader reader = XmlReader.Create(input, settings)) document.Load(reader);
                return document;
            }
            catch (XmlException ex) { throw new DstException("DST_XML", "The DST contains malformed or unsupported XML: " + ex.Message, ex); }
        }

        // Lexically index real element bodies without reserializing XML or matching commented-out tags.
        private static Dictionary<XmlElement, RawSpan> IndexRawElements(string text, XmlDocument document)
        {
            XmlNodeList elements = document.GetElementsByTagName("*");
            Dictionary<XmlElement, RawSpan> result = new Dictionary<XmlElement, RawSpan>();
            Stack<XmlElement> stack = new Stack<XmlElement>();
            int nextElement = 0, position = 0;
            while (position < text.Length)
            {
                int start = text.IndexOf('<', position);
                if (start < 0) break;
                if (At(text, start, "<!--")) { position = EndAfter(text, start + 4, "-->"); continue; }
                if (At(text, start, "<![CDATA[")) { position = EndAfter(text, start + 9, "]]>"); continue; }
                if (At(text, start, "<?")) { position = EndAfter(text, start + 2, "?>"); continue; }
                if (At(text, start, "<!")) throw new DstException("DST_XML", "Unsupported XML declaration.");
                bool closing = start + 1 < text.Length && text[start + 1] == '/';
                int nameStart = start + (closing ? 2 : 1), nameEnd = nameStart;
                while (nameEnd < text.Length && !Char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '/' && text[nameEnd] != '>') nameEnd++;
                string name = text.Substring(nameStart, nameEnd - nameStart);
                int end = FindTagEnd(text, nameEnd);
                if (closing)
                {
                    if (stack.Count == 0 || stack.Peek().Name != name) throw new DstException("DST_XML", "Mismatched XML element span.");
                    XmlElement element = stack.Pop();
                    result[element].ContentEnd = start;
                }
                else
                {
                    if (nextElement >= elements.Count || elements[nextElement].Name != name)
                        throw new DstException("DST_XML", "XML elements could not be indexed safely.");
                    XmlElement element = (XmlElement)elements[nextElement++];
                    bool selfClosing = end > start && text[end - 1] == '/';
                    result.Add(element, new RawSpan { ContentStart = end + 1, ContentEnd = end + 1, SelfClosing = selfClosing });
                    if (!selfClosing) stack.Push(element);
                }
                position = end + 1;
            }
            if (stack.Count != 0 || nextElement != elements.Count) throw new DstException("DST_XML", "Incomplete XML span index.");
            return result;
        }
        private static bool At(string text, int at, string value)
        {
            return at + value.Length <= text.Length && String.CompareOrdinal(text, at, value, 0, value.Length) == 0;
        }
        private static int EndAfter(string text, int start, string terminator)
        {
            int end = text.IndexOf(terminator, start, StringComparison.Ordinal);
            if (end < 0) throw new DstException("DST_XML", "Unterminated XML content.");
            return end + terminator.Length;
        }
        private static int FindTagEnd(string text, int start)
        {
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '\'' || c == '"') quote = c;
                else if (c == '>') return i;
            }
            throw new DstException("DST_XML", "Unterminated XML tag.");
        }
        private static int ByteOffset(string text, int target, ref int character, ref int bytes)
        {
            if (target < character) throw new DstException("VERIFY_SPANS", "XML offsets are not in order.");
            while (character < target)
            {
                char value = text[character++];
                if (value < 0x80) bytes++;
                else if (value < 0x800) bytes += 2;
                else if (Char.IsHighSurrogate(value))
                {
                    if (character >= text.Length || !Char.IsLowSurrogate(text[character]) || character >= target)
                        throw new DstException("DST_UTF8", "An edit boundary splits a Unicode character.");
                    character++; bytes += 4;
                }
                else bytes += 3;
            }
            return bytes;
        }
        private static List<XmlElement> Children(XmlElement owner, string name)
        {
            return owner.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().Where(x => x.Name == name).ToList();
        }
        private static string RequireId(XmlElement element)
        {
            string value = element.GetAttribute("ID");
            if (String.IsNullOrWhiteSpace(value)) throw new DstException("DST_SCHEMA", "A sheet or layout reference has no ID.");
            return value;
        }
        private static Dictionary<string, XmlElement> Properties(XmlElement owner)
        {
            Dictionary<string, XmlElement> result = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (XmlElement property in Children(owner, "AcSmProp"))
            {
                string name = property.GetAttribute("propname");
                if (String.IsNullOrEmpty(name) || result.ContainsKey(name))
                    throw new DstException("DST_SCHEMA", "A layout reference contains an empty or duplicate property name.");
                result.Add(name, property);
            }
            return result;
        }
        private static void RequireTextOnly(XmlElement property)
        {
            if (property.GetAttribute("vt") != "8")
                throw new DstException("DST_SCHEMA", "A path property is not a string (vt=8).");
            foreach (XmlNode child in property.ChildNodes)
                if (child.NodeType != XmlNodeType.Text && child.NodeType != XmlNodeType.Whitespace && child.NodeType != XmlNodeType.SignificantWhitespace)
                    throw new DstException("DST_SCHEMA", "Path properties must contain plain XML text, not CDATA, comments or nested elements.");
        }
        private static string EscapeText(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
        private static string ResolveStoredPath(string value, string folder)
        {
            if (value.IndexOf('\0') >= 0 || value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new DstException("DWG_PATH", "Device paths are not supported.");
            if (value.Length >= 2 && value[1] == ':' && !IsFullyQualified(value))
                throw new DstException("DWG_PATH", "Drive-relative paths such as C:drawing.dwg are not supported.");
            if (Path.IsPathRooted(value) && !IsFullyQualified(value))
                throw new DstException("DWG_PATH", "A stored DWG path is rooted without a drive or network share.");
            try { return Path.GetFullPath(IsFullyQualified(value) ? value : Path.Combine(folder, value)); }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is PathTooLongException)) throw;
                throw new DstException("DWG_PATH", "A stored DWG path is invalid or too long.", ex);
            }
        }
        private static bool IsFullyQualified(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string[] parts = path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2;
            }
            return path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }
        private static string NormalizeAbsoluteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !IsFullyQualified(path))
                throw new DstException("TARGET_FOLDER", "Choose an absolute destination folder.");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        public static void ValidateDrawingName(string name)
        {
            ValidateWindowsFileName(name);
            if (!String.Equals(Path.GetExtension(name), ".dwg", StringComparison.OrdinalIgnoreCase))
                throw new DstException("DWG_EXTENSION", "Every target drawing filename must end in .dwg.");
        }
        private static void ValidateWindowsFileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 255 || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal))
                throw new DstException("FILE_NAME", "Use a filename of 1–255 characters without leading/trailing spaces or a trailing dot.");
            if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name.Any(c => c < 32))
                throw new DstException("FILE_NAME", "A target filename contains a folder separator or a character Windows does not allow.");
            string device = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(device) ||
                ((device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                 device.Length == 4 && "123456789\u00B9\u00B2\u00B3".IndexOf(device[3]) >= 0))
                throw new DstException("FILE_NAME", "A target filename uses a reserved Windows device name.");
        }
        private static void VerifyStructure(XmlDocument before, XmlDocument after, Dictionary<string, Dictionary<string, string>> expected)
        {
            // The encoded-span construction protects lexical bytes; this independently checks the parsed object tree.
            Stack<Tuple<XmlNode, XmlNode>> pending = new Stack<Tuple<XmlNode, XmlNode>>();
            pending.Push(Tuple.Create((XmlNode)before, (XmlNode)after));
            int checkedPaths = 0;
            while (pending.Count > 0)
            {
                Tuple<XmlNode, XmlNode> pair = pending.Pop();
                XmlNode oldNode = pair.Item1, newNode = pair.Item2;
                if (oldNode.NodeType != newNode.NodeType || oldNode.Name != newNode.Name)
                    throw new DstException("VERIFY_STRUCTURE", "An XML object or its order changed unexpectedly.");
                if (oldNode.Attributes != null)
                {
                    if (newNode.Attributes == null || oldNode.Attributes.Count != newNode.Attributes.Count)
                        throw new DstException("VERIFY_STRUCTURE", "XML attributes changed unexpectedly.");
                    for (int i = 0; i < oldNode.Attributes.Count; i++)
                        if (oldNode.Attributes[i].Name != newNode.Attributes[i].Name || oldNode.Attributes[i].Value != newNode.Attributes[i].Value)
                            throw new DstException("VERIFY_STRUCTURE", "An object attribute, ID or handle changed unexpectedly.");
                }
                Dictionary<string, string> desired;
                string propertyName = oldNode is XmlElement ? ((XmlElement)oldNode).GetAttribute("propname") : "";
                XmlElement parent = oldNode.ParentNode as XmlElement;
                if (oldNode.Name == "AcSmProp" && parent != null && parent.Name == "AcSmAcDbLayoutReference" &&
                    expected.TryGetValue(parent.GetAttribute("ID"), out desired) && desired.ContainsKey(propertyName))
                {
                    RequireTextOnly((XmlElement)newNode);
                    if (newNode.InnerText != desired[propertyName])
                        throw new DstException("VERIFY_PATH", "A saved path differs from the requested mapping.");
                    checkedPaths++;
                    continue;
                }
                if (oldNode.Value != newNode.Value || oldNode.ChildNodes.Count != newNode.ChildNodes.Count)
                    throw new DstException("VERIFY_PRESERVATION", "A protected XML value changed unexpectedly.");
                for (int i = oldNode.ChildNodes.Count - 1; i >= 0; i--)
                    pending.Push(Tuple.Create(oldNode.ChildNodes[i], newNode.ChildNodes[i]));
            }
            if (checkedPaths != expected.Count * 3)
                throw new DstException("VERIFY_COUNT", "The number of verified path values is incorrect.");
        }
        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create())
                return String.Concat(hash.ComputeHash(bytes).Select(x => x.ToString("x2")));
        }
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetUniversalName(string localPath, int informationLevel, IntPtr buffer, ref int bufferSize);
        private static string ResolveUniversalName(string absolute)
        {
            if (absolute.StartsWith(@"\\", StringComparison.Ordinal)) return absolute;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 2048;
                buffer = Marshal.AllocHGlobal(size);
                int error = WNetGetUniversalName(absolute, 1, buffer, ref size);
                if (error == 234 && size > 0 && size <= 65536)
                {
                    Marshal.FreeHGlobal(buffer); buffer = Marshal.AllocHGlobal(size);
                    error = WNetGetUniversalName(absolute, 1, buffer, ref size);
                }
                if (error != 0) return "";
                string universal = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer));
                return !String.IsNullOrEmpty(universal) && universal.StartsWith(@"\\", StringComparison.Ordinal) ? universal : "";
            }
            catch (DllNotFoundException) { return ""; }
            catch (EntryPointNotFoundException) { return ""; }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
    }
}
