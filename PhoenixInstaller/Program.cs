using System.Diagnostics;
using Microsoft.Win32;
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
            if (args.Length == 1 && args[0].Equals("--background", StringComparison.OrdinalIgnoreCase))
                return RunBackground();
            var options = args.Length == 0 ? DetectOptions() : ParseArguments(args);
            if (args.Length == 0) RegisterPersistentUpdater();
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
        var running = Process.GetProcessesByName("terminal64")
            .Select(p => new { Process = p, Path = TryGetExecutablePath(p) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
            .ToArray();

        if (running.Length != 1)
            throw new InvalidOperationException(running.Length == 0
                ? "Start the MetaTrader 5 terminal where Phoenix should be installed, then run the installer again."
                : "More than one MetaTrader terminal is running. Close the extra terminals and run the installer again.");

        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine installer location.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(self))!;
        var candidates = Directory.EnumerateFiles(folder, "*.ex5", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("Phoenix", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Regex.IsMatch(Path.GetFileName(path), @"_B\d+\.ex5$", RegexOptions.IgnoreCase))
            .ToArray();

        if (candidates.Length != 1)
            throw new FileNotFoundException(candidates.Length == 0
                ? "The Phoenix .ex5 file was not found beside PhoenixInstaller.exe. Keep both downloaded files in the same folder and run the installer again."
                : "More than one Phoenix .ex5 file was found beside PhoenixInstaller.exe. Keep only the intended Phoenix EA file beside the installer.");

        var terminalExe = Path.GetFullPath(running[0].Path!);
        var dataPath = FindTerminalDataFolder(Path.GetDirectoryName(terminalExe)!);
        return new InstallerOptions(dataPath, terminalExe, candidates[0], Path.GetFileName(candidates[0]), null);
    }

    private static void RegisterPersistentUpdater()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Installer path is unavailable.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phoenix");
        Directory.CreateDirectory(folder);
        var persistentExe = Path.Combine(folder, "PhoenixInstaller.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(persistentExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, persistentExe, true);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key.SetValue("PhoenixUpdater", "\"" + persistentExe + "\" --background");
        Process.Start(new ProcessStartInfo { FileName = persistentExe, Arguments = "--background",
            WorkingDirectory = folder, UseShellExecute = true, CreateNoWindow = true });
    }

    private static int RunBackground()
    {
        using var mutex = new Mutex(true, "Local\\PhoenixUpdater", out var created);
        if (!created) return 0;
        while (true)
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
                if (Directory.Exists(root))
                    foreach (var dataPath in Directory.EnumerateDirectories(root))
                    {
                        var files = Path.Combine(dataPath, "MQL5", "Files");
                        var request = Path.Combine(files, "PhoenixUpdate.request");
                        if (!File.Exists(request)) continue;
                        var lines = File.ReadAllLines(request);
                        if (lines.Length < 2) continue;
                        var sourceName = SafeFileName(lines[0], ".datx5");
                        var targetName = SafeFileName(lines[1], ".ex5");
                        var source = Path.Combine(files, sourceName);
                        var origin = Path.Combine(dataPath, "origin.txt");
                        if (!File.Exists(source) || !File.Exists(origin)) continue;
                        var terminal = Path.Combine(File.ReadAllText(origin).Trim(), "terminal64.exe");
                        if (!File.Exists(terminal)) continue;
                        File.Delete(request);
                        Install(new InstallerOptions(dataPath, terminal, source, targetName, null));
                    }
            }
            catch (Exception ex)
            {
                try
                {
                    var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phoenix", "updater.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    File.AppendAllText(log, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
                }
                catch { }
            }
            Thread.Sleep(2000);
        }
    }

    private static string FindTerminalDataFolder(string terminalDirectory)
    {
        var fullTerminalDirectory = Path.GetFullPath(terminalDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // MetaTrader portable mode stores its data beside terminal64.exe.
        if (File.Exists(Path.Combine(fullTerminalDirectory, "portable.txt")) &&
            Directory.Exists(Path.Combine(fullTerminalDirectory, "MQL5", "Experts")))
            return fullTerminalDirectory;

        var terminalDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MetaQuotes", "Terminal");

        if (Directory.Exists(terminalDataRoot))
        {
            var matches = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(terminalDataRoot))
            {
                var originFile = Path.Combine(directory, "origin.txt");
                if (!File.Exists(originFile))
                    continue;

                var origin = File.ReadAllText(originFile).Trim()
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (origin.Length == 0)
                    continue;

                try
                {
                    if (string.Equals(Path.GetFullPath(origin), fullTerminalDirectory, StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(Path.Combine(directory, "MQL5", "Experts")))
                        matches.Add(Path.GetFullPath(directory));
                }
                catch
                {
                    // Ignore malformed origin.txt entries and continue scanning.
                }
            }

            var uniqueMatches = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (uniqueMatches.Length == 1)
                return uniqueMatches[0];
            if (uniqueMatches.Length > 1)
                throw new InvalidOperationException("More than one MetaTrader data folder matches the running terminal. Open the correct terminal's data folder and contact Phoenix support.");
        }

        throw new DirectoryNotFoundException(
            "Could not identify the data folder for the running MetaTrader 5 terminal. The installer matched the terminal executable but could not find its matching origin.txt data folder.");
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
            SafeFileName(Required("source"), ".ex5"),
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
        var source = Path.IsPathRooted(options.SourceFile) ? options.SourceFile : Path.Combine(@"C:\Phoenix", options.SourceFile);
        var experts = Path.Combine(options.DataPath, "MQL5", "Experts");
        var destination = Path.Combine(experts, options.TargetFile);

        if (!File.Exists(source))
            throw new FileNotFoundException("The downloaded update file was not found in C:\\Phoenix.", source);
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
            var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? "");
            if (sourceFolder.Equals(Path.GetFullPath(@"C:\Phoenix"), StringComparison.OrdinalIgnoreCase) ||
                sourceFolder.EndsWith(Path.Combine("MQL5", "Files"), StringComparison.OrdinalIgnoreCase))
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
