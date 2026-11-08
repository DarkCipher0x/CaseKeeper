using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Foundry;
using Anthropic.Models.Messages;
using CaseKeeper.Services.BulkImport;
using OpenAI;
using OpenAI.Chat;

namespace CaseKeeper.Services;

/// <summary>One of the AI services CaseKeeper can use: the local AI (Ollama) or a cloud service.</summary>
public sealed record AiProviderInfo(string Id, string Name, string Family, bool IsCloud, bool IsFoundry, string KeyPage, string About,
    string DefaultModel, IReadOnlyList<(string Id, string Label)> Models, IReadOnlyList<(string Id, string Label)> Efforts, string DefaultEffort);

/// <summary>Everything one cloud request needs. The key is held only for as long as the request takes.</summary>
public sealed record CloudConfig(string Provider, string Key, string Resource, string Model, string Effort, int TimeoutMinutes = 20);

/// <summary>
/// The cloud AI services CaseKeeper supports, and the rules that keep requests going only where intended: HTTPS only,
/// to a fixed list of hosts (Anthropic, OpenAI, and Microsoft Foundry resources in your own Azure subscription).
/// </summary>
public static class AiProviders
{
    public const string Local = "local", Anthropic = "anthropic", AnthropicFoundry = "anthropic-foundry", OpenAi = "openai", OpenAiFoundry = "openai-foundry";

    private static readonly (string, string)[] ClaudeModels =
    {
        ("claude-haiku-5-5", "Claude Haiku 5.5 – fast and low cost (recommended)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 – balanced"),
        ("claude-opus-5-5", "Claude Opus 5.5 – most capable, slower and dearer")
    };
    private static readonly (string, string)[] ClaudeEfforts =
    {
        ("low", "Low – quickest (recommended for sorting documents)"), ("medium", "Medium"), ("high", "High – more careful"), ("max", "Maximum – slowest")
    };
    private static readonly (string, string)[] GptModels =
    {
        ("gpt-6-luna", "GPT-6 Luna – fastest and lowest cost (recommended)"),
        ("gpt-6.1-terra", "GPT-6.1 Terra – fast"),
        ("gpt-6.1-sol", "GPT-6.1 Sol – balanced"),
        ("gpt-6-astra", "GPT-6 Astra – most capable, slower and dearer")
    };
    private static readonly (string, string)[] GptEfforts =
    {
        ("none", "None – no reasoning, quickest"), ("low", "Low – quick (recommended for sorting documents)"), ("medium", "Medium"),
        ("high", "High – more careful"), ("xhigh", "Extra high"), ("max", "Maximum – slowest")
    };

    public static readonly IReadOnlyList<AiProviderInfo> All = new[]
    {
        new AiProviderInfo(Local, "Local AI on this PC (Ollama)", "ollama", false, false, "https://ollama.com/download",
            "Free, private and offline: documents never leave this PC (or your own network). Speed depends on this PC.",
            AiHardware.DefaultModel, Array.Empty<(string, string)>(), Array.Empty<(string, string)>(), ""),
        new AiProviderInfo(Anthropic, "Anthropic (Claude)", "anthropic", true, false, "https://platform.claude.com/settings/keys",
            "Claude models from Anthropic. Needs an API key from the Claude Console; charged per use by Anthropic.",
            "claude-haiku-5-5", ClaudeModels, ClaudeEfforts, "low"),
        new AiProviderInfo(OpenAi, "OpenAI (GPT)", "openai", true, false, "https://platform.openai.com/api-keys",
            "GPT models from OpenAI. Needs an API key from the OpenAI platform; charged per use by OpenAI.",
            "gpt-6-luna", GptModels, GptEfforts, "low"),
        new AiProviderInfo(AnthropicFoundry, "Claude on Microsoft Foundry", "anthropic", true, true, "https://ai.azure.com/",
            "Claude models deployed in your own Azure subscription through Microsoft Foundry. Needs the resource name and a key; " +
            "the model is your deployment's name.", "claude-haiku-5-5", ClaudeModels, ClaudeEfforts, "low"),
        new AiProviderInfo(OpenAiFoundry, "OpenAI on Microsoft Foundry", "openai", true, true, "https://ai.azure.com/",
            "OpenAI models deployed in your own Azure subscription through Microsoft Foundry (Azure OpenAI). Needs the resource name and a key; " +
            "the model is your deployment's name.", "gpt-6-luna", GptModels, GptEfforts, "low"),
    };

