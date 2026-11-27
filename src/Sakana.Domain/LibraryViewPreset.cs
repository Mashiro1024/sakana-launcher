namespace Sakana.Domain;

/// <summary>A named set of instance-library search, filter, and sort settings.</summary>
public sealed record LibraryViewPreset(
    string Name,
    string SearchQuery,
    string? Group,
    string? Tag,
    string? Loader,
    string? State,
    string SortOrder);
