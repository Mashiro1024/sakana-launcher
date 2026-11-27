namespace Sakana.Domain;

public sealed record MinecraftServerBookmark(Guid Id, string Name, string Address, bool IsPinned = false);
