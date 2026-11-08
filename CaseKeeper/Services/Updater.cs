#nullable enable
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaseKeeper.Services;

/// <summary>What the update source says about a new version. Every field has passed <see cref="Updater.Parse"/>, and
/// the whole file has passed both signature checks, before one of these exists.</summary>
public sealed record UpdateInfo(Version Version, string Released, string Notes, bool SchemaChange, string FileName, Uri Url, long Bytes, string Sha256);

public enum UpdateVerdict { Verified, WrongProduct, WrongVersion, HashMismatch, SizeMismatch, Failed }

public sealed record VerifyResult(UpdateVerdict Verdict, string Message)
{
    public bool Ok => Verdict == UpdateVerdict.Verified;
}

/// <summary>What Settings → Updates remembers (updates.json in <see cref="AppPaths.SettingsFolder"/>). Nothing in it
/// is about your case. The GitHub token is encrypted with your Windows account (DPAPI) before it is saved.</summary>
public sealed class UpdateSettings
{
    /// <summary>"website" (default), "github" or "url" (an https address of your own, <see cref="Endpoint"/>).</summary>
    public string Source { get; set; } = "website";
    public string Endpoint { get; set; } = "";
    /// <summary>A GitHub access token, DPAPI-protected (CurrentUser) and base64. Only needed while the repository is private.</summary>
    public string TokenProtected { get; set; } = "";
    public bool AutoCheck { get; set; }
    /// <summary>"weekly" (default) or "daily".</summary>
    public string Interval { get; set; } = "weekly";
    public bool AutoDownload { get; set; }
    public DateTime LastCheck { get; set; }
    /// <summary>Set just before the installer starts; the next start compares it with the running version.</summary>
    public string PendingVersion { get; set; } = "";
    public string PendingFrom { get; set; } = "";
}

/// <summary>
/// Signed in-app updates for the website copy of CaseKeeper: the same scheme as DarkCipher ServerManagement.
/// <para><b>When it runs.</b> Only when you click Check for updates in Settings → Updates, or on the daily or weekly
/// schedule you can switch on there (off by default). Apart from the optional AI server you choose, nothing else in
/// CaseKeeper uses the network. A Microsoft Store
/// copy never uses this: the Store signs and delivers its updates.</para>
/// <para><b>Where it looks.</b> The DarkCipher Systems website by default (<see cref="DefaultEndpoint"/>), or the
/// repository's latest GitHub release (<see cref="GitHubRepo"/>; a read-only access token while the repository is
/// private), or an https address of your own. A request is a plain GET with a generic User-Agent, no cookies, no query
/// string and nothing about you, this PC or your case.</para>
/// <para><b>Trust.</b> The update information (update.json) is only believed when it carries two valid signatures from
/// the DarkCipher release keys pinned below: ECDSA P-256 (update.json.sig) and post-quantum ML-DSA-65
/// (update.json.pq.sig). The private keys never leave the release PC (tools\sign-update.ps1). The website or GitHub only
/// carry the files: nothing a hijacked server or account changes can pass. The signed file then vouches for the setup
/// program's exact size and SHA-256.</para>
/// <para><b>Checks.</b> Strict parsing; only a strictly newer x.y.z version (no downgrade, so an old signed release can't
/// be replayed as new); the file name tied to that version; the installer on the same https host as the update
/// information; a size cap. The download goes to a private temp folder and must match the signed size and SHA-256, be a
/// Windows program, and say it is CaseKeeper by DarkCipher Systems at exactly that version. It is checked again just
/// before it runs. Installing always goes through you: a backup prompt, a confirmation, then the normal setup wizard.</para>
/// </summary>
public static class Updater
{
    public const string ProductName = "DarkCipher CaseKeeper";
    public const string DefaultEndpoint = "https://darkciphersystems.com/downloads/casekeeper/update.json";
    /// <summary>The GitHub repository whose releases carry update.json, its two signatures and the setup program.</summary>
    public const string GitHubRepo = "DarkCipher0x/CaseKeeper";
    public static string GitHubLatestManifest => $"https://github.com/{GitHubRepo}/releases/latest/download/update.json";
    public const long MaxBytes = 400L << 20;
    private const int MaxManifestBytes = 256 * 1024;

