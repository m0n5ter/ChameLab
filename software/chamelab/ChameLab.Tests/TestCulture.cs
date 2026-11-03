using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChameLab.Tests;

/// <summary>Tests read the English (neutral) strings, whatever the machine's UI language.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseNeutralStrings()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
