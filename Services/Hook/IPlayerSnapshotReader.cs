using WreckfestController.Models;

namespace WreckfestController.Services.Hook;

public interface IPlayerSnapshotReader
{
    Task<(bool Success, string Message, IReadOnlyList<Player> Players)> ReadPlayerSnapshotAsync(int processId);
}