    /// <summary>The CaseKeeper release key (ECDSA P-256 public point, hex), created once on 2026-11-08 by tools\sign-update.ps1 -NewKeys.
    /// Never change it: installed copies trust only this key.</summary>
    public const string ReleaseKeyX = "3B42EA5E44D32D6ED9DAAF417F2B0CAE02E59E1B7C6B0160466DA538744A3016";
    public const string ReleaseKeyY = "1E276336C77622A12E3B9F63C0A5437D236D2B0E5EB877311AC45A52D816E504";
    public static bool ReleaseKeyPinned => ReleaseKeyX.Length == 64 && ReleaseKeyY.Length == 64;

    /// <summary>The post-quantum CaseKeeper release key (ML-DSA-65 public key, base64), created once by
    /// tools\sign-update.ps1 -NewKeys on 2026-11-08. An update is offered only when update.json carries both signatures. Never change it.</summary>
    public const string ReleasePqKey = "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";
    public static bool ReleasePqKeyPinned => ReleasePqKey.Length > 2000;

    // ---- test seams (set only by tests\CaseKeeper.Tests.Updates) ----------------------------------------------------

    /// <summary>Test release keys (ECDSA X / Y hex, ML-DSA-65 base64) in place of the pinned ones.</summary>
    internal static (string X, string Y, string Pq)? TestKeys { get; set; }
    /// <summary>The installed version, in place of this assembly's.</summary>
    internal static Version? InstalledOverride { get; set; }
    /// <summary>Where updates.json lives, in place of the real settings folder.</summary>
    internal static string? SettingsFileOverride { get; set; }
    /// <summary>The temp folder for downloads, in place of %TEMP%\CaseKeeper-Update.</summary>
    internal static string? WorkFolderOverride { get; set; }
    /// <summary>Reads (ProductName, CompanyName, ProductVersion) from a program, in place of its version resource.</summary>
    internal static Func<string, (string? Product, string? Company, string? Version)>? VersionInfoReader { get; set; }

    private static bool KeysPinned => TestKeys != null || (ReleaseKeyPinned && ReleasePqKeyPinned);

    /// <summary>On for the website copy once the release keys are pinned. Checks still happen only when you click, or on
    /// the schedule you opt into. A Microsoft Store copy never uses it.</summary>
    public static bool Enabled => KeysPinned && !AppPackage.IsPackaged;

    public static string DisabledMessage => AppPackage.IsPackaged
        ? "This copy was installed from the Microsoft Store, which checks, signs and delivers CaseKeeper's updates (Microsoft Store → Library → Get updates). Your library stays where it is."
        : "In-app updates are turned off in this build: no release key is pinned. Download new versions from the website.";

