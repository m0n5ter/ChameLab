using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Crypto;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class Recovery32GroupingTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0u, 0xABCDEF01u)]
    [InlineData(0xFFFFFFFFu, 0u)]
    [InlineData(0x12345678u, 0xABCDEF01u)]
    [InlineData(0xFFFFFFFFu, 0x55AA33CCu)]
    public void GroupedRecovery_PreservesEveryStateIncludingDuplicates(uint keystream, uint input)
    {
        using var workspace = new Crypto1RecoveryWorkspace();
        var expected = Crapto1.LfsrRecovery32(keystream, input, workspace);
        var actual = new List<Crypto1State>();
        var scratch = new uint[Crypto1RecoveryWorkspace.Recovery32BufferLength];
        workspace.Enter();
        try
        {
            Assert.True(Crapto1.Recover32Core(keystream, input, workspace, state =>
            {
                actual.Add(state);
                return true;
            }, scratch));
        }
        finally { workspace.Exit(); }

        static ulong Pack(Crypto1State state) => ((ulong)state.Odd << 32) | state.Even;
        Assert.Equal(expected.Select(Pack).Order().ToArray(), actual.Select(Pack).Order().ToArray());
    }
}