    public static AiProviderInfo Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
    public static bool IsCloud(string id) => Get(id).IsCloud;

    private static readonly Regex ResourceName = new(@"^[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Turns what the person typed for a Foundry resource (its name, or its endpoint address) into the resource name.
    /// Null, with a reason, if it isn't a Foundry address.
    /// </summary>
    public static string ParseResource(string input, out string problem)
    {
        problem = null;
        var v = (input ?? "").Trim().TrimEnd('/');
        if (v.Length == 0) { problem = "Enter your Foundry resource name (for example my-resource) or its endpoint address."; return null; }
        if (v.Contains("://"))
        {
            if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo))
            { problem = "The endpoint must be an https:// address with no user name or password in it."; return null; }
            var host = u.Host.ToLowerInvariant();
            foreach (var suffix in FoundrySuffixes)
                if (host.EndsWith(suffix, StringComparison.Ordinal) && ResourceName.IsMatch(host[..^suffix.Length])) return host[..^suffix.Length];
            problem = "That isn't a Microsoft Foundry address. It should end in .services.ai.azure.com, .openai.azure.com or .cognitiveservices.azure.com.";
            return null;
        }
        v = v.ToLowerInvariant();
        if (!ResourceName.IsMatch(v)) { problem = "A resource name has only lower-case letters, numbers and hyphens."; return null; }
        return v;
    }

    private static readonly string[] FoundrySuffixes = { ".services.ai.azure.com", ".openai.azure.com", ".cognitiveservices.azure.com" };

    /// <summary>The address a provider's requests go to (shown to the person and recorded in the audit trail).</summary>
    public static Uri BaseAddress(string provider, string resource) => provider switch
    {
        Anthropic => new Uri("https://api.anthropic.com/"),
        OpenAi => new Uri("https://api.openai.com/v1/"),
        AnthropicFoundry => new Uri($"https://{resource}.services.ai.azure.com/anthropic/"),
        OpenAiFoundry => new Uri($"https://{resource}.openai.azure.com/openai/v1/"),
        _ => throw new InvalidOperationException("Not a cloud AI provider.")
    };

    /// <summary>True for the hosts CaseKeeper will send cloud AI requests to, over HTTPS only.</summary>
    public static bool IsAllowedHost(Uri u)
    {
        if (u == null || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo) || !u.IsDefaultPort) return false;
        var h = u.Host.ToLowerInvariant();
        if (h is "api.anthropic.com" or "api.openai.com") return true;
        return FoundrySuffixes.Any(s => h.EndsWith(s, StringComparison.Ordinal) && ResourceName.IsMatch(h[..^s.Length]));
    }

    /// <summary>Checks a cloud configuration before anything is sent. Null when it's fine; otherwise why not.</summary>
    public static string Check(CloudConfig c)
    {
        var p = Get(c.Provider);
        if (!p.IsCloud) return "Not a cloud AI provider.";
        if (string.IsNullOrWhiteSpace(c.Key)) return $"Add your {p.Name} key in Settings → AI first.";
        if (p.IsFoundry && (string.IsNullOrEmpty(c.Resource) || !ResourceName.IsMatch(c.Resource))) return "Enter your Foundry resource name in Settings → AI first.";
        if (string.IsNullOrWhiteSpace(c.Model)) return p.IsFoundry ? "Enter the name of your model deployment in Settings → AI." : "Choose a model in Settings → AI.";
        if (c.Model.Length > 100 || c.Model.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch))) return "That model name isn't valid.";
        if (!IsAllowedHost(BaseAddress(c.Provider, c.Resource))) return "That address isn't one CaseKeeper will send documents to.";
        return null;
    }
}

/// <summary>
/// Sends requests to Anthropic, OpenAI and Microsoft Foundry through their official SDKs. No database access here,
/// so it can be tested on its own: the settings and the key are passed in.
/// </summary>
public static class CloudAi
{
    /// <summary>Tests only: replaces the network with a stand-in. Never set in the app.</summary>
    internal static HttpMessageHandler TestHandler { get; set; }

