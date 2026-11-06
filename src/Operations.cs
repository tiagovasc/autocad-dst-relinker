using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SheetSetRelinker
{
    public sealed class Job
    {
        public DstDocument Document { get; set; }
        public string InputPath { get; set; }
        public string DestinationFolder { get; set; }
        public string OutputName { get; set; }
        public bool CopyDrawings { get; set; }
        public Dictionary<string, string> Mapping { get; set; }
        public Dictionary<string, string> SourceOverrides { get; set; }
        public Job()
        {
            Mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SourceOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class ValidationResult
    {
        public List<string> Issues { get; private set; }
        public Dictionary<string, string> Rows { get; private set; }
        public bool IsValid { get { return Issues.Count == 0; } }
        public ValidationResult()
        {
            Issues = new List<string>();
            Rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Detailed logs remain local. The support report is built from a strict whitelist,
    // rather than trying to redact arbitrary exception messages after the fact.
    public sealed class RunLog : IDisposable
    {
        private readonly object gate = new object();
        private readonly StreamWriter writer;
        private readonly Dictionary<string, long> metrics = new Dictionary<string, long>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private string errorType = "";
        private string errorCode = "";
        private string status = "started";
        private static readonly string[] AllowedMetrics = { "sheets", "subsets", "drawings", "issues", "copied_drawings", "copied_bytes", "output_bytes" };
        public string LogPath { get; private set; }
        public string SupportPath { get; private set; }
        public string RunId { get; private set; }
        public static RunLog Create()
        {
            return Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheetSetRelinker", "Logs"));
        }
        public static RunLog Create(string directory) { return new RunLog(directory); }
        private RunLog(string directory)
        {
            Directory.CreateDirectory(directory);
            RunId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            LogPath = Path.Combine(directory, RunId + ".log");
            SupportPath = Path.Combine(directory, RunId + "-support.json");
            writer = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.AutoFlush = true;
            Info("START", "Sheet Set Relinker " + Version + "; OS=" + Environment.OSVersion + "; CLR=" + Environment.Version + "; 64bit=" + Environment.Is64BitProcess);
            WriteSupport("started");
        }
        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
        }
        public void Info(string eventName, string message)
        {
            lock (gate)
            {
                writer.WriteLine(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + eventName + " " + message);
            }
        }
        public void Error(Exception error)
        {
            lock (gate)
            {
                errorType = error.GetType().FullName;
                var dstError = error as DstException;
                if (dstError != null && !string.IsNullOrEmpty(dstError.Code) && dstError.Code.Length <= 64 && dstError.Code.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) errorCode = dstError.Code;
                else if (error is OperationCanceledException) errorCode = "CANCELLED";
                else if (error is UnauthorizedAccessException) errorCode = "ACCESS_DENIED";
                else if (error is IOException) errorCode = "IO_ERROR";
                else if (error is ArgumentException) errorCode = "ARGUMENT_ERROR";
                else if (error is InvalidOperationException) errorCode = "INVALID_OPERATION";
                else errorCode = "UNEXPECTED_ERROR";
                Info("ERROR", error.ToString());
                WriteSupport(error is OperationCanceledException ? "cancelled" : "failed");
            }
        }
        public void Metric(string key, long value)
        {
            if (!AllowedMetrics.Contains(key)) throw new ArgumentException("Unknown diagnostic metric.");
            lock (gate) { metrics[key] = value; }
        }
        public void WriteSupport(string newStatus)
        {
            string[] allowed = { "started", "validating", "validation_failed", "ready", "running", "success", "cancelled", "failed" };
            if (!allowed.Contains(newStatus)) throw new ArgumentException("Unknown diagnostic status.");
            lock (gate)
            {
                status = newStatus;
                if (newStatus == "validating")
                {
                    errorType = "";
                    errorCode = "";
                    metrics.Clear();
                }
                var text = new StringBuilder();
                text.Append("{\n  \"application\": \"Sheet Set Relinker\",\n  \"version\": ").Append(Quote(Version));
                text.Append(",\n  \"run_id\": ").Append(Quote(RunId));
                text.Append(",\n  \"status\": ").Append(Quote(status));
                text.Append(",\n  \"os_version\": ").Append(Quote(Environment.OSVersion.Version.ToString()));
                text.Append(",\n  \"runtime\": ").Append(Quote(Environment.Version.ToString()));
                text.Append(",\n  \"elapsed_ms\": ").Append(clock.ElapsedMilliseconds);
                text.Append(",\n  \"error_type\": ").Append(Quote(errorType));
                text.Append(",\n  \"last_error_code\": ").Append(Quote(errorCode));
                text.Append(",\n  \"privacy\": \"No file paths, names, XML, drawing content, hashes or exception messages are included. Review before sharing.\"");
                foreach (var item in metrics.OrderBy(p => p.Key)) text.Append(",\n  ").Append(Quote(item.Key)).Append(": ").Append(item.Value);
                text.Append("\n}\n");
                File.WriteAllText(SupportPath, text.ToString(), new UTF8Encoding(false));
            }
        }
        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }
        public string ReadText()
        {
            using (var reader = new StreamReader(new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8)) return reader.ReadToEnd();
        }
        public void Dispose()
        {
            lock (gate) { writer.Dispose(); }
        }
    }

    public static class Engine
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static ValidationResult Validate(Job job, RunLog log, CancellationToken token)
        {
            if (log == null) throw new ArgumentNullException("log");
            var result = new ValidationResult();
            log.WriteSupport("validating");
            token.ThrowIfCancellationRequested();
            if (job == null || job.Document == null)
            {
                result.Issues.Add("Choose a supported DST file first.");
                return FinishValidation(result, log);
            }
            log.Metric("drawings", job.Document.Drawings.Count);
            log.Metric("sheets", job.Document.SheetCount);
            log.Metric("subsets", job.Document.SubsetCount);
            try
            {
                string input = Full(job.InputPath);
                string destination = Full(job.DestinationFolder);
                string output = OutputPath(job);
                log.Info("VALIDATE", "input=" + input + "; destination=" + destination + "; output=" + output + "; copy=" + job.CopyDrawings);
                if (!Directory.Exists(destination)) throw new IOException("Choose an existing destination folder.");
                if (PathComparer.Equals(input, output)) throw new IOException("The output DST must have a different path from the original.");
                if (File.Exists(output) || Directory.Exists(output)) throw new IOException("The output already exists. Choose a new DST name: " + output);
                using (FileStream stream = OpenRead(input))
                {
                    if (!PathComparer.Equals(Hash(stream), job.Document.SourceSha256)) throw new IOException("The original DST has changed since it was loaded. Load it again.");
                }
                // Core also verifies names, complete mappings, collisions and byte preservation.
                job.Document.Build(destination, output, job.Mapping);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                result.Issues.Add(ex.Message);
                log.Error(ex);
            }
            foreach (Drawing drawing in job.Document.Drawings)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string name;
                    if (job.Mapping == null || !job.Mapping.TryGetValue(drawing.SourcePath, out name)) throw new IOException("Enter a new filename.");
                    ValidateBasename(name, ".dwg");
                    string target = Path.Combine(Full(job.DestinationFolder), name);
                    log.Info("MAPPING", drawing.SourcePath + " -> " + target + "; sheet_references=" + drawing.SheetCount);
                    if (job.CopyDrawings)
                    {
                        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Target already exists; choose another name or use 'DWGs already copied'.");
                        string source = ResolveSource(job, drawing);
                        if (PathComparer.Equals(Full(source), Full(target))) throw new IOException("Source and destination are the same file.");
                        CheckDrawingLocks(source);
                        using (FileStream stream = OpenRead(source)) CheckDwgHeader(stream);
                        result.Rows[drawing.SourcePath] = "Ready to copy";
                        log.Info("SOURCE", "resolved=" + source);
                    }
                    else
                    {
                        if (!File.Exists(target)) throw new FileNotFoundException("Target DWG not found: " + target);
                        CheckDrawingLocks(target);
                        using (FileStream stream = OpenRead(target)) CheckDwgHeader(stream);
                        result.Rows[drawing.SourcePath] = "Found";
                    }
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) throw;
                    result.Rows[drawing.SourcePath] = ex.Message;
                    result.Issues.Add(drawing.FileName + ": " + ex.Message);
                    log.Error(ex);
                }
            }
            return FinishValidation(result, log);
        }

        private static ValidationResult FinishValidation(ValidationResult result, RunLog log)
        {
            log.Metric("issues", result.Issues.Count);
            log.Info("VALIDATION", result.IsValid ? "Passed. DWG signatures checked; DWG layouts are not inspected." : "Failed: " + string.Join(" | ", result.Issues));
            log.WriteSupport(result.IsValid ? "ready" : "validation_failed");
            return result;
        }

        public static string Run(Job job, RunLog log, Action<int, string> progress, CancellationToken token)
        {
            string stage = null;
            var held = new List<FileStream>();
            var created = new List<string>();
            try
            {
                Report(progress, 0, "Checking files...");
                ValidationResult result = Validate(job, log, token);
                if (!result.IsValid) throw new InvalidOperationException("Validation failed. " + string.Join("\n", result.Issues));
                log.WriteSupport("running");
                token.ThrowIfCancellationRequested();
                string destination = Full(job.DestinationFolder);
                string output = OutputPath(job);
                FileStream inputLock = OpenRead(Full(job.InputPath));
                held.Add(inputLock);
                if (!PathComparer.Equals(Hash(inputLock), job.Document.SourceSha256)) throw new IOException("The original DST changed. Load it again.");
                byte[] data = job.Document.Build(destination, output, job.Mapping);
                log.Info("DST_VERIFIED", "source_sha256=" + job.Document.SourceSha256 + "; output_sha256=" + Hash(data) + "; output_bytes=" + data.Length);
                log.Metric("output_bytes", data.Length);
                stage = Path.Combine(destination, ".sheetset-relinker-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                log.Info("STAGING", stage);
                long totalBytes = 0;
                var sources = new Dictionary<string, string>(PathComparer);
                if (job.CopyDrawings)
                {
                    foreach (Drawing drawing in job.Document.Drawings)
                    {
                        string source = ResolveSource(job, drawing);
                        sources.Add(drawing.SourcePath, source);
                        totalBytes += new FileInfo(source).Length;
                    }
                }
                CheckSpace(destination, totalBytes + data.Length, log);
                long copiedBytes = 0;
                int processed = 0;
                foreach (Drawing drawing in job.Document.Drawings)
                {
                    token.ThrowIfCancellationRequested();
                    string target = Path.Combine(destination, job.Mapping[drawing.SourcePath]);
                    if (job.CopyDrawings)
                    {
                        string source = sources[drawing.SourcePath];
                        CheckDrawingLocks(source);
                        string stagedDrawing = Path.Combine(stage, job.Mapping[drawing.SourcePath]);
                        log.Info("COPY_BEGIN", "source=" + source + "; staging=" + stagedDrawing + "; target=" + target);
                        using (FileStream reader = OpenRead(source))
                        {
                            CheckDwgHeader(reader);
                            using (FileStream writer = new FileStream(stagedDrawing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[1024 * 1024];
                                int read;
                                while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    writer.Write(buffer, 0, read);
                                    copiedBytes += read;
                                    int percentage = 10 + (int)(65.0 * copiedBytes / Math.Max(1, totalBytes));
                                    Report(progress, percentage, "Copying " + (processed + 1) + " of " + job.Document.Drawings.Count + " drawings...");
                                }
                                writer.Flush(true);
                            }
                            string sourceHash = Hash(reader);
                            using (FileStream check = OpenRead(stagedDrawing))
                            {
                                if (!PathComparer.Equals(sourceHash, Hash(check))) throw new IOException("Copy verification failed: " + stagedDrawing);
                            }
                            log.Info("COPY_VERIFIED", "sha256=" + sourceHash + "; bytes=" + reader.Length);
                            token.ThrowIfCancellationRequested();
                            // File.Move refuses to overwrite a file created after validation.
                            File.Move(stagedDrawing, target);
                            created.Add(target);
                            FileStream finalDrawing = OpenRead(target);
                            held.Add(finalDrawing);
                            if (!PathComparer.Equals(sourceHash, Hash(finalDrawing))) throw new IOException("Published drawing verification failed: " + target);
                        }
                    }
                    else
                    {
                        CheckDrawingLocks(target);
                        FileStream finalDrawing = OpenRead(target);
                        held.Add(finalDrawing);
                        CheckDwgHeader(finalDrawing);
                    }
                    processed++;
                    if (!job.CopyDrawings) Report(progress, 10 + 65 * processed / Math.Max(1, job.Document.Drawings.Count), "Checking " + processed + " of " + job.Document.Drawings.Count + " drawings...");
                    log.Metric("copied_drawings", job.CopyDrawings ? processed : 0);
                    log.Metric("copied_bytes", copiedBytes);
                }
                token.ThrowIfCancellationRequested();
                Report(progress, 85, "Verifying the updated sheet set...");
                string stagedDst = Path.Combine(stage, "verified-output.dst");
                using (FileStream file = new FileStream(stagedDst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(data, 0, data.Length);
                    file.Flush(true);
                }
                DstDocument reread = DstDocument.Load(stagedDst);
                if (reread.SheetCount != job.Document.SheetCount || reread.SubsetCount != job.Document.SubsetCount || !PathComparer.Equals(reread.SourceSha256, Hash(data))) throw new IOException("Saved DST verification failed.");
                if (!PathComparer.Equals(Hash(inputLock), job.Document.SourceSha256)) throw new IOException("Original DST changed during processing.");
                token.ThrowIfCancellationRequested();
                File.Move(stagedDst, output);
                created.Add(output);
                using (FileStream file = OpenRead(output))
                {
                    if (!PathComparer.Equals(Hash(file), Hash(data))) throw new IOException("Final output verification failed.");
                }
                log.Info("SUCCESS", "output=" + output + "; original unchanged; sheets=" + reread.SheetCount + "; subsets=" + reread.SubsetCount);
                log.WriteSupport("success");
                Report(progress, 100, "Updated DST created. Open it in AutoCAD to check the sheets.");
                // Only our now-empty temporary directory is removed, never user files.
                try { Directory.Delete(stage, false); } catch (IOException) { log.Info("STAGING_RETAINED", stage); } catch (UnauthorizedAccessException) { log.Info("STAGING_RETAINED", stage); }
                return output;
            }
            catch (Exception ex)
            {
                log.Error(ex);
                log.Info("RECOVERY", "Originals were not edited. Staging=" + (stage ?? "not created") + "; created outputs=" + string.Join(" | ", created));
                log.WriteSupport(ex is OperationCanceledException ? "cancelled" : "failed");
                throw;
            }
            finally
            {
                foreach (FileStream file in held) file.Dispose();
            }
        }

        public static string OutputPath(Job job)
        {
            ValidateBasename(job.OutputName, ".dst");
            return Path.Combine(Full(job.DestinationFolder), job.OutputName);
        }
        public static string ResolveSource(Job job, Drawing drawing)
        {
            string selected;
            if (job.SourceOverrides != null && job.SourceOverrides.TryGetValue(drawing.SourcePath, out selected) && !string.IsNullOrWhiteSpace(selected))
            {
                selected = Full(selected);
                if (!File.Exists(selected)) throw new FileNotFoundException("Selected source DWG not found: " + selected);
                return selected;
            }
            var existing = drawing.SourceCandidates.Where(File.Exists).Select(Full).Distinct(PathComparer).ToList();
            if (existing.Count == 0) throw new FileNotFoundException("Source DWG not found. Select its actual location. Tried: " + string.Join("; ", drawing.SourceCandidates));
            if (existing.Count > 1)
            {
                // A mapped drive and its UNC spelling can identify the same file.
                // Compare actual Windows identities, not content hashes: two separate,
                // byte-identical copies are still ambiguous and need a user selection.
                string firstIdentity;
                bool sameFile = TryFileIdentity(existing[0], out firstIdentity);
                for (int i = 1; sameFile && i < existing.Count; i++)
                {
                    string identity;
                    sameFile = TryFileIdentity(existing[i], out identity) && PathComparer.Equals(firstIdentity, identity);
                }
                if (!sameFile) throw new IOException("Multiple distinct source files found, or their identity could not be verified. Select the intended source DWG explicitly.");
            }
            return existing[0];
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInformation
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh;
            public uint AccessLow, AccessHigh;
            public uint WriteLow, WriteHigh;
            public uint VolumeSerial;
            public uint SizeHigh, SizeLow;
            public uint Links;
            public uint IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
        private static bool TryFileIdentity(string path, out string identity)
        {
            identity = null;
            try
            {
                using (FileStream file = OpenRead(path))
                {
                    NativeFileInformation information;
                    if (!GetFileInformationByHandle(file.SafeFileHandle, out information) ||
                        (information.IndexHigh == 0 && information.IndexLow == 0)) return false;
                    string finalPath = FinalHandlePath(file.SafeFileHandle, 0);
                    if (finalPath == null) finalPath = FinalHandlePath(file.SafeFileHandle, 8); // FILE_NAME_OPENED fallback.
                    if (finalPath == null) return false;
                    if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                        finalPath = @"\\" + finalPath.Substring(8);
                    else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                        finalPath = finalPath.Substring(4);
                    string volume = Path.GetPathRoot(finalPath);
                    if (String.IsNullOrEmpty(volume)) return false;
                    // Scope serial/file IDs to the resolved local volume or server/share:
                    // unrelated servers may reuse the same serial and file-index numbers.
                    identity = volume.TrimEnd('\\', '/') + "|" + information.VolumeSerial.ToString("X8") +
                        "|" + information.IndexHigh.ToString("X8") + information.IndexLow.ToString("X8");
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
        private static string FinalHandlePath(SafeFileHandle handle, uint flags)
        {
            StringBuilder path = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, flags);
            if (length == 0) return null;
            if (length >= path.Capacity)
            {
                if (length > 32768) return null;
                path = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, flags);
                if (length == 0 || length >= path.Capacity) return null;
            }
            return path.ToString();
        }
        private static void ValidateBasename(string name, string extension)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" ") || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Enter a valid " + extension + " filename without a folder path.");
            string stem = name.Split('.')[0].ToUpperInvariant();
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reserved.Contains(stem)) throw new ArgumentException("This filename is reserved by Windows.");
        }
        private static string Full(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a folder or file path.");
            return Path.GetFullPath(path);
        }
        private static FileStream OpenRead(string path) { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        private static void CheckDwgHeader(FileStream stream)
        {
            stream.Position = 0;
            byte[] header = new byte[6];
            int count = stream.Read(header, 0, 6);
            stream.Position = 0;
            if (count != 6 || header[0] != 65 || header[1] != 67 || header[2] != 49 || header[3] != 48 || header[4] < 48 || header[4] > 57 || header[5] < 48 || header[5] > 57) throw new IOException("The file does not have a supported DWG signature.");
        }
        private static void CheckDrawingLocks(string path)
        {
            if (File.Exists(Path.ChangeExtension(path, ".dwl")) || File.Exists(Path.ChangeExtension(path, ".dwl2"))) throw new IOException("Drawing lock file found. Close the drawing in AutoCAD before continuing: " + path);
        }
        private static string Hash(byte[] value)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }
        private static string Hash(FileStream stream)
        {
            stream.Position = 0;
            string result;
            using (SHA256 hash = SHA256.Create()) result = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            stream.Position = 0;
            return result;
        }
        private static void Report(Action<int, string> progress, int percentage, string message) { if (progress != null) progress(percentage, message); }
        private static void CheckSpace(string destination, long required, RunLog log)
        {
            string root = Path.GetPathRoot(destination);
            if (root.StartsWith("\\\\", StringComparison.Ordinal)) { log.Info("SPACE", "Network free-space precheck unavailable; write errors will be reported."); return; }
            try
            {
                long free = new DriveInfo(root).AvailableFreeSpace;
                log.Info("SPACE", "required=" + required + "; available=" + free);
                if (free < required) throw new IOException("There is not enough free space in the destination.");
            }
            catch (ArgumentException) { log.Info("SPACE", "Free-space precheck unavailable."); }
        }
    }
}
