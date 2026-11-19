using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);
var storage = builder.AddGarage("storage").WithDataVolume("garage-integration-sample-data");
var photos = storage.AddBucket("photos", "sample-photos");
storage.AddBucket("documents", "sample-documents");
builder.AddDotnetProject("probe", "../Probe/Probe.csproj")
    .WithReference(photos)
    .WaitFor(storage);
builder.Build().Run();
