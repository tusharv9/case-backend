namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(Permissions.CustomersRead, Permissions.CustomersWrite)]
public class CustomersController : BaseApiController
{
    private readonly ICustomerService _customerService;
    private readonly IPiiMaskingService _piiMasking;

    public CustomersController(ICustomerService customerService, IPiiMaskingService piiMasking)
    {
        _customerService = customerService;
        _piiMasking = piiMasking;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllCustomers(
        [FromQuery] string? search,
        [FromQuery] string? preferredLanguage,
        [FromQuery] string? branch,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = null,
        [FromQuery] string sortDir = "asc",
        CancellationToken ct = default)
    {
        // Sorting is limited to a fixed list of columns: anything else is an error rather than a silent fallback.
        if (!string.IsNullOrWhiteSpace(sortBy) && !Repositories.CustomerRepository.SortableColumns.Contains(sortBy, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Cannot sort by '{sortBy}'. Sortable columns: {string.Join(", ", Repositories.CustomerRepository.SortableColumns)}." });

        // Always paginated (and masked per row by the service). The former "return every customer"
        // branch handed out raw entities for large page sizes, so it is gone; page size is capped.
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var paged = await _customerService.GetPaginatedCustomersAsync(search, preferredLanguage, branch, page, pageSize, sortBy, string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase), ct);
        return Ok(paged);
    }

    [HttpGet("{id:guid}/360")]
    public async Task<IActionResult> GetCustomer360(Guid id, CancellationToken ct)
    {
        var customer = await _customerService.GetCustomer360Async(id, ct);
        if (customer == null) return NotFound();

        // Apply PII masking based on FieldConfiguration metadata
        await _piiMasking.MaskCustomerDetailAsync(customer, ct);

        return Ok(customer);
    }

    [HttpGet("{id:guid}/cases")]
    public async Task<IActionResult> GetCustomerCases(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var result = await _customerService.GetCustomerCasesAsync(id, Math.Max(1, page), Math.Clamp(pageSize, 1, 100), ct);
        return Ok(result);
    }

    /// <summary>Everything that happened across the customer's cases (milestones and customer-visible messages), newest first.</summary>
    [HttpGet("{id:guid}/timeline")]
    public async Task<IActionResult> GetCustomerTimeline(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(await _customerService.GetCustomerTimelineAsync(id, page, pageSize, ct));

    [HttpPost]
    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerDto dto)
    {
        try
        {
            var newCustomer = await _customerService.CreateCustomerAsync(dto, CurrentUserId);

            // Respond with the same masked view everyone gets, never the stored entity.
            var detail = await _customerService.GetCustomer360Async(newCustomer.Id);
            if (detail != null) await _piiMasking.MaskCustomerDetailAsync(detail);
            return CreatedAtAction(nameof(GetCustomer360), new { id = newCustomer.Id }, detail);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("search")]
    [RequirePermission(Permissions.CustomersRead)] // a read, even though it is a POST
    public async Task<IActionResult> SearchCustomer([FromBody] CustomerSearchDto dto, CancellationToken ct)
    {
        var customer = await _customerService.SearchCustomerAsync(dto, ct);
        if (customer == null)
        {
            return NotFound(new { message = "Customer not found. Please verify the entered information or use Create Customer." });
        }

        // Apply PII masking before returning
        await _piiMasking.MaskCustomerDetailAsync(customer, ct);

        return Ok(customer);
    }
}
