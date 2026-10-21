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

    /// <summary>
    /// One customer created through New Customer, masked. Case Management keeps no customer directory and no
    /// customer profile page: existing customers are looked up in Customer 360, and only the customers deliberately
    /// created here (kept locally for now) are read from this table.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCustomer(Guid id, CancellationToken ct)
    {
        var customer = await _customerService.GetCustomerDetailAsync(id, ct);
        if (customer == null) return NotFound();

        // Apply PII masking based on FieldConfiguration metadata
        await _piiMasking.MaskCustomerDetailAsync(customer, ct);

        return Ok(customer);
    }

    [HttpPost]
    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerDto dto)
    {
        try
        {
            var newCustomer = await _customerService.CreateCustomerAsync(dto, CurrentUserId);

            // Respond with the same masked view everyone gets, never the stored entity.
            var detail = await _customerService.GetCustomerDetailAsync(newCustomer.Id);
            if (detail != null) await _piiMasking.MaskCustomerDetailAsync(detail);
            return CreatedAtAction(nameof(GetCustomer), new { id = newCustomer.Id }, detail);
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