    private static HttpClient _http;
    private static HttpClient Http => TestHandler != null
        ? new HttpClient(new HostGuard { InnerHandler = TestHandler }, true) { Timeout = Timeout.InfiniteTimeSpan }
        : _http ??= new HttpClient(new HostGuard
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                // A redirect could take documents somewhere else: never follow one
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = true,
                SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }
            }
        }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// The last line of defence: whatever the SDKs or the environment do (for example an ANTHROPIC_BASE_URL or
    /// OPENAI_BASE_URL variable set by another program), a request goes only to an allowed host over HTTPS.
    /// </summary>
    private sealed class HostGuard : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!AiProviders.IsAllowedHost(request.RequestUri))
            {
                AppLog.Warn("AI", "Blocked a cloud AI request to a host that isn't allowed");
                throw new AiUnavailableException("CaseKeeper blocked a request to an address that isn't an allowed AI service.");
            }
            return base.SendAsync(request, ct);
        }
    }

    private const int Retries = 3;

    private static AnthropicClient AnthropicFor(CloudConfig c)
    {
        var timeout = TimeSpan.FromMinutes(Math.Clamp(c.TimeoutMinutes, 1, 240));
        if (c.Provider == AiProviders.AnthropicFoundry)
            return new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(c.Key, c.Resource))
            { HttpClient = Http, MaxRetries = Retries, Timeout = timeout };
        // Everything set explicitly, so nothing is picked up from environment variables (ANTHROPIC_AUTH_TOKEN, ANTHROPIC_BASE_URL…)
        return new AnthropicClient { ApiKey = c.Key, AuthToken = null, HttpClient = Http, MaxRetries = Retries, Timeout = timeout, BaseUrl = "https://api.anthropic.com" };
    }

    private static OpenAIClientOptions OpenAiOptions(CloudConfig c) => new()
    {
        Endpoint = AiProviders.BaseAddress(c.Provider, c.Resource),
        Transport = new HttpClientPipelineTransport(Http),
        NetworkTimeout = TimeSpan.FromMinutes(Math.Clamp(c.TimeoutMinutes, 1, 240)),
        RetryPolicy = new ClientRetryPolicy(Retries),
        UserAgentApplicationId = "CaseKeeper"
    };

    private static ChatClient OpenAiFor(CloudConfig c) => new(c.Model, new ApiKeyCredential(c.Key), OpenAiOptions(c));

    /// <summary>
    /// One request. With a <paramref name="schema"/> the answer is JSON matching it (structured outputs); otherwise text,
    /// streamed to <paramref name="onToken"/> as it arrives. Errors come back as CaseKeeper's own AI exceptions.
    /// </summary>
    public static async Task<string> CompleteAsync(CloudConfig c, string system, IReadOnlyList<(string Role, string Content)> turns,
        JsonObject schema, int maxTokens, Action<string> onToken, CancellationToken ct)
    {
        var problem = AiProviders.Check(c);
        if (problem != null) throw new AiUnavailableException(problem);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(c.TimeoutMinutes, 1, 240)));
        try
        {
            return AiProviders.Get(c.Provider).Family == "anthropic"
                ? await ClaudeAsync(c, system, turns, schema, maxTokens, onToken, cts.Token)
                : await GptAsync(c, system, turns, schema, maxTokens, onToken, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The AI took too long to answer."); }
        catch (Exception ex) when (Map(c, ex) is { } mapped) { throw mapped; }
    }

    // ------------------------------------------------------------------ Anthropic (direct and Foundry)
    private static async Task<string> ClaudeAsync(CloudConfig c, string system, IReadOnlyList<(string Role, string Content)> turns,
        JsonObject schema, int maxTokens, Action<string> onToken, CancellationToken ct)
    {
        var client = AnthropicFor(c);
        var messages = turns.Select(t => t.Role == "assistant"
            ? (MessageParam)new AssistantMessageParam { Content = t.Content ?? "" }
            : new UserMessageParam { Content = t.Content ?? "" }).ToList();
        var output = schema != null
            ? new OutputConfig { Effort = ClaudeEffort(c.Effort), Format = new JsonOutputFormat { Schema = ToElements(StructuredSchema.ForClaude(schema)) } }
            : new OutputConfig { Effort = ClaudeEffort(c.Effort) };
        var p = new MessageCreateParams
        {
            Model = c.Model,
            // Thinking shares the budget with the answer, so leave room for both
            MaxTokens = Math.Clamp(maxTokens + 8000, 2048, 32000),
            System = system ?? "",
            Messages = messages,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = output
        };
        if (schema == null && onToken != null)
        {
            var sb = new StringBuilder();
            await foreach (var ev in client.Messages.CreateStreaming(p, ct))
            {
                if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var text))
                {
                    sb.Append(text.Text);
                    onToken(text.Text);
                }
                else if (ev.TryPickDelta(out var md) && StopText(md.Delta.StopReason) == "refusal")
                    throw new AnalysisValidationException("AI_REFUSED", "The AI declined to answer this request.");
            }
            return sb.ToString();
        }
        var m = await client.Messages.Create(p, ct);
        var stop = StopText(m.StopReason);
        if (stop == "refusal") throw new AnalysisValidationException("AI_REFUSED", "The AI declined to read this document.");
        if (stop == "max_tokens") throw new AnalysisValidationException("AI_INVALID_JSON", "The AI's answer was cut off before it finished.");
        var answer = string.Concat(m.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        onToken?.Invoke(answer);
        return answer;
    }

    /// <summary>The stop reason as the API wrote it ("end_turn", "max_tokens", "refusal"…).</summary>
    private static string StopText(object stop)
    {
        if (stop == null) return "";
        var raw = stop.GetType().GetMethod("Raw", System.Type.EmptyTypes)?.Invoke(stop, null);
        var text = (raw as string ?? (raw is JsonElement je && je.ValueKind == JsonValueKind.String ? je.GetString() : null) ?? stop.ToString() ?? "");
        return text switch
        {
            "Refusal" => "refusal", "MaxTokens" => "max_tokens", "EndTurn" => "end_turn", _ => text.Trim('"')
        };
    }

    private static Effort ClaudeEffort(string e) => e switch
    {
        "medium" => Effort.Medium, "high" => Effort.High, "max" => Effort.Max, _ => Effort.Low
    };

    private static Dictionary<string, JsonElement> ToElements(JsonObject o) =>
        o.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

    // ------------------------------------------------------------------ OpenAI (direct and Foundry)
    private static async Task<string> GptAsync(CloudConfig c, string system, IReadOnlyList<(string Role, string Content)> turns,
        JsonObject schema, int maxTokens, Action<string> onToken, CancellationToken ct)
    {
        var client = OpenAiFor(c);
        var messages = new List<ChatMessage> { new SystemChatMessage(system ?? "") };
        foreach (var (role, content) in turns)
            messages.Add(role == "assistant" ? new AssistantChatMessage(content ?? "") : new UserChatMessage(content ?? ""));
        var effort = string.IsNullOrEmpty(c.Effort) ? "low" : c.Effort;
        var options = new ChatCompletionOptions { MaxOutputTokenCount = Math.Clamp(maxTokens + 8000, 2048, 32000) };
#pragma warning disable OPENAI001 // Reasoning effort is marked experimental in the SDK; the version is pinned in packages.lock.json
        if (effort != "none") options.ReasoningEffortLevel = new ChatReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
        else options.Temperature = 0f;   // only allowed without reasoning
        if (schema != null)
            options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("analysis", BinaryData.FromString(StructuredSchema.ForOpenAi(schema).ToJsonString()),
                jsonSchemaIsStrict: true);

        if (schema == null && onToken != null)
        {
            var sb = new StringBuilder();
            await foreach (var up in client.CompleteChatStreamingAsync(messages, options, ct))
            {
                foreach (var part in up.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    sb.Append(part.Text);
                    onToken(part.Text);
                }
                if (up.RefusalUpdate is { Length: > 0 }) throw new AnalysisValidationException("AI_REFUSED", "The AI declined to answer this request.");
            }
            return sb.ToString();
        }
        ChatCompletion r = await client.CompleteChatAsync(messages, options, ct);
        if (!string.IsNullOrEmpty(r.Refusal) || r.FinishReason == ChatFinishReason.ContentFilter)
            throw new AnalysisValidationException("AI_REFUSED", "The AI declined to read this document.");
        if (r.FinishReason == ChatFinishReason.Length) throw new AnalysisValidationException("AI_INVALID_JSON", "The AI's answer was cut off before it finished.");
        var answer = string.Concat(r.Content.Select(p => p.Text));
        onToken?.Invoke(answer);
        return answer;
    }

    // ------------------------------------------------------------------ errors
    /// <summary>Turns an SDK or network error into one CaseKeeper understands, with a plain message (never the key).</summary>
    private static Exception Map(CloudConfig c, Exception ex)
    {
        var name = AiProviders.Get(c.Provider).Name;
        int status = ex switch
        {
            AnthropicApiException a => (int)a.StatusCode,
            ClientResultException o => o.Status,
            _ => 0
        };
        string Detail() => ex switch
        {
            AnthropicApiException a => Short(a.ResponseBody),
            ClientResultException o => Short(o.GetRawResponse()?.Content?.ToString()),
            _ => ""
        };
        // Retries that all failed come back bundled: the last one says what went wrong
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            var last = agg.InnerExceptions[^1];
            return Map(c, last) ?? (last is AiUnavailableException or AiOverloadException or AnalysisValidationException or TimeoutException ? last : null)
                   ?? new AiUnavailableException($"{name} can't be reached. Check this PC is online.", ex);
        }
        switch (ex)
        {
            case AiUnavailableException or AiOverloadException or AnalysisValidationException or TimeoutException:
                return null;   // already ours
            case AnthropicIOException or HttpRequestException:
                return new AiUnavailableException($"{name} can't be reached. Check this PC is online.", ex);
        }
        if (status == 0 && ex is ClientResultException && ex.InnerException is HttpRequestException)
            return new AiUnavailableException($"{name} can't be reached. Check this PC is online.", ex);
        return status switch
        {
            401 => new AiUnavailableException($"{name} rejected the key. Check it in Settings → AI.", null),
            403 => new AiUnavailableException($"{name} refused access with this key (it may lack permission for this model or resource).", null),
            404 => new AiUnavailableException(AiProviders.Get(c.Provider).IsFoundry
                ? $"No deployment called “{c.Model}” was found in the Foundry resource “{c.Resource}”."
                : $"{name} doesn't offer the model “{c.Model}” to this key.", null),
            429 or 529 => new AiOverloadException($"{name} is busy or this key has reached its rate limit."),
            >= 500 => new AiUnavailableException($"{name} had a problem ({status}). Try again shortly.", null),
            400 or 413 or 422 => new AnalysisValidationException("AI_REQUEST", $"{name} couldn't accept this request{(Detail() is { Length: > 0 } d ? ": " + d : ".")}"),
            > 0 => new AnalysisValidationException("AI_REQUEST", $"{name} refused the request ({status})."),
            _ => null
        };
    }

    /// <summary>The provider's own error message, cut short and with anything that looks like a key removed.</summary>
    private static string Short(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        string msg = body;
        try
        {
            var n = JsonNode.Parse(body);
            msg = n?["error"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>() ?? body;
        }
        catch (JsonException) { }
        msg = Regex.Replace(msg, @"(sk-[A-Za-z0-9_\-]{6,}|[A-Za-z0-9]{32,})", "…");
        return msg.Length > 160 ? msg[..160] + "…" : msg;
    }

    // ------------------------------------------------------------------ setup
    /// <summary>Models the key can use (Anthropic and OpenAI). Foundry has no list: there it's your deployment names.</summary>
    public static async Task<List<string>> ListModelsAsync(CloudConfig c, CancellationToken ct)
    {
        var problem = AiProviders.Check(c with { Model = string.IsNullOrWhiteSpace(c.Model) ? "x" : c.Model });
        if (problem != null) throw new AiUnavailableException(problem);
        try
        {
            if (c.Provider == AiProviders.Anthropic)
            {
                var page = await AnthropicFor(c).Models.List(cancellationToken: ct);
                return page.Items.Select(m => m.ID).ToList();
            }
            if (c.Provider == AiProviders.OpenAi)
            {
                var mc = new OpenAI.Models.OpenAIModelClient(new ApiKeyCredential(c.Key), OpenAiOptions(c));
                var list = (await mc.GetModelsAsync(ct)).Value;
                string[] skip = { "embed", "audio", "image", "tts", "whisper", "dall-e", "realtime", "transcribe", "search", "moderation", "sora", "codex" };
                return list.Select(m => m.Id).Where(id => id.StartsWith("gpt-", StringComparison.Ordinal) && !skip.Any(s => id.Contains(s, StringComparison.Ordinal)))
                    .OrderBy(id => id, StringComparer.Ordinal).ToList();
            }
            return new List<string>();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The AI service took too long to answer."); }
        catch (Exception ex) when (Map(c, ex) is { } mapped) { throw mapped; }
    }

    /// <summary>A tiny request to check the key, resource and model all work. Sends no case material.</summary>
    public static async Task TestAsync(CloudConfig c, CancellationToken ct)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
            ["required"] = new JsonArray("ok")
        };
        var answer = await CompleteAsync(c with { TimeoutMinutes = 2 }, "Reply with JSON only.", new[] { ("user", "Connection test. Reply {\"ok\": true}.") },
            schema, 64, null, ct);
        if (!answer.Contains("ok", StringComparison.OrdinalIgnoreCase)) throw new AnalysisValidationException("AI_REQUEST", "The AI answered, but not as expected.");
    }
}

