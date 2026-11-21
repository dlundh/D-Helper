namespace DHelper;

internal static class Program
{
    // Same ID as the Start menu / desktop shortcuts (tools/make-shortcut.ps1): a pinned shortcut and the
    // running window then share one taskbar button with the D-Helper icon.
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main(string[] args)
    {
        SetCurrentProcessExplicitAppUserModelID("DHelper.App");
        using var mutex = new Mutex(true, "DHelper.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("D-Helper is already running (check the system tray).", "D-Helper",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        using var acpi = new AsusAcpi();
        if (!acpi.IsConnected)
        {
            MessageBox.Show(
                "Can't open the ASUS ATKACPI driver.\n\nInstall the \"ASUS System Control Interface\" driver from ASUS support for your model, then start D-Helper again.",
                "D-Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool startMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        using var smu = new RyzenSmu();
        smu.Initialize(); // failure is shown in the Game mode section; the rest of the app still works
        Application.Run(new MainForm(acpi, smu, AppConfig.Load(), startMinimized));
    }
}
