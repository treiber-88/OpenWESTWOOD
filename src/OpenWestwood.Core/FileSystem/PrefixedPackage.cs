namespace OpenWestwood.Core.FileSystem;

/// <summary>A package whose files appear under a folder ("RASFX/GUN8.AUD"), so another game's files can't clash.</summary>
public sealed class PrefixedPackage(IPackage inner, string prefix) : IPackage
{
    readonly string prefix = PathUtil.Normalize(prefix).TrimEnd('/') + "/";

    public string Name => $"{inner.Name}@{prefix}";

    public IEnumerable<string> FilePaths => inner.FilePaths.Select(p => prefix + p);

    string? Inner(string path)
    {
        string normal = PathUtil.Normalize(path);
        return normal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normal[prefix.Length..] : null;
    }

    public bool Contains(string path) => Inner(path) is { } p && inner.Contains(p);

    public Stream Open(string path) => Inner(path) is { } p ? inner.Open(p) : throw new FileNotFoundException(path);

    public void Dispose() => inner.Dispose();
}
