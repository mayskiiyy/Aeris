using System.IO;

namespace Aeris;

/// <summary>Which copy stays when Aeris picks the extra copies of a duplicate group.</summary>
public static class DuplicateRules
{
    public static readonly string[] Names = ["Oldest", "Newest", "ShortestPath", "ShortestName"];

    /// <summary>
    /// The copy to keep: one inside a priority folder wins, then the rule decides. Ties fall back to the path
    /// so the choice is the same every time.
    /// </summary>
    public static DuplicateFile Keeper(IReadOnlyCollection<DuplicateFile> group, string rule, IReadOnlyCollection<string> priority)
    {
        IOrderedEnumerable<DuplicateFile> ordered = group.OrderBy(f => InAny(f.Path, priority) ? 0 : 1);
        ordered = rule switch
        {
            "Newest" => ordered.ThenByDescending(f => f.File.Modified),
            "ShortestPath" => ordered.ThenBy(f => f.Path.Length),
            "ShortestName" => ordered.ThenBy(f => f.Name.Length),
            _ => ordered.ThenBy(f => f.File.Modified),
        };
        return ordered.ThenBy(f => f.Path, FileSystemPaths.Comparer).First();
    }

    /// <summary>Every copy except the keeper, minus copies inside protected folders, which are never picked.</summary>
    public static List<DuplicateFile> Extra(IEnumerable<DuplicateFile> files, string rule, IReadOnlyCollection<string> priority, IReadOnlyCollection<string> protectedFolders)
    {
        var extra = new List<DuplicateFile>();
        foreach (var group in files.GroupBy(f => f.Group))
        {
            var keeper = Keeper(group.ToList(), rule, priority);
            extra.AddRange(group.Where(f => f != keeper && !InAny(f.Path, protectedFolders)));
        }
        return extra;
    }

    public static bool InAny(string path, IReadOnlyCollection<string> folders) => folders.Any(folder => PathGuard.IsInside(path, folder));
}

public static class Duplicates
{
    /// <summary>
    /// Plan for moving the selected copies to the Recycle Bin. Each item names the copy that stays; a group whose
    /// copies are all selected is refused, so the domain, not only the UI, keeps one copy per group.
    /// </summary>
    public static CleanupPlan Plan(IReadOnlyCollection<DuplicateFile> selected, IReadOnlyCollection<DuplicateFile> all)
    {
        var chosen = selected.ToHashSet();
        var categories = new List<PlanCategory>();
        foreach (var group in selected.GroupBy(f => f.Group).OrderBy(g => g.Key))
        {
            var keeper = all.FirstOrDefault(d => d.Group == group.Key && !chosen.Contains(d))
                ?? throw new InvalidOperationException(Loc.Get("Recycle.KeepOne"));
            var items = group.Select(f => new PlanItem(f.Path, f.File.Size, f.File.Modified, f.File.Created, $"dup-{group.Key}")
                { Keeper = keeper.Path, Hash = f.Hash }).ToList();
            categories.Add(new($"dup-{group.Key}", Loc.Format("Preview.DuplicateGroup", group.Key, keeper.Path), items));
        }
        return new(HistoryAction.Recycle, CleanupMode.Recycle, categories) { Details = string.Join("\n", selected.Select(f => f.Path)) };
    }

    /// <summary>The same guarantee for similar images: each group keeps at least one picture, the best one if it isn't selected.</summary>
    public static CleanupPlan Plan(IReadOnlyCollection<SimilarImage> selected, IReadOnlyCollection<SimilarImage> all)
    {
        var chosen = selected.ToHashSet();
        var categories = new List<PlanCategory>();
        foreach (var group in selected.GroupBy(f => f.Group).OrderBy(g => g.Key))
        {
            var keeper = all.Where(i => i.Group == group.Key && !chosen.Contains(i)).OrderByDescending(i => i.Best).FirstOrDefault()
                ?? throw new InvalidOperationException(Loc.Get("Recycle.KeepOne"));
            var items = group.Select(i => new PlanItem(i.Path, i.Size, i.Modified, new FileInfo(i.Path) is { Exists: true } f ? Storage.Created(f) : 0, $"img-{group.Key}") { Keeper = keeper.Path }).ToList();
            categories.Add(new($"img-{group.Key}", Loc.Format("Preview.DuplicateGroup", group.Key, keeper.Path), items));
        }
        return new(HistoryAction.Recycle, CleanupMode.Recycle, categories) { Details = string.Join("\n", selected.Select(f => f.Path)) };
    }

    /// <summary>True when, after the user's choices in the preview, every group still keeps at least one file on disk.</summary>
    public static bool KeepsOne(CleanupPlan plan) =>
        plan.Included.Where(i => i.Keeper != null).All(i => !plan.Included.Any(o => string.Equals(o.Path, i.Keeper, FileSystemPaths.Comparison)));

    /// <summary>
    /// The copy that stays still exists. For exact duplicates both files must also still be two different files
    /// with the hash found during the search.
    /// </summary>
    public static void Verify(PlanItem copy)
    {
        if (copy.Keeper == null) return;
        var keeperInfo = new FileInfo(copy.Keeper);
        if (!keeperInfo.Exists || !Storage.SafePath(keeperInfo.DirectoryName!) || (copy.Hash != null && keeperInfo.Length != copy.Size))
            throw new ChangedException(Loc.Get("Error.KeeperChanged"));
        if (copy.Hash == null) return;
        using var original = new FileStream(copy.Keeper, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var candidate = new FileStream(copy.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (Analysis.Identity(original) == Analysis.Identity(candidate) || Analysis.Hash(original, CancellationToken.None) != copy.Hash || Analysis.Hash(candidate, CancellationToken.None) != copy.Hash)
            throw new ChangedException(Loc.Get("Error.ContentChanged"));
    }
}
