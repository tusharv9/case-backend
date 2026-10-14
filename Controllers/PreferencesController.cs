namespace CaseManagement.Api.Controllers;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public class PreferenceValueDto
{
    public string? Value { get; set; }
}

/// <summary>
/// The signed-in user's own UI preferences. Open to every signed-in user, and always scoped to the caller: there is no way to read or
/// write someone else's. Keys are free-form identifiers (so a new preference needs no change here); size and count are capped.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PreferencesController : BaseApiController
{
    private const int MaxValueLength = 4000, MaxKeysPerUser = 100;
    private static readonly Regex KeyPattern = new(@"^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly AppDbContext _db;
    public PreferencesController(AppDbContext db) => _db = db;

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        if (!KeyPattern.IsMatch(key)) return BadRequest(new { error = "Invalid preference key." });
        var value = await _db.UserPreferences.AsNoTracking().Where(p => p.UserId == CurrentUserId && p.Key == key).Select(p => p.Value).FirstOrDefaultAsync(ct);
        return Ok(new { key, value });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] PreferenceValueDto body, CancellationToken ct)
    {
        if (!KeyPattern.IsMatch(key)) return BadRequest(new { error = "Invalid preference key." });
        var value = body?.Value ?? string.Empty;
        if (value.Length > MaxValueLength) return BadRequest(new { error = $"A preference value can be at most {MaxValueLength} characters." });

        var userId = CurrentUserId;
        var row = await _db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);
        if (row == null)
        {
            if (await _db.UserPreferences.CountAsync(p => p.UserId == userId, ct) >= MaxKeysPerUser)
                return BadRequest(new { error = "Too many saved preferences." });
            _db.UserPreferences.Add(new UserPreference { Id = Guid.NewGuid(), UserId = userId, Key = key, Value = value, CreatedAt = DateTime.UtcNow });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
        }

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)   // two tabs saving the same new key at once: the other one won; ours is a plain update now
        {
            _db.ChangeTracker.Clear();
            await _db.UserPreferences.Where(p => p.UserId == userId && p.Key == key).ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, value).SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);
        }
        return Ok(new { key, value });
    }
}
