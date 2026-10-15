namespace CaseManagement.Api.DTOs;

/// <summary>One way to look up an existing Customer 360 customer, as Customer 360 itself offers it.</summary>
public class Customer360SearchOptionDto
{
    /// <summary>Stable key sent back with a search: NRIC, SecondaryID:PASSPORT, BRN, OLDBRN…</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>Customer 360's own label for it.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Individual or NonIndividual.</summary>
    public string CustomerType { get; set; } = string.Empty;
}

/// <summary>
/// An existing customer as read from Customer 360 for display — never stored. Property names are the ones the
/// masking service binds (FullName, IdValue, PhoneNumber, Email, DateOfBirth, Branch, CustomerSegment,
/// PreferredLanguage), so Customer 360's field settings in Configurable Settings mask them like any customer.
/// </summary>
public class Customer360CustomerDto
{
    public string CustomerSource { get; set; } = "Customer360";
    /// <summary>Individual or NonIndividual.</summary>
    public string CustomerType { get; set; } = string.Empty;
    /// <summary>
    /// Customer 360's own key for the customer (phprId / customerId). Sent back when the customer is chosen, so
    /// the server can tell several matches of one search apart. Not shown to people.
    /// </summary>
    public string ExternalCustomerId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Salutation { get; set; }
    /// <summary>Label of the identifier shown (e.g. "NRIC", "BRN").</summary>
    public string IdType { get; set; } = string.Empty;
    public string IdValue { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    /// <summary>Individuals: date of birth. Withheld when masked.</summary>
    public DateTime? DateOfBirth { get; set; }
    /// <summary>Non-individuals: date of business registration.</summary>
    public DateTime? RegistrationDate { get; set; }
    public string? Branch { get; set; }
    public string? CustomerSegment { get; set; }
    /// <summary>Customer 360's preferred language, as it spells it; the form uses it only when it is one of its options.</summary>
    public string? PreferredLanguage { get; set; }
    /// <summary>Customer 360's preferred communication channel, used the same way.</summary>
    public string? PreferredCommunicationChannel { get; set; }
    /// <summary>False when the contact details could not be read (not permitted, or Customer 360 had none).</summary>
    public bool ContactDetailsAvailable { get; set; }
}

/// <summary>The result of an existing-customer search.</summary>
public class Customer360SearchResultDto
{
    public List<Customer360CustomerDto> Matches { get; set; } = new();
    /// <summary>True when Customer 360 returned more matches than are listed.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// The existing customer chosen on the Create Case form. The server repeats the search with the caller's own
/// Customer 360 access and keeps the match whose <see cref="ExternalCustomerId"/> this names — nothing the
/// browser sends about the customer is taken on trust.
/// </summary>
public class Customer360SelectionDto
{
    public string CustomerType { get; set; } = string.Empty;
    public string SearchBy { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string ExternalCustomerId { get; set; } = string.Empty;
}

/// <summary>A case's Customer 360 customer, read live when the case is shown.</summary>
public class CaseCustomer360Dto
{
    /// <summary>Available, NotFound (no longer in Customer 360), Unavailable (could not be read) or NotPermitted.</summary>
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string CustomerType { get; set; } = string.Empty;
    public Customer360CustomerDto? Customer { get; set; }
}

/// <summary>
/// Whose cases to list: a local customer (<see cref="CustomerId"/>) or a Customer 360 customer (type + Customer 360's
/// key). Sent in a body, so nothing that identifies the customer sits in a URL.
/// </summary>
public class CustomerCasesQueryDto
{
    public Guid? CustomerId { get; set; }
    public string? CustomerType { get; set; }
    public string? ExternalCustomerId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}
