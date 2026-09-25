using System.Text;
using Microsoft.Extensions.Configuration;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class AllowedTrackConfigurationTests
{
    private static Dictionary<string, string?> Tracks(params string[] ids)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < ids.Length; i++)
        {
            values[$"Vote:AllowedTracks:{i}:Id"] = ids[i];
            values[$"Vote:AllowedTracks:{i}:Name"] = $"Name of {ids[i]}";
        }

        return values;
    }

    [Fact]
    public void Read_ShorterLaterList_ReplacesEarlierListOutright()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Tracks("a", "b", "c", "d"))
            .AddInMemoryCollection(Tracks("x", "y"))
            .Build();

        // The merged view is what VotingService used to read: indices 2 and 3 leak
        // through from the earlier source.
        Assert.Equal("c", configuration["Vote:AllowedTracks:2:Id"]);

        var tracks = AllowedTrackConfiguration.Read(configuration);

        Assert.Equal(["x", "y"], tracks.Select(t => t.Id));
        Assert.Equal("Name of x", tracks[0].Name);
    }

    [Fact]
    public void Read_JsonSourcesOfDifferentLengths_UsesUserListOnly()
    {
        const string shipped = """{"Vote":{"AllowedTracks":[{"Id":"a","Name":"A"},{"Id":"b","Name":"B"},{"Id":"c","Name":"C"}]}}""";
        const string user = """{"Vote":{"Mode":"Voting","AllowedTracks":[{"Id":"b","Name":"B"}]}}""";
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(shipped)))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(user)))
            .Build();

        var track = Assert.Single(AllowedTrackConfiguration.Read(configuration));

        Assert.Equal("b", track.Id);
    }

    [Fact]
    public void Read_LaterSourceWithoutList_FallsBackToEarlierList()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Tracks("a", "b"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vote:Mode"] = "Voting" })
            .Build();

        Assert.Equal(["a", "b"], AllowedTrackConfiguration.Read(configuration).Select(t => t.Id));
    }

    [Fact]
    public void Read_NoSourceDefinesList_ReturnsEmpty()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vote:Mode"] = "Voting" })
            .Build();

        Assert.Empty(AllowedTrackConfiguration.Read(configuration));
    }
}
