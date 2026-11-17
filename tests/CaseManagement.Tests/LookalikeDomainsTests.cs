using CaseManagement.Api.Services.EmailIntake;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The look-alike domain check that decides whether an unregistered sender's email waits as "similar to an organisation" or as unknown.
/// </summary>
/// <remarks>
/// Look-alike domains are how a client's name is borrowed to get a forged request worked: a Cyrillic letter that renders like a Latin one,
/// a one-letter typo, the same name under another ending. Missing one files the email as unknown (the reviewer is not warned); flagging
/// unrelated domains buries the real warnings. Each case below pins one rule, and the reason it gives, since the reason is what a
/// reviewer reads. None of this verifies anything: it only says what resembles what.
/// </remarks>
public class LookalikeDomainsTests
{
    private static readonly LookalikeDomains.Registered[] Registered =
    [
        new("bsn.example", "BSN Organization", false),
        new("maybank.com.my", "Maybank", false),
    ];

    private static LookalikeDomains.Resemblance? Find(string domain, string? name = null) => LookalikeDomains.Find(domain, name, Registered);

    [Theory]
    [InlineData("bѕn.example", "Cyrillic")]           // Cyrillic "ѕ"
    [InlineData("xn--bn-eoc.example", "Cyrillic")]    // the same domain as it travels (punycode)
    [InlineData("maybаnk.com.my", "Cyrillic")]        // Cyrillic "а"
    public void Look_alike_characters_are_named_with_their_script(string domain, string script)
    {
        var hit = Find(domain);

        Assert.NotNull(hit);
        Assert.Contains("look-alike characters", hit!.Reason);
        Assert.Contains(script, hit.Reason);
    }

    [Theory]
    [InlineData("b5n.example")]        // 5 for s
    [InlineData("rnaybank.com.my")]    // rn for m
    public void Digits_and_letter_pairs_that_read_as_letters_are_a_resemblance(string domain) =>
        Assert.Contains("look-alike characters are read as letters", Find(domain)!.Reason);

    [Theory]
    [InlineData("bsm.example", "1 character away")]
    [InlineData("maybamk.com.my", "1 character away")]
    [InlineData("maybnak.com.my", "2 characters away")]
    public void A_typo_of_the_name_is_a_resemblance(string domain, string reason) =>
        Assert.Contains(reason, Find(domain)!.Reason);

    [Fact]
    public void The_same_name_under_another_ending_is_a_resemblance() =>
        Assert.Contains("under a different ending", Find("maybank.com")!.Reason);

    [Fact]
    public void An_unaccepted_sub_domain_is_a_resemblance() =>
        Assert.Contains("registered without its sub-domains", Find("it.bsn.example")!.Reason);

    [Fact]
    public void The_name_with_other_words_around_it_is_a_resemblance_but_a_short_name_is_too_common_to_say_so()
    {
        Assert.Contains("contains the name", Find("maybank-support.com")!.Reason);
        Assert.Null(Find("absnet.org"));   // "bsn" alone (3 letters) is too short to mean anything
    }

    [Fact]
    public void A_stranger_using_an_organisations_name_is_a_resemblance() =>
        Assert.Contains("mentions Maybank", Find("gmail.com", "Maybank Branch Manager")!.Reason);

    [Theory]
    [InlineData("gmail.com", "Ravi")]
    [InlineData("example.org", null)]
    [InlineData("bsn.example", null)]   // the registered domain itself is an exact match, not a resemblance
    public void Unrelated_domains_and_the_exact_domain_are_not_resemblances(string domain, string? name) =>
        Assert.Null(Find(domain, name));
}
