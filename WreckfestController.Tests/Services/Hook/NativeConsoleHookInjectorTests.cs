using WreckfestController.Services.Hook;
using Xunit;

namespace WreckfestController.Tests.Services.Hook;

/// <summary>
/// An export is called at an address worked out from the DLL file, so it must only be
/// called in a loaded image of that same build. The PE identity is what tells them apart.
/// </summary>
public class NativeConsoleHookInjectorTests
{
    [Fact]
    public void ImageIdentity_ReadsTheHookDll()
    {
        var identity = NativeConsoleHookInjector.ImageIdentity(HookHeaders());

        Assert.NotNull(identity);
        Assert.NotEqual(0u, identity.Value.SizeOfImage);
    }

    [Fact]
    public void ImageIdentity_DiffersForAnotherBuild()
    {
        var headers = HookHeaders();
        var original = NativeConsoleHookInjector.ImageIdentity(headers);

        // Another link of the same DLL: its timestamp differs.
        var pe = BitConverter.ToInt32(headers, 0x3C);
        headers[pe + 8] ^= 0xFF;

        Assert.NotEqual(original, NativeConsoleHookInjector.ImageIdentity(headers));
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 })]
    public void ImageIdentity_IsNullForSomethingThatIsNotAnImage(byte[] start)
    {
        var headers = new byte[0x400];
        start.CopyTo(headers, 0);
        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(headers, 0x3C);

        Assert.Null(NativeConsoleHookInjector.ImageIdentity(headers));
    }

    private static byte[] HookHeaders()
    {
        using var file = File.OpenRead(FindHookDll());
        var headers = new byte[0x400];
        file.ReadExactly(headers);
        return headers;
    }

    private static string FindHookDll()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(directory.FullName, "NativeHooks", "WreckfestConsoleHook", "bin", configuration, "WreckfestConsoleHook.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("WreckfestConsoleHook.dll was not found above the test output; building the app builds it.");
    }
}
