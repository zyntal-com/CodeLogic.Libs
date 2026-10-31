using System.Collections.Frozen;
using CL.Data.Errors;
using CodeLogic.Core.Results;

namespace CL.Data.Providers;

/// <summary>
/// The providers available in this process. Provider libraries register themselves when CodeLogic configures
/// them; hosts without the framework (and tests) call <see cref="Register"/> directly.
/// </summary>
public static class DataProviderRegistry
{
    private static readonly Lock Gate = new();
    private static FrozenDictionary<string, DataProvider> _providers =
        new Dictionary<string, DataProvider>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of the registered providers.</summary>
    public static IReadOnlyCollection<string> Names => _providers.Keys;

    /// <summary>
    /// Registers <paramref name="provider"/>, replacing a provider with the same name. Fails with
    /// <c>data.provider_incompatible</c> when it implements a different contract version.
    /// </summary>
    public static Result Register(DataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Name))
            return DataErrors.Validation("A provider must have a name.");
        if (provider.SpiVersion != DataPackage.SpiVersion)
            return DataErrors.ProviderIncompatible(provider.Name, provider.SpiVersion, DataPackage.SpiVersion);

        lock (Gate)
        {
            var next = new Dictionary<string, DataProvider>(_providers, StringComparer.OrdinalIgnoreCase)
            {
                [provider.Name] = provider,
            };
            _providers = next.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        return Result.Success();
    }

    /// <summary>Removes a provider (tests).</summary>
    public static bool Unregister(string name)
    {
        lock (Gate)
        {
            if (!_providers.ContainsKey(name)) return false;
            _providers = _providers.Where(kv => !string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            return true;
        }
    }

    /// <summary>The provider named <paramref name="name"/>, or <c>data.provider_missing</c>.</summary>
    public static Result<DataProvider> Get(string name) =>
        _providers.TryGetValue(name.Trim(), out var provider)
            ? Result<DataProvider>.Success(provider)
            : DataErrors.ProviderMissing(name);

    /// <summary>True when a provider named <paramref name="name"/> is registered.</summary>
    public static bool IsRegistered(string name) => _providers.ContainsKey(name.Trim());
}
