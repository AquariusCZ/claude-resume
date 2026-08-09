using Tomlyn;
using Tomlyn.Model;

namespace AiResume.Wrapper;

public sealed record CcConnectProviderModel(string Model, string Alias);

public sealed record CcConnectProviderDescriptor(
    string Name,
    string BaseUrl,
    string Model,
    IReadOnlyList<string> AgentTypes,
    IReadOnlyDictionary<string, string> Endpoints,
    IReadOnlyDictionary<string, string> AgentModels,
    IReadOnlyList<CcConnectProviderModel> Models,
    bool HasModelsDefinition,
    IReadOnlyDictionary<string, IReadOnlyList<CcConnectProviderModel>> AgentModelLists,
    bool HasAgentModelListsDefinition)
{
    public bool SupportsAgent(string agentType)
    {
        if (agentType.Length == 0)
        {
            return true;
        }

        if (AgentTypes.Count > 0 &&
            !AgentTypes.Contains(agentType, StringComparer.Ordinal))
        {
            return false;
        }

        string effectiveEndpoint = Endpoints.TryGetValue(agentType, out string? endpoint) && endpoint.Length > 0
            ? endpoint
            : BaseUrl;
        return agentType.Equals("claudecode", StringComparison.Ordinal) ||
               !effectiveEndpoint.Contains("/anthropic", StringComparison.OrdinalIgnoreCase);
    }

    public string EffectiveModel(string agentType) =>
        AgentModels.TryGetValue(agentType, out string? model) && model.Length > 0
            ? model
            : Model;

    public IReadOnlyList<CcConnectProviderModel> EffectiveModels(string agentType) =>
        AgentModelLists.TryGetValue(agentType, out IReadOnlyList<CcConnectProviderModel>? models) &&
        models.Count > 0
            ? models
            : Models;
}

/// <summary>
/// Structured, read-only view of cc-connect global providers. The source TOML remains user-owned;
/// this class never serializes it, so comments, ordering and unknown fields are preserved.
/// </summary>
public sealed class CcConnectProviderCatalog
{
    private readonly IReadOnlyList<CcConnectProviderDescriptor> _providers;

    private CcConnectProviderCatalog(IReadOnlyList<CcConnectProviderDescriptor> providers)
    {
        _providers = providers;
    }

    public IReadOnlyList<CcConnectProviderDescriptor> Providers => _providers;

    public static CcConnectProviderCatalog Parse(string? toml)
    {
        TomlTable root = TomlSerializer.Deserialize<TomlTable>(toml ?? string.Empty)
            ?? new TomlTable();
        if (!root.TryGetValue("providers", out object? raw) || raw is not TomlTableArray array)
        {
            return new CcConnectProviderCatalog(Array.Empty<CcConnectProviderDescriptor>());
        }

        // Upstream builds globalByName by assignment, so duplicate names are last-wins.
        var last = new Dictionary<string, (int Index, CcConnectProviderDescriptor Provider)>(
            StringComparer.Ordinal);
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not TomlTable table)
            {
                continue;
            }

            CcConnectProviderDescriptor provider = ParseProvider(table);
            if (provider.Name.Length > 0)
            {
                last[provider.Name] = (i, provider);
            }
        }

        return new CcConnectProviderCatalog(last.Values
            .OrderBy(value => value.Index)
            .Select(value => value.Provider)
            .ToArray());
    }

    public CcConnectProviderDescriptor? Find(string name) =>
        _providers.LastOrDefault(provider =>
            provider.Name.Equals(name, StringComparison.Ordinal));

    private static CcConnectProviderDescriptor ParseProvider(TomlTable table)
    {
        return new CcConnectProviderDescriptor(
            ReadString(table, "name"),
            ReadString(table, "base_url"),
            ReadString(table, "model"),
            ReadStringArray(table, "agent_types"),
            ReadStringMap(table, "endpoints"),
            ReadStringMap(table, "agent_models"),
            ReadModels(table, "models"),
            table.ContainsKey("models"),
            ReadModelLists(table, "agent_model_lists"),
            table.ContainsKey("agent_model_lists"));
    }

    private static string ReadString(TomlTable table, string key) =>
        table.TryGetValue(key, out object? value) && value is string text ? text : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out object? value) || value is not TomlArray array)
        {
            return Array.Empty<string>();
        }

        return array.OfType<string>().ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(TomlTable table, string key)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!table.TryGetValue(key, out object? value) || value is not TomlTable map)
        {
            return result;
        }

        foreach ((string name, object? raw) in map)
        {
            if (raw is string text)
            {
                result[name] = text;
            }
        }

        return result;
    }

    private static IReadOnlyList<CcConnectProviderModel> ReadModels(TomlTable table, string key)
    {
        return ReadModelArray(table.TryGetValue(key, out object? value) ? value : null)
            .Select(model => new CcConnectProviderModel(
                ReadString(model, "model"),
                ReadString(model, "alias")))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CcConnectProviderModel>> ReadModelLists(
        TomlTable table,
        string key)
    {
        var result = new Dictionary<string, IReadOnlyList<CcConnectProviderModel>>(
            StringComparer.Ordinal);
        if (!table.TryGetValue(key, out object? value) || value is not TomlTable lists)
        {
            return result;
        }

        foreach ((string agent, object? raw) in lists)
        {
            CcConnectProviderModel[] models = ReadModelArray(raw)
                .Select(model => new CcConnectProviderModel(
                    ReadString(model, "model"),
                    ReadString(model, "alias")))
                .ToArray();
            result[agent] = models;
        }

        return result;
    }

    private static IEnumerable<TomlTable> ReadModelArray(object? value) => value switch
    {
        TomlTableArray tables => tables.OfType<TomlTable>(),
        TomlArray array => array.OfType<TomlTable>(),
        _ => Array.Empty<TomlTable>(),
    };
}
