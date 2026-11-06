using System;
using System.IO;
using System.Windows.Forms;

public static class TestMain
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "ssr-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            int count = CoreTests.RunAll(root);
            count += OperationTests.RunAll(root);
            count += UiTests.RunAll(root);
            string result = "PASS: " + count + " tests. AutoCAD opening/layout tests not performed by this suite.";
            Console.WriteLine(result); Console.WriteLine("Evidence: " + root);
            File.WriteAllText(Path.Combine(root, "result.txt"), result);
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); File.WriteAllText(Path.Combine(root, "failure.txt"), e.ToString()); return 1; }
    }
}
