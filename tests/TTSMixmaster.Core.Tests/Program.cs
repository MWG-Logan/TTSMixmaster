using Microsoft.Testing.Platform.Builder;
using TTSMixmaster.Core.Tests.Files;
using TTSMixmaster.Tests;

namespace TTSMixmaster.Core.Tests;

/// <summary>
/// Test-host entry point. Normally it runs Microsoft.Testing.Platform (what MSTest.Sdk would generate);
/// the cross-process tests start this same assembly as a child process instead:
/// <c>dotnet TTSMixmaster.Core.Tests.dll state-writer &lt;dataRoot&gt; &lt;folder&gt; &lt;key&gt;</c>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // First, for the test host and every child it starts: a crash ends with an exit code, never a WER dialog.
        NoCrashDialogs.Apply();
        if (args is [StateWriter.Command, var dataRoot, var folder, var key])
        {
            // A child must never die with an unhandled exception: report it on stderr and exit non-zero.
            try
            {
                return StateWriter.Run(dataRoot, folder, key);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{StateWriter.Command} {key} failed: {ex}");
                return 3;
            }
        }

        var builder = await TestApplication.CreateBuilderAsync(args);
        builder.AddSelfRegisteredExtensions(args);
        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}
