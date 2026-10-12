using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WreckfestController.Data;
using WreckfestController.Data.Catalogue;

namespace WreckfestController.Services.Catalogue;

/// <summary>One stored image without its bytes: what lists and lookups read.</summary>
public sealed record ImageSlot(int Id, int? TrackId, int? TrackVariantId, ImageSize Size, ImageOrigin Origin, string Hash, int Length, bool IsOverlay);

/// <summary>An image to send: its bytes, type and hash.</summary>
public sealed record StoredImage(byte[] Data, string ContentType, string Hash);

/// <summary>
/// Reads, writes and picks the catalogue's menu images (#260). Which image a track or
/// variant shows is decided here, the same way for the admin pages and the public ones:
/// <list type="number">
/// <item>The asked size before the other: a missing small image falls back to the large
/// one, which the browser scales down. A large one never falls back to a small one.</item>
/// <item>Within a size, the variant's own image, then its track's.</item>
/// <item>Within a row, an admin's upload before an import.</item>
/// </list>
/// A track with no image of its own shows its first variant's, by name, so a mod that
/// ships only variant pictures still has one.
/// <para>
/// A layout outline (<see cref="CatalogueImage.IsOverlay"/>) is never a picture: the base
/// game's variant images are all outlines, drawn over the track's picture. A variant's
/// outline is its <see cref="ForVariantLayoutAsync">layout</see> instead.
/// </para>
/// </summary>
public static class CatalogueImageStore
{
    /// <summary>The largest image taken, imported or uploaded. A large menu image is about 0.4 MB as PNG.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";

    private static readonly ImageOrigin[] OriginOrder = [ImageOrigin.Upload, ImageOrigin.Import];

