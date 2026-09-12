using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Garage;

internal sealed class GarageProvisioningHealthCheck(
    ResourceNotificationService notifications,
    GarageProvisionerResource provisioner) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!notifications.TryGetCurrentState(provisioner.Name, out var current))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Garage provisioning has not started."));
        }

        var snapshot = current.Snapshot;
        var completed = snapshot.State?.Text == KnownResourceStates.Exited
            || snapshot.State?.Text == KnownResourceStates.Finished;
        return Task.FromResult(completed && snapshot.ExitCode == 0
            ? HealthCheckResult.Healthy("Garage provisioning completed successfully.")
            : HealthCheckResult.Unhealthy("Garage provisioning has not completed successfully."));
    }
}
