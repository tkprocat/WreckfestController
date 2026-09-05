using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WreckfestController.Models;
using WreckfestController.Services;
using Xunit;

namespace WreckfestController.Tests.Services;

public class EventStorageServiceTests
{
    // The old code deleted the schedule before moving the replacement in, so an
    // interruption between the two left no schedule at all and the next load
    // returned an empty one.
    [Fact]
    public void SaveSchedule_OverExistingFile_ReplacesItAndLeavesNoTempBehind()
    {
        var schedulePath = Path.Combine(Path.GetTempPath(), $"wreckfest-schedule-{Guid.NewGuid():N}.json");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["EventSchedulePath"] = schedulePath })
                .Build();
            var service = new EventStorageService(configuration, Mock.Of<ILogger<EventStorageService>>());

            Assert.True(service.SaveSchedule(new EventSchedule
            {
                Events = [new Event { Id = 1, Name = "Before", StartTime = DateTime.UtcNow }]
            }));
            Assert.True(service.SaveSchedule(new EventSchedule
            {
                Events = [new Event { Id = 2, Name = "After", StartTime = DateTime.UtcNow }]
            }));

            var saved = JsonSerializer.Deserialize<EventSchedule>(File.ReadAllText(schedulePath));
            Assert.Equal("After", Assert.Single(saved!.Events).Name);
            Assert.False(File.Exists(schedulePath + ".tmp"));
        }
        finally
        {
            DeleteIfExists(schedulePath);
            DeleteIfExists(schedulePath + ".tmp");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
