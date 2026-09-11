# Garage hosting integration for Aspire

An independent extraction from Cantaro, structured for a future Aspire Community Toolkit contribution. This is **not an official Community Toolkit release**.

The `CommunityToolkit.Aspire.Hosting.Garage` package targets .NET 10 and stable Aspire 13.5.3. It starts Garage and a one-shot provisioner, declares any number of buckets, and supplies S3 connection strings. Both resources are containers, so the same model works locally and in Aspire-generated Docker Compose deployments.

```csharp
using CommunityToolkit.Aspire.Hosting.Garage;

var garage = builder.AddGarage("storage", new GarageResourceOptions
{
    Region = "garage",
    CapacityBytes = 10_000_000_000
}).WithDataVolume("my-storage-data");
var photos = garage.AddBucket("photos", "my-place-photos");
builder.AddProject<Projects.Api>("api")
    .WithReference(photos)
    .WaitForCompletion(garage.Resource.Provisioner);
```

`AddGarage` accepts optional secret parameter resources for RPC, admin, and S3 credentials. Defaults are generated and persisted by Aspire. Keep the same secrets and volume on subsequent deployments. Never regenerate credentials for an existing volume.

`ConfigContents` or `ConfigPath` replaces the built-in TOML; a relative path is resolved against the AppHost. Custom configuration must retain the configured S3/admin ports and data paths, or explicitly update the endpoints/mounts through standard Aspire APIs. `Region` must match custom TOML. `ProvisionSingleNodeLayout=false` leaves layout management to the operator. The default replication factor is one: this integration does not claim production cluster management or redundancy.

Use `ProvisionerImage` / `ProvisionerTag` to override the provisioner container. Standard Aspire `WithImage`, `WithImageTag`, endpoint and mount methods remain available. The Garage image is pinned to a digest; remove/update that digest deliberately when choosing another image version.

All declared buckets share the configured application key; admin credentials are passed only to the provisioner. Consumers should reference a bucket, not the Garage admin connection string. The provisioner is idempotent and refuses to rotate a pre-existing key with a different secret.

## Development

```sh
dotnet test
aspire start --apphost samples/AppHost/AppHost.csproj --non-interactive
aspire logs probe --apphost samples/AppHost/AppHost.csproj --non-interactive
dotnet pack src/CommunityToolkit.Aspire.Hosting.Garage -c Release -o artifacts/packages
docker build -t ghcr.io/patrickmatthiesen/garage-provisioner:0.1.0-preview.3 .
```

The hosting package and provisioner image must be released together. Package publication is a separate explicit operation; local packing does not publish to NuGet.org. Preserve the Apache-2.0 license and NOTICE when redistributing.

Build or pull the provisioner image before starting the sample. Its probe uploads, downloads and removes a 1.2 MB random object and verifies every byte. Stop it afterward with `aspire stop --apphost samples/AppHost/AppHost.csproj --non-interactive`.

