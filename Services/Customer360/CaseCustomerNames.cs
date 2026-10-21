namespace CaseManagement.Api.Services.Customer360;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>Names the Customer 360 customer on each case of a list, read live — a case stores only identifiers.</summary>
public interface ICaseCustomerNames
{
    /// <summary>
    /// Sets the customer name on every listed Customer 360 case that Customer 360 can name for the signed-in person.
    /// A case it cannot name keeps an empty name, which the screens show as "Customer 360 · Individual" as before.
    /// </summary>
    Task FillAsync<T>(IReadOnlyCollection<T> items, Func<T, Guid> id, Func<T, string?> source, Func<T, string?> name,
        Action<T, string> setName, CancellationToken ct);
}

/// <summary>
/// Case lists show who each case is for. A Customer 360 customer's name is never stored with the case (only the CRM
/// key and the encrypted identifier that finds them again), so the name is read from Customer 360 when the list is
/// read, exactly as the case drawer does for one case.
/// </summary>
/// <remarks>
/// <para><b>Cost kept bounded:</b> one Customer 360 call per stored identifier on the page, however many cases and
/// customers share it; the profile call only (no contact details); a few calls at a time; at most
/// <see cref="MaxLookupsPerList"/> calls per list. A name is remembered for <see cref="NameLifetime"/> in this process's memory only — never in
/// the database — keyed by the signed-in person, because Customer 360 decides per person what they may see. Without
/// it a list that refreshes itself would repeat (and Customer 360 would audit) the same lookups every few seconds.</para>
/// <para><b>Never blocks the list:</b> when Customer 360 is down or refuses the person, the remaining names are left
/// empty and the list answers as it did before. A failure is remembered briefly so a down Customer 360 is not called
/// for every row of every refresh.</para>
/// <para>Names are masked by the same field rules as everywhere else (<see cref="IPiiMaskingService"/>).</para>
/// </remarks>
public sealed class CaseCustomerNames(
    AppDbContext context,
    ICustomer360CustomerService customer360,
    ICustomerIdentifierProtector protector,
    IPiiMaskingService masking,
    ICurrentUserAccessor currentUser,
    IMemoryCache cache,
    ILogger<CaseCustomerNames> logger) : ICaseCustomerNames
{
    internal const int MaxLookupsPerList = 50;
    private const int Parallelism = 4;
    internal static readonly TimeSpan NameLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);

    /// <summary>One customer on one case: the identifier the case stores, and which of its matches the customer is.</summary>
    private sealed record Key(Lookup Lookup, string ExternalCustomerId);

    /// <summary>One Customer 360 lookup: every customer it returns is named by a single call.</summary>
    private sealed record Lookup(string CustomerType, string LookupType, string LookupKeyHash);

    private sealed record Row(Guid Id, string? CustomerType, string? LookupType, string? Encrypted, string? Hash, string? ExternalId);

    public async Task FillAsync<T>(IReadOnlyCollection<T> items, Func<T, Guid> id, Func<T, string?> source, Func<T, string?> name,
        Action<T, string> setName, CancellationToken ct)
    {
        var wanted = items.Where(i => source(i) == CustomerSources.Customer360 && string.IsNullOrEmpty(name(i))).ToList();
        if (wanted.Count == 0) return;

        var ids = wanted.Select(id).Distinct().ToList();
        var rows = await context.Cases.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.CustomerSource == CustomerSources.Customer360)
            .Select(c => new Row(c.Id, c.CustomerType, c.CustomerLookupType, c.CustomerLookupKeyEncrypted, c.CustomerLookupKeyHash, c.ExternalCustomerId))
            .ToListAsync(ct);

        var keyOf = new Dictionary<Guid, Key>();
        var encryptedOf = new Dictionary<Lookup, string>();
        foreach (var r in rows)
        {
            if (r.CustomerType == null || r.LookupType == null || r.Encrypted == null || r.Hash == null || r.ExternalId == null) continue;
            var lookup = new Lookup(r.CustomerType, r.LookupType, r.Hash);
            keyOf[r.Id] = new Key(lookup, r.ExternalId);
            encryptedOf.TryAdd(lookup, r.Encrypted);
        }
        if (keyOf.Count == 0) return;

        var person = currentUser.Principal?.ExternalUserId ?? currentUser.LocalUserId?.ToString() ?? "anonymous";
        string CacheKey(Key k) => $"case-customer-name:{person}:{k.Lookup.CustomerType}:{k.Lookup.LookupType}:{k.Lookup.LookupKeyHash}:{k.ExternalCustomerId}";

        var names = new Dictionary<Key, string>();
        var toLookUp = new Dictionary<Lookup, string>();   // lookup -> encrypted identifier
        var failedRecently = false;
        foreach (var key in keyOf.Values.Distinct())
        {
            if (cache.TryGetValue(CacheKey(key), out string? cached))
            {
                if (cached == null) failedRecently = true;
                else if (cached.Length > 0) names[key] = cached;
                continue;
            }
            if (toLookUp.Count < MaxLookupsPerList || toLookUp.ContainsKey(key.Lookup)) toLookUp[key.Lookup] = encryptedOf[key.Lookup];
        }

        if (toLookUp.Count > 0 && !failedRecently)
        {
            var (found, failed) = await LookUpAsync(toLookUp, ct);
            foreach (var key in keyOf.Values.Distinct().Where(k => toLookUp.ContainsKey(k.Lookup)))
            {
                if (!found.TryGetValue(key.Lookup, out var matches))
                {
                    if (failed) cache.Set<string?>(CacheKey(key), null, FailureLifetime);
                    continue;
                }
                var dto = matches.FirstOrDefault(m => string.Equals(m.ExternalCustomerId, key.ExternalCustomerId, StringComparison.Ordinal));
                if (dto != null) await masking.MaskAsync(dto, ct);   // one at a time: masking reads configuration
                var shown = dto?.FullName ?? string.Empty;          // "" = no longer in Customer 360: keep the label
                cache.Set(CacheKey(key), shown, NameLifetime);
                if (shown.Length > 0) names[key] = shown;
            }
        }

        foreach (var item in wanted)
            if (keyOf.TryGetValue(id(item), out var key) && names.TryGetValue(key, out var shown))
                setName(item, shown);
    }

    private async Task<(Dictionary<Lookup, IReadOnlyList<Customer360CustomerDto>> Found, bool Failed)> LookUpAsync(
        Dictionary<Lookup, string> lookups, CancellationToken ct)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<Lookup, IReadOnlyList<Customer360CustomerDto>>();
        var failed = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try { await customer360.GetSearchOptionsAsync(ct); }   // read once (cached) before the parallel lookups use it
        catch (Customer360Exception ex)
        {
            logger.LogInformation("Customer names not shown in a case list: {Reason}", ex.Message);
            return (new(), true);
        }

        try
        {
            await Parallel.ForEachAsync(lookups, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stop.Token },
                async (pair, token) =>
                {
                    try
                    {
                        var lookupKey = protector.Unprotect(pair.Value);
                        results[pair.Key] = await customer360.FindForListAsync(pair.Key.CustomerType, pair.Key.LookupType, lookupKey, token);
                    }
                    catch (Customer360Exception ex)
                    {
                        // Down or refusing this person: no point asking for the rest of the page.
                        if (Interlocked.Exchange(ref failed, 1) == 0)
                            logger.LogInformation("Customer names not shown in a case list: {Reason}", ex.Message);
                        await stop.CancelAsync();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "A case's Customer 360 identifier could not be read for its list name.");
                    }
                });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Stopped early after Customer 360 failed: the names found so far are still used.
        }

        return (new Dictionary<Lookup, IReadOnlyList<Customer360CustomerDto>>(results), failed == 1);
    }
}
