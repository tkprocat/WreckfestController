using Microsoft.EntityFrameworkCore;
using WreckfestController.Data.Catalogue;

namespace WreckfestController.Data.Collections;

/// <summary>How the catalogue and collections refer to each other.</summary>
public static class CollectionLinks
{
    /// <summary>The names of the collections that use any of <paramref name="variantIds"/>, by name.</summary>
    public static Task<List<string>> CollectionsUsingAsync(this ControllerDbContext db, IQueryable<int> variantIds) =>
        db.TrackCollections
            .Where(c => c.Entries.Any(e => e.TrackVariantId != null && variantIds.Contains(e.TrackVariantId.Value)))
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();

    /// <summary>
    /// Links the collection entries that name <paramref name="variant"/>'s id but are not
    /// linked yet: workshop tracks saved before the catalogue knew them. Saved with the
    /// caller's next SaveChanges.
    /// </summary>
    public static async Task LinkEntriesAsync(this ControllerDbContext db, TrackVariant variant)
    {
        var entries = await db.TrackCollectionEntries
            .Where(e => e.TrackVariantId == null && e.TrackId == variant.VariantId)
            .ToListAsync();

        foreach (var entry in entries)
        {
            entry.TrackVariant = variant;
        }
    }
}
