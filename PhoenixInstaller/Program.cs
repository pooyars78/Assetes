using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhoenixInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = args.Length == 0 ? DetectOptions() : ParseArguments(args);
            return Install(options);
        }
        catch (Exception ex)
        {
            NativeDialogs.Show("Phoenix Update Installer could not continue.\n\n" + ex.Message,
                "Phoenix Update Installer", NativeDialogs.Ok | NativeDialogs.IconError);
            return 1;
        }
    }

    private static InstallerOptions DetectOptions()
    {
        var candidateDataFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDataFolderFromFilesDirectory(AppContext.BaseDirectory, candidateDataFolders);
        AddDataFolderFromFilesDirectory(Environment.CurrentDirectory, candidateDataFolders);

        var terminalDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
        if (Directory.Exists(terminalDataRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(terminalDataRoot))
                if (Directory.Exists(Path.Combine(directory, "MQL5", "Files")))
                    candidateDataFolders.Add(Path.GetFullPath(directory));
        }

        var updates = candidateDataFolders
            .SelectMany(dataPath =>
            {
                var filesPath = Path.Combine(dataPath, "MQL5", "Files");
                return Directory.Exists(filesPath)
                    ? Directory.EnumerateFiles(filesPath, "Phoenix*_B*.datx5", SearchOption.TopDirectoryOnly)
                    : Enumerable.Empty<string>();
            })
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^Phoenix.*_B\d+\.datx5$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (updates.Length == 0)
            throw new FileNotFoundException("No Phoenix update .datx5 file was found. Keep the downloaded Phoenix*_B####.datx5 file in the MetaTrader MQL5\\Files folder.");
        if (updates.Length > 1)
            throw new InvalidOperationException("More than one Phoenix update file was found in MetaTrader data folders. Keep only the one update you want to install, then run the installer again.");

        var source = updates[0];
        var dataPath = Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(source)!)!.FullName)!.FullName;
        var match = Regex.Match(Path.GetFileName(source), @"^(?<target>.+)_B\d+\.datx5$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["target"].Value.StartsWith("Phoenix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update filename is not in the expected Phoenix build format.");

        var terminals = Process.GetProcessesByName("terminal64")
            .Select(p => new { Process = p, Path = TryGetExecutablePath(p) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
            .ToArray();

        if (terminals.Length == 0)
            throw new InvalidOperationException("MetaTrader 5 is not running. Start the terminal that owns this update, then run the installer again.");

        var terminalPaths = terminals.Select(x => Path.GetFullPath(x.Path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terminalPaths.Length != 1 || terminals.Length != 1)
            throw new InvalidOperationException("More than one MetaTrader terminal process is running. Close the extra terminals and run the installer again so the update is applied to the correct terminal.");

        return new InstallerOptions(dataPath, terminalPaths[0], Path.GetFileName(source), match.Groups["target"].Value + ".ex5", null);
    }

    private static void AddDataFolderFromFilesDirectory(string directory, HashSet<string> results)
    {
        var full = Path.GetFullPath(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var info = new DirectoryInfo(full);
        if (info.Name.Equals("Files", StringComparison.OrdinalIgnoreCase) &&
            info.Parent?.Name.Equals("MQL5", StringComparison.OrdinalIgnoreCase) == true &&
            info.Parent.Parent is not null)
            results.Add(info.Parent.Parent.FullName);
    }

    private static string? TryGetExecutablePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    private static InstallerOptions ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("Invalid installer arguments.");
            var key = args[i][2..];
            if (key is not ("data" or "terminal" or "source" or "target" or "sha256"))
                throw new ArgumentException("Unknown installer argument: " + key);
            if (!values.TryAdd(key, args[++i]))
                throw new ArgumentException("Duplicate installer argument: " + key);
        }

        string Required(string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException("Missing required argument: --" + key);

        return new InstallerOptions(
            Path.GetFullPath(Required("data")),
            Path.GetFullPath(Required("terminal")),
            SafeFileName(Required("source"), ".datx5"),
            SafeFileName(Required("target"), ".ex5"),
            values.GetValueOrDefault("sha256"));
    }

    private static string SafeFileName(string value, string extension)
    {
        if (value != Path.GetFileName(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid file name: " + value);
        return value;
    }

    private static int Install(InstallerOptions options)
    {
        var source = Path.Combine(options.DataPath, "MQL5", "Files", options.SourceFile);
        var experts = Path.Combine(options.DataPath, "MQL5", "Experts");
        var destination = Path.Combine(experts, options.TargetFile);

        if (!File.Exists(source))
            throw new FileNotFoundException("The downloaded update file was not found.", source);
        if (new FileInfo(source).Length <= 0)
            throw new InvalidDataException("The downloaded update file is empty.");
        if (!File.Exists(options.TerminalExe))
            throw new FileNotFoundException("MetaTrader terminal executable was not found.", options.TerminalExe);
        if (!Directory.Exists(experts))
            throw new DirectoryNotFoundException("The MetaTrader MQL5\\Experts folder was not found.");

        if (!string.IsNullOrWhiteSpace(options.Sha256))
        {
            var expected = options.Sha256.Trim();
            if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("The expected SHA-256 value is invalid.");
            using var stream = File.OpenRead(source);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 verification failed. The existing EA was not changed.");
        }

        var confirmation = NativeDialogs.Show(
            "A Phoenix update is ready to install.\n\nMetaTrader 5 must close briefly so the EA file can be replaced. After installation, this installer will reopen the same terminal.\n\nDo you want to close MetaTrader 5 and install the update?",
            "Phoenix XAU — Update Available",
            NativeDialogs.YesNo | NativeDialogs.IconQuestion | NativeDialogs.DefaultButton2);

        if (confirmation != NativeDialogs.Yes)
        {
            NativeDialogs.Show("Update cancelled. MetaTrader 5 was not closed and no files were changed.",
                "Phoenix Update Installer", NativeDialogs.Ok | NativeDialogs.IconInformation);
            return 2;
        }

        CloseMatchingTerminal(options.TerminalExe);
        var backup = destination + ".phoenix-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var hadOriginal = File.Exists(destination);
        var staged = destination + ".phoenix-new";

        try
        {
            if (hadOriginal) File.Copy(destination, backup, false);
            File.Copy(source, staged, true);
            var length = new FileInfo(source).Length;
            if (new FileInfo(staged).Length != length)
                throw new IOException("The staged update size does not match the downloaded file.");
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(staged, destination);
            if (new FileInfo(destination).Length != length)
                throw new IOException("The installed EA size does not match the downloaded file.");
            File.Delete(source);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = options.TerminalExe,
                    WorkingDirectory = Path.GetDirectoryName(options.TerminalExe)!,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                NativeDialogs.Show("The update was installed successfully, but MetaTrader could not be reopened automatically.\n\n" +
                    ex.Message + "\n\nPlease start MetaTrader manually.",
                    "Phoenix Update Installed", NativeDialogs.Ok | NativeDialogs.IconWarning);
                return 0;
            }

            NativeDialogs.Show("Phoenix update installed successfully. MetaTrader 5 is starting.",
                "Phoenix Update Complete", NativeDialogs.Ok | NativeDialogs.IconInformation);
            return 0;
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            if (hadOriginal && File.Exists(backup))
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Copy(backup, destination, true);
            }
            throw;
        }
    }

    private static void CloseMatchingTerminal(string executable)
    {
        var expected = Path.GetFullPath(executable);
        var name = Path.GetFileNameWithoutExtension(executable);
        var matches = Process.GetProcessesByName(name).Where(p => IsSameExecutable(p, expected)).ToArray();

        foreach (var process in matches)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (!matches.Any(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            }))
                return;
            Thread.Sleep(500);
        }

        throw new TimeoutException("MetaTrader did not close within 60 seconds. No update files were replaced. Please close the terminal manually and run the update again.");
    }

    private static bool IsSameExecutable(Process process, string expected)
    {
        try
        {
            return string.Equals(Path.GetFullPath(process.MainModule!.FileName!), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private sealed record InstallerOptions(string DataPath, string TerminalExe, string SourceFile, string TargetFile, string? Sha256);
}

internal static class NativeDialogs
{
    internal const uint Ok = 0x00000000;
    internal const uint YesNo = 0x00000004;
    internal const uint IconError = 0x00000010;
    internal const uint IconQuestion = 0x00000020;
    internal const uint IconWarning = 0x00000030;
    internal const uint IconInformation = 0x00000040;
    internal const uint DefaultButton2 = 0x00000100;
    internal const int Yes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    internal static int Show(string text, string caption, uint type) =>
        MessageBox(IntPtr.Zero, text, caption, type);
}
