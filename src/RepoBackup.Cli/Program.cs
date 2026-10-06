using RepoBackup.Cli;
using RepoBackup.Core.Infrastructure;
using System.Text;

using var installationLifetime = new InstallationLifetime();
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await CommandHost.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
