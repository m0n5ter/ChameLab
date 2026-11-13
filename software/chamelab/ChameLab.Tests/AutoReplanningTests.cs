using System;
using System.Linq;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public partial class MifareWorkflowTests
{
    [Theory]
    [InlineData(MifareClassicPrngType.Weak, false)]
    [InlineData(MifareClassicPrngType.Static, false)]
    [InlineData(MifareClassicPrngType.Hard, false)]
    [InlineData(MifareClassicPrngType.Weak, true)]
    [InlineData(MifareClassicPrngType.Static, true)]
    [InlineData(MifareClassicPrngType.Hard, true)]
    public async Task Auto_ConfirmedSeedUsesItsPrngFamilyBeforeNormalKeySen(
        MifareClassicPrngType prng, bool retainedSeed)
    {
        var seed = Convert.FromHexString("112233445566");
        var other = Convert.FromHexString("665544332211");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        if (retainedSeed) dump.KeyA[1] = seed;
        var transport = AutoTransport(prng, (s, _) => s == 1 ? seed : other, backdoor: true);
        var reply = transport.Response!;
        var counter = 0;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success,
                Convert.FromHexString("07DC07DD07DE07DF07E707D307D507D6")),
            Command.Mf1EncNestedAcquire => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1EncNestedWithKey => throw new InvalidOperationException("Normal-key SEN delayed the seeded family."),
            Command.Mf1HardnestedAcquire => (Status.HfTagOk, HardnestedBatch(ref counter)),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation is KeyRecoveryOperation.Nested or KeyRecoveryOperation.StaticNested
            ? [0x665544332211UL] : throw new InvalidOperationException("Unnecessary SEN candidate calculation."));
        var hardnested = new HardnestedEngine { Result = 0x665544332211UL };
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var result = await new MifareKeyRecovery(new ChameleonClient(connection), hardnested, keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [seed], options: new(MifareKeySearchMethod.OneSector)
                { SelectedSector = 1 }, useKnownUidKeys: false);

        Assert.True(result.Complete);
        Assert.Equal(4, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedAcquire));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1EncNestedWithKey or Command.Mf1CheckKeysOnBlock);
        var attack = prng switch
        {
            MifareClassicPrngType.Weak => Command.Mf1NestedAcquire,
            MifareClassicPrngType.Static => Command.Mf1StaticNestedAcquire,
            _ => Command.Mf1HardnestedAcquire,
        };
        var firstAttack = transport.Requests.FindIndex(r => r.Command == attack);
        Assert.True(firstAttack > 0);
        Assert.Contains(transport.Requests.Take(firstAttack), r => r.Command == Command.Mf1CheckKeysOfSectors
            && r.Data[10..].SequenceEqual(seed));
        Assert.Equal(seed, dump.KeyA[1]);
        Assert.Equal(seed, dump.KeyB[1]);
        Assert.All(new[] { 0, 2, 3, 4 }, s => Assert.Equal(other, dump.KeyA[s]));
        Assert.Equal(prng == MifareClassicPrngType.Hard ? 1 : 0, hardnested.Calls);
    }

    [Fact]
    public async Task Auto_CheapPreparedSenPathPrecedesAcquiringNoncesForAnotherFamily()
    {
        var first = Convert.FromHexString("000000000001");
        var second = Convert.FromHexString("000000000002");
        var capture = AutoEncryptedCapture(5);
        for (var i = 0; i < 10; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(capture.AsSpan(7 + i * 7), (uint)i);
        var transport = AutoTransport(MifareClassicPrngType.Weak,
            (s, t) => s == 0 && t == MfKeyType.B ? second : first, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, capture),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(second).ToArray()),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation switch
        {
            KeyRecoveryOperation.StaticEncryptedNested => r.Nonces[0].NtEnc == 1 ? [1, 2] : [1],
            KeyRecoveryOperation.OrderStaticEncrypted => r.Keys,
            _ => throw new InvalidOperationException("A prepared SEN path was cheaper than another capture."),
        });
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [], useKnownUidKeys: false)).Complete);

        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1NestedAcquire
            or Command.Mf1HardnestedAcquire or Command.Mf1DetectPrng);
        var check = Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Equal(second, check.Data[3..]);
        Assert.Equal(second, dump.KeyB[0]);
    }

    [Fact]
    public async Task Auto_StaticHardnestedCaptureFallsBackToDictionary()
    {
        var other = Convert.FromHexString("112233445566");
        var dump = KnownCard();
        dump.KeyB[4] = null;
        var transport = AutoTransport(MifareClassicPrngType.Hard,
            (s, t) => s == 4 && t == MfKeyType.B ? other : Key);
        var reply = transport.Response!;
        transport.Response = r => r.Command == Command.Mf1HardnestedAcquire
            ? (Status.HfTagOk, new byte[495]) : reply(r);
        var hardnested = new HardnestedEngine();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), hardnested)
            .RecoverAutomaticallyAsync(dump, [other], useKnownUidKeys: false)).Complete);

        Assert.Equal(4, transport.Requests.Count(r => r.Command == Command.Mf1HardnestedAcquire));
        Assert.Equal(0, hardnested.Calls);
        Assert.Equal(other, dump.KeyB[4]);
    }

    [Fact]
    public async Task Auto_FailedNestedEnablesNormalKeySenAsFallback()
    {
        var seed = Convert.FromHexString("112233445566");
        var other = Convert.FromHexString("665544332211");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(MifareClassicPrngType.Weak, (s, _) => s == 1 ? seed : other, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07DE07DF07E707D507D6")),
            Command.Mf1EncNestedAcquire => (Status.MfErrAuth, Array.Empty<byte>()),
            Command.Mf1EncNestedWithKey => (Status.HfTagOk, ValidSenCapture(0x112233445566UL)),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation switch
        {
            KeyRecoveryOperation.Nested => [],
            KeyRecoveryOperation.MatchStaticEncrypted => ManagedKeyRecoveryEngine.Execute(r),
            KeyRecoveryOperation.StaticEncryptedNested => [0x665544332211UL],
            KeyRecoveryOperation.OrderStaticEncrypted => r.Keys,
            _ => throw new InvalidOperationException("Unexpected recovery operation."),
        });
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [seed], options: new(MifareKeySearchMethod.OneSector)
                { SelectedSector = 1 }, useKnownUidKeys: false)).Complete);

        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1NestedAcquire));
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedWithKey));
        Assert.True(transport.Requests.FindLastIndex(r => r.Command == Command.Mf1NestedAcquire)
            < transport.Requests.FindIndex(r => r.Command == Command.Mf1EncNestedWithKey));
        Assert.Equal(seed, dump.KeyA[1]);
        Assert.All(new[] { 0, 2, 3, 4 }, s => Assert.Equal(other, dump.KeyA[s]));
    }

    [Fact]
    public async Task Auto_OfflineSenDictionaryHitSpreadsThenUsesStaticNestedWithoutGeneratingSenLists()
    {
        var seed = Convert.FromHexString("112233445566");
        var other = Convert.FromHexString("665544332211");
        var capture = ValidSenCapture(0x665544332211UL);
        // Only sector 1 matches the selected dictionary's single key.
        ValidSenCapture(0x112233445566UL).AsSpan(18, 14).CopyTo(capture.AsSpan(18));
        var transport = AutoTransport(MifareClassicPrngType.Static, (s, _) => s == 1 ? seed : other, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, capture),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, new byte[] { 1 }.Concat(seed).ToArray()),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation switch
        {
            KeyRecoveryOperation.MatchStaticEncrypted => ManagedKeyRecoveryEngine.Execute(r),
            KeyRecoveryOperation.StaticNested => [0x665544332211UL],
            _ => throw new InvalidOperationException("SEN lists were unnecessary after finding a seed."),
        });
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(dump, [seed], useKnownUidKeys: false)).Complete);

        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Single(engine.Operations, op => op == KeyRecoveryOperation.StaticNested);
        var firstAttack = transport.Requests.FindIndex(r => r.Command == Command.Mf1StaticNestedAcquire);
        Assert.Contains(transport.Requests.Take(firstAttack), r => r.Command == Command.Mf1CheckKeysOfSectors);
        Assert.Equal(seed, dump.KeyB[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Auto_SenBlockHitUsesStaticNestedWithoutBatchFirmware(bool globalReuse)
    {
        var seed = Convert.FromHexString("112233445566");
        var other = Convert.FromHexString("665544332211");
        var capture = AutoEncryptedCapture(5);
        for (var i = 0; i < 10; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(capture.AsSpan(7 + i * 7), (uint)i);
        var transport = AutoTransport(MifareClassicPrngType.Static, (s, _) => s == 0 ? seed : other, backdoor: true);
        var reply = transport.Response!;
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DE07DF07D3")),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, capture),
            Command.Mf1CheckKeysOnBlock => (Status.HfTagOk, r.Data[0] == 0
                ? new byte[] { 1 }.Concat(seed).ToArray() : new byte[7]),
            _ => reply(r),
        };
        var engine = new AdaptiveEngine(r => r.Operation switch
        {
            KeyRecoveryOperation.StaticEncryptedNested => globalReuse || r.Nonces[0].NtEnc == 0 ? [0x112233445566UL] : [],
            KeyRecoveryOperation.OrderStaticEncrypted => r.Keys,
            KeyRecoveryOperation.StaticNested => [0x665544332211UL],
            _ => throw new InvalidOperationException("SEN continued after an applicable seed was confirmed."),
        });
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        Assert.True((await new MifareKeyRecovery(new ChameleonClient(connection), keyRecovery: engine)
            .RecoverAutomaticallyAsync(new(MifareClassicType.Mini, [1, 2, 3, 4]), [], useKnownUidKeys: false)).Complete);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1CheckKeysOnBlock);
        Assert.Single(transport.Requests, r => r.Command == Command.Mf1StaticNestedAcquire);
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1CheckKeysOfSectors
            or Command.Mf1NestedAcquire or Command.Mf1HardnestedAcquire);
    }

    [Theory]
    [InlineData(MifareClassicPrngType.Static, true, false)]
    [InlineData(MifareClassicPrngType.Static, true, true)]
    [InlineData(MifareClassicPrngType.Static, false, false)]
    [InlineData(MifareClassicPrngType.Weak, true, false)]
    [InlineData(MifareClassicPrngType.Hard, true, false)]
    public async Task Auto_GlobalSenHitYieldsOnlyToAnApplicableFamilyAndRetainsTheTailForFallback(
        MifareClassicPrngType prng, bool seededSupported, bool seededFails)
    {
        var candidates = Enumerable.Range(1, 160).Select(i => (ulong)i).ToArray();
        var seed = Convert.FromHexString("000000000001");
        var other = Convert.FromHexString("0000000000A0");
        var dump = new MifareDump(MifareClassicType.Mini, [1, 2, 3, 4]);
        var transport = AutoTransport(prng, (s, _) => s == 1 ? seed : other, backdoor: true);
        var reply = transport.Response!;
        var seededStarted = false;
        var senBatchesBeforeSeeded = 0;
        var generationCount = 0;
        var engine = new AdaptiveEngine(r => r.Operation switch
        {
            KeyRecoveryOperation.StaticEncryptedNested => Generate(),
            KeyRecoveryOperation.OrderStaticEncrypted => r.Keys,
            KeyRecoveryOperation.FilterStaticEncrypted => r.Keys,
            KeyRecoveryOperation.StaticNested => seededFails ? [] : [160],
            _ => throw new InvalidOperationException("Unexpected recovery operation."),
        });
        ulong[] Generate() { generationCount++; return candidates; }
        transport.Response = r => r.Command switch
        {
            Command.GetDeviceCapabilities => (Status.Success, Convert.FromHexString("07DC07DE07DF" +
                (seededSupported ? prng switch
                {
                    MifareClassicPrngType.Static => "07D3",
                    MifareClassicPrngType.Weak => "07D507D6",
                    _ => "07DD",
                } : ""))),
            Command.Mf1EncNestedAcquire => (Status.HfTagOk, AutoEncryptedCapture(5)),
            Command.Mf1HardnestedAcquire => (Status.HfTagOk, new byte[495]),
            _ => reply(r),
        };
        transport.OnRequest = r =>
        {
            if (r.Command is Command.Mf1StaticNestedAcquire or Command.Mf1NestedAcquire or Command.Mf1HardnestedAcquire)
                seededStarted = true;
            if (!seededStarted && r.Command == Command.Mf1CheckKeysOfSectors && r.Data.Length > 16)
                senBatchesBeforeSeeded++;
        };
        var updates = new System.Collections.Generic.List<KeyRecoveryProgress>();
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var hardnested = new HardnestedEngine();
        Assert.True((await new MifareKeyRecovery(client, hardnested, keyRecovery: engine).RecoverAutomaticallyAsync(dump, [],
            progress: new CallbackProgress<KeyRecoveryProgress>(updates.Add), useKnownUidKeys: false)).Complete);

        // Stable SEN evidence rules out Nested/Hardnested even if the first-auth
        // PRNG profile and firmware capabilities would otherwise allow them.
        var seededApplicable = seededSupported && prng == MifareClassicPrngType.Static;
        Assert.Equal(seededApplicable, seededStarted);
        if (seededApplicable) Assert.Equal(1, senBatchesBeforeSeeded);
        else Assert.True(senBatchesBeforeSeeded > 1); // No alternative: continue the untested tail.
        Assert.Equal(10, generationCount); // Fallback reuses the original ten candidate lists.
        Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.Mf1EncNestedAcquire));
        Assert.Equal(seededApplicable ? seededFails ? 24 : 1 : 0,
            transport.Requests.Count(r => r.Command == Command.Mf1StaticNestedAcquire));
        Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1NestedAcquire or Command.Mf1HardnestedAcquire);
        Assert.Equal(0, hardnested.Calls);
        Assert.Contains(updates, p => p.Status == Strings.Recovery_CheckStaticEnc && p.Sector == -1
            && p.Total == 160 && p.Tried > 0 && !string.IsNullOrEmpty(p.Detail));
        Assert.Equal(seed, dump.KeyB[1]);
        Assert.All(new[] { 0, 2, 3, 4 }, s => Assert.Equal(other, dump.KeyA[s]));
        // In a failed seeded attack, the resumed global pass must not resend its
        // exhausted first chunk. Propagation requests carry just one key.
        var globals = transport.Requests.Where(r => r.Command == Command.Mf1CheckKeysOfSectors && r.Data.Length > 16).ToArray();
        Assert.Single(globals, r => r.Data.AsSpan(10, 6).SequenceEqual(seed));
    }
}
