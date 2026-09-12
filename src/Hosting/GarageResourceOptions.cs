namespace Garage.Hosting;

/// <summary>Single-node defaults. Supply explicit configuration for other topologies.</summary>
public sealed class GarageResourceOptions
{
    public string Region { get; init; } = "garage";
    public long CapacityBytes { get; init; } = 1_000_000_000;
    public string? ConfigPath { get; init; }
    public string? ConfigContents { get; init; }
    public bool ProvisionSingleNodeLayout { get; init; } = true;
    public string ProvisionerImage { get; init; } = "ghcr.io/patrickmatthiesen/garage-provisioner";
    public string ProvisionerTag { get; init; } = "0.1.0-preview.6";
}
