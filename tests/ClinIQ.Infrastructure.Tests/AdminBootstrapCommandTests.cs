using System.Diagnostics;
using Xunit;

namespace ClinIQ.Infrastructure.Tests;

public sealed class AdminBootstrapCommandTests
{
    [Theory]
    [InlineData("Production", "environment")]
    [InlineData("Development", "redirected")]
    [InlineData("Development", "conflicting")]
    [InlineData("Development", "credentials")]
    public async Task OperatorCommandRejectsUnsafeInvocationWithoutStartingHttp(string environment, string scenario)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClinIQ.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var apiRoot = Path.Combine(root, "src", "ClinIQ.API");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(apiRoot, "bin", configuration, "net10.0", "ClinIQ.API.dll"));
        start.ArgumentList.Add("--bootstrap-admin");
        if (scenario == "conflicting") start.ArgumentList.Add("--seed-specialties");
        if (scenario == "credentials")
        {
            start.ArgumentList.Add("--password"); start.ArgumentList.Add("Synthetic-NeverEcho1!");
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ConnectionStrings__DefaultConnection"] =
            "Server=bootstrap-command-must-not-connect.invalid;Database=master;Integrated Security=True;Connect Timeout=1";
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout + await stderr;
            Assert.Equal(1, process.ExitCode);
            Assert.DoesNotContain("Now listening on", output);
            Assert.DoesNotContain("Synthetic-NeverEcho1!", output);
            Assert.Contains(scenario switch
            {
                "environment" => "only in Development",
                "redirected" => "interactive terminal",
                "conflicting" => "one maintenance command",
                _ => "accepts only --bootstrap-admin"
            }, output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
