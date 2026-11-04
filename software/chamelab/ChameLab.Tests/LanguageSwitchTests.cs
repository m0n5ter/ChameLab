using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

/// <summary>Tests that change the process-wide UI language run on their own, after the others.</summary>
[CollectionDefinition(nameof(UiLanguageCollection), DisableParallelization = true)]
public sealed class UiLanguageCollection;

[Collection(nameof(UiLanguageCollection))]
public sealed class LanguageSwitchTests
{
    [Fact]
    public void Apply_SwitchesStringsAtOnceAndTellsTheViews()
    {
        var raised = 0;
        void OnChanged() => raised++;
        LocalizationService.LanguageChanged += OnChanged;
        try
        {
            LocalizationService.Apply("ru");

            Assert.Equal(1, raised);
            Assert.Equal("Язык", ReadElsewhere(() => Strings.Settings_Language));
            // "System default" is translated too, so the picker's list is rebuilt.
            Assert.Equal("Как в системе", ReadElsewhere(() => LocalizationService.Languages[0].Name));

            LocalizationService.Apply("ru");
            Assert.Equal(1, raised);

            LocalizationService.Apply("de");
            Assert.Equal(2, raised);
            Assert.Equal("Sprache", ReadElsewhere(() => Strings.Settings_Language));
        }
        finally
        {
            LocalizationService.LanguageChanged -= OnChanged;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Reads on a thread pool thread that doesn't inherit this test's culture, as the app's
    /// threads and continuations do: only the default UI culture is changed.
    /// </summary>
    private static string ReadElsewhere(System.Func<string> read)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(read).Result;
    }
}
