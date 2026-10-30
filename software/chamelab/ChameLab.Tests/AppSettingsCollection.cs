using Xunit;

namespace ChameLab.Tests;

/// <summary>
/// Tests that change process-wide state (AppSettings such as the dump key source selection, or the
/// AppServices store behind it) run alone, so no other test sees their temporary values.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppSettingsCollection
{
    public const string Name = "AppSettings";
}
