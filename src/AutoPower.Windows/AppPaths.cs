namespace AutoPower.Windows;

public static class AppPaths
{
    private static string? DataRootOverride => Environment.GetEnvironmentVariable("ESLEE_AUTOPOWER_DATA_ROOT");

    public static string LocalDataDirectory => Path.Combine(
        DataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataRootOverride is null ? Path.Combine("eslee", "AutoPower") : "local");

    public static string SharedStateDirectory => Path.Combine(
        DataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        DataRootOverride is null ? Path.Combine("eslee", "AutoPower") : "shared");

    public static string DatabasePath => Path.Combine(SharedStateDirectory, "autopower.db");
    public static string LogDirectory => Path.Combine(SharedStateDirectory, "logs");
    public static string AutologonJournalPath => Path.Combine(SharedStateDirectory, "autologon-journal.json");
    public static string ResumeSignInJournalPath => Path.Combine(SharedStateDirectory, "resume-signin-journal.json");
    public static string RemovedPowerTestStatePath => Path.Combine(SharedStateDirectory, "s5-test-state.json");
    public static string SleepWakeTestStatePath => Path.Combine(SharedStateDirectory, "sleep-wake-test-state.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(SharedStateDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
