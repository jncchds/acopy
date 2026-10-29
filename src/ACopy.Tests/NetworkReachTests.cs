using ACopy.Api.Docker;

namespace ACopy.Tests;

public class NetworkReachTests
{
    private static readonly Dictionary<string, string> None = [];

    private static ContainerDetails C(string name, string mode = "bridge", ContainerNetwork[]? nets = null,
        int[]? exposed = null, PublishedPort[]? published = null) => new($"id-{name}", name, "img", true, None, None)
    {
        NetworkMode = mode,
        Networks = nets ?? [],
        ExposedPorts = exposed ?? [],
        PublishedPorts = published ?? [],
    };

    private static ContainerNetwork Net(string name, string ip, string gw = "172.18.0.1") => new(name, ip, gw, []);

    [Fact]
    public void SharedUserNetwork_UsesContainerName()
    {
        var db = C("db", "app_default", [Net("app_default", "172.18.0.3")], [5432]);
        var me = C("acopy", "app_default", [Net("app_default", "172.18.0.2")]);
        var s = NetworkReach.Suggest(db, me, 5432);
        Assert.Equal(("db", 5432, ReachLevel.Good), (s.Host, s.Port, s.Level));
    }

    [Fact]
    public void DefaultBridgeOnly_UsesIpAndWarns()
    {
        var db = C("db", "bridge", [Net("bridge", "172.17.0.3")]);
        var me = C("acopy", "bridge", [Net("bridge", "172.17.0.2")]);
        var s = NetworkReach.Suggest(db, me, 3306);
        Assert.Equal(("172.17.0.3", 3306, ReachLevel.Fragile), (s.Host, s.Port, s.Level));
    }

    [Fact]
    public void NoSharedNetwork_PublishedPort_GoesThroughGateway()
    {
        var db = C("db", "other", [Net("other", "172.20.0.3")], [5432], [new(5432, "0.0.0.0", 15432)]);
        var me = C("acopy", "acopy_default", [Net("acopy_default", "172.19.0.2", "172.19.0.1")]);
        var s = NetworkReach.Suggest(db, me, 5432);
        Assert.Equal(("172.19.0.1", 15432, ReachLevel.Fragile), (s.Host, s.Port, s.Level));
        Assert.Contains("other", s.Fix);
    }

    [Fact]
    public void NoSharedNetwork_LoopbackPublish_IsUnreachable()
    {
        var db = C("db", "other", [Net("other", "172.20.0.3")], [5432], [new(5432, "127.0.0.1", 5432)]);
        var me = C("acopy", "acopy_default", [Net("acopy_default", "172.19.0.2")]);
        var s = NetworkReach.Suggest(db, me, 5432);
        Assert.Equal(ReachLevel.Unreachable, s.Level);
        Assert.Contains("127.0.0.1", s.Message);
        Assert.Contains("networks: [default, other]", s.Fix);
    }

    [Fact]
    public void AcopyOnHostNetwork_UsesPublishedPortOnLoopback()
    {
        var db = C("db", "app", [Net("app", "172.20.0.3")], [6379], [new(6379, "127.0.0.1", 16379)]);
        var me = C("acopy", "host");
        var s = NetworkReach.Suggest(db, me, 6379);
        Assert.Equal(("127.0.0.1", 16379, ReachLevel.Good), (s.Host, s.Port, s.Level));
    }

    [Fact]
    public void TargetOnHostNetwork_UsesGateway()
    {
        var db = C("db", "host");
        var me = C("acopy", "acopy_default", [Net("acopy_default", "172.19.0.2", "172.19.0.1")]);
        var s = NetworkReach.Suggest(db, me, 5432);
        Assert.Equal(("172.19.0.1", 5432, ReachLevel.Fragile), (s.Host, s.Port, s.Level));
    }

    [Fact]
    public void AcopyNotInContainer_FallsBackToName()
    {
        var s = NetworkReach.Suggest(C("db", "app", [Net("app", "172.20.0.3")]), null, 27017);
        Assert.Equal(("db", 27017, ReachLevel.Unknown), (s.Host, s.Port, s.Level));
    }

    [Theory]
    [InlineData(new int[0], 5432, 5432)]       // nothing declared → engine default
    [InlineData(new[] { 5432, 8008 }, 5432, 5432)]
    [InlineData(new[] { 5433 }, 5432, 5433)]   // custom image that only exposes another port
    [InlineData(new[] { 1, 2 }, 5432, 5432)]   // ambiguous → engine default
    public void ContainerPort_PrefersEngineDefault(int[] exposed, int def, int expected) =>
        Assert.Equal(expected, NetworkReach.ContainerPort(C("db", exposed: exposed), def));
}
