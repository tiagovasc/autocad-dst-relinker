Download **SheetSetRelinker.exe** below and double-click it. The runtime is included; no installer, Python, Visual Studio, AutoCAD installation, or separate .NET setup is needed to run the utility.

This Windows x64 tool updates drawing filenames and folder paths inside an AutoCAD Sheet Set (`.dst`) after a project copy or revision rename. It preserves the sheets, subsets, layout identifiers and other metadata. Drag in a DST, choose the destination, review the filename mapping, validate, and create a separate updated DST. Optional copy mode copies each referenced DWG once and verifies its hash.

Includes progress, overwrite protection, missing-file and collision checks, detailed local logs, a sanitized support report, and embedded license notices. Originals are never replaced.

Validation: 52 automated core, file-operation and native UI checks; a packaged-EXE end-to-end check with installed-runtime lookup disabled; and a read-only regression against a real 134-sheet DST. The underlying approach has one user-confirmed AutoCAD 2027 success. Native AutoCAD opening of outputs from this reusable executable remains a separate user check; automated tests cannot inspect DWG layouts.

Supports Windows 10/11 x64. The executable is unsigned; Windows or an organization's application policy may prompt or block it. ARM64 and x86 are not verified release targets. Use copies of the original DWGs retaining their layouts. This does not modify Xrefs or other references inside drawings.

See the [README](https://github.com/tiagovasc/autocad-dst-relinker#readme) for instructions and [troubleshooting](https://github.com/tiagovasc/autocad-dst-relinker/blob/main/docs/TROUBLESHOOTING.md) for logs and diagnostics.
