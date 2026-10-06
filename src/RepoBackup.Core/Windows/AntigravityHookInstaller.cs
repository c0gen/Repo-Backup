using System.Text.Json.Nodes;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Windows;

public static class AntigravityHookInstaller
{
    private const string HookName = "repo-backup-discovery";

    public static string Merge(string? existing, string runnerPath, string? dataDirectory = null)
    {
        var document = existing is null ? new JsonObject() : JsonNode.Parse(existing)?.AsObject() ?? throw new InvalidDataException("Invalid Antigravity hook configuration.");
        var command = HookCommandBuilder.Build(runnerPath, DiscoveryProviders.Antigravity, dataDirectory);
        if (document[HookName] is JsonObject current)
        {
            if (current["PostInvocation"] is JsonArray handlers && handlers.Any(h => h?["command"]?.GetValue<string>() == command))
                return document.ToJsonString(Json.Options);
            throw new InvalidDataException("The repo-backup-discovery hook name is already in use. Existing configuration was preserved.");
        }
        if (document.ContainsKey(HookName)) throw new InvalidDataException("Invalid existing discovery hook.");
        document[HookName] = new JsonObject
        {
            ["PostInvocation"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 5 })
        };
        return document.ToJsonString(Json.Options);
    }
}
