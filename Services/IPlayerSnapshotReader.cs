using WreckfestController.Models;

namespace WreckfestController.Services;

public interface IPlayerSnapshotReader
{
    Task<(bool Success, string Message, IReadOnlyList<Player> Players)> ReadPlayerSnapshotAsync(int processId);
}
