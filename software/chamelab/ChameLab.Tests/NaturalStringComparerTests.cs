using System.Linq;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class NaturalStringComparerTests
{
    [Fact]
    public void DigitRunsSortByValue()
    {
        string[] names = ["A21", "a11", "A2", "B1", "A1", "A", "A02", "Key 100", "Key 9"];
        var sorted = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[] { "A", "A1", "A2", "A02", "a11", "A21", "B1", "Key 9", "Key 100" }, sorted);
    }

    [Fact]
    public void HugeNumbersDoNotOverflow()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("x99999999999999999999", "x100000000000000000000") < 0);
    }
}
