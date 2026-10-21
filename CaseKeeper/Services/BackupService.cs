using System.IO;
using CaseKeeper.Data;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Data.Sqlite;

namespace CaseKeeper.Services;

/// <summary>Raised when a backup needs a password, or the one given was wrong.</summary>
public class BackupPasswordException : Exception
{
    public BackupPasswordException(string message) : base(message) { }
}

/// <summary>
/// Creates and restores a single .zip backup of the whole library (database + all files).
/// A backup can optionally be encrypted with AES-256, which 7-Zip and WinRAR can open with the
/// password. Windows File Explorer's built-in zip viewer does not support AES, so an encrypted
/// backup has to be restored through CaseKeeper (or one of those tools).
/// </summary>
public static class BackupService
{
    private const string DbEntry = "casekeeper.db";

    /// <summary>
    /// Writes the whole library to one zip: the database plus a copy of every document file.
    ///
    /// The contents always go in unencrypted, even when the library itself is password protected.
    /// That keeps a backup restorable on any machine without needing the library's password, and
    /// means it can be merged into a library with a different password. The zip's own AES-256
    /// password is what protects it, so backing up a protected library without one is a real
    /// downgrade - the app warns about exactly that.
    /// </summary>
    public static void CreateBackup(LibraryService lib, string zipPath, string password = null)
    {
        lib.Db.Checkpoint();
        SqliteConnection.ClearAllPools();
        bool encrypt = !string.IsNullOrEmpty(password);

        // Write to a temporary file first so a failure never leaves a half-written backup behind
        var temp = zipPath + ".part";
        var plainDb = lib.DatabasePath + ".backup-copy";
        try
        {
            var dbToPack = lib.DatabasePath;
            if (lib.Vault.IsEncrypted)
            {
                Database.ExportPlaintextCopy(lib.DatabasePath, lib.Vault.DatabasePassword, plainDb);
                dbToPack = plainDb;
            }

            using (var fs = File.Create(temp))
            using (var zip = new ZipOutputStream(fs))
            {
                zip.SetLevel(6);
                if (encrypt) zip.Password = password;

                void AddBytes(string entryName, byte[] data, DateTime stamp)
                {
                    var entry = new ZipEntry(entryName) { DateTime = stamp, Size = data.Length };
                    if (encrypt) entry.AESKeySize = 256;
                    zip.PutNextEntry(entry);
                    zip.Write(data, 0, data.Length);
                    zip.CloseEntry();
                }

                AddBytes(DbEntry, File.ReadAllBytes(dbToPack), File.GetLastWriteTime(lib.DatabasePath));
                foreach (var f in Directory.EnumerateFiles(lib.FilesFolder))
                    AddBytes("Files/" + Path.GetFileName(f),
                        lib.Vault.UnprotectFile(File.ReadAllBytes(f)), File.GetLastWriteTime(f));
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(temp, zipPath);
        }
        finally
        {
            foreach (var leftover in new[] { temp, plainDb })
                if (File.Exists(leftover)) try { File.Delete(leftover); } catch { /* best effort */ }
        }
    }

    /// <summary>Checks the zip looks like a CaseKeeper backup, and whether it needs a password.</summary>
    public static bool IsValidBackup(string zipPath, out bool isEncrypted)
    {
        isEncrypted = false;
        try
        {
            using var zf = new ZipFile(zipPath);
            foreach (ZipEntry e in zf)
            {
                if (!string.Equals(e.Name, DbEntry, StringComparison.OrdinalIgnoreCase)) continue;
                isEncrypted = e.IsCrypted;
                return true;
            }
        }
        catch { /* not a readable zip */ }
        return false;
    }

    /// <summary>
    /// Unpacks a backup into a staging folder, checking the password before anything else happens.
    /// The caller owns the folder and must delete it when finished.
    /// </summary>
    private static string ExtractToStaging(LibraryService lib, string zipPath, string password, string folderName)
    {
        SqliteConnection.ClearAllPools();
        var staging = Path.Combine(lib.RootFolder, folderName);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        try
        {
            using var zf = new ZipFile(zipPath);
            if (!string.IsNullOrEmpty(password)) zf.Password = password;
            foreach (ZipEntry entry in zf)
            {
                if (!entry.IsFile) continue;
                var name = entry.Name.Replace('\\', '/');
                // Ignore any path trying to climb out of the staging folder
                if (name.Contains("..") || Path.IsPathRooted(name)) continue;
                var dest = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var input = zf.GetInputStream(entry);
                using var output = File.Create(dest);
                input.CopyTo(output);
            }
        }
        catch (Exception ex) when (IsPasswordProblem(ex))
        {
            TryDelete(staging);
            throw new BackupPasswordException(string.IsNullOrEmpty(password)
                ? "This backup is password protected."
                : "That password did not work for this backup.");
        }
        catch
        {
            TryDelete(staging);
            throw;
        }

        if (!File.Exists(Path.Combine(staging, DbEntry)))
        {
            TryDelete(staging);
            throw new InvalidDataException("The backup did not contain a CaseKeeper database.");
        }
        return staging;
    }

    /// <summary>
    /// Adds the contents of a backup to the current library instead of replacing it. Documents
    /// already present (same file fingerprint) are skipped, so importing the same backup twice is safe.
    /// No restart needed.
    /// </summary>
    public static MergeResult MergeBackup(LibraryService lib, string zipPath, string password = null)
    {
        var staging = ExtractToStaging(lib, zipPath, password, "_import");
        try
        {
            // StoreImportedFile re-encrypts into this library if it is protected
            return lib.Db.MergeFrom(Path.Combine(staging, DbEntry), Path.Combine(staging, "Files"),
                lib.StoreImportedFile);
        }
        finally { TryDelete(staging); }
    }

    /// <summary>Replaces the current library with the backup. The app must restart afterwards.</summary>
    public static void RestoreBackup(LibraryService lib, string zipPath, string password = null)
    {
        var staging = ExtractToStaging(lib, zipPath, password, "_restore");

        // Keep the current library aside rather than deleting it, in case the backup is the wrong one
        var previous = Path.Combine(lib.RootFolder, $"_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(previous);
        foreach (var f in new[] { lib.DatabasePath, lib.DatabasePath + "-wal", lib.DatabasePath + "-shm" })
            if (File.Exists(f)) File.Move(f, Path.Combine(previous, Path.GetFileName(f)));
        if (Directory.Exists(lib.FilesFolder)) Directory.Move(lib.FilesFolder, Path.Combine(previous, "Files"));

        File.Move(Path.Combine(staging, DbEntry), lib.DatabasePath);
        var stagedFiles = Path.Combine(staging, "Files");
        if (Directory.Exists(stagedFiles)) Directory.Move(stagedFiles, lib.FilesFolder);
        else Directory.CreateDirectory(lib.FilesFolder);
        TryDelete(staging);

        // A backup's contents are always plain, so a protected library has to re-apply its
        // encryption to what has just landed.
        if (lib.Vault.IsEncrypted && lib.Vault.IsUnlocked)
        {
            lib.EncryptAllFiles(null);
            Database.ConvertEncryption(lib.DatabasePath, null, lib.Vault.DatabasePassword);
        }
    }

    private static bool IsPasswordProblem(Exception ex) =>
        ex is ZipException && (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }
}
