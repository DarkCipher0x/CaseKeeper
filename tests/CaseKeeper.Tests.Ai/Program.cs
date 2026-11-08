using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CaseKeeper.Services;
using CaseKeeper.Services.BulkImport;

/// <summary>
/// Cloud AI tests. Everything is synthetic: invented names, invented keys, and a scripted stand-in for the network,
/// so nothing is sent anywhere and results are exact.
/// </summary>
static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    const string FakeKey = "test-key-not-real-0000";

    sealed class Stub : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)> Seen = new();
        public Func<Uri, string, HttpResponseMessage> Reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .GroupBy(h => h.Key.ToLowerInvariant()).ToDictionary(g => g.Key, g => string.Join(",", g.SelectMany(h => h.Value)));
            lock (Seen) Seen.Add((request.Method, request.RequestUri, headers, body));
            var r = Reply(request.RequestUri, body);
            r.RequestMessage = request;
            return r;
        }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        // Retry quickly in tests (both SDKs honour this header)
        r.Headers.TryAddWithoutValidation("retry-after-ms", "1");
        r.Headers.TryAddWithoutValidation("x-request-id", "req_test");
        return r;
    }

    static string ClaudeReply(string text, string stop = "end_turn") => new JsonObject
    {
        ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-haiku-5-5",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["stop_reason"] = stop, ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
    }.ToJsonString();

    static string GptReply(string text, string finish = "stop", string refusal = null) => new JsonObject
    {
        ["id"] = "chatcmpl-test", ["object"] = "chat.completion", ["created"] = 1_700_000_000, ["model"] = "gpt-6-luna",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0, ["finish_reason"] = finish,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text, ["refusal"] = refusal }
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 }
    }.ToJsonString();

    static string ErrorBody(string message) => new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "x", ["message"] = message } }.ToJsonString();

    static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["summary"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
            ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["findings"] = new JsonObject { ["type"] = "array", ["maxItems"] = 12, ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["excerpt"] = new JsonObject { ["type"] = "string" }, ["role"] = new JsonObject { ["enum"] = new JsonArray("a", "b") } },
                ["required"] = new JsonArray("excerpt")
            } }
        },
        ["required"] = new JsonArray("summary", "confidence")
    };

    static async Task<Exception> Throws(Func<Task> f)
    {
        try { await f(); return null; }
        catch (Exception ex) { return ex; }
    }

    static int Main()
    {
        try { Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FAIL unexpected: " + ex); failures++; }
        finally { CloudAi.TestHandler = null; }
        Console.WriteLine(failures == 0 ? "\nAll AI tests passed." : $"\n{failures} AI test(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    static async Task Run()
    {
        Hosts();
        Schemas();
        Masking();
        await Anthropic();
        await Foundry();
        await OpenAi();
        await Errors();
        await MaskedClient();
    }

    // ------------------------------------------------------------------ hosts
    static void Hosts()
    {
        Console.WriteLine("\n== Where requests may go");
        Check(AiProviders.IsAllowedHost(new Uri("https://api.anthropic.com/v1/messages")), "api.anthropic.com allowed");
        Check(AiProviders.IsAllowedHost(new Uri("https://api.openai.com/v1/")), "api.openai.com allowed");
        Check(AiProviders.IsAllowedHost(new Uri("https://my-res.services.ai.azure.com/anthropic/")), "a Foundry resource allowed");
        Check(AiProviders.IsAllowedHost(new Uri("https://my-res.openai.azure.com/openai/v1/")), "an Azure OpenAI resource allowed");
        Check(!AiProviders.IsAllowedHost(new Uri("http://api.anthropic.com/")), "plain http refused");
        Check(!AiProviders.IsAllowedHost(new Uri("https://api.anthropic.com.evil.example/")), "look-alike host refused");
        Check(!AiProviders.IsAllowedHost(new Uri("https://evil.example/?x=api.openai.com")), "other host refused");
        Check(!AiProviders.IsAllowedHost(new UriBuilder("https", "api.openai.com") { UserName = "user", Password = "pw" }.Uri), "user name in address refused");
        Check(!AiProviders.IsAllowedHost(new Uri("https://api.openai.com:8443/")), "unusual port refused");
        Check(!AiProviders.IsAllowedHost(new Uri("https://a.b.openai.azure.com/")), "nested sub-domain refused");

        Check(AiProviders.ParseResource("my-resource", out _) == "my-resource", "resource name accepted");
        Check(AiProviders.ParseResource("https://My-Resource.services.ai.azure.com/anthropic/", out _) == "my-resource", "resource taken from a Foundry endpoint");
        Check(AiProviders.ParseResource("https://res1.openai.azure.com/openai/v1/", out _) == "res1", "resource taken from an Azure OpenAI endpoint");
        Check(AiProviders.ParseResource("https://evil.example/", out var p1) == null && p1 != null, "non-Foundry endpoint refused with a reason");
        Check(AiProviders.ParseResource("http://res.openai.azure.com/", out _) == null, "http endpoint refused");
        Check(AiProviders.ParseResource("bad name!", out _) == null, "invalid resource name refused");
        Check(AiProviders.ParseResource("", out _) == null, "empty resource refused");

        Check(AiProviders.Check(new CloudConfig(AiProviders.Anthropic, "", "", "claude-haiku-5-5", "low")) != null, "no key: refused before sending");
        Check(AiProviders.Check(new CloudConfig(AiProviders.AnthropicFoundry, FakeKey, "", "dep", "low")) != null, "Foundry without a resource: refused");
        Check(AiProviders.Check(new CloudConfig(AiProviders.OpenAi, FakeKey, "", "gpt 6", "low")) != null, "model name with a space refused");
        Check(AiProviders.Check(new CloudConfig(AiProviders.Local, FakeKey, "", "x", "low")) != null, "local isn't a cloud provider");
        Check(AiProviders.Check(new CloudConfig(AiProviders.OpenAiFoundry, FakeKey, "res", "my-deploy", "low")) == null, "a complete Foundry config passes");
        Check(AiProviders.Get(AiProviders.Anthropic).DefaultModel == "claude-haiku-5-5" && AiProviders.Get(AiProviders.OpenAi).DefaultModel == "gpt-6-luna",
            "defaults are the fast, low-cost models");
        Check(AiProviders.All.Where(p => p.IsCloud).All(p => p.DefaultEffort == "low"), "default reasoning effort is low everywhere");
    }

    // ------------------------------------------------------------------ schemas
    static void Schemas()
    {
        Console.WriteLine("\n== Structured output schemas");
        var c = StructuredSchema.ForClaude(Schema);
        var text = c.ToJsonString();
        Check(!text.Contains("maxLength") && !text.Contains("minimum") && !text.Contains("maxItems"), "Claude: unsupported limits removed");
        Check(c["additionalProperties"]?.GetValue<bool>() == false && c["properties"]!["findings"]!["items"]!["additionalProperties"]?.GetValue<bool>() == false,
            "Claude: every object closed");
        Check(c["required"]!.AsArray().Count == 2, "Claude: required list unchanged");
        var o = StructuredSchema.ForOpenAi(Schema);
        Check(o["required"]!.AsArray().Count == 4, "OpenAI strict: every property required");
        Check(o["properties"]!["page"]!["type"] is JsonArray t && t.Any(x => x?.GetValue<string>() == "null"), "OpenAI strict: optional property may be null");
        var role = o["properties"]!["findings"]!["items"]!["properties"]!["role"]!;
        Check(role["enum"]!.AsArray().Any(x => x == null), "OpenAI strict: optional enum allows null");
        Check(Schema["properties"]!["summary"]!["maxLength"] != null, "the original schema is left untouched");
    }

    // ------------------------------------------------------------------ masking
    static void Masking()
    {
        Console.WriteLine("\n== Masking");
        var m = new Masker(new[] { "Jordan Ashworth-Bell", "Priya Quill" }, new[] { "14 Larkspur Road, Exampletown" });
        var original = "Statement of Jordan Ashworth-Bell (DOB: 03/04/1985), of 14 Larkspur Road, Exampletown EX1 2AB.\n" +
                       "Email jordan.ab@example.com, mobile 07700 900123. NI number JK 12 34 56 C. Case number ZZ24P01234.\n" +
                       "Priya said Jordan collected the children. Mr Halvorsen from the school also attended. Sort code 12-34-56, account number 12345678.\n" +
                       "Card 4111 1111 1111 1111 was used.";
        var masked = m.Mask(original);
        string[] secrets = { "Jordan", "Ashworth", "Priya", "Quill", "Larkspur", "EX1 2AB", "jordan.ab@example.com", "07700 900123", "JK 12 34 56 C",
                             "ZZ24P01234", "03/04/1985", "Halvorsen", "12-34-56", "12345678", "4111 1111 1111 1111" };
        foreach (var s in secrets) Check(!masked.Contains(s, StringComparison.OrdinalIgnoreCase), $"masked: {s}");
        Check(masked.Contains("[PERSON-") && masked.Contains("[EMAIL-1]") && masked.Contains("[PHONE-1]") && masked.Contains("[NINO-1]") && masked.Contains("[CASE-1]"),
            "placeholders used");
        Check(masked.Contains("collected the children") && masked.Contains("from the school"), "ordinary words kept");
        Check(m.Unmask(masked) == original, "unmasking gives back exactly the original text");
        Check(m.Mask("Jordan Ashworth-Bell") == m.Mask("Jordan Ashworth-Bell"), "same value, same placeholder");
        var full = m.Mask("Jordan Ashworth-Bell");
        Check(m.Unmask("[PERSON-99] and " + full) == "[PERSON-99] and Jordan Ashworth-Bell", "unknown placeholders left alone");

        // JSON answers: unmasked inside strings only, and still valid JSON
        var json = new JsonObject { ["excerpt"] = $"{full} said \"hello\"", ["n"] = 3, ["list"] = new JsonArray("[EMAIL-1]") }.ToJsonString();
        var back = JsonNode.Parse(m.UnmaskJson(json))!;
        Check(back["excerpt"]!.GetValue<string>() == "Jordan Ashworth-Bell said \"hello\"" && back["n"]!.GetValue<int>() == 3 &&
              back["list"]![0]!.GetValue<string>() == "jordan.ab@example.com", "JSON unmasked and still valid");
        Check(m.UnmaskJson("not json [EMAIL-1]") == "not json jordan.ab@example.com", "unreadable JSON unmasked as text");

        // Streaming: a placeholder split across pieces is put back whole
        var stream = new Masker.StreamUnmasker(m);
        var shown = stream.Push("Contact [EM") + stream.Push("AIL-1] now. ") + stream.Push("[PHONE") + stream.Push("-1]") + stream.Flush();
        Check(shown == "Contact jordan.ab@example.com now. 07700 900123", "streamed answer unmasked across pieces");

        var poor = Masker.Assess(new Masker().Mask("Report by Sandra Pemberton. Kevin Oduya visited. Lena Marsh and Tobias Wren attended. Grace Holloway noted it. Felix Arden agreed."));
        Check(poor.Poor && poor.LikelyNamesLeft >= 4, "names masking missed are noticed");
        var good = Masker.Assess(m.Mask(original));
        Check(!good.Poor, "a well masked text is not flagged");
        var scan = Masker.Assess(string.Concat(Enumerable.Repeat("~|#}{ ^^ ¬¬ |l1 ;; ", 60)));
        Check(scan.LowQualityText && scan.Poor, "a garbled scan is flagged");
        var probe = m.CloneKnown();
        probe.Mask("Priya Quill");
        Check(probe.Counts.GetValueOrDefault("PERSON") == 1, "per-document counts start from zero");
    }

    // ------------------------------------------------------------------ Anthropic
    static async Task Anthropic()
    {
        Console.WriteLine("\n== Anthropic");
        var stub = new Stub { Reply = (_, _) => Json(ClaudeReply("{\"summary\":\"ok\",\"confidence\":0.9}")) };
        CloudAi.TestHandler = stub;
        var c = new CloudConfig(AiProviders.Anthropic, FakeKey, "", "claude-haiku-5-5", "low");
        var answer = await CloudAi.CompleteAsync(c, "system text", new[] { ("user", "hello") }, Schema, 1024, null, CancellationToken.None);
        Check(answer.Contains("\"summary\":\"ok\""), "structured answer returned");
        var req = stub.Seen.Last();
        Check(req.Uri.Host == "api.anthropic.com" && req.Uri.AbsolutePath == "/v1/messages", "sent to api.anthropic.com/v1/messages");
        Check(req.Headers.Count(h => h.Value.Contains(FakeKey)) == 1 && (req.Headers.ContainsKey("x-api-key") || req.Headers.ContainsKey("authorization"))
              && !req.Body.Contains(FakeKey) && !req.Uri.ToString().Contains(FakeKey), "key sent once, in the authentication header only");
        var body = JsonNode.Parse(req.Body)!;
        Check(body["model"]?.GetValue<string>() == "claude-haiku-5-5", "model sent");
        Check(body["output_config"]?["effort"]?.GetValue<string>() == "low", "effort sent");
        Check(body["output_config"]?["format"]?["type"]?.GetValue<string>() == "json_schema" && !req.Body.Contains("maxLength"), "adapted JSON schema sent");
        Check(body["thinking"]?["type"]?.GetValue<string>() == "adaptive", "adaptive thinking");
        Check(body["system"]?.ToJsonString().Contains("system text") == true, "system prompt sent");
        Check(!req.Body.Contains("temperature"), "no temperature with thinking");

        stub.Reply = (_, _) => Json(ClaudeReply("", "refusal"));
        var ex = await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
        Check(ex is AnalysisValidationException { Code: "AI_REFUSED" }, "refusal → AI_REFUSED");
        stub.Reply = (_, _) => Json(ClaudeReply("{\"summ", "max_tokens"));
        ex = await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
        Check(ex is AnalysisValidationException { Code: "AI_INVALID_JSON" }, "cut-off answer → AI_INVALID_JSON");

        stub.Reply = (_, _) => Json(new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "claude-haiku-5-5", ["type"] = "model", ["display_name"] = "Claude Haiku 5.5", ["created_at"] = "2026-01-01T00:00:00Z" }), ["has_more"] = false, ["first_id"] = "a", ["last_id"] = "a" }.ToJsonString());
        var models = await CloudAi.ListModelsAsync(c, CancellationToken.None);
        Check(models.Contains("claude-haiku-5-5"), "models listed");
    }

    static async Task Foundry()
    {
        Console.WriteLine("\n== Microsoft Foundry");
        var stub = new Stub { Reply = (_, _) => Json(ClaudeReply("{\"summary\":\"ok\",\"confidence\":1}")) };
        CloudAi.TestHandler = stub;
        var c = new CloudConfig(AiProviders.AnthropicFoundry, FakeKey, "contoso-ai", "my-haiku", "medium");
        await CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None);
        var req = stub.Seen.Last();
        Check(req.Uri.Host == "contoso-ai.services.ai.azure.com" && req.Uri.AbsolutePath.StartsWith("/anthropic/"), "Claude on Foundry: your resource's address");
        Check(req.Headers.Values.Any(v => v.Contains(FakeKey)), "Claude on Foundry: key sent");
        Check(JsonNode.Parse(req.Body)!["model"]?.GetValue<string>() == "my-haiku", "Claude on Foundry: deployment name used as the model");
        Check(JsonNode.Parse(req.Body)!["output_config"]?["effort"]?.GetValue<string>() == "medium", "Claude on Foundry: effort sent");

        stub.Reply = (_, _) => Json(GptReply("{\"summary\":\"ok\",\"confidence\":1}"));
        var o = new CloudConfig(AiProviders.OpenAiFoundry, FakeKey, "contoso-oai", "my-luna", "low");
        await CloudAi.CompleteAsync(o, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None);
        req = stub.Seen.Last();
        Check(req.Uri.Host == "contoso-oai.openai.azure.com" && req.Uri.AbsolutePath == "/openai/v1/chat/completions", "OpenAI on Foundry: the v1 endpoint");
        Check(JsonNode.Parse(req.Body)!["model"]?.GetValue<string>() == "my-luna", "OpenAI on Foundry: deployment name used as the model");
    }

    static async Task OpenAi()
    {
        Console.WriteLine("\n== OpenAI");
        var stub = new Stub { Reply = (_, _) => Json(GptReply("{\"summary\":\"ok\",\"confidence\":0.5}")) };
        CloudAi.TestHandler = stub;
        var c = new CloudConfig(AiProviders.OpenAi, FakeKey, "", "gpt-6-luna", "low");
        var answer = await CloudAi.CompleteAsync(c, "sys", new[] { ("user", "u1"), ("assistant", "a1"), ("user", "u2") }, Schema, 500, null, CancellationToken.None);
        Check(answer.Contains("\"confidence\":0.5"), "structured answer returned");
        var req = stub.Seen.Last();
        Check(req.Uri.Host == "api.openai.com" && req.Uri.AbsolutePath == "/v1/chat/completions", "sent to api.openai.com");
        Check(req.Headers.GetValueOrDefault("authorization") == "Bearer " + FakeKey, "key sent as a bearer token");
        var body = JsonNode.Parse(req.Body)!;
        Check(body["reasoning_effort"]?.GetValue<string>() == "low" && body["temperature"] == null, "effort sent, no temperature");
        Check(body["response_format"]?["type"]?.GetValue<string>() == "json_schema" && body["response_format"]?["json_schema"]?["strict"]?.GetValue<bool>() == true,
            "strict JSON schema sent");
        Check(body["messages"]?.AsArray().Count == 4 && body["messages"]![0]!["role"]?.GetValue<string>() is "system" or "developer", "system prompt and turns sent in order");

        await CloudAi.CompleteAsync(c with { Effort = "none" }, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None);
        body = JsonNode.Parse(stub.Seen.Last().Body)!;
        Check(body["reasoning_effort"] == null && body["temperature"]?.GetValue<double>() == 0, "effort none: no reasoning, temperature 0");
        await CloudAi.CompleteAsync(c with { Effort = "xhigh" }, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None);
        Check(JsonNode.Parse(stub.Seen.Last().Body)!["reasoning_effort"]?.GetValue<string>() == "xhigh", "effort xhigh sent");

        stub.Reply = (_, _) => Json(GptReply(null, "stop", "I can't help with that."));
        var ex = await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
        Check(ex is AnalysisValidationException { Code: "AI_REFUSED" }, "refusal → AI_REFUSED");
        stub.Reply = (_, _) => Json(GptReply("{\"su", "length"));
        ex = await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
        Check(ex is AnalysisValidationException { Code: "AI_INVALID_JSON" }, "cut-off answer → AI_INVALID_JSON");
    }

    static async Task Errors()
    {
        Console.WriteLine("\n== Errors");
        foreach (var provider in new[] { AiProviders.Anthropic, AiProviders.OpenAi })
        {
            var stub = new Stub();
            CloudAi.TestHandler = stub;
            var c = new CloudConfig(provider, FakeKey, "", provider == AiProviders.Anthropic ? "claude-haiku-5-5" : "gpt-6-luna", "low");
            async Task<Exception> With(HttpStatusCode code, string message = "nope")
            {
                stub.Reply = (_, _) => Json(ErrorBody(message), code);
                return await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
            }
            var e401 = await With(HttpStatusCode.Unauthorized);
            Check(e401 is AiUnavailableException && e401.Message.Contains("rejected the key") && !e401.Message.Contains(FakeKey), $"{provider}: 401 → key rejected (key not in message)");
            Check(await With(HttpStatusCode.NotFound) is AiUnavailableException, $"{provider}: 404 → model not available");
            int before = stub.Seen.Count;
            Check(await With((HttpStatusCode)429) is AiOverloadException, $"{provider}: 429 → busy (CaseKeeper slows down)");
            Check(stub.Seen.Count - before >= 2, $"{provider}: 429 retried before giving up");
            Check(await With(HttpStatusCode.InternalServerError) is AiUnavailableException, $"{provider}: 500 → unavailable");
            var e400 = await With(HttpStatusCode.BadRequest, "bad thing with key sk-ABCDEFGHIJKLMNOP1234");
            Check(e400 is AnalysisValidationException { Code: "AI_REQUEST" } && !e400.Message.Contains("sk-ABCDEFGHIJ"), $"{provider}: 400 → this request only, key-like text hidden");

            stub.Reply = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("https://evil.example/steal");
                return r;
            };
            await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
            Check(stub.Seen.All(s => s.Uri.Host != "evil.example"), $"{provider}: redirect not followed");
            stub.Seen.Clear();
            stub.Reply = (_, _) => throw new HttpRequestException("offline");
            Check(await Throws(() => CloudAi.CompleteAsync(c, "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None)) is AiUnavailableException,
                $"{provider}: no network → unavailable");
        }
        // Whatever the SDK is told, nothing goes to a host that isn't allowed (an environment variable pointing elsewhere, say)
        var guard = new Stub { Reply = (_, _) => Json(ClaudeReply("{}")) };
        CloudAi.TestHandler = guard;
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "env-token-should-not-be-sent");
        await CloudAi.CompleteAsync(new CloudConfig(AiProviders.Anthropic, FakeKey, "", "claude-haiku-5-5", "low"), "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None);
        Check(guard.Seen.All(r => !r.Headers.Values.Any(v => v.Contains("env-token"))), "an ANTHROPIC_AUTH_TOKEN variable is never sent");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        var none = await Throws(() => CloudAi.CompleteAsync(new CloudConfig(AiProviders.OpenAi, "", "", "gpt-6-luna", "low"), "s", new[] { ("user", "u") }, Schema, 100, null, CancellationToken.None));
        Check(none is AiUnavailableException, "no key: nothing sent");
    }

    // ------------------------------------------------------------------ masked bulk client
    static async Task MaskedClient()
    {
        Console.WriteLine("\n== Bulk import through a cloud AI, masked");
        foreach (var provider in new[] { AiProviders.Anthropic, AiProviders.OpenAi })
        {
            var stub = new Stub();
            // The stand-in AI quotes the (masked) text back, as a real one would
            stub.Reply = (_, body) =>
            {
                var sent = body;
                var token = System.Text.RegularExpressions.Regex.Match(sent, @"\[PERSON-\d+\]").Value;
                var json = new JsonObject { ["summary"] = $"Letter from {token}", ["confidence"] = 0.9 }.ToJsonString();
                return Json(provider == AiProviders.Anthropic ? ClaudeReply(json) : GptReply(json));
            };
            CloudAi.TestHandler = stub;
            var masker = new Masker(new[] { "Morwenna Treloar" });
            var client = new CloudJsonClient(new CloudConfig(provider, FakeKey, "", AiProviders.Get(provider).DefaultModel, "low"), masker);
            var answer = await client.CompleteJsonAsync("Sort this.", "Letter from Morwenna Treloar, email m.treloar@example.org, about contact.", Schema, CancellationToken.None);
            var body = stub.Seen.Last().Body;
            Check(!body.Contains("Morwenna") && !body.Contains("Treloar") && !body.Contains("m.treloar@example.org"), $"{provider}: no names or e-mail in what was sent");
            Check(body.Contains("placeholders"), $"{provider}: the AI is told about placeholders");
            Check(JsonNode.Parse(answer)!["summary"]!.GetValue<string>() == "Letter from Morwenna Treloar", $"{provider}: answer unmasked before CaseKeeper checks it");
            Check(client.ModelName.StartsWith(provider + "/"), $"{provider}: model name includes the provider (re-analysed if it changes)");
            Check(client.Host is "api.anthropic.com" or "api.openai.com", $"{provider}: host shown for the review");
        }
    }
}
