using System.Text.Json.Nodes;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Windows;

internal static class SessionStartHookMerger
{
    public static string Merge(string? existing, string command, string? legacyCommand = null)
    {
        var document = existing is null ? new JsonObject() : JsonNode.Parse(existing)?.AsObject() ?? throw new InvalidDataException("Invalid hook configuration.");
        document["hooks"] ??= new JsonObject(); var hooks = document["hooks"]!.AsObject(); hooks["SessionStart"] ??= new JsonArray();
        var groups = hooks["SessionStart"]!.AsArray();
        foreach (var group in groups)
        {
            if (group?["hooks"] is not JsonArray handlers) throw new InvalidDataException("Invalid SessionStart hook group.");
            foreach (var handler in handlers)
                if (legacyCommand is not null && handler?["command"]?.GetValue<string>() == legacyCommand)
                {
                    handler!["command"] = command; handler["async"] = true; handler["timeout"] = 5;
                }
        }
        if (!groups.Any(g => g?["hooks"] is JsonArray list && list.Any(h => h?["command"]?.GetValue<string>() == command)))
            groups.Add(new JsonObject { ["matcher"] = "startup|resume", ["hooks"] = new JsonArray(new JsonObject
            { ["type"] = "command", ["command"] = command, ["async"] = true, ["timeout"] = 5 }) });
        return document.ToJsonString(Json.Options);
    }
}
