using System.IO;
using CaseKeeper.Models;

namespace CaseKeeper.Services.BulkImport;

/// <summary>Choices for walking a folder. Everything defaults to the cautious option.</summary>
public sealed class ScanOptions
{
    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludeHiddenFiles { get; set; }
    public bool IncludeHiddenFolders { get; set; }
    /// <summary>Follow symbolic links and junctions. Off by default: links can point back up the tree or off the drive.</summary>
    public bool FollowLinks { get; set; }
    public bool SkipSystemFiles { get; set; } = true;
    /// <summary>Deepest folder level walked. Protects against pathological trees.</summary>
    public int MaxDepth { get; set; } = 64;
    /// <summary>Stop listing after this many files and say so, rather than exhausting memory.</summary>
    public int MaxFiles { get; set; } = 50_000;
}

/// <summary>One file found by a scan. Nothing has been imported or changed.</summary>
public sealed class ScanEntry
{
    public string RelativePath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Extension { get; init; } = "";
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public DateTime? Created { get; init; }
    public ImportFileStatus Status { get; set; } = ImportFileStatus.Pending;
    /// <summary>Plain-English reason when a file can't be imported. Never includes file contents.</summary>
    public string Message { get; set; } = "";
    public string Hash { get; set; } = "";
    public long? DuplicateOfDocumentId { get; set; }
    /// <summary>Index (in <see cref="ScanResult.Entries"/>) of an earlier file in the same folder with identical contents.</summary>
    public int? DuplicateOfEntry { get; set; }
}

public sealed class ScanResult
{
    public string Root { get; init; } = "";
    public List<ScanEntry> Entries { get; } = new();
    /// <summary>Folders that couldn't be opened (relative paths), so the person knows something was missed.</summary>
    public List<string> InaccessibleFolders { get; } = new();
    public int HiddenSkipped { get; set; }
    public int LinksNotFollowed { get; set; }
    public int LoopsAvoided { get; set; }
    public bool TruncatedAtLimit { get; set; }
    public bool TooDeep { get; set; }

    public int Supported => Entries.Count(e => e.Status == ImportFileStatus.Pending);
    public long TotalBytes => Entries.Sum(e => e.Size);
}

/// <summary>
/// Finds every file in a folder (and, by default, its subfolders) without importing or changing
/// anything. Copes with folders it isn't allowed into, files that vanish mid-scan, very long paths,
/// and link loops. Pure file-system code, so it can be tested on its own.
/// </summary>
public static class FolderScanner
{
    public static readonly string[] SupportedExtensions =
        new[] { ".pdf", ".docx" }.Concat(FileKinds.ImageExtensions).Concat(FileKinds.TextExtensions).ToArray();

    private static readonly string[] SystemNames = { "desktop.ini", "thumbs.db", "ehthumbs.db", ".ds_store", "icon\r" };
    private static readonly string[] SystemExtensions = { ".tmp", ".temp", ".lnk", ".url", ".crdownload", ".part", ".partial", ".bak", ".swp" };
    private static readonly string[] SystemFolders = { "$recycle.bin", "system volume information", ".git", ".svn", "__macosx", ".trash" };

    public static bool IsSupported(string extension) => SupportedExtensions.Contains((extension ?? "").ToLowerInvariant());

