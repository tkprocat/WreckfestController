using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;
using WreckfestController.Services.Auth;

namespace WreckfestController.Controllers;

/// <summary>Workshop mods that tracks can come from.</summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/mods")]
public class CatalogueModsController : ControllerBase
{
    private readonly ControllerDbContext _db;
    private readonly ILogger<CatalogueModsController> _logger;

    public CatalogueModsController(ControllerDbContext db, ILogger<CatalogueModsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IEnumerable<ModResponse>> List()
    {
        var mods = await _db.Mods.AsNoTracking().OrderBy(m => m.Name).ToListAsync();
        return mods.Select(ModResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ModResponse>> Create(ModRequest request)
    {
        var folderName = request.FolderName.Trim();
        if (await _db.Mods.AnyAsync(m => m.FolderName == folderName))
        {
            return DuplicateFolder(folderName);
        }

        var mod = new Mod
        {
            Name = request.Name.Trim(),
            FolderName = folderName,
            WorkshopId = request.WorkshopId,
        };
        _db.Mods.Add(mod);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (CatalogueHttp.IsUniqueViolation(ex))
        {
            return DuplicateFolder(folderName);
        }

        _logger.LogInformation("{Caller} added mod {FolderName}", this.Caller(), mod.FolderName);
        return StatusCode(StatusCodes.Status201Created, ModResponse.From(mod));
    }

    /// <summary>Deletes a mod that no track comes from.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var mod = await _db.Mods.Include(m => m.Tracks).SingleOrDefaultAsync(m => m.Id == id);
        if (mod is null)
        {
            return NotFound();
        }

        if (mod.Tracks.Count > 0)
        {
            var names = string.Join(", ", mod.Tracks.Select(t => t.Name).Order());
            return this.Refused($"Tracks still come from this mod: {names}. Delete them or move them to another mod first.");
        }

        _db.Mods.Remove(mod);
        await _db.SaveChangesAsync();
        _logger.LogInformation("{Caller} deleted mod {FolderName}", this.Caller(), mod.FolderName);
        return NoContent();
    }

    private ActionResult DuplicateFolder(string folderName) =>
        this.Invalid("folderName", $"A mod with folder '{folderName}' already exists.");
}

/// <summary>The weather names the game knows. Fixed: shipped by the catalogue migration.</summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/catalogue/weather")]
public class CatalogueWeatherController : ControllerBase
{
    private readonly ControllerDbContext _db;

    public CatalogueWeatherController(ControllerDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IEnumerable<string>> List() =>
        await _db.WeatherConditions.OrderBy(w => w.Id).Select(w => w.Name).ToListAsync();
}
