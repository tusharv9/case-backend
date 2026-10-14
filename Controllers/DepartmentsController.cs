namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Data;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(null, Permissions.ConfigManage)]
public class DepartmentsController : BaseApiController
{
    private readonly IDepartmentService _departmentService;

    public DepartmentsController(IDepartmentService departmentService)
    {
        _departmentService = departmentService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDepartments(CancellationToken ct)
    {
        var departments = await _departmentService.GetDepartmentsAsync(ct);
        return Ok(departments);
    }

    [HttpPost]
    public async Task<IActionResult> CreateDepartment([FromBody] CaseManagement.Api.DTOs.CreateDepartmentDto dto)
    {
        var department = await _departmentService.CreateDepartmentAsync(dto, CurrentUserId);
        return CreatedAtAction(nameof(GetDepartments), new { id = department.Id }, department);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDepartment(Guid id, [FromBody] CaseManagement.Api.DTOs.UpdateDepartmentDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Code))
        {
            return BadRequest(new { error = "Department name and code are required." });
        }

        var department = await _departmentService.UpdateDepartmentAsync(id, dto, CurrentUserId);
        return Ok(department);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDepartment(Guid id)
    {
        await _departmentService.DeleteDepartmentAsync(id, CurrentUserId);
        return Ok(new { message = "Department deleted successfully." });
    }

    [HttpPut("{id:guid}/owner")]
    public async Task<IActionResult> SetDepartmentOwner(Guid id, [FromBody] CaseManagement.Api.DTOs.SetDepartmentOwnerDto dto)
    {
        try
        {
            await _departmentService.SetDepartmentOwnerAsync(id, dto, CurrentUserId);
            return Ok(new { message = "Department owner updated successfully." });
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
