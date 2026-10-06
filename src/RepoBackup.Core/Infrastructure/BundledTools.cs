namespace RepoBackup.Core.Infrastructure;

public static class BundledTools
{
    public static string GitExecutable
    {
        get
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
            return File.Exists(bundled) ? bundled : "git";
        }
    }
}
