namespace Aspire.Hosting.ApplicationModel;

/// <summary>A one-shot container that initializes Garage layout, keys, buckets and permissions.</summary>
public sealed class GarageProvisionerResource(string name, GarageResource parent, long capacityBytes)
    : ContainerResource(name)
{
    public GarageResource Parent { get; } = parent;
    public long CapacityBytes { get; } = capacityBytes;
}
