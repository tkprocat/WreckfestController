using Microsoft.Extensions.Configuration;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Reads Vote:AllowedTracks as a whole list from a single configuration source.
/// </summary>
/// <remarks>
/// IConfiguration flattens arrays to Vote:AllowedTracks:0:Id, :1:Id, ... so a later
/// source overrides only the indices it has, and every index beyond its length still
/// comes from an earlier one. A curated 115-track list in user-settings.json over the
/// shipped 284 in appsettings.json made all 284 votable. The list therefore comes
/// from the highest-priority source that defines it, and replaces the others outright.
/// </remarks>
public static class AllowedTrackConfiguration
{
    public const string SectionPath = "Vote:AllowedTracks";

    public static List<AllowedVoteTrack> Read(IConfiguration configuration)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                CollectValues(provider, SectionPath, values);
                if (values.Count > 0)
                {
                    return Bind(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
                }
            }

            return new List<AllowedVoteTrack>();
        }

        return Bind(configuration);
    }

    private static List<AllowedVoteTrack> Bind(IConfiguration configuration) =>
        configuration.GetSection(SectionPath).Get<List<AllowedVoteTrack>>() ?? new List<AllowedVoteTrack>();

    private static void CollectValues(IConfigurationProvider provider, string path, Dictionary<string, string?> values)
    {
        foreach (var child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var childPath = ConfigurationPath.Combine(path, child);
            if (provider.TryGet(childPath, out var value) && value is not null)
            {
                values[childPath] = value;
            }

            CollectValues(provider, childPath, values);
        }
    }
}
