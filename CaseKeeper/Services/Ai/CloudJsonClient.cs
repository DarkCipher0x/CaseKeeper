using System.Text.Json.Nodes;
using CaseKeeper.Services.BulkImport;

namespace CaseKeeper.Services;

/// <summary>
/// Bulk Case Import through a cloud AI. With masking on, personal details are replaced with placeholders before each
/// request, and put back in the answer before CaseKeeper checks it (so quotes and dates are checked against the real text).
/// </summary>
public sealed class CloudJsonClient : ICloudAiClient
{
    private readonly CloudConfig _config;
    public Masker Masker { get; }
    public CloudJsonClient(CloudConfig config, Masker masker, bool categoriesOnly = false)
    {
        _config = config;
        Masker = masker;
        CategoriesOnly = categoriesOnly;
    }

    public string ModelName => $"{_config.Provider}/{_config.Model}";
    public string ProviderId => _config.Provider;
    public string ProviderName => AiProviders.Get(_config.Provider).Name;
    public string Host => AiProviders.BaseAddress(_config.Provider, _config.Resource).Host;
    public bool CategoriesOnly { get; }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(AiProviders.Check(_config) == null);

    public async Task<string> CompleteJsonAsync(string system, string user, JsonObject schema, CancellationToken ct)
    {
        if (Masker != null)
        {
            system = (system ?? "") + "\n\n" + Masker.Instructions;
            user = Masker.Mask(user);
        }
        var json = await CloudAi.CompleteAsync(_config, system, new[] { ("user", user) }, schema, CategoriesOnly ? 1024 : 4096, null, ct);
        return Masker != null ? Masker.UnmaskJson(json) : json;
    }
}
