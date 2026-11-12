using System.Runtime.CompilerServices;
using ChameLab.Services;

namespace ChameLab.Tests;

/// <summary>
/// Tests never touch the user's real app data: without this, AppSettings and the libraries
/// fall back to the file store under LocalApplicationData, the same one the app reads.
/// </summary>
internal static class TestStore
{
    [ModuleInitializer]
    internal static void UseMemoryStore() => AppServices.Register([], new MemoryKeyValueStore());
}