    /// <summary>Temporary and system clutter: Office lock files (~$…), thumbnails, shortcuts, part-downloads.</summary>
    public static bool IsSystemFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return SystemNames.Contains(lower) || lower.StartsWith("~$") || lower.StartsWith(".~lock.") ||
               SystemExtensions.Contains(Path.GetExtension(lower));
    }

    public static ScanResult Scan(string root, ScanOptions options, CancellationToken ct = default, IProgress<string> progress = null)
    {
        options ??= new ScanOptions();
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists) throw new DirectoryNotFoundException("That folder can't be found. If it's on a network drive, check it's connected.");
        var result = new ScanResult { Root = rootInfo.FullName };

        // Every folder visited, by its real location, so a link back up the tree can never loop
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Canonical(rootInfo) };
        var stack = new Stack<(DirectoryInfo Dir, string Rel, int Depth)>();
        stack.Push((rootInfo, "", 0));
        var lastReport = DateTime.UtcNow;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel, depth) = stack.Pop();
            var subdirs = new List<(DirectoryInfo, string)>();

            IEnumerator<FileSystemInfo> it;
            try
            {
                it = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false
                }).GetEnumerator();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                result.InaccessibleFolders.Add(rel.Length == 0 ? "(the chosen folder)" : rel);
                continue;
            }

            using (it)
            {
                while (true)
                {
                    FileSystemInfo item;
                    try
                    {
                        if (!it.MoveNext()) break;
                        item = it.Current;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        // The folder stopped being readable part-way through (permissions, a drive dropping off)
                        result.InaccessibleFolders.Add(rel.Length == 0 ? "(the chosen folder)" : rel);
                        break;
                    }
                    ct.ThrowIfCancellationRequested();

                    var childRel = rel.Length == 0 ? item.Name : rel + Path.DirectorySeparatorChar + item.Name;
                    FileAttributes attrs;
                    try { attrs = item.Attributes; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AddInaccessible(result, item, childRel, "Its details couldn't be read");
                        continue;
                    }

                    bool hidden = (attrs & FileAttributes.Hidden) != 0;
                    bool system = (attrs & FileAttributes.System) != 0;
                    bool isLink = IsLink(item);

                    if (item is DirectoryInfo sub)
                    {
                        if (!options.IncludeSubfolders) continue;
                        if (SystemFolders.Contains(item.Name.ToLowerInvariant()) || (system && options.SkipSystemFiles)) continue;
                        if (hidden && !options.IncludeHiddenFolders) { result.HiddenSkipped++; continue; }
                        if (isLink && !options.FollowLinks) { result.LinksNotFollowed++; continue; }
                        if (depth + 1 > options.MaxDepth) { result.TooDeep = true; continue; }
                        var real = isLink ? ResolveLink(sub) : sub;
                        if (real == null) { result.InaccessibleFolders.Add(childRel); continue; }
                        if (!visited.Add(Canonical(real))) { result.LoopsAvoided++; continue; }
                        subdirs.Add((real, childRel));
                        continue;
                    }

                    if (item is not FileInfo file) continue;
                    if (hidden && !options.IncludeHiddenFiles) { result.HiddenSkipped++; continue; }
                    if (isLink && !options.FollowLinks) { result.LinksNotFollowed++; continue; }
                    if (result.Entries.Count >= options.MaxFiles) { result.TruncatedAtLimit = true; break; }

                    var ext = file.Extension.ToLowerInvariant();
                    ScanEntry entry;
                    try
                    {
                        entry = new ScanEntry
                        {
                            RelativePath = childRel, FullPath = file.FullName, Extension = ext, Size = file.Length,
                            Modified = file.LastWriteTime, Created = file.CreationTime
                        };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AddInaccessible(result, item, childRel, "Its details couldn't be read");
                        continue;
                    }

                    if (options.SkipSystemFiles && (IsSystemFile(file.Name) || system))
                    {
                        entry.Status = ImportFileStatus.SystemFile;
                        entry.Message = "Temporary or system file";
                    }
                    else if (!IsSupported(ext))
                    {
                        entry.Status = ImportFileStatus.Unsupported;
                        entry.Message = ext.Length == 0 ? "No file type" : $"{ext.TrimStart('.').ToUpperInvariant()} files can't be imported yet";
                    }
                    else if (file.Length == 0)
                    {
                        entry.Status = ImportFileStatus.Failed;
                        entry.Message = "The file is empty";
                    }
                    result.Entries.Add(entry);

                    if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report($"Found {result.Entries.Count:N0} file(s)…");
                    }
                }
            }
            if (result.TruncatedAtLimit) break;
            // Push in reverse so folders are visited in name order
            foreach (var s in subdirs.OrderByDescending(s => s.Item2, StringComparer.OrdinalIgnoreCase)) stack.Push((s.Item1, s.Item2, depth + 1));
        }

        // Stable, predictable order: by folder then name, which also decides which duplicate counts as "first"
        var sorted = result.Entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        result.Entries.Clear();
        result.Entries.AddRange(sorted);
        progress?.Report($"Found {result.Entries.Count:N0} file(s).");
        return result;
    }

    private static void AddInaccessible(ScanResult result, FileSystemInfo item, string rel, string why)
    {
        if (item is DirectoryInfo) { result.InaccessibleFolders.Add(rel); return; }
        result.Entries.Add(new ScanEntry
        {
            RelativePath = rel, FullPath = item.FullName, Extension = Path.GetExtension(item.Name).ToLowerInvariant(),
            Status = ImportFileStatus.Inaccessible, Message = why
        });
    }

    /// <summary>
    /// A real symbolic link or junction. Cloud placeholders (OneDrive "files on demand") are reparse points
    /// too, but have no link target, so they're treated as the ordinary files they are.
    /// </summary>
    private static bool IsLink(FileSystemInfo item)
    {
        try { return (item.Attributes & FileAttributes.ReparsePoint) != 0 && item.LinkTarget != null; }
        catch { return false; }
    }

    private static DirectoryInfo ResolveLink(DirectoryInfo link)
    {
        try { return link.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo is { Exists: true } d ? d : null; }
        catch { return null; }
    }

    private static string Canonical(DirectoryInfo d) => Path.TrimEndingDirectorySeparator(d.FullName);

    /// <summary>
    /// Works out each supported file's SHA-256 fingerprint, then marks exact duplicates: of a document
    /// already in the library, or of an earlier file in the same folder. Matching is by content, never by name.
    /// </summary>
    public static void FindDuplicates(ScanResult scan, IReadOnlyDictionary<string, long> libraryHashes, CancellationToken ct = default,
        IProgress<(int Done, int Total)> progress = null)
    {
        var candidates = scan.Entries.Select((e, i) => (e, i)).Where(x => x.e.Status == ImportFileStatus.Pending).ToList();
        var firstWithHash = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        foreach (var (e, i) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try { e.Hash = LibraryService.ComputeHash(e.FullPath); }
            catch (FileNotFoundException) { e.Status = ImportFileStatus.Missing; e.Message = "The file was removed after the scan"; }
            catch (DirectoryNotFoundException) { e.Status = ImportFileStatus.Missing; e.Message = "The file was removed after the scan"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                e.Status = ImportFileStatus.Inaccessible;
                e.Message = ex is UnauthorizedAccessException ? "Windows won't let CaseKeeper read it" : "It's open in another program or can't be read";
            }
            if (e.Hash.Length > 0)
            {
                if (libraryHashes != null && libraryHashes.TryGetValue(e.Hash, out var docId)) e.DuplicateOfDocumentId = docId;
                else if (firstWithHash.TryGetValue(e.Hash, out var first)) e.DuplicateOfEntry = first;
                else firstWithHash[e.Hash] = i;
            }
            progress?.Report((++done, candidates.Count));
        }
    }
}
