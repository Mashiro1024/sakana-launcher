namespace Sakana.Application;

public sealed class ProfileNameInUseException(string name)
    : InvalidOperationException($"A profile named '{name}' already exists.");
