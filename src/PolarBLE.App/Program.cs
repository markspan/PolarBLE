namespace PolarBLE.App;

internal static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Without these, an exception on a background thread tears the process down silently
        // and takes the recording with it.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ReportFatal(Exception? exception)
    {
        MessageBox.Show(
            exception?.ToString() ?? "An unknown error occurred.",
            "PolarBLE",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
