using System;
using System.Collections.Generic;
using ChameLab.Models;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

/// <summary>
/// Structural checks for the darkside solver. A faithful correctness vector needs a real
/// card's NACK behaviour (no synthetic forward model exists that isn't circular), so the
/// numerical validation of darkside is done on-device; these tests only pin the pipeline's
/// shape and its handling of degenerate input.
/// </summary>
public class DarksideRecoveryTests
{
    [Fact]
    public void Recover_NoItems_ReturnsEmpty()
    {
        Assert.Empty(DarksideRecovery.Recover(0x12345678, Array.Empty<DarksideItem>()));
    }

    [Fact]
    public void Recover_GarbageItem_DoesNotThrow()
    {
        // A non-vulnerable / nonsensical round must simply yield no keys, never crash.
        var items = new List<DarksideItem> { new(Nt: 0, Ks1: 0, Par: 1, Nr: 0, Ar: 0) };
        var keys = DarksideRecovery.Recover(0x12345678, items);
        Assert.NotNull(keys);
    }
}
