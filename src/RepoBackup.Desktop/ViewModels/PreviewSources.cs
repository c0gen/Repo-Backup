using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.ViewModels;

internal static class PreviewSources
{
    public static List<SourceNode> Build(Preview loaded, bool fullProject)
    {
        var nodes = new List<SourceNode>();
        foreach (var source in loaded.Sources.Where(s => s.Kind != SourceKind.Worktree))
        {
            var children = new List<SourceNode>();
            if (fullProject && source.Kind != SourceKind.GitMetadata)
            {
                children.Add(new("Working files", "Staged, unstaged & untracked", "\uE73E", []));
                if (Directory.Exists(Path.Combine(source.OriginalPath, ".git")) || File.Exists(Path.Combine(source.OriginalPath, ".git"))) children.Add(new(".git · Git history", "Branches, index & local Git data", "\uE73E", []));
                children.Add(new("Configuration & assets", ".env and ignored configuration included", "\uE73E", []));
            }
            nodes.Add(new(PathSafety.DisplayName(source.OriginalPath), source.OriginalPath, source.Kind == SourceKind.GitMetadata ? "\uE8F1" : "\uE8B7", children));
        }
        var worktrees = loaded.Sources.Where(s => s.Kind == SourceKind.Worktree).Select(s => new SourceNode(PathSafety.DisplayName(s.OriginalPath), s.OriginalPath, "\uE73E", [])).ToList();
        if (worktrees.Count > 0) nodes.Add(new("Worktrees", $"{worktrees.Count} included", "\uE8B7", worktrees));
        return nodes;
    }
}
