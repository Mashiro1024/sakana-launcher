using Sakana.Domain;

namespace Sakana.Application;

public interface IMinecraftLaunchPlanner
{
    Task<bool> SupportsQuickPlayAsync(string minecraftVersionId, CancellationToken cancellationToken = default);

    Task<MinecraftLaunchPlan> CreatePlanAsync(
        InstanceProfile profile,
        MinecraftAccountSession session,
        CancellationToken cancellationToken = default,
        MinecraftQuickPlayTarget? quickPlayTarget = null);
}
