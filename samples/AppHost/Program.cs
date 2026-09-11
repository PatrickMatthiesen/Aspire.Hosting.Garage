using Aspire.Hosting.Garage;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);
var storage = builder.AddGarage("storage").WithDataVolume("garage-integration-sample-data");
var photos = storage.AddBucket("photos", "sample-photos");
storage.AddBucket("documents", "sample-documents");
builder.AddProject<Projects.Probe>("probe")
    .WithReference(photos)
    .WaitForCompletion(storage.Resource.Provisioner);
builder.Build().Run();
