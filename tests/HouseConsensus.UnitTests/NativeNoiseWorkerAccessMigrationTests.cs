using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class NativeNoiseWorkerAccessMigrationTests
{
    private static readonly string Root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void Migration_grants_only_native_worker_noise_access_when_runtime_role_exists()
    {
        var path = Path.Combine(Root, "src/Server/Data/Migrations/202608230002_GrantNativeNoiseWorkerAccess.cs");

        Assert.True(File.Exists(path));
        var migration = File.ReadAllText(path);
        Assert.Contains("rolname = 'house_consensus'", migration, StringComparison.Ordinal);
        Assert.Contains("GRANT USAGE ON SCHEMA noise TO house_consensus", migration, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON noise.noise_source, noise.noise_areas TO house_consensus", migration, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON public.property_noise_samples TO house_consensus", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ALL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" TO PUBLIC", migration, StringComparison.OrdinalIgnoreCase);
    }
}
