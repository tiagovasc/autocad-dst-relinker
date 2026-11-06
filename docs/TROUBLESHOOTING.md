# Troubleshooting Sheet Set Relinker

Start with the error displayed in the window and **View log**. Keep the original DST and DWGs available. A successfully written DST means the utility completed its structural checks; it does not prove that AutoCAD can resolve every layout or external project resource.

## The application does not open

Use `SheetSetRelinker.exe` from the Windows x64 release or a build you trust. Double-click it; the release contains its own .NET runtime and needs no installation, Python, Visual Studio, or separately installed .NET. It targets Windows 10/11 x64. x86 and ARM64 have not been verified.

The executable is unsigned, so Windows may display a publisher warning. If Windows, antivirus, or an organization's application-control policy blocks it, record the exact message and contact the appropriate administrator; do not disable protection to troubleshoot.

If a copy asks you to install .NET, confirm you downloaded the self-contained release. The optional developer `build.ps1` output is a different, Framework-dependent build. The runtime may extract its bundled native components to your user temporary area on first launch, so that location must be writable and allowed by your organization's application policy.

If the window never appears, include the application version, Windows version, and exact startup error in your report. An application that did not start may not have created a log.

## The DST is rejected

An unknown byte encoding, malformed XML, unsupported structure, or ambiguous layout reference should stop the operation. This is deliberate: a partial or guessed rewrite could reconnect the wrong drawing.

1. Confirm that the file is a Sheet Set Manager `.dst`, rather than a renamed DWG, shortcut, or backup of a different format.
2. Check that the original opens correctly in AutoCAD.
3. Close the sheet set and retry with a separate copy of the original.
4. If it still fails, keep the exact error and application version. Report the AutoCAD version and whether the file was created or last saved by another CAD product.

Do not use a text editor or a general binary search-and-replace tool to bypass the rejection.

## A drawing is missing

| Mode | What must exist |
| --- | --- |
| **DWGs already copied** | Every mapped **new filename** in the selected destination folder. |
| **Copy DWGs for me** | Every referenced **original DWG** at an accessible source location. The new destination filenames must be available for creation. |

Check the full path in the local log, not just the basename. Common causes are a misspelled revision suffix, an extra space, a file in a subfolder, a disconnected mapped drive, or a network share that the current Windows account cannot access.

Open the same location in File Explorer using the same account. Reconnect an approved drive or choose an accessible folder. Do not assume a drive letter such as `P:` identifies the same share on every computer. In copy mode, use **Locate…** on a row to select the correct source DWG when its stored source is missing or ambiguous. Alternatively, prepare the drawings yourself and use **DWGs already copied**.

The destination mapping is one folder plus each new filename. A matching filename confirms existence only; it does not confirm the DWG's layout content.

## Invalid names, duplicates, or existing output

Give each distinct source DWG a distinct new filename. Do not map two different drawings to the same destination. New names must be filenames ending in `.dwg`, not full paths or nested folders. Avoid Windows reserved device names, invalid characters, and trailing spaces or dots.

If a copy destination or output DST already exists, choose a new name or a new destination folder. The tool refuses to overwrite it. If the DWGs are already correct and in place, choose **DWGs already copied** rather than asking the tool to copy over them.

## Access denied, locked files, or interrupted operations

Close any application holding the relevant file and confirm you can create files in the destination. The destination folder must already exist. Check available disk space and, for shared folders, network connectivity. If you can write locally but not to a shared folder, resolve the share permissions before retrying.

After a failure, inspect the log and destination before retrying. Some drawing copies may have completed before a later step failed. An incomplete operation is not a usable new revision merely because files appear in the destination. Keep those files until you understand the logged outcome, then retry with a fresh destination or use the already-copied mode if all required drawings are present and correct.

## The new DST opens, but a sheet fails or shows the wrong drawing

First confirm you opened the **new** DST and that AutoCAD resolves its sheets to the new folder. A successful rename does not change the identity of a layout inside a DWG.

The replacement drawings must be copies retaining the layout names and internal layout handles expected by the source sheet set. Independently recreated drawings, deleted/recreated layouts, and similarly named layouts can break that relationship. Use an appropriate original copy, or repair that specific sheet/layout association in AutoCAD.

The tool does not parse DWG contents, so it cannot verify layout handles, page content, title-block fields, or drawing validity. Check at least one sheet from every referenced drawing and verify the full sheet list before delivery.

## Xrefs, data links, templates, or publishing still use old paths

Those are separate references. Sheet Set Relinker changes the sheet-to-DWG file references in the DST only. It does not edit Xrefs or other dependencies inside drawings, and it does not update unrelated DST publishing, template, page-setup, or support-resource references. Inspect those resources through the relevant AutoCAD tools and your normal project-copy workflow.

## Logs and safe support reports

Keep the detailed local log for diagnosis. Logs are stored in `%LOCALAPPDATA%\SheetSetRelinker\Logs`; use **View log** to open the current log. It may contain source and destination paths, filenames, sheet information, exception messages, and operation details. These can reveal client names, usernames, server/share names, and project locations.

Click **Save support report** to export a sanitized report, then open and review it before sharing. Also review screenshots: visible title bars, paths, and filenames can disclose the same information. Do not attach original DWGs, DSTs, decoded DST XML, or full logs to a public issue unless you have intentionally prepared a non-confidential example.

Include this information in an issue or an LLM troubleshooting request:

```text
Application version:
Windows version:
AutoCAD/CAD product and version:
Mode: DWGs already copied / Copy DWGs for me
Stage that failed: load / validate / copy / write / open in CAD / publish
Exact error, with private details redacted:
Did the original DST open correctly in CAD?
Were the replacement DWGs copied from the original drawings?
Were any layouts deleted, recreated, or renamed?
Local disk / mapped drive / UNC share:
Expected result:
Actual result:
Smallest non-confidential reproduction:
Reviewed sanitized report attached: yes / no
```

For an LLM, add: **“Diagnose from this report. Distinguish verified facts from guesses. Do not overwrite originals, edit DST binary text directly, or assume a matching DWG filename proves a matching layout.”**

## Optional packaged diagnostic

This is a troubleshooting check, not a normal setup step. The release executable can test itself without a real project or an AutoCAD installation.

From PowerShell in the folder containing the executable, choose a fresh diagnostic folder:

```powershell
.\SheetSetRelinker.exe --self-test 'D:\Temp\RelinkerCheck'
```

The command runs without the normal application window. It creates a new `self-test-*` subfolder under the folder you selected. Read its `result.txt` for `PASS` or `FAIL`; `ui.png` is a rendered native window from a successful run. The test creates synthetic DST/DWG fixtures, exercises both modes, checks missing files and output conflicts, and compares original/copy hashes. It does not read or alter your project files.

The generated `.dwg` files contain only test signatures and cannot be used as real CAD drawings. A passing report demonstrates the application's packaged workflow on that machine, not successful AutoCAD opening or publishing. Review the diagnostic report and generated logs before sharing them; they can include the executable location and exception details.

## Recovering the original state

Open the original DST with its original drawings in their original locations. The utility creates separate outputs and does not edit those source files. Keep the unsuccessful output separate while investigating; do not replace your working project with it.
