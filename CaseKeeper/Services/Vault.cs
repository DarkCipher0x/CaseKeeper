using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseKeeper.Services;

public class WrongPasswordException : Exception
{
    public WrongPasswordException(string message) : base(message) { }
}

/// <summary>
/// Holds the key material for an encrypted library.
///
/// How it fits together:
///   password --PBKDF2--> wrapping key --unlocks--> master key --> everything else
///
/// The master key is random and never changes. The password only ever protects the wrapped
/// copy of it, which is why changing your password takes a moment rather than re-encrypting
/// every document. The master key encrypts document files directly (AES-256-GCM) and is also
/// handed to SQLCipher as the database passphrase.
///
/// vault.json holds the salt, the wrapped master key and a verifier. None of it is secret;
/// without the password none of it is any use. There is deliberately no recovery path:
/// if the password is lost the data cannot be recovered by anyone, including us.
/// </summary>
public sealed class Vault
{
    public const string FileName = "vault.json";
    private const string Magic = "CKE1";                 // marks an encrypted document file
    private const int SaltBytes = 16, NonceBytes = 12, TagBytes = 16, KeyBytes = 32;
    private const string VerifierText = "CaseKeeper vault v1";

    private readonly string _vaultPath;
    private VaultFile _file;
    private byte[] _masterKey;

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; }
        public int Iterations { get; set; }
        public string WrappedKey { get; set; }
        public string Verifier { get; set; }
        public string CreatedAt { get; set; }
    }

    /// <summary>OWASP's current guidance for PBKDF2-HMAC-SHA256. Costs about 50 ms to unlock.</summary>
    public const int DefaultIterations = 600_000;

    private Vault(string vaultPath, VaultFile file)
    {
        _vaultPath = vaultPath;
        _file = file;
    }

    public static Vault Load(string libraryRoot)
    {
        var path = Path.Combine(libraryRoot, FileName);
        VaultFile file = null;
        if (File.Exists(path))
        {
            try { file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path)); }
            catch { /* corrupt vault file is handled as "needs a password we can't check" below */ }
        }
        return new Vault(path, file);
    }

    /// <summary>True when this library is password protected.</summary>
    public bool IsEncrypted => _file != null;

    /// <summary>True when the data can actually be read right now.</summary>
    public bool IsUnlocked => !IsEncrypted || _masterKey != null;

    /// <summary>The passphrase handed to SQLCipher, or null when the library isn't encrypted.</summary>
    public string DatabasePassword => _masterKey == null ? null : Convert.ToBase64String(_masterKey);

    public DateTime? ProtectedSince =>
        DateTime.TryParse(_file?.CreatedAt, out var d) ? d : null;

    // ------------------------------------------------------------------ unlocking
    /// <summary>Checks a password and, if correct, unlocks this vault for the rest of the session.</summary>
    public bool TryUnlock(string password)
    {
        if (_file == null) return true;
        try
        {
            var wrappingKey = DeriveKey(password, Convert.FromBase64String(_file.Salt), _file.Iterations);
            var verifier = Decrypt(Convert.FromBase64String(_file.Verifier), wrappingKey);
            if (Encoding.UTF8.GetString(verifier) != VerifierText) return false;
            _masterKey = Decrypt(Convert.FromBase64String(_file.WrappedKey), wrappingKey);
            return _masterKey.Length == KeyBytes;
        }
        catch (CryptographicException) { return false; }   // wrong password fails the GCM tag check
        catch (FormatException) { return false; }
    }

    public void Lock() => _masterKey = null;

    // ------------------------------------------------------------------ setting up / changing
    /// <summary>Creates the key material for a newly protected library and unlocks it.</summary>
    public void Create(string password)
    {
        if (_file != null) throw new InvalidOperationException("This library is already protected.");
        _masterKey = RandomNumberGenerator.GetBytes(KeyBytes);
        WriteVaultFile(password, DateTime.Now);
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (!TryUnlock(currentPassword)) throw new WrongPasswordException("That is not the current password.");
        WriteVaultFile(newPassword, ProtectedSince ?? DateTime.Now);
    }

    /// <summary>Removes the vault file. Call only once the data itself has been decrypted.</summary>
    public void Remove()
    {
        if (File.Exists(_vaultPath)) File.Delete(_vaultPath);
        _file = null;
        _masterKey = null;
    }

    private void WriteVaultFile(string password, DateTime created)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var wrappingKey = DeriveKey(password, salt, DefaultIterations);
        _file = new VaultFile
        {
            Salt = Convert.ToBase64String(salt),
            Iterations = DefaultIterations,
            WrappedKey = Convert.ToBase64String(Encrypt(_masterKey, wrappingKey)),
            Verifier = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(VerifierText), wrappingKey)),
            CreatedAt = created.ToString("o")
        };
        // Write to a temporary file then swap, so a crash can't leave a half-written vault
        var temp = _vaultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(_vaultPath)) File.Delete(_vaultPath);
        File.Move(temp, _vaultPath);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations,
            HashAlgorithmName.SHA256, KeyBytes);

    // ------------------------------------------------------------------ document files
    private byte[] RequireKey() =>
        _masterKey ?? throw new InvalidOperationException("The library is locked.");

    public static bool LooksEncrypted(byte[] data) =>
        data.Length >= Magic.Length && Encoding.ASCII.GetString(data, 0, Magic.Length) == Magic;

    /// <summary>Encrypts a document for storage. Returns the bytes unchanged if the library isn't protected.</summary>
    public byte[] ProtectFile(byte[] plain)
    {
        if (!IsEncrypted) return plain;
        if (LooksEncrypted(plain)) return plain;        // already done; makes migration repeatable
        var body = Encrypt(plain, RequireKey());
        var result = new byte[Magic.Length + body.Length];
        Encoding.ASCII.GetBytes(Magic).CopyTo(result, 0);
        body.CopyTo(result, Magic.Length);
        return result;
    }

    /// <summary>Decrypts a stored document. Returns the bytes unchanged if they were never encrypted.</summary>
    public byte[] UnprotectFile(byte[] stored)
    {
        if (!LooksEncrypted(stored)) return stored;
        return Decrypt(stored.AsSpan(Magic.Length).ToArray(), RequireKey());
    }

    // ------------------------------------------------------------------ AES-256-GCM
    // Layout: nonce (12) | tag (16) | ciphertext. GCM authenticates as well as encrypts, which is
    // what makes a wrong password fail loudly instead of returning rubbish.
    private static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes)) aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceBytes);
        cipher.CopyTo(result, NonceBytes + TagBytes);
        return result;
    }

    private static byte[] Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < NonceBytes + TagBytes) throw new CryptographicException("Data is too short to be valid.");
        var nonce = blob.AsSpan(0, NonceBytes);
        var tag = blob.AsSpan(NonceBytes, TagBytes);
        var cipher = blob.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    /// <summary>Rough strength feedback for the password box. Not a guarantee of anything.</summary>
    public static (int Score, string Label) RatePassword(string password)
    {
        password ??= "";
        int score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (password.Length >= 16) score++;
        if (password.Any(char.IsLower) && password.Any(char.IsUpper)) score++;
        if (password.Any(char.IsDigit)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;
        if (password.Length < 8) score = 0;

        return score switch
        {
            0 => (0, "Too short"),
            1 or 2 => (1, "Weak"),
            3 or 4 => (2, "Reasonable"),
            5 => (3, "Strong"),
            _ => (4, "Very strong")
        };
    }
}
