using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace CaseKeeper.Services;

/// <summary>
/// Talks to an Ollama server (your own PC by default) to summarise and analyse case material.
///
/// Privacy: nothing is sent anywhere until AI is switched on in Settings. Only the address you set
/// is contacted. Addresses on other machines must be allowed explicitly, and must use HTTPS unless
/// they're on your own network. Each request is recorded in the audit trail (what kind of report,
/// which model, how much text) but the content itself is not logged.
/// </summary>
public static class AiService
{
    private static Data.Database Db => App.Library.Db;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(8),
        AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // ------------------------------------------------------------------ settings
    public static bool Enabled => Db.GetSetting("ai.enabled", "0") == "1";
    public static string Endpoint => (Db.GetSetting("ai.endpoint", "http://localhost:11434") ?? "").Trim().TrimEnd('/');
    public static string Model => Db.GetSetting("ai.model", "") ?? "";
    public static double Temperature => double.TryParse(Db.GetSetting("ai.temperature", "0.2"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var t) ? Math.Clamp(t, 0, 2) : 0.2;
    public static int ContextTokens => int.TryParse(Db.GetSetting("ai.numCtx", "8192"), out var n) ? Math.Clamp(n, 2048, 262144) : 8192;
    public static int MaxOutputTokens => int.TryParse(Db.GetSetting("ai.numPredict", "2048"), out var n) ? Math.Clamp(n, 128, 32768) : 2048;
    /// <summary>"auto", "cpu" or "gpu".</summary>
    public static string GpuMode => Db.GetSetting("ai.gpu", "auto") ?? "auto";
    public static int ThreadOverride => int.TryParse(Db.GetSetting("ai.threads", "0"), out var n) ? Math.Max(0, n) : 0;
    public static string KeepAlive => Db.GetSetting("ai.keepAlive", "10m") ?? "10m";
    public static int TimeoutMinutes => int.TryParse(Db.GetSetting("ai.timeoutMinutes", "20"), out var n) ? Math.Clamp(n, 1, 240) : 20;
    public static bool AllowRemote => Db.GetSetting("ai.allowRemote", "0") == "1";
    public static bool IncludeCaseSummary => Db.GetSetting("ai.includeCaseSummary", "1") == "1";

    // ------------------------------------------------------------------ provider (local or cloud)
    /// <summary>Which AI CaseKeeper uses: "local" (Ollama, the default) or one of the cloud services in <see cref="AiProviders"/>.</summary>
    public static string Provider => AiProviders.Get(Db.GetSetting("ai.provider", AiProviders.Local)).Id;
    public static bool UsingCloud => AiProviders.IsCloud(Provider);
    /// <summary>Replace names and other personal details with placeholders before anything goes to a cloud AI. On unless turned off.</summary>
    public static bool MaskCloud => Db.GetSetting("ai.cloud.mask", "1") != "0";
    /// <summary>Documents read at once by a cloud AI (it slows down by itself if the service says it's busy).</summary>
    public static int CloudWorkers => int.TryParse(Db.GetSetting("ai.cloud.concurrency", "4"), out var n) ? Math.Clamp(n, 1, 8) : 4;

    public static string ProviderModel(string provider) =>
        (Db.GetSetting($"ai.{provider}.model", "") ?? "").Trim() is { Length: > 0 } m ? m : AiProviders.Get(provider).DefaultModel;
    public static string ProviderEffort(string provider) =>
        (Db.GetSetting($"ai.{provider}.effort", "") ?? "").Trim() is { Length: > 0 } e && AiProviders.Get(provider).Efforts.Any(x => x.Id == e)
            ? e : AiProviders.Get(provider).DefaultEffort;
    public static string ProviderResource(string provider) => (Db.GetSetting($"ai.{provider}.resource", "") ?? "").Trim();

    /// <summary>A cloud service's key, encrypted with Windows (DPAPI) for this Windows account only. Never exported or logged.</summary>
    public static string ProviderKey(string provider)
    {
        var stored = Db.GetSetting($"ai.{provider}.key", "");
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), KeyEntropy(provider), DataProtectionScope.CurrentUser)); }
        catch { return ""; }   // encrypted for another account or PC: ask again
    }

    public static void SetProviderKey(string provider, string key)
    {
        if (string.IsNullOrEmpty(key)) { Db.SetSetting($"ai.{provider}.key", ""); return; }
        var bytes = Encoding.UTF8.GetBytes(key);
        try { Db.SetSetting($"ai.{provider}.key", Convert.ToBase64String(ProtectedData.Protect(bytes, KeyEntropy(provider), DataProtectionScope.CurrentUser))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool HasProviderKey(string provider) => !string.IsNullOrEmpty(Db.GetSetting($"ai.{provider}.key", ""));

    private static byte[] KeyEntropy(string provider) => Encoding.UTF8.GetBytes($"CaseKeeper.ai.{provider}.key.v1");

    /// <summary>Everything a cloud request needs, read fresh from Settings (the key is decrypted just for this).</summary>
    public static CloudConfig CloudConfigFor(string provider, string model = null, string effort = null) =>
        new(provider, ProviderKey(provider), ProviderResource(provider), string.IsNullOrWhiteSpace(model) ? ProviderModel(provider) : model.Trim(),
            string.IsNullOrWhiteSpace(effort) ? ProviderEffort(provider) : effort, TimeoutMinutes);

    /// <summary>A masker that knows the case's own people (the most reliable names to hide).</summary>
    public static Masker NewMasker()
    {
        var parties = Db.GetParties();
        return new Masker(parties.Select(p => p.Name).Concat(parties.Select(p => p.Representative)), parties.Select(p => p.Contact));
    }

    /// <summary>The model in use, for labels.</summary>
    public static string ActiveModel => UsingCloud ? ProviderModel(Provider) : Model;
    /// <summary>Where requests go, for labels and the audit trail.</summary>
    public static string ActiveHost => UsingCloud ? AiProviders.BaseAddress(Provider, ProviderResource(Provider)).Host
        : Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) ? u.Host : Endpoint;
    /// <summary>"gemma3:4b on your Ollama server" or "claude-haiku-5-5 from Anthropic (Claude) (api.anthropic.com), personal details masked".</summary>
    public static string ActiveDescription => UsingCloud
        ? $"{ActiveModel} from {AiProviders.Get(Provider).Name} ({ActiveHost}){(MaskCloud ? ", personal details masked" : "")}"
        : $"{(string.IsNullOrWhiteSpace(Model) ? "(no model chosen)" : Model)} on your Ollama server";

    /// <summary>Roughly how much text fits in one request, leaving room for the instructions and the answer.</summary>
    public static int ChunkChars => UsingCloud ? 160_000 : Math.Max(4000, (int)((ContextTokens - MaxOutputTokens - 800) * 3.2));

    public static string ApiKey
    {
        get
        {
            var stored = Db.GetSetting("ai.apiKey", "");
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
            }
            catch
            {
                // Encrypted for another Windows account or PC (e.g. after restoring a backup): ask again
                return "";
            }
        }
        set => Db.SetSetting("ai.apiKey", string.IsNullOrEmpty(value) ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser)));
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CaseKeeper.ai.apiKey.v1");

    // ------------------------------------------------------------------ endpoint safety
    public static bool IsLocal(Uri u) =>
        u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(u.Host, out var ip) && IsPrivate(ip)) ||
        u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivate(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || System.Net.IPAddress.IsLoopback(ip) || (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>Checks the configured address. Null when it's fine to use; otherwise why not.</summary>
    public static string CheckEndpoint(string endpoint, bool allowRemote)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "The address must start with http:// or https://, e.g. http://localhost:11434";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Don't put a user name or password in the address. Use the API key box instead.";
        bool local = IsLocal(u);
        if (!local && !allowRemote)
            return "That address isn't on this PC or your home network. Tick 'Allow a server on another network' if you really mean " +
                   "to send case material there.";
        if (!local && u.Scheme != "https")
            return "A server outside your network must use https://, so your case material is encrypted on the way.";
        return null;
    }

    /// <summary>Makes sure AI is switched on and set up, offering to go to Settings if not. Cloud AI asks for consent once.</summary>
    public static bool EnsureEnabled(Window owner, string provider = null)
    {
        provider ??= Provider;
        bool cloud = AiProviders.IsCloud(provider);
        string problem = !Enabled ? "The AI tools are switched off."
            : cloud ? AiProviders.Check(CloudConfigFor(provider))
            : string.IsNullOrWhiteSpace(Model) ? "No AI model has been chosen yet."
            : CheckEndpoint(Endpoint, AllowRemote);
        // Bulk import shows its own detailed review before every cloud run, so the one-off consent is for everything else
        if (problem == null) return !cloud || provider != Provider || EnsureCloudConsent(owner);
        if (MessageBox.Show(owner, problem + (cloud ? "" : "\n\nCaseKeeper's AI features use Ollama, a free program that runs AI models on your own " +
                                   "computer so your documents never leave it, or a cloud AI service you choose.") + "\n\nOpen the AI settings now?", "AI tools",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Views.MainWindow.Shell?.GoToSettings("ai");
        return false;
    }

    /// <summary>
    /// The first time a cloud AI is used for anything, says plainly where material will go and asks to continue.
    /// Asked again for each provider. Bulk Case Import also shows its own review before every run.
    /// </summary>
    public static bool HasCloudConsent(string provider) => Db.GetSetting($"ai.cloud.consent.{provider}", "0") == "1";

    public static bool EnsureCloudConsent(Window owner, string provider = null)
    {
        provider ??= Provider;
        var key = $"ai.cloud.consent.{provider}";
        if (Db.GetSetting(key, "0") == "1") return true;
        var p = AiProviders.Get(provider);
        var host = AiProviders.BaseAddress(provider, ProviderResource(provider)).Host;
        if (MessageBox.Show(owner,
                $"The AI tools are set to use {p.Name}.\n\nWhatever you ask the AI to read (documents, notes, the case summary) will be sent over an " +
                $"encrypted connection to {host}, which is outside this PC. " +
                (MaskCloud ? "Names and other personal details CaseKeeper recognises are replaced with placeholders first, and put back in the answer here. Masking is best effort and can miss things."
                           : "Masking is OFF, so personal details are sent as they are.") + "\n\n" +
                (p.IsFoundry ? "With Microsoft Foundry it stays in your own Azure subscription, under your Azure terms."
                             : $"It's handled under {p.Name}'s API terms, which can include keeping it for a limited time.") +
                " CaseKeeper records that you agreed, never the content.\n\nUse this cloud AI?", "Cloud AI – what is sent",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        Db.SetSetting(key, "1");
        Db.Audit("ai.consent", "ai", null, $"Agreed to use {p.Name} for AI features", $"Host: {host}. Masking: {(MaskCloud ? "on" : "off")}.");
        return true;
    }

    /// <summary>Forgets every "agreed to use this cloud AI", so each asks again.</summary>
    public static void ResetCloudConsent()
    {
        foreach (var p in AiProviders.All.Where(p => p.IsCloud))
        {
            Db.SetSetting($"ai.cloud.consent.{p.Id}", "");
            Db.SetSetting($"bulk.cloudReview.skip.{p.Id}", "");
        }
        Db.Audit("settings.change", "settings", null, "Cloud AI consent reset", "");
    }

    // ------------------------------------------------------------------ requests
    private static HttpRequestMessage Request(HttpMethod method, string path, string endpoint = null)
    {
        var req = new HttpRequestMessage(method, (endpoint ?? Endpoint) + path);
        var key = ApiKey;
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    public sealed record ServerInfo(string Version, List<string> Models);

    /// <summary>Checks a server responds, and lists the models it has installed.</summary>
    public static async Task<ServerInfo> ProbeAsync(string endpoint, bool allowRemote, CancellationToken ct = default)
    {
        endpoint = endpoint.Trim().TrimEnd('/');
        var problem = CheckEndpoint(endpoint, allowRemote);
        if (problem != null) throw new InvalidOperationException(problem);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        string version = "";
        using (var v = await Http.SendAsync(Request(HttpMethod.Get, "/api/version", endpoint), cts.Token))
            if (v.IsSuccessStatusCode)
                version = JsonNode.Parse(await v.Content.ReadAsStringAsync(cts.Token))?["version"]?.GetValue<string>() ?? "";

        using var r = await Http.SendAsync(Request(HttpMethod.Get, "/api/tags", endpoint), cts.Token);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"The server answered {(int)r.StatusCode} {r.ReasonPhrase}.");
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        var models = json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();
        return new ServerInfo(version, models!);
    }

    private static JsonObject Options()
    {
        var o = new JsonObject
        {
            ["temperature"] = Temperature,
            ["num_ctx"] = ContextTokens,
            ["num_predict"] = MaxOutputTokens
        };
        if (GpuMode == "cpu") o["num_gpu"] = 0;
        else if (GpuMode == "gpu") o["num_gpu"] = 999;
        int threads = ThreadOverride > 0 ? ThreadOverride : Performance.AiThreads;
        if (threads > 0) o["num_thread"] = threads;
        return o;
    }

    /// <summary>
    /// Sends one conversation turn and streams the reply back a piece at a time.
    /// Returns the whole reply. Throws OperationCanceledException if stopped.
    /// </summary>
    public static Task<string> ChatAsync(string system, string user, Action<string> onToken, CancellationToken ct) =>
        ChatAsync(system, new[] { ("user", user ?? "") }, onToken, ct);

    /// <summary>A conversation: earlier turns as (role, text) pairs, "user" or "assistant", ending with the new question.</summary>
    public static async Task<string> ChatAsync(string system, IEnumerable<(string Role, string Content)> turns, Action<string> onToken, CancellationToken ct)
    {
        if (UsingCloud) return await CloudChatAsync(system, turns.ToList(), onToken, ct);
        var problem = CheckEndpoint(Endpoint, AllowRemote);
        if (problem != null) throw new InvalidOperationException(problem);
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Choose an AI model in Settings first.");

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system ?? "" } };
        foreach (var (role, content) in turns)
            messages.Add(new JsonObject { ["role"] = role == "assistant" ? "assistant" : "user", ["content"] = content ?? "" });
        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = true,
            ["keep_alive"] = KeepAlive,
            ["options"] = Options(),
            ["messages"] = messages
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(TimeoutMinutes));
        var req = Request(HttpMethod.Post, "/api/chat");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            string msg = err;
            try { msg = JsonNode.Parse(err)?["error"]?.GetValue<string>() ?? err; } catch { /* not JSON */ }
            throw new HttpRequestException($"Ollama said: {msg} ({(int)resp.StatusCode})");
        }

        var sb = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // ReadLineAsync returns null at the end; EndOfStream would block this thread on the network instead
        string line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }
            if (node?["error"] is { } e) throw new HttpRequestException("Ollama said: " + e.GetValue<string>());
            var piece = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                sb.Append(piece);
                onToken?.Invoke(piece);
            }
            if (node?["done"]?.GetValue<bool>() == true) break;
        }
        return sb.ToString();
    }

    /// <summary>A conversation with the cloud AI, masked when masking is on, with the answer unmasked as it streams in.</summary>
    private static async Task<string> CloudChatAsync(string system, List<(string Role, string Content)> turns, Action<string> onToken, CancellationToken ct)
    {
        // The last check, whichever screen asked: nothing goes to a cloud AI the person hasn't agreed to
        if (!HasCloudConsent(Provider))
            throw new InvalidOperationException("You haven't agreed to use this cloud AI yet. Use the AI tools again to see what is sent and agree.");
        var config = CloudConfigFor(Provider);
        var masker = MaskCloud ? NewMasker() : null;
        if (masker != null)
        {
            system = (system ?? "") + "\n\n" + Masker.Instructions;
            turns = turns.Select(t => (t.Role, masker.Mask(t.Content))).ToList();
        }
        var stream = masker != null ? new Masker.StreamUnmasker(masker) : null;
        var sb = new StringBuilder();
        void Piece(string p)
        {
            var shown = stream != null ? stream.Push(p) : p;
            if (shown.Length == 0) return;
            sb.Append(shown);
            onToken?.Invoke(shown);
        }
        try
        {
            await CloudAi.CompleteAsync(config, system, turns, null, Math.Max(MaxOutputTokens, 4096), Piece, ct);
        }
        catch (BulkImport.AiUnavailableException ex) { throw new HttpRequestException(ex.Message, ex); }
        catch (BulkImport.AiOverloadException ex) { throw new HttpRequestException(ex.Message + " Wait a minute and try again.", ex); }
        catch (BulkImport.AnalysisValidationException ex) { throw new HttpRequestException(ex.Message, ex); }
        if (stream != null && stream.Flush() is { Length: > 0 } rest) { sb.Append(rest); onToken?.Invoke(rest); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ structured output (Bulk Case Import)
    /// <summary>
    /// One non-streamed request whose answer must match a JSON Schema (Ollama "structured outputs").
    /// Older Ollama versions that don't understand a schema get plain JSON mode instead; CaseKeeper checks
    /// the answer against the schema itself either way. Temperature 0, so results are as repeatable as
    /// the model allows. Models that "think aloud" first have that switched off (or turned down), since only
    /// the answer is needed.
    /// </summary>
    public static async Task<string> ChatJsonAsync(string model, string system, string user, JsonNode schema, int outputTokens, CancellationToken ct)
    {
        var problem = CheckEndpoint(Endpoint, AllowRemote);
        if (problem != null) throw new InvalidOperationException(problem);
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("No AI model is available. Install one in Ollama, then try again.");
        var think = AiHardware.ThinkSetting(model);

        async Task<HttpResponseMessage> Send(JsonNode format, bool withThink, CancellationToken token)
        {
            var options = Options();
            options["temperature"] = 0;
            options["num_predict"] = outputTokens;
            var body = new JsonObject
            {
                ["model"] = model, ["stream"] = false, ["keep_alive"] = KeepAlive, ["options"] = options, ["format"] = format,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system ?? "" },
                    new JsonObject { ["role"] = "user", ["content"] = user ?? "" }
                }
            };
            if (withThink && think != null) body["think"] = think is bool b ? JsonValue.Create(b) : JsonValue.Create((string)think);
            var req = Request(HttpMethod.Post, "/api/chat");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return await Http.SendAsync(req, token);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(TimeoutMinutes));
        try
        {
            var resp = await Send(schema.DeepClone(), true, cts.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest && think != null)
            {
                // An Ollama that doesn't know the thinking switch: ask again without it
                resp.Dispose();
                resp = await Send(schema.DeepClone(), false, cts.Token);
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // An Ollama from before structured outputs: fall back to JSON mode
                resp.Dispose();
                resp = await Send(JsonValue.Create("json"), false, cts.Token);
            }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                if ((int)resp.StatusCode == 404) throw new BulkImport.AiUnavailableException("The chosen AI model isn't installed in Ollama.");
                // Running out of memory is about this model on this PC, not the server being away: slow down, don't stop
                if (text.Contains("memory", StringComparison.OrdinalIgnoreCase) && !resp.IsSuccessStatusCode)
                    throw new BulkImport.AiOverloadException("The AI ran out of memory.");
                if ((int)resp.StatusCode is 502 or 503) throw new BulkImport.AiUnavailableException($"The AI server isn't ready ({(int)resp.StatusCode}).");
                // Any other refusal is about this one request: that document fails, the rest carry on
                if (!resp.IsSuccessStatusCode)
                    throw new BulkImport.AnalysisValidationException("AI_REQUEST", $"The AI server refused the request ({(int)resp.StatusCode}).");
                JsonNode json;
                try { json = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { throw new BulkImport.AnalysisValidationException("AI_REQUEST", "The AI server's reply couldn't be read."); }
                if (json?["error"] != null) throw new BulkImport.AnalysisValidationException("AI_REQUEST", "The AI server returned an error for this document.");
                return json?["message"]?["content"]?.GetValue<string>() ?? "";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The AI took too long to answer.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            // No answer at all: Ollama isn't running, or the address is wrong
            throw new BulkImport.AiUnavailableException("The AI server can't be reached.", ex);
        }
    }

    /// <summary>The models installed in Ollama, with their sizes, for the hardware recommendations.</summary>
    public static async Task<List<(string Name, long Size)>> InstalledModelsAsync(CancellationToken ct = default)
    {
        var problem = CheckEndpoint(Endpoint, AllowRemote);
        if (problem != null) throw new InvalidOperationException(problem);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var r = await Http.SendAsync(Request(HttpMethod.Get, "/api/tags"), cts.Token);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"The server answered {(int)r.StatusCode}.");
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        return json?["models"]?.AsArray()
            .Select(m => (Name: m?["name"]?.GetValue<string>() ?? "", Size: m?["size"]?.GetValue<long>() ?? 0L))
            .Where(m => m.Name.Length > 0).ToList() ?? new List<(string, long)>();
    }

    /// <summary>How much of a loaded model sits on the graphics card (1 = all of it), from Ollama's list of running models.</summary>
    public static async Task<double?> GpuShareAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var r = await Http.SendAsync(Request(HttpMethod.Get, "/api/ps"), cts.Token);
            if (!r.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token));
            var m = json?["models"]?.AsArray().FirstOrDefault(x => AiHardware.Same(x?["name"]?.GetValue<string>(), model));
            long size = m?["size"]?.GetValue<long>() ?? 0, vram = m?["size_vram"]?.GetValue<long>() ?? 0;
            return size > 0 ? (double)vram / size : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Downloads a model into Ollama (it can take a few minutes). The person asks for this; it's never automatic.</summary>
    public static async Task PullModelAsync(string model, CancellationToken ct)
    {
        var problem = CheckEndpoint(Endpoint, AllowRemote);
        if (problem != null) throw new InvalidOperationException(problem);
        var req = Request(HttpMethod.Post, "/api/pull");
        req.Content = new StringContent(new JsonObject { ["model"] = model, ["stream"] = false }.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromHours(2));
        using var r = await Http.SendAsync(req, cts.Token);
        var text = await r.Content.ReadAsStringAsync(cts.Token);
        if (!r.IsSuccessStatusCode || text.Contains("\"error\"")) throw new HttpRequestException("Ollama couldn't download the model.");
        Db.Audit("ai.model", "ai", null, $"Downloaded the AI model {model} into Ollama", "");
    }

    // ---- Bulk Case Import settings: recommended for this PC, unless the person has chosen otherwise
    /// <summary>"fast" (default) or "careful".</summary>
    public static string BulkPreset => Db.GetSetting("bulk.preset", "fast") == "careful" ? "careful" : "fast";
    /// <summary>A model the person picked themselves for bulk analysis; empty means "recommended".</summary>
    public static string BulkModelOverride => (Db.GetSetting("bulk.model", "") ?? "").Trim();
    /// <summary>The AI Bulk Case Import uses: the person's choice in Review options, otherwise the one in Settings → AI.</summary>
    public static string BulkProvider => (Db.GetSetting("bulk.provider", "") ?? "") is { Length: > 0 } p ? AiProviders.Get(p).Id : Provider;
    /// <summary>A cloud model and effort chosen just for bulk import; empty means the ones in Settings → AI.</summary>
    public static string BulkCloudModel => (Db.GetSetting("bulk.cloudModel", "") ?? "").Trim();
    public static string BulkCloudEffort => (Db.GetSetting("bulk.cloudEffort", "") ?? "").Trim();

    /// <summary>The AI client for one Bulk Case Import run: the local AI, or a cloud AI with a fresh masker.</summary>
    public static BulkImport.IAiJsonClient BulkClient()
    {
        var p = BulkProvider;
        if (!AiProviders.IsCloud(p)) return new OllamaJsonClient();
        var effort = AiProviders.Get(p).Efforts.Any(e => e.Id == BulkCloudEffort) ? BulkCloudEffort : null;
        return new CloudJsonClient(CloudConfigFor(p, BulkCloudModel, effort), MaskCloud ? NewMasker() : null, BulkDepthSetting == "categories");
    }

    /// <summary>"auto", "full" or "categories".</summary>
    public static string BulkDepthSetting => Db.GetSetting("bulk.depth", "auto") is "full" or "categories" ? Db.GetSetting("bulk.depth", "auto") : "auto";

    /// <summary>Documents the AI reads at once: the person's choice, or this PC's tier. Always one on battery.</summary>
    public static int BulkWorkers()
    {
        if (AiProviders.IsCloud(BulkProvider)) return CloudWorkers;
        if (Performance.OnBattery || Performance.Effective == PerformanceMode.PowerSaver) return 1;
        if (int.TryParse(Db.GetSetting("bulk.aiWorkers", ""), out var n) && n > 0) return Math.Clamp(n, 1, 4);
        return AiHardware.Tier switch { AiTier.Mid => 2, AiTier.High => 3, AiTier.VeryHigh => 4, _ => 1 };
    }

    /// <summary>
    /// The configured Ollama server, with the model chosen for this PC: the person's own pick if they made one,
    /// otherwise the Fast or Careful preset's model that fits this PC, otherwise the general AI model.
    /// </summary>
    public sealed class OllamaJsonClient : BulkImport.IAiJsonClient
    {
        private string _model;
        public string ModelName => _model ?? (BulkModelOverride.Length > 0 ? BulkModelOverride : Model);
        /// <summary>Why this model was chosen, for the review screen.</summary>
        public string Choice { get; private set; } = "";
        public bool CategoriesOnly { get; private set; }
        private List<(string Name, long Size)> _installed = new();

        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            try
            {
                var installed = _installed = await InstalledModelsAsync(ct);
                bool Has(string m) => !string.IsNullOrEmpty(m) && installed.Any(x => AiHardware.Same(x.Name, m));
                var rec = AiHardware.Recommend(installed);
                var depth = BulkDepthSetting;
                CategoriesOnly = depth == "categories" || (depth == "auto" && rec.Depth == "categories");
                if (Has(BulkModelOverride)) { _model = installed.First(x => AiHardware.Same(x.Name, BulkModelOverride)).Name; Choice = "your choice"; }
                else if (BulkPreset == "careful" && rec.CarefulModel != null) { _model = rec.CarefulModel; Choice = "Careful preset"; }
                else if (rec.FastModel != null) { _model = rec.FastModel; Choice = BulkPreset == "careful" ? "Fast preset (no Careful model fits this PC)" : "Fast preset"; }
                else if (Has(Model)) { _model = Model; Choice = "your AI model"; }
                else return false;
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLog.Info("AI", $"AI server not available for bulk analysis ({ex.GetType().Name})");
                return false;
            }
        }

        public Task<string> CompleteJsonAsync(string system, string user, JsonObject schema, CancellationToken ct) =>
            ChatJsonAsync(ModelName, system, user, schema, CategoriesOnly ? 1024 : 2048, ct);

        public Task<double?> GpuShareAsync(CancellationToken ct) => AiService.GpuShareAsync(ModelName, ct);

        public bool Lighten()
        {
            var lighter = AiHardware.LighterThan(ModelName, _installed, AiHardware.ThisPc);
            if (lighter == null) return false;
            _model = lighter;
            Choice = "a lighter model, because this PC was struggling";
            return true;
        }
    }

    /// <summary>How much text goes in one bulk-analysis request, leaving room for the structured answer.</summary>
    public static BulkImport.AnalysisSettings BulkAnalysisSettings() => new()
    {
        // Its own instructions only: the general system prompt asks for Markdown, which would fight the JSON format
        Prompt = Prompt("prompt.bulk.analyse"),
        // Basic PCs read with a 4,096-token context at most: smaller parts are much quicker on a processor
        PartChars = Math.Max(3000, (int)(((AiHardware.Tier <= AiTier.Basic ? Math.Min(ContextTokens, 4096) : ContextTokens) - 2048 - 1000) * 3.0)),
        MaxParts = int.TryParse(Db.GetSetting("bulk.maxParts", "40"), out var mp) ? Math.Clamp(mp, 1, 500) : 40
    };

    // ------------------------------------------------------------------ granular analysis
    public sealed class SourceText
    {
        public string Label { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public sealed class AnalysisResult
    {
        public string Output { get; set; } = "";
        public int Chunks { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Runs a report over any amount of material. If it fits in one request it's sent as it is.
    /// Otherwise each part is first analysed on its own for the task ("map"), then the findings are
    /// combined into one report ("reduce") – so nothing is silently cut off.
    /// </summary>
    public static async Task<AnalysisResult> AnalyseAsync(string task, IReadOnlyList<SourceText> sources, bool granular,
        IProgress<string> status, Action<string> onToken, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var system = Prompt("prompt.system");
        var background = IncludeCaseSummary ? CaseBackground() : "";
        int budget = ChunkChars - background.Length;
        var chunks = Chunk(sources, Math.Max(3000, budget));
        var result = new AnalysisResult { Chunks = chunks.Count };
        int totalChars = sources.Sum(s => s.Text.Length);

        Db.Audit("ai.request", "ai", null, $"AI analysis requested ({ActiveModel})",
            $"Server: {ActiveHost}{(UsingCloud ? $" ({AiProviders.Get(Provider).Name}, masking {(MaskCloud ? "on" : "off")})" : "")}\nTask: {Clip(task, 160)}\nSources: {sources.Count} ({totalChars:N0} characters)\nParts: {chunks.Count}" +
            (granular ? "\nPer-document detail: yes" : ""));
        AppLog.Info("AI", $"Analysis started: {sources.Count} sources, {chunks.Count} part(s), model {ActiveModel}");

        if (chunks.Count == 1 && !granular)
        {
            status?.Report("Writing the report…");
            result.Output = await ChatAsync(system, Compose(task, background, chunks[0]), onToken, ct);
        }
        else
        {
            var notes = new List<string>();
            var mapPrompt = Prompt("prompt.map");
            for (int i = 0; i < chunks.Count; i++)
            {
                status?.Report($"Reading part {i + 1} of {chunks.Count}…");
                onToken?.Invoke($"\n\n### Notes on part {i + 1} of {chunks.Count}\n\n");
                var note = await ChatAsync(system,
                    mapPrompt.Replace("{task}", task) + "\n\n" + background + "\n\n" + chunks[i], onToken, ct);
                notes.Add($"## Findings from part {i + 1}\n{note}");
            }
            status?.Report("Combining the findings into one report…");
            onToken?.Invoke("\n\n---\n\n# Combined report\n\n");
            var reduce = Prompt("prompt.reduce").Replace("{task}", task);
            var combined = await ChatAsync(system, reduce + "\n\n" + background + "\n\n" + string.Join("\n\n", notes), onToken, ct);
            result.Output = granular
                ? "# Combined report\n\n" + combined + "\n\n---\n\n# Detail by part\n\n" + string.Join("\n\n", notes)
                : combined;
        }
        result.DurationMs = sw.ElapsedMilliseconds;
        AppLog.Info("AI", $"Analysis finished in {result.DurationMs / 1000.0:0.0}s");
        return result;
    }

    private static string Compose(string task, string background, string material) =>
        $"TASK:\n{task}\n\n{background}\n\nMATERIAL:\n{material}";

    private static List<string> Chunk(IReadOnlyList<SourceText> sources, int size)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            var block = $"=== {s.Label} ===\n{s.Text.Trim()}\n\n";
            if (sb.Length + block.Length <= size) { sb.Append(block); continue; }
            if (sb.Length > 0) { chunks.Add(sb.ToString()); sb.Clear(); }
            if (block.Length <= size) { sb.Append(block); continue; }
            // A single long document: split on paragraph boundaries, keeping its label on each piece
            int part = 1;
            var text = s.Text;
            int pos = 0;
            while (pos < text.Length)
            {
                int len = Math.Min(size - 200, text.Length - pos);
                int cut = len < text.Length - pos ? text.LastIndexOf("\n", pos + len, Math.Min(len, 2000), StringComparison.Ordinal) : -1;
                if (cut > pos + 500) len = cut - pos;
                chunks.Add($"=== {s.Label} (part {part++}) ===\n{text.Substring(pos, len)}\n");
                pos += len;
            }
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        if (chunks.Count == 0) chunks.Add("(No material was selected.)");
        return chunks;
    }

    public static string CaseBackground()
    {
        var parts = new List<string>();
        void Add(string label, string key)
        {
            var v = Db.GetSetting(key, "");
            if (!string.IsNullOrWhiteSpace(v)) parts.Add($"{label}: {v}");
        }
        Add("Case", "case.title");
        Add("Type", "case.type");
        Add("Stage", "case.stage");
        var people = Db.GetParties().Select(p => $"{p.Role}{(string.IsNullOrWhiteSpace(p.Name) ? "" : ": " + p.Name)}{(p.IsMe ? " (the person using this app)" : "")}{(p.AgeText.Length > 0 ? ", " + p.AgeText : "")}");
        if (people.Any()) parts.Add("People: " + string.Join("; ", people));
        Add("Summary", "case.summary");
        return parts.Count == 0 ? "" : "CASE BACKGROUND (for context only):\n" + string.Join("\n", parts);
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ------------------------------------------------------------------ prompts
    public sealed record ReportType(string Key, string Name, string Description);

    public static readonly ReportType[] ReportTypes =
    {
        new("summary", "Summary", "A clear summary of what the material says"),
        new("chronology", "Chronology", "Dated events pulled out of the material, in order"),
        new("issues", "Key issues & allegations", "What each side says, and what's disputed"),
        new("inconsistencies", "Inconsistencies", "Where accounts or documents contradict each other"),
        new("welfare", "Welfare checklist", "The material mapped against the s.1(3) welfare checklist"),
        new("children", "The children's experience", "Everything about the children's needs, wishes and wellbeing"),
        new("safeguarding", "Safeguarding concerns", "Concerns raised, by whom, and any response"),
        new("questions", "Questions to consider", "Gaps and questions to raise or prepare for"),
        new("position", "Position statement outline", "A neutral outline to help draft a position statement"),
        new("compliance", "Compliance with orders", "What orders required and whether it appears to have been done"),
        new("overview", "Case overview", "The whole case at a glance: who's involved, what's happened, where it stands and what's outstanding"),
    };

    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["prompt.system"] =
            "You are a careful, neutral assistant helping someone organise and understand papers from family court proceedings in England and Wales. " +
            "Rules: (1) Use only the material provided. Never invent facts, dates, names, quotations or legal citations. " +
            "(2) When you state something from the material, say where it comes from (document title and page number if given). " +
            "(3) If the material doesn't say, write \"Not stated in the material\". " +
            "(4) Be factual, balanced and calm, and give both sides fairly. Do not take sides or speculate about anyone's character. " +
            "(5) Use plain UK English and British date format. (6) You do not give legal advice; where a legal decision is involved, " +
            "suggest the person checks with a solicitor or advice service. (7) Format your answer in Markdown with short headings and bullet points.",
        ["prompt.map"] =
            "You are reading one part of a larger set of case papers. Extract everything relevant to this task: {task}\n" +
            "Keep exact dates, names, and document/page references. Be concise but don't drop facts. If nothing is relevant, say so in one line.",
        ["prompt.reduce"] =
            "Below are notes taken from each part of the case papers. Combine them into one well-organised report for this task: {task}\n" +
            "Remove repetition, keep all document/page references, put dated events in date order, and point out any contradictions between parts.",
        ["prompt.report.summary"] = "Write a clear, neutral summary of this material: what it is, who it's from, the main points and any requests or decisions. End with a short 'Key points' list.",
        ["prompt.report.chronology"] = "Extract every dated event from the material into a chronology table with the columns Date | Event | Source (document and page). Sort by date. Mark approximate dates as approximate.",
        ["prompt.report.issues"] = "Identify the key issues and allegations. For each: what is alleged or proposed, by whom, what the response is (if any), and which documents support each side. Finish with a list of the matters that appear to be disputed.",
        ["prompt.report.inconsistencies"] = "Find inconsistencies and contradictions: between documents, between what people say at different times, and between accounts and records. For each, quote the two versions briefly with their sources. Do not draw conclusions about honesty.",
        ["prompt.report.welfare"] = "Organise the relevant material under each heading of the welfare checklist in section 1(3) of the Children Act 1989: (a) the child's wishes and feelings in light of age and understanding; (b) physical, emotional and educational needs; (c) likely effect of any change in circumstances; (d) age, sex, background and relevant characteristics; (e) harm suffered or at risk of; (f) how capable each parent (and anyone else relevant) is of meeting the child's needs; (g) the range of powers available to the court. Cite sources. Where the material says nothing, say so.",
        ["prompt.report.children"] = "Pull together everything the material says about the children: their routines, health, education, relationships, wishes and feelings, and any worries. Keep it child-focused and neutral, with sources.",
        ["prompt.report.safeguarding"] = "List every safeguarding concern raised in the material: what, when, raised by whom, any agency involved (police, school, social services, health), reference numbers and the outcome or response. Use a table where helpful.",
        ["prompt.report.questions"] = "Identify gaps, unclear points and questions arising from the material that the person may want to raise, clarify or prepare for at a hearing or with Cafcass. Group them by topic. Keep the tone neutral.",
        ["prompt.report.position"] = "Draft a neutral outline to help the person write their own position statement: background, current arrangements, issues for the court, the person's apparent proposals (only as shown in the material), and the orders sought. Use [square brackets] for anything they need to fill in. Remind them to check and rewrite it in their own words.",
        ["prompt.report.compliance"] = "List what the court orders and directions in the material required (who, what, by when), and whether the material shows each was done, done late, or not done. Use a table with sources.",
        ["prompt.report.overview"] = "Write an overview of the whole case from the document summaries and records provided, under these headings: " +
            "**Who's involved** (parties, children, professionals and organisations); **What has happened** (the history in date order, briefly); " +
            "**Where things stand now** (current arrangements and the stage of proceedings); **Coming up** (the next hearing and deadlines); " +
            "**Outstanding** (actions, complaints awaiting a reply, Scott schedule items); **Key issues** (what is disputed, neutrally, with who says what); " +
            "**Gaps** (documents referred to but missing, undated or unclear points). Cite document titles. Never state an allegation as a fact.",
        ["prompt.rewrite"] = "Rewrite the following text so it is clear, polite, concise and factual, in plain UK English, suitable for a letter or statement in family court proceedings. Keep every fact, date and name exactly as given. Do not add anything new. Reply with the rewritten text only.",
        ["prompt.explain"] = "Explain the following in plain, friendly English for someone with no legal background. Keep it short, use bullet points, and add a one-line reminder that this is general information, not legal advice.",
        ["prompt.guide.answer"] = "Answer the person's question using ONLY the guide articles provided below, which are about family law in England and Wales. If the articles don't cover it, say so and suggest where to find out (e.g. gov.uk, an advice service or a solicitor). Be brief and practical, mention which article(s) you used, and remind them this is general information, not legal advice.",
        ["prompt.case.answer"] =
            "You are CaseKeeper's case assistant. You help one person keep track of their own family court case in England and Wales by " +
            "answering questions about it from their own records and documents, given below. Rules: (1) Answer ONLY from what is provided: the " +
            "case records and the extracts from their documents. If they don't cover the question, say so plainly and suggest where to look or " +
            "what to add (for example a document to import, or Check my library). Never guess or invent dates, names, orders or outcomes. " +
            "(2) After every fact taken from a document, cite it as (doc:ID:PAGE) using the reference shown before the extract, e.g. (doc:12:3) – it " +
            "becomes a button that opens that page. Facts from the records need no citation. (3) Report allegations and opinions as what someone " +
            "says, never as fact. (4) Be brief and practical: short sentences or bullets, dates written out (3 March 2026), plain UK English. " +
            "(5) You may suggest one next step in CaseKeeper with a link such as (go:actions), (go:timeline), (go:complaints) or (go:review). " +
            "(6) You help the person organise and understand their own papers; you don't give legal advice or predict what a court will decide. " +
            "If they ask for that, say so briefly and suggest the Guide & FAQ (go:guide), an advice service or a solicitor.",
        ["prompt.helpdesk"] =
            "You are the CaseKeeper help assistant. CaseKeeper is a Windows app for organising a family court case in England and Wales. " +
            "Your only job is to help people use CaseKeeper: tell them exactly where to go and what to click to do what they want. " +
            "Rules: (1) Use ONLY the app map and help sections provided. Never invent screens, buttons, menus, settings or features. If CaseKeeper " +
            "can't do something, say so plainly and suggest the closest thing it can do. (2) Give short numbered steps using the exact on-screen names " +
            "in bold, e.g. **Documents** → right-click the document → **Move to category**. (3) Mention a keyboard shortcut or right-click option when " +
            "it is quicker. (4) Add one or two links to the right place using ONLY the link forms listed in the app map, written as " +
            "[Open Letters & templates](go:letters) or [AI settings](settings:ai) – they become buttons. (5) Plain, friendly UK English; assume the person " +
            "isn't technical, but be precise enough for an expert. (6) If the question is about the law or their case rather than the app, say briefly " +
            "that the Guide & FAQ explains the general process and link it with (go:guide), and that you can't give legal advice. " +
            "(7) If the request is unclear, ask one short clarifying question. Keep answers under about 180 words unless asked for more.",
        ["prompt.bulk.analyse"] =
            "TASK: You are helping to organise one document from family court proceedings in England and Wales. Read the text and reply ONLY with JSON " +
            "in the required format. Rules: (1) Use only what the text says. Never guess or invent dates, times, names, case numbers, courts, outcomes " +
            "or deadlines; use null when something is not stated. (2) categoryId must be one of the candidate keys given (K1, K2…) or UNKNOWN if none " +
            "clearly fits. (3) For every finding give the page number shown as [Page N] and copy a short exact quote (under 200 characters) from that " +
            "page as the excerpt. (4) Copy dateText exactly as written. Put a date in 'date' (YYYY-MM-DD) only when the day, month and year are all " +
            "written; UK dates are day first. If only a month or year is given, leave date null and set datePrecision. (5) Give every date its meaning " +
            "(dateRole): a hearing 'listed for' a date is hearingListed, not hearingHeld; an adjourned hearing's old date is hearingAdjournedFrom and its " +
            "new date hearingAdjournedTo; a date only mentioned in passing is 'mentioned'. (6) Report allegations neutrally as what someone states or " +
            "alleges, and name who in assertedBy. Never present an allegation as a fact. (7) Never calculate a deadline. If an order says 'within 14 days' " +
            "or 'by 4pm 3 clear days before the hearing', copy the wording, set dueDateExplicit to false and leave dueDate null. (8) confidence (0 to 1) " +
            "is how clearly the text supports the finding. (9) Keep descriptions short and factual. Only include findings that matter to the case: " +
            "hearings, orders, directions, deadlines, events, contact, people, complaints, safeguarding concerns, and documents referred to but missing. " +
            "(10) People (party/child): give their role and, for professionals and organisations only, any representative and contact details " +
            "(contactDetails: phone, email or address exactly as written). Never give contact details for a child. (11) Complaints: give the " +
            "organisation (recipient), any reference, the date it was sent, any reply deadline written as a date (responseDue) or as wording " +
            "(responseDueText), and set isResponse to true when this document is the organisation's reply. (12) scottItem: one allegation of " +
            "something someone is said to have done, for a Scott schedule: who alleges it (allegedBy), the allegation in neutral words " +
            "(description), its date or dateText, and any finding sought. Only allegations actually made in the document.",
        ["prompt.guide.apply"] = "Using the guide article below and the case background provided, explain in plain English how the general information in the article may relate to this person's situation, and what they might want to check or prepare. Do not give legal advice or predict outcomes; point out where they should take advice.",
    };

    public static string Prompt(string key)
    {
        var v = Db.GetSetting(key, null);
        return string.IsNullOrWhiteSpace(v) ? Defaults.GetValueOrDefault(key, "") : v;
    }

    public static string ReportPrompt(string typeKey) => Prompt("prompt.report." + typeKey);
}
