namespace WreckfestController.Services;

/// <summary>
/// Reads module-relative memory out of the running server through the injected
/// hook. Addresses are RVAs (module-relative), never absolute: the hook bounds
/// them against SizeOfImage so a bad offset cannot read arbitrary process memory.
/// </summary>
public interface IHookMemoryReader
{
    Task<(bool Success, string Message, byte[] Data)> ReadModuleMemoryAsync(int processId, uint rva, int size);
}
