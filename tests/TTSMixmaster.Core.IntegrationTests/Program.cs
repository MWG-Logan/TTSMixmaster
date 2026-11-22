using Microsoft.Testing.Platform.Builder;
using TTSMixmaster.Tests;

namespace TTSMixmaster.Core.IntegrationTests;

/// <summary>
/// Test-host entry point (what MSTest.Sdk would generate) with two guards: no WER crash dialog for the host or
/// the real tools it starts, and host-infrastructure failures end in a non-zero exit instead of a crash.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        NoCrashDialogs.Apply();
        try
        {
            var builder = await TestApplication.CreateBuilderAsync(args);
            builder.AddSelfRegisteredExtensions(args);
            using var app = await builder.BuildAsync();
            return await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"The test host couldn't start or finish: {ex}");
            return 4;
        }
    }
}