    public static Version Installed => InstalledOverride ?? (Version.TryParse(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0], out var v) ? v : new Version(0, 0, 0));

    // ---- settings ---------------------------------------------------------------------------------------------------

    private static readonly object Gate = new();
    private static UpdateSettings? _settings;
    private static string SettingsFile => SettingsFileOverride ?? Path.Combine(AppPaths.SettingsFolder, "updates.json");

    public static UpdateSettings Settings
    {
        get
        {
            lock (Gate)
            {
                if (_settings != null) return _settings;
                try { _settings = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsFile)) : null; }
                catch (Exception ex) { AppLog.Warn("Updates", "The update settings couldn't be read; using the defaults", ex); }
                return _settings ??= new UpdateSettings();
            }
        }
    }

    public static void SaveSettings()
    {
        lock (Gate)
        {
            var s = Settings;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsFile, true);
        }
    }

    /// <summary>Forgets the in-memory copy (tests, and after Remove all data).</summary>
    internal static void ReloadSettings() { lock (Gate) _settings = null; }

    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("CaseKeeper.Updates.GitHubToken.v1");

    /// <summary>The GitHub access token, or null. Kept encrypted with your Windows account; sent only to api.github.com.</summary>
    public static string? GitHubToken
    {
        get
        {
            var p = Settings.TokenProtected;
            if (string.IsNullOrEmpty(p)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), TokenEntropy, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }
        set => Settings.TokenProtected = string.IsNullOrWhiteSpace(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), TokenEntropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>Why a pasted token is refused, or null.</summary>
    public static string? ValidateToken(string t) =>
        t.Length is < 20 or > 400 || t.Any(c => char.IsWhiteSpace(c) || c > 0x7e || c < 0x21) ? "That doesn't look like a GitHub access token." : null;

    public static bool FromGitHub => Settings.Source == "github";
    public static string CustomEndpoint => Settings.Source == "url" && !string.IsNullOrWhiteSpace(Settings.Endpoint) ? Settings.Endpoint.Trim() : DefaultEndpoint;
    /// <summary>Where the update information is read from.</summary>
    public static string Endpoint => FromGitHub ? GitHubLatestManifest : CustomEndpoint;
    public static TimeSpan CheckInterval => Settings.Interval == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
    public static bool AutoCheckDue => Enabled && Settings.AutoCheck && DateTime.Now - Settings.LastCheck >= CheckInterval;

    /// <summary>Why an address is refused, or null: https only, no user name or password in it, no query string.</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out var u) ? "That isn't a valid address."
        : u.Scheme != Uri.UriSchemeHttps ? "The update address must start with https://."
        : u.UserInfo.Length > 0 ? "The update address can't contain a user name or password."
        : u.Query.Length > 0 || u.Fragment.Length > 0 ? "The update address can't have a query (?…) or a #fragment."
        : !u.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "The update address must point at the update information file (…/update.json)."
        : null;

    // ---- fetching ---------------------------------------------------------------------------------------------------

    /// <summary>No automatic redirects (<see cref="FetchAsync"/> decides), no cookies, no proxy credentials of ours.</summary>
    private static HttpClient NewHttp(HttpMessageHandler? h) =>
        new(h ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, Credentials = null })
        { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", "DarkCipher-Updater" } } };

    /// <summary>github.com, its API and GitHub's release download hosts, nothing else.</summary>
    public static bool GitHubHost(Uri u) => u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || IsGitHubApi(u) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static bool IsGitHubApi(Uri u) => u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A GET with a size cap, following at most 5 redirects and only while <paramref name="allowed"/> accepts
    /// every hop (https only). <paramref name="apiToken"/> and <paramref name="accept"/> go only on requests to
    /// api.github.com, never with a redirect to the download host. Returns the status, the body (2xx only) or why it was
    /// refused.</summary>
    private static async Task<(int Status, byte[]? Body, string? Refused)> FetchAsync(HttpClient http, Uri url, long cap, Func<Uri, bool>? allowed, CancellationToken ct,
        IProgress<double>? progress = null, Stream? to = null, string? apiToken = null, string? accept = null)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps) return (0, null, "an address that isn't https");
            if (hop > 0 && (allowed == null || !allowed(url))) return (0, null, $"a redirect to {url.Host}, which isn't allowed");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsGitHubApi(url))
            {
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (accept != null) req.Headers.Accept.ParseAdd(accept);
                if (apiToken is { Length: > 0 }) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)r.StatusCode;
            if (code is >= 300 and < 400)
            {
                if (allowed == null || r.Headers.Location is not { } loc) return (code, null, null);
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                continue;
            }
            if (!r.IsSuccessStatusCode) return (code, null, null);
            if (r.Content.Headers.ContentLength > cap) return (code, null, "a larger file than allowed");
            await using var src = await r.Content.ReadAsStreamAsync(ct);
            var mem = to == null ? new MemoryStream() : null;
            var dst = to ?? mem!;
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > cap) return (code, null, "a larger file than announced");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                progress?.Report((double)total / cap);
            }
            return (code, mem?.ToArray() ?? Array.Empty<byte>(), null);
        }
        return (0, null, "too many redirects");
    }

    /// <summary>Fetches the update information and both signatures, verifies them, and parses it. Returns (info, message)
    /// when a newer version is available, (null, message) otherwise. Never throws for network trouble.</summary>
    public static async Task<(UpdateInfo? Info, string Message)> CheckAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        if (!Enabled) return (null, DisabledMessage);
        try
        {
            if (!FromGitHub) return await CheckUrlAsync(ct, handler);
            return GitHubToken is { } token ? await CheckGitHubApiAsync(token, ct, handler) : await CheckGitHubAsync(ct, handler);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, "Couldn't reach the update server: " + (ex is TaskCanceledException ? "it timed out." : ex.Message));
        }
    }

    private const string BadEcdsa = "The update information's signature doesn't match the CaseKeeper release key (tampered with, or not ours), so nothing is offered.";
    private const string BadPq = "The update information's post-quantum signature doesn't match the CaseKeeper release key (tampered with, or not ours), so nothing is offered.";

    /// <summary>Both signatures over the exact bytes: null when they pass, else why not.</summary>
    private static string? SignatureProblem(byte[] manifest, byte[]? sig, byte[]? pqSig, string where)
    {
        if (sig == null) return $"{where} has no signature (update.json.sig), so nothing is offered.";
        if (!VerifyManifest(manifest, sig)) return BadEcdsa;
        if (pqSig == null) return $"{where} has no post-quantum signature (update.json.pq.sig), so nothing is offered.";
        if (!VerifyManifestPq(manifest, pqSig)) return BadPq;
        return null;
    }

    private static async Task<(UpdateInfo? Info, string Message)> CheckUrlAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var url = CustomEndpoint;
        if (ValidateEndpoint(url) is { } bad) return (null, bad);
        var u = new Uri(url);
        using var http = NewHttp(handler);
        var (code, body, refused) = await FetchAsync(http, u, MaxManifestBytes, null, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No update information is published at {u.Host} yet.");
        if (code is >= 300 and < 400) return (null, "The update address redirected somewhere else; redirects aren't followed. Check the address in Settings → Updates.");
        if (body == null) return (null, $"The update server answered HTTP {code}.");
        var (_, sig, sr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".sig"), 4096, null, ct);
        var (_, pqSig, pr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".pq.sig"), 8192, null, ct);
        if (SignatureProblem(body, sr == null ? sig : null, pr == null ? pqSig : null, "The update information at " + u.Host) is { } why) return (null, why);
        return Parse(Utf8(body), u);
    }

    private static async Task<(UpdateInfo? Info, string Message)> CheckGitHubAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        using var http = NewHttp(handler);
        var (code, body, refused) = await FetchAsync(http, new Uri(GitHubLatestManifest), MaxManifestBytes, GitHubHost, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No release with update information is published on GitHub yet (github.com/{GitHubRepo}/releases), or the repository is private: add an access token below, or use the website.");
        if (body == null) return (null, $"GitHub answered HTTP {code}.");
        // Only the version (digits and dots) from the still-untrusted file goes into the next address.
        var ver = VersionField(body);
        if (ver == null) return (null, "The update information on GitHub is malformed (no valid version).");
        var release = new Uri($"https://github.com/{GitHubRepo}/releases/download/v{ver}/update.json");
        var (_, sig, sr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".sig"), 4096, GitHubHost, ct);
        var (_, pqSig, pr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".pq.sig"), 8192, GitHubHost, ct);
        if (SignatureProblem(body, sr == null ? sig : null, pr == null ? pqSig : null, $"The GitHub release v{ver}") is { } why) return (null, why);
        return Parse(Utf8(body), release);
    }

    /// <summary>A private repository: the latest release through api.github.com with your read-only token, each file as a
    /// release asset (the asset ids come from GitHub; the addresses are built here from the repository and the number).
    /// The same signature and parsing rules as the other sources.</summary>
    private static async Task<(UpdateInfo? Info, string Message)> CheckGitHubApiAsync(string token, CancellationToken ct, HttpMessageHandler? handler)
    {
        using var http = NewHttp(handler);
        var api = $"https://api.github.com/repos/{GitHubRepo}";
        var (code, body, refused) = await FetchAsync(http, new Uri(api + "/releases/latest"), 1024 * 1024, GitHubHost, ct, apiToken: token, accept: "application/vnd.github+json");
        if (refused != null) return (null, "The release information was refused: " + refused + ".");
        if (code is 401 or 403) return (null, "GitHub refused the access token (expired, revoked, or without read access to this repository). Check it in Settings → Updates.");
        if (code == 404) return (null, $"No release is published on GitHub yet, or the access token can't see github.com/{GitHubRepo}.");
        if (body == null) return (null, $"GitHub answered HTTP {code}.");
        var assets = new Dictionary<string, long>(StringComparer.Ordinal);
        string tag;
        try
        {
            var rel = JsonNode.Parse(Utf8(body)) as JsonObject ?? throw new FormatException();
            tag = rel["tag_name"]?.ToString() ?? "";
            foreach (var a in (rel["assets"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                if (a["name"]?.ToString() is { Length: > 0 } n && long.TryParse(a["id"]?.ToString(), out var id) && id > 0) assets[n] = id;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or JsonException) { return (null, "GitHub's release information is malformed."); }
        Uri Asset(long id) => new($"{api}/releases/assets/{id}");
        const string Octet = "application/octet-stream";
        if (!assets.TryGetValue("update.json", out var mid)) return (null, $"The latest GitHub release ({tag}) has no update information (update.json), so nothing is offered.");
        var (_, manifest, mr) = await FetchAsync(http, Asset(mid), MaxManifestBytes, GitHubHost, ct, apiToken: token, accept: Octet);
        if (mr != null || manifest == null) return (null, "Couldn't download the update information from GitHub" + (mr != null ? $": {mr}." : "."));
        byte[]? sig = null, pqSig = null;
        if (assets.TryGetValue("update.json.sig", out var sid)) { var (_, b, why) = await FetchAsync(http, Asset(sid), 4096, GitHubHost, ct, apiToken: token, accept: Octet); if (why == null) sig = b; }
        if (assets.TryGetValue("update.json.pq.sig", out var qid)) { var (_, b, why) = await FetchAsync(http, Asset(qid), 8192, GitHubHost, ct, apiToken: token, accept: Octet); if (why == null) pqSig = b; }
        if (SignatureProblem(manifest, sig, pqSig, $"The latest GitHub release ({tag})") is { } bad) return (null, bad);
        var ver = VersionField(manifest);
        if (ver == null || tag != "v" + ver) return (null, $"The signed update information (version {ver ?? "?"}) doesn't belong to the release {tag}, so nothing is offered.");
        var (info, msg) = Parse(Utf8(manifest), new Uri($"https://github.com/{GitHubRepo}/releases/download/v{ver}/update.json"));
        if (info == null) return (null, msg);
        if (!assets.TryGetValue(info.FileName, out var pid)) return (null, $"The release {tag} doesn't carry {info.FileName}, so nothing is offered.");
        return (info with { Url = Asset(pid) }, msg);
    }

    private static string? VersionField(byte[] json)
    {
        try
        {
            var v = (JsonNode.Parse(Utf8(json)) as JsonObject)?["version"]?.ToString();
            return v != null && Regex.IsMatch(v, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$") ? v : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b).TrimStart((char)0xFEFF);

    // ---- signatures -------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="sigFile"/> (base64 of an ECDSA P-256 / SHA-256 signature, r||s) signs exactly
    /// <paramref name="data"/> with the release key (or the given or test key).</summary>
    public static bool VerifyManifest(byte[] data, byte[] sigFile, string? keyX = null, string? keyY = null)
    {
        try
        {
            var sig = Convert.FromBase64String(Encoding.ASCII.GetString(sigFile).Trim());
            if (sig.Length != 64) return false;
            using var ec = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Convert.FromHexString(keyX ?? TestKeys?.X ?? ReleaseKeyX), Y = Convert.FromHexString(keyY ?? TestKeys?.Y ?? ReleaseKeyY) },
            });
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>True when <paramref name="pqSigFile"/> (base64 of an ML-DSA-65 signature) signs exactly <paramref name="data"/>
    /// with the post-quantum release key (or the given or test key, base64).</summary>
    public static bool VerifyManifestPq(byte[] data, byte[] pqSigFile, string? pubB64 = null)
    {
        try { return Pqc.Verify(Convert.FromBase64String(pubB64 ?? TestKeys?.Pq ?? ReleasePqKey), data, Convert.FromBase64String(Encoding.ASCII.GetString(pqSigFile).Trim())); }
        catch (FormatException) { return false; }
    }

    // ---- parsing ----------------------------------------------------------------------------------------------------

    public static string SetupFileName(Version v) => $"CaseKeeper-Setup-v{v}.exe";

    /// <summary>Strict parsing of update information that has already passed both signature checks. Never trusts an
    /// address, name or number it doesn't check.</summary>
    public static (UpdateInfo? Info, string Message) Parse(string json, Uri manifestUrl, Version? installed = null)
    {
        installed ??= Installed;
        JsonObject? o;
        try { o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false }) as JsonObject; }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return (null, "The update information is malformed (not JSON)."); }
        // Duplicate keys or wrongly typed values only surface when read: the same answer as malformed JSON.
        try { return ParseChecked(o, manifestUrl, installed); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException) { return (null, "The update information is malformed."); }
    }

    private static (UpdateInfo? Info, string Message) ParseChecked(JsonObject? o, Uri manifestUrl, Version installed)
    {
        if (o == null || o["product"]?.ToString() != ProductName) return (null, "The update information is for a different product.");
        var vs = o["version"]?.ToString() ?? "";
        if (!Regex.IsMatch(vs, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$") || !Version.TryParse(vs, out var v)) return (null, "The update information has no valid version (x.y.z).");
        if (v <= installed) return (null, v == installed ? $"You have the latest version ({installed})." : $"You have {installed}; the published version is {v}, which is older. Older versions are never offered.");
        var pkg = (o["packages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => p["kind"]?.ToString() == "setup" && p["arch"]?.ToString() == "x64");
        if (pkg == null) return (null, "The update information lists no Windows x64 setup program.");
        var file = pkg["file"]?.ToString() ?? "";
        if (file != SetupFileName(v)) return (null, "The setup program's file name doesn't match the version, so nothing is offered.");
        if (!Uri.TryCreate(manifestUrl, pkg["url"]?.ToString() ?? file, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals(manifestUrl.Host, StringComparison.OrdinalIgnoreCase)
            || url.UserInfo.Length > 0 || !url.AbsolutePath.EndsWith("/" + file, StringComparison.Ordinal) || url.Query.Length > 0 || url.Fragment.Length > 0)
            return (null, "The setup program must be on the same https server as the update information, so nothing is offered.");
        if (!long.TryParse(pkg["bytes"]?.ToString(), out var bytes) || bytes <= 0 || bytes > MaxBytes) return (null, "The setup program's size is missing or implausible, so nothing is offered.");
        var sha = pkg["sha256"]?.ToString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) return (null, "The setup program's checksum is missing, so nothing is offered.");
        var notes = o["notes"]?.ToString() ?? "";
        if (notes.Length > 8000) notes = notes[..8000] + "…";
        var released = o["released"]?.ToString() ?? "";
        if (!Regex.IsMatch(released, @"^\d{4}-\d{2}-\d{2}$")) released = "";
        bool schema = o["schemaChange"] is JsonValue sc && sc.TryGetValue<bool>(out var b) && b;
        return (new UpdateInfo(v, released, notes, schema, file, url, bytes, sha.ToLowerInvariant()), $"Version {v} is available (you have {installed}).");
    }

    // ---- download and verification ----------------------------------------------------------------------------------

    /// <summary>%TEMP%\CaseKeeper-Update: downloads, and the staged setup program while CaseKeeper closes.</summary>
    public static string WorkFolder => WorkFolderOverride ?? AppPaths.UpdateWorkFolder;
    private static string StagingDir => Path.Combine(WorkFolder, "ready");

    /// <summary>Downloads into a new private folder with the size cap (redirects only within GitHub for a GitHub release,
    /// none otherwise), then verifies. A download that fails any check is deleted.</summary>
    public static async Task<(string? Path, VerifyResult Result)> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var dir = Path.Combine(WorkFolder, "dl-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.FileName);
        try
        {
            using var http = NewHttp(handler);
            http.Timeout = TimeSpan.FromMinutes(20);
            int code; string? refused;
            bool api = IsGitHubApi(info.Url);
            await using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                (code, _, refused) = await FetchAsync(http, info.Url, Math.Min(info.Bytes, MaxBytes), GitHubHost(info.Url) ? GitHubHost : null, ct, progress, dst,
                    apiToken: api ? GitHubToken : null, accept: api ? "application/octet-stream" : null);
            if (refused != null)
            {
                TryDelete(dir);
                bool larger = refused.Contains("larger", StringComparison.Ordinal);
                return (null, new VerifyResult(larger ? UpdateVerdict.SizeMismatch : UpdateVerdict.Failed, larger ? "The download is larger than announced; it was stopped and deleted." : "The download was refused: " + refused + "."));
            }
            if (code is < 200 or >= 300) { TryDelete(dir); return (null, new VerifyResult(UpdateVerdict.Failed, $"The download failed: HTTP {code}.")); }
            var v = Verify(path, info);
            if (!v.Ok) { TryDelete(dir); return (null, v); }
            return (path, v);
        }
        catch (OperationCanceledException) { TryDelete(dir); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(dir);
            return (null, new VerifyResult(UpdateVerdict.Failed, "The download failed: " + ex.Message));
        }
    }

    private static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static (string? Product, string? Company, string? Version) ReadVersionInfo(string path)
    {
        if (VersionInfoReader != null) return VersionInfoReader(path);
        var fi = FileVersionInfo.GetVersionInfo(path);
        return (fi.ProductName?.Trim(), fi.CompanyName?.Trim(), fi.ProductVersion?.Trim());
    }

    /// <summary>Every check on a downloaded setup program, cheapest first: the size and SHA-256 the signed update
    /// information gave, a Windows program, and CaseKeeper by DarkCipher Systems at exactly that version.</summary>
    public static VerifyResult Verify(string path, UpdateInfo info)
    {
        var len = new FileInfo(path).Length;
        if (len != info.Bytes) return new(UpdateVerdict.SizeMismatch, $"The file is {len:N0} bytes, not the {info.Bytes:N0} announced, so it was refused.");
        using (var fs = File.OpenRead(path))
        {
            if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(UpdateVerdict.HashMismatch, "The SHA-256 doesn't match the signed checksum (damaged or substituted), so it was refused.");
            fs.Position = 0;
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return new(UpdateVerdict.WrongProduct, "The download isn't a Windows program, so it was refused.");
        }
        var (product, company, version) = ReadVersionInfo(path);
        if (product != "CaseKeeper" || company != "DarkCipher Systems")
            return new(UpdateVerdict.WrongProduct, $"The setup program says it is “{product}” by “{company}”, not CaseKeeper by DarkCipher Systems, so it was refused.");
        if (version != info.Version.ToString())
            return new(UpdateVerdict.WrongVersion, $"The setup program says version {version}, not {info.Version}, so it was refused.");
        return new(UpdateVerdict.Verified, $"Verified: the size and SHA-256 match the update information signed by both CaseKeeper release keys, and the setup program is CaseKeeper {info.Version} by DarkCipher Systems.");
    }

    // ---- install and the check after it ------------------------------------------------------------------------------

    /// <summary>An update the opt-in background download fetched and verified this session. Never saved: Settings →
    /// Updates offers it, and it is checked again before it runs.</summary>
    public static (UpdateInfo Info, string Path)? Ready { get; set; }

    /// <summary>Starts the setup wizard for an already verified setup program. It is copied to its own folder and verified
    /// again there first. What is expected is saved, so the next start can say whether the update happened. The caller
    /// then closes CaseKeeper.</summary>
    public static void LaunchInstaller(string path, UpdateInfo info)
    {
        TryDelete(StagingDir);
        Directory.CreateDirectory(StagingDir);
        var staged = Path.Combine(StagingDir, info.FileName);
        File.Copy(path, staged, true);
        var again = Verify(staged, info);
        if (!again.Ok) { TryDelete(StagingDir); throw new InvalidOperationException(again.Message); }
        Settings.PendingVersion = info.Version.ToString();
        Settings.PendingFrom = Installed.ToString();
        SaveSettings();
        AppLog.Info("Updates", $"Setup program started: {Installed} → {info.Version}");
        // Shell execute so Windows shows its usual permission prompt; the setup program's own wizard does the rest.
        Process.Start(new ProcessStartInfo(staged) { UseShellExecute = true, WorkingDirectory = StagingDir });
    }

    /// <summary>On start: if an update was started last time, compares the running version with what was expected and
    /// tidies the temp folder. Returns (message, completed), or null when nothing was pending.</summary>
    public static (string Message, bool Completed)? ConfirmPendingUpdate()
    {
        var s = Settings;
        if (string.IsNullOrEmpty(s.PendingVersion)) { CleanWorkFolder(); return null; }
        var expected = s.PendingVersion; var from = s.PendingFrom;
        s.PendingVersion = ""; s.PendingFrom = "";
        try { SaveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Updates", "Couldn't save the update settings", ex); }
        CleanWorkFolder();
        if (Installed.ToString() == expected)
        {
            AppLog.Info("Updates", $"Update verified: {from} → {expected}");
            return ($"Update verified: CaseKeeper {expected} is now running (it was {from}).", true);
        }
        AppLog.Warn("Updates", $"Update not completed: expected {expected}, running {Installed}");
        return ($"The update to {expected} didn't complete: CaseKeeper {Installed} is still running. Your library is unchanged.", false);
    }

    /// <summary>Deletes leftover downloads and the staged setup program (it may still be running just after an update,
    /// in which case it is left for next time).</summary>
    public static void CleanWorkFolder() => TryDelete(WorkFolder);
}
