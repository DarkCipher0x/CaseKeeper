using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CaseKeeper.Services;

// The updater logs through AppLog; the tests keep that off the real log folder.
namespace CaseKeeper.Services
{
    public static class AppLog
    {
        public static readonly List<string> Lines = new();
        public static void Info(string a, string m) => Lines.Add("INFO " + a + ": " + m);
        public static void Warn(string a, string m, Exception e = null) => Lines.Add("WARN " + a + ": " + m);
    }
}

/// <summary>
/// The signed updater, end to end, with throwaway test keys and a scripted stand-in for the website and GitHub.
/// Nothing here touches the internet, the real settings folder or the real release keys, and nothing is installed.
/// </summary>
static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    // ---- throwaway release keys -----------------------------------------------------------------------------------
    static ECDsa ec;
    static byte[] pqSeed;
    static string SignEc(byte[] data) => Convert.ToBase64String(ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n";
    static string SignPq(byte[] data) => Convert.ToBase64String(Pqc.Sign(pqSeed, data)) + "\n";

    // ---- a scripted server ----------------------------------------------------------------------------------------
    sealed class Stub : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new();
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(r);
            return Task.FromResult(Routes.TryGetValue(r.RequestUri!.AbsoluteUri, out var f) ? f(r) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public void Bytes(string url, byte[] b) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        public void Text(string url, string s) => Bytes(url, Encoding.UTF8.GetBytes(s));
        public void Redirect(string url, string to) => Routes[url] = _ => { var m = new HttpResponseMessage(HttpStatusCode.Found); m.Headers.Location = new Uri(to); return m; };
    }

    static readonly Version Installed = new(0, 5, 0);
    static readonly Version Next = new(0, 5, 1);
    static byte[] setup;          // the fake setup program: "MZ" and random bytes
    static string SetupSha => Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant();

    static string Manifest(string version = "0.5.1", string product = Updater.ProductName, string file = null, string url = null, long? bytes = null, string sha = null,
        string extra = "", string kind = "setup")
    {
        file ??= $"CaseKeeper-Setup-v{version}.exe";
        url ??= file;
        return $$"""
        {"product":"{{product}}","version":"{{version}}","label":"","released":"2026-11-08","notes":"Signed updates.","schemaChange":false,"signed":true,{{extra}}
         "packages":[{"kind":"{{kind}}","arch":"x64","file":"{{file}}","url":"{{url}}","bytes":{{bytes ?? setup.Length}},"sha256":"{{sha ?? SetupSha}}"}]}
        """;
    }

    /// <summary>Publishes update.json, both signatures and the setup program at an address on the stub.</summary>
    static void Publish(Stub s, string manifestUrl, string json, bool ecSig = true, bool pqSig = true, string tamper = null)
    {
        var b = Encoding.UTF8.GetBytes(json);
        s.Text(manifestUrl, tamper ?? json);
        if (ecSig) s.Text(manifestUrl + ".sig", SignEc(b));
        if (pqSig) s.Text(manifestUrl + ".pq.sig", SignPq(b));
    }

    static void ResetSettings(string dir)
    {
        Updater.SettingsFileOverride = Path.Combine(dir, "updates-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        Updater.ReloadSettings();
    }

    static async Task<(UpdateInfo Info, string Msg, Stub Stub)> Check(Action<Stub> arrange)
    {
        var s = new Stub();
        arrange(s);
        var (i, m) = await Updater.CheckAsync(CancellationToken.None, s);
        return (i, m, s);
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify") return VerifyRelease(args[1]);
        var root = Path.Combine(Path.GetTempPath(), "ckt", "updates-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try { await Run(root); }
        finally
        {
            Updater.TestKeys = null; Updater.InstalledOverride = null; Updater.SettingsFileOverride = null; Updater.WorkFolderOverride = null; Updater.VersionInfoReader = null;
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine(failures == 0 ? "\nAll updater checks passed." : $"\n{failures} updater check(s) FAILED.");
        return failures;
    }

    static async Task Run(string root)
    {
        // ---- the pinned release keys --------------------------------------------------------------------------------
        Check(Updater.ReleaseKeyPinned && Updater.ReleasePqKeyPinned, "pins: both release keys are pinned");
        bool onCurve;
        try
        {
            using var pinned = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromHexString(Updater.ReleaseKeyX), Y = Convert.FromHexString(Updater.ReleaseKeyY) } });
            pinned.ExportParameters(false); onCurve = true;
        }
        catch (CryptographicException) { onCurve = false; }
        Check(onCurve, "pins: the ECDSA release key is a valid P-256 point");
        Check(Convert.FromBase64String(Updater.ReleasePqKey).Length == 1952, "pins: the ML-DSA-65 release key is 1,952 bytes");

        // ---- signatures ---------------------------------------------------------------------------------------------
        ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ep = ec.ExportParameters(false);
        var (seed, pub) = Pqc.SigGenerate();
        pqSeed = seed;
        Updater.TestKeys = (Convert.ToHexString(ep.Q.X), Convert.ToHexString(ep.Q.Y), Convert.ToBase64String(pub));
        Updater.InstalledOverride = Installed;
        Updater.WorkFolderOverride = Path.Combine(root, "work");
        ResetSettings(root);

        var data = Encoding.UTF8.GetBytes("{\"product\":\"x\"}");
        Check(Updater.VerifyManifest(data, Encoding.ASCII.GetBytes(SignEc(data))), "ECDSA: a good signature verifies");
        var other = (byte[])data.Clone(); other[3] ^= 1;
        Check(!Updater.VerifyManifest(other, Encoding.ASCII.GetBytes(SignEc(data))), "ECDSA: one changed byte fails");
        Check(!Updater.VerifyManifest(data, Encoding.ASCII.GetBytes(SignEc(data)), Updater.ReleaseKeyX, Updater.ReleaseKeyY), "ECDSA: a signature from another key fails against the pinned key");
        Check(!Updater.VerifyManifest(data, Encoding.ASCII.GetBytes("not base64!")) && !Updater.VerifyManifest(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[63]))),
            "ECDSA: garbage and wrong-length signatures fail (no exception)");
        var der = Convert.ToBase64String(ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Check(!Updater.VerifyManifest(data, Encoding.ASCII.GetBytes(der)), "ECDSA: only the r||s form is accepted");

        Check(Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes(SignPq(data))), "ML-DSA-65: a good signature verifies");
        Check(!Updater.VerifyManifestPq(other, Encoding.ASCII.GetBytes(SignPq(data))), "ML-DSA-65: one changed byte fails");
        Check(!Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes(SignPq(data)), Updater.ReleasePqKey), "ML-DSA-65: a signature from another key fails against the pinned key");
        Check(!Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes("@@@")) && !Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[3309]))),
            "ML-DSA-65: garbage and all-zero signatures fail (no exception)");
        Check(Pqc.Sign(pqSeed, data).Length == 3309, "ML-DSA-65: signatures are 3,309 bytes");

        // ---- parsing ------------------------------------------------------------------------------------------------
        setup = new byte[48_000];
        RandomNumberGenerator.Fill(setup);
        setup[0] = (byte)'M'; setup[1] = (byte)'Z';
        var site = new Uri("https://darkciphersystems.com/downloads/casekeeper/update.json");
        (UpdateInfo, string) P(string json) => Updater.Parse(json, site, Installed);

        var (ok, okMsg) = P(Manifest());
        Check(ok != null && ok.Version == Next && ok.Url.AbsoluteUri == "https://darkciphersystems.com/downloads/casekeeper/CaseKeeper-Setup-v0.5.1.exe" && ok.Bytes == setup.Length && ok.Sha256 == SetupSha,
            "parse: a good file gives the version, the address beside update.json, the size and the checksum");
        Check(okMsg.Contains("0.5.1") && okMsg.Contains("0.5.0"), "parse: says which version is available and which is installed");
        Check(P(Manifest(product: "DarkCipher ServerManagement")).Item1 == null, "parse: another product is refused");
        Check(P(Manifest(version: "0.5.0")).Item2.Contains("latest"), "parse: the same version is not offered");
        Check(P(Manifest(version: "0.4.9")).Item1 == null && P(Manifest(version: "0.4.9")).Item2.Contains("older"), "parse: an older version (a replayed release) is never offered");
        foreach (var bad in new[] { "0.6", "0.6.0.1", "0.6.0-beta", " 0.6.0", "v0.6.0", "00000.1.1" })
            Check(P(Manifest(version: bad, file: "CaseKeeper-Setup-v0.6.0.exe")).Item1 == null, $"parse: version \"{bad}\" is refused");
        Check(P(Manifest(file: "CaseKeeper-Setup-v0.5.2.exe")).Item1 == null, "parse: a file name for another version is refused");
        Check(P(Manifest(file: "evil.exe", url: "evil.exe")).Item1 == null, "parse: any other file name is refused");
        Check(P(Manifest(kind: "msi")).Item1 == null, "parse: no x64 setup program, nothing offered");
        Check(P(Manifest(url: "https://evil.example/CaseKeeper-Setup-v0.5.1.exe")).Item1 == null, "parse: a setup program on another server is refused");
        Check(P(Manifest(url: "http://darkciphersystems.com/downloads/casekeeper/CaseKeeper-Setup-v0.5.1.exe")).Item1 == null, "parse: a plain-http setup program is refused");
        Check(P(Manifest(url: "CaseKeeper-Setup-v0.5.1.exe?x=1")).Item1 == null, "parse: a query string is refused");
        Check(P(Manifest(url: "https://user:pw@darkciphersystems.com/CaseKeeper-Setup-v0.5.1.exe")).Item1 == null, "parse: a user name in the address is refused");
        Check(P(Manifest(url: "other/CaseKeeper-Setup-v0.5.1.exe")).Item1 != null, "parse: another folder on the same https server is allowed");
        Check(P(Manifest(bytes: 0)).Item1 == null && P(Manifest(bytes: Updater.MaxBytes + 1)).Item1 == null && P(Manifest(bytes: -5)).Item1 == null, "parse: an implausible size is refused");
        Check(P(Manifest(sha: "abc")).Item1 == null && P(Manifest(sha: new string('g', 64))).Item1 == null, "parse: a missing or malformed checksum is refused");
        Check(P("not json").Item1 == null && P("[]").Item1 == null && P("").Item1 == null, "parse: malformed files are refused");
        Check(P(Manifest(extra: "\"version\":\"9.9.9\",")).Item1 == null, "parse: duplicate keys are refused");
        Check(P(new string('[', 200) + new string(']', 200)).Item1 == null, "parse: deeply nested input is refused (no stack trouble)");
        var longNotes = P(Manifest().Replace("\"Signed updates.\"", "\"" + new string('n', 20000) + "\"")).Item1;
        Check(longNotes != null && longNotes.Notes.Length <= 8001, "parse: release notes are capped");
        Check(P(Manifest().Replace("\"2026-11-08\"", "\"<script>\"")).Item1?.Released == "", "parse: a malformed release date is dropped");
        Check(P(Manifest().Replace("\"schemaChange\":false", "\"schemaChange\":\"yes\"")).Item1?.SchemaChange == false, "parse: a wrongly typed flag is read as false");
        Check(P(Manifest().Replace("\"schemaChange\":false", "\"schemaChange\":true")).Item1?.SchemaChange == true, "parse: the data-format warning flag is read");

        // ---- addresses ----------------------------------------------------------------------------------------------
        Check(Updater.ValidateEndpoint("https://darkciphersystems.com/downloads/casekeeper/update.json") == null, "address: the website is accepted");
        foreach (var bad in new[] { "http://x.example/update.json", "ftp://x.example/update.json", "https://u:p@x.example/update.json", "https://x.example/update.json?id=1", "https://x.example/update.json#a", "https://x.example/", "not a url", "file:///C:/update.json" })
            Check(Updater.ValidateEndpoint(bad) != null, $"address: \"{bad}\" is refused");

        // ---- the website (default source) ---------------------------------------------------------------------------
        Check(Updater.Enabled && !Updater.FromGitHub && Updater.Endpoint == Updater.DefaultEndpoint, "source: the website is the default");
        var w = Updater.DefaultEndpoint;
        var (wi, wm, ws) = await Check(s => Publish(s, w, Manifest()));
        Check(wi != null && wi.Version == Next, "website: a signed newer version is offered");
        Check(ws.Seen.All(r => r.Method == HttpMethod.Get && r.RequestUri!.Query == "" && r.Headers.Authorization == null && !r.Headers.Contains("Cookie")),
            "website: requests are plain GETs with no query, no credentials and no cookies");
        Check(ws.Seen.All(r => r.Headers.UserAgent.ToString() == "DarkCipher-Updater"), "website: a generic User-Agent, nothing about this PC");
        Check(ws.Seen.Select(r => r.RequestUri!.AbsoluteUri).SequenceEqual(new[] { w, w + ".sig", w + ".pq.sig" }), "website: only update.json and its two signatures are fetched");

        (wi, wm, _) = await Check(s => Publish(s, w, Manifest(), ecSig: false));
        Check(wi == null && wm.Contains("no signature"), "website: no ECDSA signature, nothing offered");
        (wi, wm, _) = await Check(s => Publish(s, w, Manifest(), pqSig: false));
        Check(wi == null && wm.Contains("post-quantum"), "website: no post-quantum signature, nothing offered");
        (wi, wm, _) = await Check(s => Publish(s, w, Manifest(), tamper: Manifest(sha: new string('a', 64))));
        Check(wi == null && wm.Contains("doesn't match"), "website: a changed update.json fails the signature");
        (wi, wm, _) = await Check(s => { Publish(s, w, Manifest()); s.Text(w + ".pq.sig", SignPq(Encoding.UTF8.GetBytes(Manifest(version: "0.5.2")))); });
        Check(wi == null && wm.Contains("post-quantum"), "website: a post-quantum signature over other bytes fails even when ECDSA passes");
        (wi, wm, _) = await Check(s => { Publish(s, w, Manifest()); s.Text(w + ".sig", SignEc(Encoding.UTF8.GetBytes(Manifest(version: "0.5.2")))); });
        Check(wi == null && wm.Contains("doesn't match"), "website: an ECDSA signature over other bytes fails even when ML-DSA passes");
        (wi, wm, _) = await Check(s => Publish(s, w, Manifest(version: "0.4.0")));
        Check(wi == null && wm.Contains("older"), "website: an old, correctly signed release is not offered");
        (wi, wm, _) = await Check(_ => { });
        Check(wi == null && wm.Contains("No update information"), "website: nothing published yet is explained");
        (wi, wm, _) = await Check(s => s.Redirect(w, "https://evil.example/update.json"));
        Check(wi == null && wm.Contains("redirect"), "website: redirects are not followed");
        (wi, wm, _) = await Check(s => s.Bytes(w, new byte[300 * 1024]));
        Check(wi == null && wm.Contains("refused"), "website: an oversized update.json is refused");
        var (ti, tm, _) = await Check(s => s.Routes[w] = _ => throw new HttpRequestException("no route to host"));
        Check(ti == null && tm.StartsWith("Couldn't reach"), "website: a network failure is a message, not a crash");

        // ---- an address of your own ---------------------------------------------------------------------------------
        Updater.Settings.Source = "url"; Updater.Settings.Endpoint = "https://mirror.example/ck/update.json";
        (wi, wm, _) = await Check(s => Publish(s, "https://mirror.example/ck/update.json", Manifest()));
        Check(wi != null && wi.Url.Host == "mirror.example", "own address: works the same, with the setup program beside it");
        Updater.Settings.Endpoint = "http://mirror.example/ck/update.json";
        (wi, wm, _) = await Check(_ => { });
        Check(wi == null && wm.Contains("https"), "own address: plain http is refused before anything is sent");
        Updater.Settings.Source = "website"; Updater.Settings.Endpoint = "";

        // ---- a public GitHub release --------------------------------------------------------------------------------
        Updater.Settings.Source = "github";
        var latest = Updater.GitHubLatestManifest;
        var rel = $"https://github.com/{Updater.GitHubRepo}/releases/download/v0.5.1/update.json";
        var cdn = "https://objects.githubusercontent.com/release-assets/1/update.json";
        (wi, wm, _) = await Check(s =>
        {
            s.Redirect(latest, rel); s.Redirect(rel, cdn); Publish(s, cdn, Manifest());
            s.Redirect(rel + ".sig", cdn + ".sig"); s.Redirect(rel + ".pq.sig", cdn + ".pq.sig");
        });
        Check(wi != null && wi.Url.AbsoluteUri == $"https://github.com/{Updater.GitHubRepo}/releases/download/v0.5.1/CaseKeeper-Setup-v0.5.1.exe",
            "GitHub: redirects within GitHub are followed, and the setup program comes from that release");
        (wi, wm, _) = await Check(s => { s.Redirect(latest, "https://evil.example/update.json"); Publish(s, "https://evil.example/update.json", Manifest()); });
        Check(wi == null && wm.Contains("isn't allowed"), "GitHub: a redirect away from GitHub is refused");
        (wi, wm, _) = await Check(s =>
        {
            s.Redirect(latest, "https://github.com/x"); s.Redirect("https://github.com/x", "https://github.com/y"); s.Redirect("https://github.com/y", "https://github.com/z");
            s.Redirect("https://github.com/z", "https://github.com/a"); s.Redirect("https://github.com/a", "https://github.com/b"); s.Redirect("https://github.com/b", "https://github.com/c");
            s.Redirect("https://github.com/c", "https://github.com/d");
        });
        Check(wi == null && wm.Contains("too many redirects"), "GitHub: redirect loops stop after five hops");
        (wi, wm, _) = await Check(s => { s.Text(latest, Manifest()); s.Text(rel + ".sig", SignEc(Encoding.UTF8.GetBytes(Manifest()))); });
        Check(wi == null && wm.Contains("post-quantum"), "GitHub: a release without update.json.pq.sig offers nothing");
        (wi, wm, _) = await Check(s => s.Text(latest, Manifest().Replace("\"0.5.1\"", "\"../../x\"")));
        Check(wi == null && wm.Contains("malformed"), "GitHub: an unsafe version can't steer the next address");

        // ---- a private GitHub release, through the API with a token -------------------------------------------------
        Updater.GitHubToken = "github_pat_TESTONLY_0123456789abcdefghijklmnop";
        var api = $"https://api.github.com/repos/{Updater.GitHubRepo}";
        string Release(string tag, bool withPq = true) => $$"""
            {"tag_name":"{{tag}}","assets":[{"name":"update.json","id":11},{"name":"update.json.sig","id":12}{{(withPq ? ",{\"name\":\"update.json.pq.sig\",\"id\":13}" : "")}},{"name":"CaseKeeper-Setup-v0.5.1.exe","id":14}]}
            """;
        void Assets(Stub s, string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            s.Text(api + "/releases/assets/11", json); s.Text(api + "/releases/assets/12", SignEc(b)); s.Text(api + "/releases/assets/13", SignPq(b));
        }
        Stub seen;
        (wi, wm, seen) = await Check(s => { s.Text(api + "/releases/latest", Release("v0.5.1")); Assets(s, Manifest()); });
        Check(wi != null && wi.Url.AbsoluteUri == api + "/releases/assets/14", "private GitHub: the setup program is the release asset");
        Check(seen.Seen.All(r => r.RequestUri!.Host == "api.github.com" && r.Headers.Authorization?.Scheme == "Bearer"), "private GitHub: the token goes to api.github.com only");
        (wi, wm, _) = await Check(s => { s.Text(api + "/releases/latest", Release("v0.6.0")); Assets(s, Manifest()); });
        Check(wi == null && wm.Contains("doesn't belong"), "private GitHub: update information from another release is refused");
        (wi, wm, _) = await Check(s => { s.Text(api + "/releases/latest", Release("v0.5.1", withPq: false)); Assets(s, Manifest()); });
        Check(wi == null && wm.Contains("post-quantum"), "private GitHub: no post-quantum signature asset, nothing offered");
        (wi, wm, _) = await Check(s => s.Routes[api + "/releases/latest"] = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Check(wi == null && wm.Contains("access token"), "private GitHub: a refused token is explained");
        (wi, wm, _) = await Check(s => s.Text(api + "/releases/latest", "{\"tag_name\":5,\"assets\":\"x\"}"));
        Check(wi == null, "private GitHub: malformed release information offers nothing");

        // The download from the API: the token goes with the API request but never follows a redirect to the CDN
        var (apiInfo, _, _) = await Check(s => { s.Text(api + "/releases/latest", Release("v0.5.1")); Assets(s, Manifest()); });
        Updater.VersionInfoReader = _ => ("CaseKeeper", "DarkCipher Systems", "0.5.1");
        var dl = new Stub();
        dl.Redirect(api + "/releases/assets/14", "https://objects.githubusercontent.com/release-assets/14");
        dl.Bytes("https://objects.githubusercontent.com/release-assets/14", setup);
        var (gp, gv) = await Updater.DownloadAndVerifyAsync(apiInfo, null, CancellationToken.None, dl);
        Check(gv.Ok && gp != null, "private GitHub: the setup program downloads and verifies");
        Check(dl.Seen[0].Headers.Authorization != null && dl.Seen.Skip(1).All(r => r.Headers.Authorization == null), "private GitHub: the token is not sent to GitHub's download host");
        if (gp != null) Directory.Delete(Path.GetDirectoryName(gp)!, true);

        // ---- the token is stored encrypted --------------------------------------------------------------------------
        Updater.SaveSettings();
        var saved = File.ReadAllText(Updater.SettingsFileOverride);
        Check(!saved.Contains("github_pat_") && saved.Contains("TokenProtected"), "settings: the token is saved encrypted (DPAPI), never in plain text");
        Updater.ReloadSettings();
        Check(Updater.GitHubToken == "github_pat_TESTONLY_0123456789abcdefghijklmnop" && Updater.Settings.Source == "github", "settings: saved and read back");
        Updater.Settings.TokenProtected = "AAAA";
        Check(Updater.GitHubToken == null, "settings: an unreadable token is treated as none (no crash)");
        Check(Updater.ValidateToken("ghp_" + new string('a', 36)) == null && Updater.ValidateToken("has space in it 0123456789") != null && Updater.ValidateToken("short") != null,
            "settings: a pasted token is sanity-checked");
        Updater.GitHubToken = null;
        Check(Updater.Settings.TokenProtected == "", "settings: removing the token clears it");
        File.WriteAllText(Updater.SettingsFileOverride, "{ not json");
        Updater.ReloadSettings();
        Check(Updater.Settings.Source == "website" && !Updater.Settings.AutoCheck, "settings: a damaged file falls back to the safe defaults (website, no automatic check)");

        // ---- automatic checks are opt-in --------------------------------------------------------------------------
        ResetSettings(root);
        Check(!Updater.AutoCheckDue, "schedule: off by default");
        Updater.Settings.AutoCheck = true;
        Check(Updater.AutoCheckDue, "schedule: due when switched on and never run");
        Updater.Settings.LastCheck = DateTime.Now.AddDays(-2);
        Check(!Updater.AutoCheckDue, "schedule: weekly by default (not due after two days)");
        Updater.Settings.Interval = "daily";
        Check(Updater.AutoCheckDue, "schedule: daily is due after two days");
        ResetSettings(root);

        // ---- download and verification ----------------------------------------------------------------------------
        var info = ok;
        var srv = new Stub();
        srv.Bytes(info.Url.AbsoluteUri, setup);
        var progress = new List<double>();
        var (path, v) = await Updater.DownloadAndVerifyAsync(info, new SyncProgress(progress.Add), CancellationToken.None, srv);
        Check(v.Ok && path != null && File.Exists(path) && path.StartsWith(Updater.WorkFolder, StringComparison.OrdinalIgnoreCase), "download: verified, in the private temp folder");
        Check(progress.Count > 0 && progress.All(p => p is > 0 and <= 1), "download: progress is reported");

        async Task<VerifyResult> Bad(byte[] body, UpdateInfo i = null)
        {
            var s = new Stub(); s.Bytes((i ?? info).Url.AbsoluteUri, body);
            var (p, r) = await Updater.DownloadAndVerifyAsync(i ?? info, null, CancellationToken.None, s);
            Check(p == null, "download: a refused file leaves no path");
            return r;
        }
        var changed = (byte[])setup.Clone(); changed[^1] ^= 1;
        Check((await Bad(changed)).Verdict == UpdateVerdict.HashMismatch, "download: one changed byte is refused (SHA-256)");
        Check((await Bad(setup[..^1])).Verdict == UpdateVerdict.SizeMismatch, "download: a short file is refused");
        Check((await Bad(setup.Concat(new byte[10]).ToArray())).Verdict == UpdateVerdict.SizeMismatch, "download: a longer file is stopped at the announced size");
        var notExe = (byte[])setup.Clone(); notExe[0] = (byte)'P'; notExe[1] = (byte)'K';
        Check((await Bad(notExe, info with { Sha256 = Convert.ToHexString(SHA256.HashData(notExe)).ToLowerInvariant() })).Verdict == UpdateVerdict.WrongProduct, "download: a file that isn't a Windows program is refused");
        Updater.VersionInfoReader = _ => ("Some Other App", "DarkCipher Systems", "0.5.1");
        Check((await Bad(setup)).Verdict == UpdateVerdict.WrongProduct, "download: another product's setup program is refused");
        Updater.VersionInfoReader = _ => ("CaseKeeper", "Someone Else", "0.5.1");
        Check((await Bad(setup)).Verdict == UpdateVerdict.WrongProduct, "download: another publisher is refused");
        Updater.VersionInfoReader = _ => ("CaseKeeper", "DarkCipher Systems", "0.4.2");
        Check((await Bad(setup)).Verdict == UpdateVerdict.WrongVersion, "download: a setup program for another version is refused");
        Updater.VersionInfoReader = _ => ("CaseKeeper", "DarkCipher Systems", "0.5.1");
        var notFound = new Stub();
        var (np, nv) = await Updater.DownloadAndVerifyAsync(info, null, CancellationToken.None, notFound);
        Check(np == null && nv.Verdict == UpdateVerdict.Failed && nv.Message.Contains("404"), "download: a missing file is a message");
        var odd = new Stub(); odd.Redirect(info.Url.AbsoluteUri, "https://evil.example/x.exe");
        var (op, ov) = await Updater.DownloadAndVerifyAsync(info, null, CancellationToken.None, odd);
        Check(op == null && !ov.Ok, "download: the website's setup program is never fetched through a redirect");
        Check(Directory.GetDirectories(Updater.WorkFolder).Length == 1, "download: refused downloads are deleted, only the verified one is kept");

        // ---- the check just before it runs, and after the update ---------------------------------------------------
        File.AppendAllText(path, "x");
        bool threw = false;
        try { Updater.LaunchInstaller(path, info); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && Updater.Settings.PendingVersion == "", "install: a file changed after the download is refused just before it would run, and nothing is recorded");

        Updater.Settings.PendingVersion = "0.5.0"; Updater.Settings.PendingFrom = "0.4.2"; Updater.SaveSettings();
        var done = Updater.ConfirmPendingUpdate();
        Check(done is { Completed: true } && done.Value.Message.Contains("0.5.0") && Updater.Settings.PendingVersion == "", "after the update: the new version is confirmed, once");
        Check(!Directory.Exists(Updater.WorkFolder), "after the update: downloads and the staged setup program are tidied away");
        Check(Updater.ConfirmPendingUpdate() == null, "after the update: nothing to report on the next start");
        Updater.Settings.PendingVersion = "0.5.1"; Updater.Settings.PendingFrom = "0.5.0"; Updater.SaveSettings();
        var notDone = Updater.ConfirmPendingUpdate();
        Check(notDone is { Completed: false } && notDone.Value.Message.Contains("didn't complete"), "after the update: an update that didn't happen is said plainly");

        // ---- the real setup program's version information -----------------------------------------------------------
        Updater.VersionInfoReader = null;
        var real = Directory.Exists(Path.Combine(RepoRoot(), "dist"))
            ? Directory.GetFiles(Path.Combine(RepoRoot(), "dist"), "CaseKeeper-Setup-v*.exe", SearchOption.AllDirectories).OrderBy(f => f).LastOrDefault() : null;
        if (real == null) Console.WriteLine("SKIP the real setup program's version information (no dist\\ build on this PC)");
        else
        {
            var ver = Version.Parse(Path.GetFileNameWithoutExtension(real)["CaseKeeper-Setup-v".Length..]);
            var bytes = File.ReadAllBytes(real);
            var realInfo = new UpdateInfo(ver, "", "", false, Path.GetFileName(real), new Uri("https://darkciphersystems.com/" + Path.GetFileName(real)), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            var rv = Updater.Verify(real, realInfo);
            Check(rv.Ok, $"real setup program: {Path.GetFileName(real)} says CaseKeeper by DarkCipher Systems, version {ver} ({rv.Message})");
        }

        // ---- the Store copy and a build without keys ------------------------------------------------------------------
        Updater.TestKeys = null;
        Check(Updater.Enabled == (!AppPackage.IsPackaged), "Store copy: off when installed from the Store (this test isn't), on for the website copy");
        Check(Updater.DisabledMessage.Length > 0, "Store copy: says why");
    }

    /// <summary>build-release.ps1: checks a signed release the way an installed copy will, with the app's own code and
    /// the pinned keys: both signatures over update.json, strict parsing, and the setup program beside it.</summary>
    static int VerifyRelease(string manifest)
    {
        var bytes = File.ReadAllBytes(manifest);
        Check(File.Exists(manifest + ".sig") && Updater.VerifyManifest(bytes, File.ReadAllBytes(manifest + ".sig")), "release: update.json.sig verifies against the pinned ECDSA key");
        Check(File.Exists(manifest + ".pq.sig") && Updater.VerifyManifestPq(bytes, File.ReadAllBytes(manifest + ".pq.sig")), "release: update.json.pq.sig verifies against the pinned ML-DSA-65 key");
        var (info, msg) = Updater.Parse(Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF), new Uri(Updater.DefaultEndpoint), new Version(0, 0, 0));
        Check(info != null, "release: update.json parses (" + msg + ")");
        if (info != null)
        {
            var exe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, info.FileName);
            var v = File.Exists(exe) ? Updater.Verify(exe, info) : new VerifyResult(UpdateVerdict.Failed, "missing " + info.FileName);
            Check(v.Ok, "release: " + v.Message);
        }
        return failures;
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "build-release.ps1"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    /// <summary>Progress reported straight away (Progress&lt;T&gt; posts to a thread pool, after the test has moved on).</summary>
    sealed class SyncProgress(Action<double> f) : IProgress<double> { public void Report(double v) => f(v); }
}
