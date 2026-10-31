namespace Data.Tests;

public class SkeletonTests
{
    [Fact]
    public void Contract_version_is_set() => Assert.True(CL.Data.DataPackage.SpiVersion >= 1);
}
