namespace CaseManagement.Api.Services.Customer360;

using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// The customer Customer 360 should open: how Customer 360's own search finds them, and which of the matches they are.
/// </summary>
/// <param name="CustomerType">Individual or NonIndividual.</param>
/// <param name="IdType">Customer 360's search type: NRIC or SECONDARYID for an individual, BRN or OLDBRN for a company.</param>
/// <param name="IdSubtype">For SECONDARYID only: PASSPORT, OLDID, …</param>
/// <param name="Value">The identifier itself (unmasked — this is what the search needs).</param>
/// <param name="ExternalCustomerId">Customer 360's own key for the customer, to pick them among several matches.</param>
public sealed record Customer360HandoffTarget(string CustomerType, string IdType, string? IdSubtype, string Value, string ExternalCustomerId);

/// <summary>"Open Customer 360" from Case Management, without the customer's identifier ever being in an address.</summary>
public interface ICustomer360Handoff
{
    /// <summary>A one-time code for <paramref name="target"/>, redeemable only by <paramref name="person"/>, for a short while.</summary>
    string Issue(Customer360HandoffTarget target, string person);

    /// <summary>The target behind <paramref name="code"/> — once, and only for the person it was issued to; otherwise null.</summary>
    Customer360HandoffTarget? Redeem(string code, string person);
}

/// <summary>
/// Customer 360 opens a customer by searching for their identifier (NRIC / BRN …). That identifier must not travel in
/// a URL (proxies and access logs keep URLs), and Case Management's screens only ever hold it masked. So the button
/// asks Case Management for a one-time code; the code — meaningless on its own — travels to Customer 360 in the
/// host's in-memory navigation state; Customer 360 redeems it here for the identifier and runs its own search with
/// the person's own token, so Customer 360 still decides (and audits) whether they may see the profile.
/// </summary>
/// <remarks>
/// A code lives <see cref="Lifetime"/>, works once, and only for the person it was issued to. It is held in this
/// process's memory: with several Case Management instances behind a load balancer the redeem may land on another
/// instance and fail (the person simply clicks again) — move this to the distributed cache before scaling out.
/// </remarks>
public sealed class Customer360Handoff(IMemoryCache cache) : ICustomer360Handoff
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Entry(string Person, Customer360HandoffTarget Target);

    public string Issue(Customer360HandoffTarget target, string person)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        cache.Set(Key(code), new Entry(person, target), Lifetime);
        return code;
    }

    public Customer360HandoffTarget? Redeem(string code, string person)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100) return null;
        var key = Key(code.Trim());
        if (!cache.TryGetValue(key, out Entry? entry) || entry == null) return null;
        cache.Remove(key);   // once, whoever redeems it: a code tried by someone else is spent, not left to guess at
        return string.Equals(entry.Person, person, StringComparison.Ordinal) ? entry.Target : null;
    }

    private static string Key(string code) => $"customer360-handoff:{code}";
}
