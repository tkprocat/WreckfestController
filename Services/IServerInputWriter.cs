namespace WreckfestController.Services;

public interface IServerInputWriter
{
    Task<(bool Success, string Message)> SendCommandAsync(string command, int processId);
}
