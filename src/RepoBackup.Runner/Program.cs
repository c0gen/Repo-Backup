using RepoBackup.Cli;
using System.Text;

// WinExe runs without flashing a console when Task Scheduler or a discovery hook launches it.
Console.InputEncoding = new UTF8Encoding(false);
return await CommandHost.RunAsync(args, TextWriter.Null, TextWriter.Null);
