using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PhoenixInstaller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try { var options = args.Length == 0 ? DetectOptions() : ParseArguments(args); return Install(options); }
        catch (Exception ex) { MessageBox.Show("Phoenix Update Installer could not continue.\n\n" + ex.Message, "Phoenix Update Installer", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    private static InstallerOptions DetectOptions()
    {
        var candidateDataFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDataFolderFromFilesDirectory(AppContext.BaseDirectory, candidateDataFolders);
        AddDataFolderFromFilesDirectory(Environment.CurrentDirectory, candidateDataFolders);
        var terminalDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
        if (Directory.Exists(terminalDataRoot)) foreach (var directory in Directory.EnumerateDirectories(terminalDataRoot)) if (Directory.Exists(Path.Combine(directory, "MQL5", "Files"))) candidateDataFolders.Add(Path.GetFullPath(directory));
        var updates = candidateDataFolders.SelectMany(dataPath => { var filesPath = Path.Combine(dataPath, "MQL5", "Files"); return Directory.Exists(filesPath) ? Directory.EnumerateFiles(filesPath, "Phoenix*_B*.bin", SearchOption.TopDirectoryOnly) : Enumerable.Empty<string>(); }).Where(path => Regex.IsMatch(Path.GetFileName(path), @"^Phoenix.*_B\d+\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (updates.Length == 0) throw new FileNotFoundException("No Phoenix update .bin file was found. Keep the downloaded Phoenix*_B####.bin file in the MetaTrader MQL5\\Files folder.");
        if (updates.Length > 1) throw new InvalidOperationException("More than one Phoenix update file was found in MetaTrader data folders. Keep only the one update you want to install, then run the installer again.");
        var source = updates[0];
        var dataPath = Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(source)!)!.FullName)!.FullName;
        var match = Regex.Match(Path.GetFileName(source), @"^(?<target>.+)_B\d+\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["target"].Value.StartsWith("Phoenix", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update filename is not in the expected Phoenix build format.");
        var terminals = Process.GetProcessesByName("terminal64").Select(p => new { Process = p, Path = TryGetExecutablePath(p) }).Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path)).ToArray();
        if (terminals.Length == 0) throw new InvalidOperationException("MetaTrader 5 is not running. Start the terminal that owns this update, then run the installer again.");
        var terminalPaths = terminals.Select(x => Path.GetFullPath(x.Path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terminalPaths.Length != 1 || terminals.Length != 1) throw new InvalidOperationException("More than one MetaTrader terminal process is running. Close the extra terminals and run the installer again so the update is applied to the correct terminal.");
        return new InstallerOptions(dataPath, terminalPaths[0], Path.GetFileName(source), match.Groups["target"].Value + ".ex5", null);
    }
    private static void AddDataFolderFromFilesDirectory(string directory, HashSet<string> results)
    {
        var full = Path.GetFullPath(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); var info = new DirectoryInfo(full);
        if (info.Name.Equals("Files", StringComparison.OrdinalIgnoreCase) && info.Parent?.Name.Equals("MQL5", StringComparison.OrdinalIgnoreCase) == true && info.Parent.Parent is not null) results.Add(info.Parent.Parent.FullName);
    }
    private static string? TryGetExecutablePath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }
    private static InstallerOptions ParseArguments(string[] args)
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (var i=0;i<args.Length;i++) { if (!args[i].StartsWith("--",StringComparison.Ordinal) || i+1>=args.Length) throw new ArgumentException("Invalid installer arguments."); var key=args[i][2..]; if (key is not ("data" or "terminal" or "source" or "target" or "sha256")) throw new ArgumentException("Unknown installer argument: "+key); if(!values.TryAdd(key,args[++i])) throw new ArgumentException("Duplicate installer argument: "+key); }
        string Required(string key)=>values.TryGetValue(key,out var v)&&!string.IsNullOrWhiteSpace(v)?v:throw new ArgumentException("Missing required argument: --"+key);
        return new InstallerOptions(Path.GetFullPath(Required("data")),Path.GetFullPath(Required("terminal")),SafeFileName(Required("source"),".bin"),SafeFileName(Required("target"),".ex5"),values.GetValueOrDefault("sha256"));
    }
    private static string SafeFileName(string v,string ext) { if(v!=Path.GetFileName(v)||v.IndexOfAny(Path.GetInvalidFileNameChars())>=0||!v.EndsWith(ext,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid file name: "+v); return v; }
    private static int Install(InstallerOptions o)
    {
        var source=Path.Combine(o.DataPath,"MQL5","Files",o.SourceFile); var experts=Path.Combine(o.DataPath,"MQL5","Experts"); var dest=Path.Combine(experts,o.TargetFile);
        if(!File.Exists(source)) throw new FileNotFoundException("The downloaded update file was not found.",source); if(new FileInfo(source).Length<=0) throw new InvalidDataException("The downloaded update file is empty."); if(!File.Exists(o.TerminalExe)) throw new FileNotFoundException("MetaTrader terminal executable was not found.",o.TerminalExe); if(!Directory.Exists(experts)) throw new DirectoryNotFoundException("The MetaTrader MQL5\\Experts folder was not found.");
        if(!string.IsNullOrWhiteSpace(o.Sha256)){var expected=o.Sha256.Trim();if(expected.Length!=64||expected.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("The expected SHA-256 value is invalid.");using var s=File.OpenRead(source);var actual=Convert.ToHexString(SHA256.HashData(s));if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 verification failed. The existing EA was not changed.");}
        if(MessageBox.Show("A Phoenix update is ready to install.\n\nMetaTrader 5 must close briefly so the EA file can be replaced. After installation, this installer will reopen the same terminal.\n\nDo you want to close MetaTrader 5 and install the update?","Phoenix XAU — Update Available",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes){MessageBox.Show("Update cancelled. MetaTrader 5 was not closed and no files were changed.","Phoenix Update Installer",MessageBoxButtons.OK,MessageBoxIcon.Information);return 2;}
        CloseMatchingTerminal(o.TerminalExe); var backup=dest+".phoenix-backup-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss");var had=File.Exists(dest);var staged=dest+".phoenix-new";
        try{if(had)File.Copy(dest,backup,false);File.Copy(source,staged,true);var len=new FileInfo(source).Length;if(new FileInfo(staged).Length!=len)throw new IOException("The staged update size does not match the downloaded file.");if(File.Exists(dest))File.Delete(dest);File.Move(staged,dest);if(new FileInfo(dest).Length!=len)throw new IOException("The installed EA size does not match the downloaded file.");File.Delete(source);
            try{Process.Start(new ProcessStartInfo{FileName=o.TerminalExe,WorkingDirectory=Path.GetDirectoryName(o.TerminalExe)!,UseShellExecute=true});}catch(Exception e){MessageBox.Show("The update was installed successfully, but MetaTrader could not be reopened automatically.\n\n"+e.Message+"\n\nPlease start MetaTrader manually.","Phoenix Update Installed",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 0;}
            MessageBox.Show("Phoenix update installed successfully. MetaTrader 5 is starting.","Phoenix Update Complete",MessageBoxButtons.OK,MessageBoxIcon.Information);return 0;
        }catch{if(File.Exists(staged))File.Delete(staged);if(had&&File.Exists(backup)){if(File.Exists(dest))File.Delete(dest);File.Copy(backup,dest,true);}throw;}
    }
    private static void CloseMatchingTerminal(string exe)
    {
        var expected=Path.GetFullPath(exe);var name=Path.GetFileNameWithoutExtension(exe);var matches=Process.GetProcessesByName(name).Where(p=>IsSameExecutable(p,expected)).ToArray();foreach(var p in matches){try{p.CloseMainWindow();}catch(InvalidOperationException){}}
        var deadline=DateTime.UtcNow.AddSeconds(60);while(DateTime.UtcNow<deadline){if(!matches.Any(p=>{try{return !p.HasExited;}catch{return false;}}))return;Thread.Sleep(500);}throw new TimeoutException("MetaTrader did not close within 60 seconds. No update files were replaced. Please close the terminal manually and run the update again.");
    }
    private static bool IsSameExecutable(Process p,string expected){try{return string.Equals(Path.GetFullPath(p.MainModule!.FileName!),expected,StringComparison.OrdinalIgnoreCase);}catch{return false;}}
    private sealed record InstallerOptions(string DataPath,string TerminalExe,string SourceFile,string TargetFile,string? Sha256);
}
