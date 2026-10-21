using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services;

/// <summary>
/// Moves your set-up – preferences, AI prompts, letterheads and templates – to another PC or
/// library in one small file, optionally encrypted with a password. Case details, documents and
/// anything machine-specific (like a saved AI key) are never included.
/// </summary>
public static class SettingsTransfer
{
    private const string Magic = "CKP1";

    private static readonly string[] NeverExported = { "case.", "bundle.", "lastBackup", "ai.apiKey", "onboarded", "station.label" };

    private sealed class Profile
    {
        public int Version { get; set; } = 1;
        public string App { get; set; } = "CaseKeeper";
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> Settings { get; set; } = new();
        public List<Letterhead> Letterheads { get; set; } = new();
        public List<DocTemplate> Templates { get; set; } = new();
    }

    public static bool IsTransferable(string key) => !NeverExported.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static void Export(Database db, string path, string password)
    {
        var profile = new Profile
        {
            Settings = db.AllSettings().Where(kv => IsTransferable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Letterheads = db.All<Letterhead>(),
            Templates = db.All<DocTemplate>().Where(t => !t.IsBuiltIn || t.UpdatedAt > DateTime.MinValue).ToList()
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(profile, new JsonSerializerOptions { WriteIndented = false });
        byte[] output;
        if (string.IsNullOrEmpty(password)) output = json;
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Vault.DefaultIterations, HashAlgorithmName.SHA256, 32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[json.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, json, cipher, tag);
            output = Encoding.ASCII.GetBytes(Magic).Concat(salt).Concat(nonce).Concat(tag).Concat(cipher).ToArray();
        }
        File.WriteAllBytes(path, output);
        db.Audit("settings.export", "settings", null, "Exported settings, letterheads and templates",
            $"{profile.Settings.Count} settings, {profile.Letterheads.Count} letterhead(s), {profile.Templates.Count} template(s)" +
            (string.IsNullOrEmpty(password) ? "" : ", password protected"));
    }

    public static bool IsEncrypted(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[4];
        return fs.Read(head, 0, 4) == 4 && Encoding.ASCII.GetString(head) == Magic;
    }

    /// <summary>Applies a settings file. Returns a description of what changed.</summary>
    public static string Import(Database db, string path, string password)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length > 50 * 1024 * 1024) throw new InvalidDataException("That file is too large to be a CaseKeeper settings file.");
        byte[] json;
        if (data.Length > 4 && Encoding.ASCII.GetString(data, 0, 4) == Magic)
        {
            if (string.IsNullOrEmpty(password)) throw new WrongPasswordException("This settings file is password protected.");
            var salt = data.AsSpan(4, 16).ToArray();
            var nonce = data.AsSpan(20, 12).ToArray();
            var tag = data.AsSpan(32, 16).ToArray();
            var cipher = data.AsSpan(48).ToArray();
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Vault.DefaultIterations, HashAlgorithmName.SHA256, 32);
            json = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, cipher, tag, json);
            }
            catch (CryptographicException) { throw new WrongPasswordException("That password didn't work for this settings file."); }
        }
        else json = data;

        Profile profile;
        try { profile = JsonSerializer.Deserialize<Profile>(json); }
        catch (Exception ex) { throw new InvalidDataException("That isn't a CaseKeeper settings file: " + ex.Message); }
        if (profile?.App != "CaseKeeper") throw new InvalidDataException("That isn't a CaseKeeper settings file.");

        int settings = 0, heads = 0, templates = 0;
        foreach (var (k, v) in profile.Settings.Where(kv => IsTransferable(kv.Key)))
        {
            db.SetSetting(k, v);
            settings++;
        }
        var haveHeads = db.All<Letterhead>().Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in profile.Letterheads)
        {
            l.Id = 0;
            if (haveHeads.Contains(l.Name)) l.Name += " (imported)";
            l.IsDefault = false;
            db.Save(l);
            heads++;
        }
        var have = db.All<DocTemplate>();
        foreach (var t in profile.Templates)
        {
            if (t.IsBuiltIn && have.FirstOrDefault(x => x.BuiltInKey == t.BuiltInKey) is { } existing)
            {
                if (existing.Body == t.Body && existing.WordTemplate?.Length == t.WordTemplate?.Length) continue;
                t.Id = existing.Id;      // an edited built-in replaces the local copy
            }
            else
            {
                t.Id = 0;
                if (have.Any(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))) t.Name += " (imported)";
            }
            db.Save(t);
            templates++;
        }
        var summary = $"{settings} setting(s), {heads} letterhead(s) and {templates} template(s) imported.";
        db.Audit("settings.import", "settings", null, "Imported a settings file", summary);
        return summary;
    }
}