/// <summary>
/// Adapts CaseKeeper's JSON Schemas to what each service's structured outputs accept. CaseKeeper checks every answer
/// against its own rules afterwards, so dropping a limit here (such as a maximum number of findings) loses nothing.
/// </summary>
public static class StructuredSchema
{
    private static readonly string[] Unsupported = { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "maxItems", "pattern", "format" };

    /// <summary>Claude: every object closed (additionalProperties false), no numeric or length limits.</summary>
    public static JsonObject ForClaude(JsonObject schema) => (JsonObject)Clean(schema.DeepClone(), strictAll: false);

    /// <summary>OpenAI strict mode: also every property required, with optional ones allowed to be null.</summary>
    public static JsonObject ForOpenAi(JsonObject schema) => (JsonObject)Clean(schema.DeepClone(), strictAll: true);

    private static JsonNode Clean(JsonNode node, bool strictAll)
    {
        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++) arr[i] = arr[i] is null ? null : Clean(arr[i], strictAll);
            return arr;
        }
        if (node is not JsonObject o) return node;
        foreach (var k in Unsupported) o.Remove(k);
        if (o["minItems"] is JsonValue mi && mi.TryGetValue<int>(out var n) && n > 1) o["minItems"] = 1;
        if (o["items"] is JsonNode items) o["items"] = Clean(items.DeepClone(), strictAll);
        if (o["anyOf"] is JsonArray any) o["anyOf"] = Clean(any.DeepClone(), strictAll);
        if (o["properties"] is JsonObject props)
        {
            var required = (o["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).Where(r => r != null).ToHashSet() ?? new HashSet<string>();
            foreach (var key in props.Select(p => p.Key).ToList())
            {
                var child = Clean(props[key]!.DeepClone(), strictAll);
                if (strictAll && !required.Contains(key)) child = Nullable(child);
                props[key] = child;
            }
            o["additionalProperties"] = false;
            if (strictAll) o["required"] = new JsonArray(props.Select(p => (JsonNode)JsonValue.Create(p.Key)).ToArray());
        }
        return o;
    }

    /// <summary>Lets an optional property be null, so it can be listed as required in strict mode.</summary>
    private static JsonNode Nullable(JsonNode n)
    {
        if (n is not JsonObject o) return n;
        switch (o["type"])
        {
            case JsonValue v when v.TryGetValue<string>(out var t) && t != "null":
                o["type"] = new JsonArray(t, "null");
                break;
            case JsonArray a when !a.Any(x => x?.GetValue<string>() == "null"):
                a.Add("null");
                break;
        }
        if (o["enum"] is JsonArray e && !e.Any(x => x == null)) e.Add(null);
        return o;
    }
}
