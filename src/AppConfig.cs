using System.Text.Json;
using Microsoft.Win32;

namespace DHelper;

public sealed class AppConfig
{
    public int PerformanceMode { get; set; } = AsusAcpi.ModeBalanced;
    public int BatteryLimit { get; set; } = 80;
    public int KeyboardLevel { get; set; } = 2;
    public bool ApplyOnStartup { get; set; } = true;

    // Lighting (only re-applied once the user has changed it in D-Helper)
    public bool LightingSet { get; set; }
    public int LightMode { get; set; } = 0;                 // AsusHid.AuraMode
    public int LightColor { get; set; } = unchecked((int)0xFFBB0000);
    public int LightSpeed { get; set; } = 1;                // AsusHid.AuraSpeed
    public bool LightBarAwake { get; set; } = true;

    // Game mode
    public bool GameModeEnabled { get; set; } = true;
    public List<string> GameExecutables { get; set; } = new() { "bf6.exe" };
    public int GameUndervolt { get; set; } = -40;   // Curve Optimizer all-core, only under sustained load
    public int SafeUndervolt { get; set; } = 0;     // used at all other times
    public int GameTempTarget { get; set; } = 93;   // CPU Tctl limit while gaming; fans boost 3 °C below it

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D-Helper");

    private static string FilePath => Path.Combine(Folder, "config.json");

    public static bool Exists => File.Exists(FilePath);

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Write("Config load failed: " + ex.Message);
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("Config save failed: " + ex.Message);
        }
    }

    private const string TaskName = "D-Helper";

    /// <summary>
    /// D-Helper runs as administrator (needed for the SMU), so it starts at sign-in through a Task Scheduler
    /// task with highest privileges instead of the Run key, which cannot start elevated apps.
    /// </summary>
    public static bool RunAtLogin
    {
        get => RunSchtasks($"/query /tn \"{TaskName}\"") == 0;
        set
        {
            // Clean up the Run-key entry used by earlier D-Helper versions.
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                key?.DeleteValue("D-Helper", false);

            if (value)
            {
                // XML instead of plain /sc onlogon: the defaults would refuse to start on battery
                // and stop the app after 3 days.
                string xmlPath = Path.Combine(Path.GetTempPath(), "dhelper-task.xml");
                File.WriteAllText(xmlPath, TaskXml(), System.Text.Encoding.Unicode);
                RunSchtasks($"/create /f /tn \"{TaskName}\" /xml \"{xmlPath}\"");
                try { File.Delete(xmlPath); } catch { }
            }
            else
                RunSchtasks($"/delete /f /tn \"{TaskName}\"");
        }
    }

    private static string TaskXml()
    {
        string user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string exe = System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts D-Helper at sign-in with administrator rights.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--minimized</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}

public static class Log
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppConfig.Folder);
                File.AppendAllText(Path.Combine(AppConfig.Folder, "log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
