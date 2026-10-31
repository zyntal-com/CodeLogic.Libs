namespace CL.Data.Routing;

/// <summary>Where reads go when a connection has replicas. Writes, transactions and locking reads always use the primary.</summary>
public enum ReadPreference
{
    /// <summary>All reads go to the primary.</summary>
    Primary,

    /// <summary>Reads go to a healthy replica within the lag limit, falling back to the primary.</summary>
    PreferReplica,

    /// <summary>Reads go to a healthy replica; fail with <c>data.no_healthy_node</c> when none is available.</summary>
    ReplicaOnly,
}
