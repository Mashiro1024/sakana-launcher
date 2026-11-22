using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftLaunchPlanner
{
    Task<MinecraftLaunchPlan> CreatePlanAsync(
        InstanceProfile profile,
        MinecraftAccountSession session,
        CancellationToken cancellationToken = default,
        MinecraftQuickPlayTarget? quickPlayTarget = null);
}