    /// <summary>
    /// The image type the bytes start with: PNG, JPEG or WebP, or null for anything else.
    /// The type served is this one, never what the upload claimed, so nothing is served as
    /// a type a browser would run (such as SVG or HTML).
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return null;
    }

    public static string Hash(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data))[..CatalogueImage.HashLength];

    /// <summary>
    /// Stores <paramref name="data"/> as the row's image of this size and origin, replacing
    /// one already there. Does not save. The caller has checked the bytes with <see cref="Sniff"/>.
    /// </summary>
    /// <returns>False when the stored image already had these bytes.</returns>
    public static async Task<bool> PutAsync(
        ControllerDbContext db,
        ImageOwner owner,
        ImageSize size,
        ImageOrigin origin,
        byte[] data,
        string contentType,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var hash = Hash(data);
        var existing = await Owned(db, owner)
            .Where(i => i.Size == size && i.Origin == origin)
            .Select(i => new { i.Id, i.Hash })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing?.Hash == hash)
        {
            return false;
        }

        if (existing is not null)
        {
            // Updated through a stub, so the old bytes are never read just to be overwritten.
            // Each column is marked: a value equal to the stub's default (IsOverlay false)
            // would otherwise not count as a change, and the old one would stay.
            var stored = new CatalogueImage
            {
                Id = existing.Id,
                ContentType = contentType,
                Hash = hash,
                Data = data,
                IsOverlay = contentType == Png && PngTransparency.IsOverlay(data),
                UpdatedAt = now,
            };
            var entry = db.CatalogueImages.Attach(stored);
            entry.Property(i => i.ContentType).IsModified = true;
            entry.Property(i => i.Hash).IsModified = true;
            entry.Property(i => i.Data).IsModified = true;
            entry.Property(i => i.IsOverlay).IsModified = true;
            entry.Property(i => i.UpdatedAt).IsModified = true;
        }
        else
        {
            db.CatalogueImages.Add(new CatalogueImage
            {
                TrackId = owner.TrackId,
                TrackVariantId = owner.TrackVariantId,
                Size = size,
                Origin = origin,
                ContentType = contentType,
                Hash = hash,
                Data = data,
                IsOverlay = contentType == Png && PngTransparency.IsOverlay(data),
                UpdatedAt = now,
            });
        }

        return true;
    }

    /// <summary>Removes the row's image of this size and origin, if any. Saves.</summary>
    /// <returns>False when there was none.</returns>
    public static async Task<bool> RemoveAsync(
        ControllerDbContext db,
        ImageOwner owner,
        ImageSize size,
        ImageOrigin origin,
        CancellationToken cancellationToken = default) =>
        await Owned(db, owner)
            .Where(i => i.Size == size && i.Origin == origin)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    /// <summary>Every stored image, without its bytes.</summary>
    public static Task<List<ImageSlot>> SlotsAsync(ControllerDbContext db, CancellationToken cancellationToken = default) =>
        Slots(db.CatalogueImages).ToListAsync(cancellationToken);

    /// <summary>A track's or variant's own images, without their bytes.</summary>
    public static Task<List<ImageSlot>> SlotsAsync(ControllerDbContext db, ImageOwner owner, CancellationToken cancellationToken = default) =>
        Slots(Owned(db, owner)).ToListAsync(cancellationToken);

    /// <summary>The image a track shows at this size, or null when it has none.</summary>
    public static async Task<StoredImage?> ForTrackAsync(
        ControllerDbContext db,
        string key,
        ImageSize size,
        CancellationToken cancellationToken = default)
    {
        var track = await db.Tracks.AsNoTracking()
            .Where(t => t.Key == key)
            .Select(t => new { t.Id, Variants = t.Variants.Where(v => !v.IsHidden).OrderBy(v => v.Name).Select(v => v.Id).ToList() })
            .SingleOrDefaultAsync(cancellationToken);
        if (track is null)
        {
            return null;
        }

        var slots = await Slots(db.CatalogueImages.Where(i => i.TrackId == track.Id || track.Variants.Contains(i.TrackVariantId!.Value)))
            .ToListAsync(cancellationToken);
        var chosen = Pick(slots, size, track.Id, variantId: null)
            ?? track.Variants.Select(v => Pick(slots, size, trackId: null, v)).FirstOrDefault(s => s is not null);
        return await LoadAsync(db, chosen, cancellationToken);
    }

    /// <summary>The image a variant shows at this size, its track's if it has none, or null.</summary>
    public static async Task<StoredImage?> ForVariantAsync(
        ControllerDbContext db,
        string variantId,
        ImageSize size,
        CancellationToken cancellationToken = default)
    {
        var variant = await db.TrackVariants.AsNoTracking()
            .Where(v => v.VariantId == variantId)
            .Select(v => new { v.Id, v.TrackId })
            .SingleOrDefaultAsync(cancellationToken);
        if (variant is null)
        {
            return null;
        }

        var slots = await Slots(db.CatalogueImages.Where(i => i.TrackId == variant.TrackId || i.TrackVariantId == variant.Id))
            .ToListAsync(cancellationToken);
        return await LoadAsync(db, Pick(slots, size, variant.TrackId, variant.Id), cancellationToken);
    }

    /// <summary>
    /// A variant's layout outline: its own outline image, an upload first, the small size
    /// first. Null when it has none; a track's outline is not used.
    /// </summary>
    public static async Task<StoredImage?> ForVariantLayoutAsync(
        ControllerDbContext db,
        string variantId,
        CancellationToken cancellationToken = default)
    {
        var slots = await Slots(db.CatalogueImages.Where(i => i.IsOverlay && db.TrackVariants.Any(v => v.Id == i.TrackVariantId && v.VariantId == variantId)))
            .ToListAsync(cancellationToken);
        var chosen = slots
            .OrderBy(s => s.Origin == ImageOrigin.Upload ? 0 : 1)
            .ThenBy(s => s.Size == ImageSize.Small ? 0 : 1)
            .FirstOrDefault();
        return await LoadAsync(db, chosen, cancellationToken);
    }

    /// <summary>
    /// The image that shows for a row, by the rules in the class summary, from slots that
    /// include the row's own and its track's. For a track pass only <paramref name="trackId"/>,
    /// for a variant both.
    /// </summary>
    public static ImageSlot? Pick(IEnumerable<ImageSlot> slots, ImageSize size, int? trackId, int? variantId)
    {
        var list = slots as IReadOnlyCollection<ImageSlot> ?? slots.ToList();
        ImageSize[] sizes = size == ImageSize.Small ? [ImageSize.Small, ImageSize.Large] : [ImageSize.Large];
        foreach (var candidateSize in sizes)
        {
            foreach (var owner in new[] { (Track: (int?)null, Variant: variantId), (Track: trackId, Variant: (int?)null) })
            {
                if (owner.Track is null && owner.Variant is null)
                {
                    continue;
                }

                foreach (var origin in OriginOrder)
                {
                    var match = list.FirstOrDefault(s =>
                        !s.IsOverlay
                        && s.Size == candidateSize
                        && s.Origin == origin
                        && (owner.Variant is { } v ? s.TrackVariantId == v : s.TrackId == owner.Track));
                    if (match is not null)
                    {
                        return match;
                    }
                }
            }
        }

        return null;
    }

    private static async Task<StoredImage?> LoadAsync(ControllerDbContext db, ImageSlot? slot, CancellationToken cancellationToken) =>
        slot is null
            ? null
            : await db.CatalogueImages.AsNoTracking()
                .Where(i => i.Id == slot.Id)
                .Select(i => new StoredImage(i.Data, i.ContentType, i.Hash))
                .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<CatalogueImage> Owned(ControllerDbContext db, ImageOwner owner) =>
        owner.TrackVariantId is { } variantId
            ? db.CatalogueImages.Where(i => i.TrackVariantId == variantId)
            : db.CatalogueImages.Where(i => i.TrackId == owner.TrackId);

    private static IQueryable<ImageSlot> Slots(IQueryable<CatalogueImage> images) =>
        images.AsNoTracking().Select(i => new ImageSlot(i.Id, i.TrackId, i.TrackVariantId, i.Size, i.Origin, i.Hash, i.Data.Length, i.IsOverlay));
}

/// <summary>The row an image belongs to: a track or a variant, never both.</summary>
public readonly record struct ImageOwner(int? TrackId, int? TrackVariantId)
{
    public static ImageOwner Track(int id) => new(id, null);

    public static ImageOwner Variant(int id) => new(null, id);
}
