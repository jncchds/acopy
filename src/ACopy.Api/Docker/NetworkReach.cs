namespace ACopy.Api.Docker;

public enum ReachLevel
{
    /// <summary>Both containers share a user-defined network: the container name resolves and stays stable.</summary>
    Good,
    /// <summary>Reachable, but through something that can change or be blocked (an IP, a published host port).</summary>
    Fragile,
    /// <summary>No route ACopy can use as things are.</summary>
    Unreachable,
    /// <summary>ACopy is not in a container on this Docker host, so the networks cannot be compared.</summary>
    Unknown,
}

/// <summary>A host/port for network mode and how ACopy would get there.</summary>
public record ReachSuggestion(string? Host, int? Port, ReachLevel Level, string Message, string? Fix = null);

/// <summary>
/// Works out how ACopy's own container can reach a database container over the network, from the two inspect
/// results only (pure, so it is unit tested). The UI then confirms it with a TCP probe.
/// </summary>
public static class NetworkReach
{
    public static ReachSuggestion Suggest(ContainerDetails target, ContainerDetails? self, int? defaultPort)
    {
        var port = ContainerPort(target, defaultPort);

        if (target.NetworkMode.StartsWith("container:"))
            return new(null, port, ReachLevel.Unreachable,
                $"'{target.Name}' uses another container's network ({target.NetworkMode}) — point ACopy at that container instead.");

        if (target.NetworkMode == "host")
        {
            if (self == null) return new("host.docker.internal", port, ReachLevel.Unknown,
                $"'{target.Name}' uses the host's network; ACopy reaches it on the host's address.");
            if (self.NetworkMode == "host") return new("127.0.0.1", port, ReachLevel.Good,
                "Both ACopy and this container use the host's network.");
            var gw = self.Networks.Select(n => n.Gateway).FirstOrDefault(g => g != null);
            return new(gw, port, ReachLevel.Fragile,
                $"'{target.Name}' uses the host's network; ACopy reaches it through the Docker gateway {gw}. " +
                "The database must listen on that interface and the host firewall must allow it.");
        }

        if (self == null)
            return new(target.Name, port, ReachLevel.Unknown,
                "ACopy is not running in a container on this Docker host, so it cannot compare networks. The container name " +
                "works when ACopy shares a Docker network with it; otherwise use the host's address and a published port.");

        var published = target.PublishedPorts.Where(p => p.ContainerPort == port).ToList();
        var bindAll = published.FirstOrDefault(p => !IsLoopback(p.HostIp));
        var firstNet = target.Networks.FirstOrDefault(n => n.Name is not ("bridge" or "none"))?.Name;
        var fix = firstNet == null ? null : JoinFix(firstNet);

        if (self.NetworkMode == "host")
        {
            if (published.Count > 0)
                return new("127.0.0.1", published[0].HostPort, ReachLevel.Good,
                    $"ACopy uses the host's network; '{target.Name}' publishes port {port} on the host as {published[0].HostPort}.");
            var ip = target.Networks.Select(n => n.IpAddress).FirstOrDefault(i => i != null);
            return ip == null
                ? new(null, port, ReachLevel.Unreachable, $"'{target.Name}' has no IP address and no published port.")
                : new(ip, port, ReachLevel.Fragile,
                    $"ACopy uses the host's network and reaches '{target.Name}' by its container IP, which can change when it is recreated. " +
                    $"Publishing port {port} (and using 127.0.0.1) is stable.");
        }

        var shared = target.Networks.Where(t => t.Name != "none" && self.Networks.Any(s => s.Name == t.Name)).ToList();
        if (shared.FirstOrDefault(n => n.Name != "bridge") is { } net)
        {
            // On user-defined networks Docker DNS resolves the container name and its aliases.
            return new(target.Name, port, ReachLevel.Good, $"Both containers are on the '{net.Name}' network — ACopy reaches it by name.");
        }
        if (shared.FirstOrDefault() is { IpAddress: { } bridgeIp })
            return new(bridgeIp, port, ReachLevel.Fragile,
                "Both containers are only on Docker's default bridge, which has no name resolution, so ACopy has to use the " +
                "container IP — it can change when the container is recreated. Put both on a user-defined network to use the name.",
                fix);

        if (bindAll != null)
        {
            var gw = self.Networks.Select(n => n.Gateway).FirstOrDefault(g => g != null);
            return new(gw, bindAll.HostPort, ReachLevel.Fragile,
                $"No shared network; ACopy goes out to the host (gateway {gw}) and back in through published port {bindAll.HostPort}. " +
                "A host firewall can block this; sharing a network is more reliable.", fix);
        }

        var loopbackOnly = published.Count > 0 ? $" Port {port} is published only on {published[0].HostIp}, which other containers cannot reach." : "";
        return new(target.Name, port, ReachLevel.Unreachable,
            $"ACopy and '{target.Name}' share no Docker network and the database port is not published.{loopbackOnly} " +
            (fix != null ? $"Add ACopy to the '{firstNet}' network, or use docker exec mode." : "Use docker exec mode, or connect both to one network."),
            fix);
    }

    /// <summary>The port the database listens on inside its container: the engine default if declared (or nothing is), else the only EXPOSE.</summary>
    public static int? ContainerPort(ContainerDetails c, int? defaultPort)
    {
        if (c.ExposedPorts.Count == 0 || (defaultPort != null && c.ExposedPorts.Contains(defaultPort.Value))) return defaultPort;
        return c.ExposedPorts.Count == 1 ? c.ExposedPorts[0] : defaultPort;
    }

    private static bool IsLoopback(string? hostIp) => hostIp is "127.0.0.1" or "::1" || (hostIp?.StartsWith("127.") ?? false);

    private static string JoinFix(string network) => $$"""
        # docker-compose.yml of ACopy
        services:
          acopy:
            networks: [default, {{network}}]
        networks:
          {{network}}:
            external: true
        """;
}
