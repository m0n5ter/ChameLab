using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Crapto1Sharp;
using Xunit;

namespace ChameLab.Tests;

public class HfSniffTests
{
    private const uint Uid = 0x12345678;
    private const ulong Key = 0xA0A1A2A3A4A5;

    /// <summary>A reader frame as the firmware stores it: a parity bit after each byte, least significant bit first.</summary>
    private static IEnumerable<byte> ReaderFrame(params byte[] data)
    {
        var bits = new List<int>();
        foreach (var b in data)
        {
            for (var i = 0; i < 8; i++) bits.Add(b >> i & 1);
            bits.Add(Crypto1.OddParity8(b));
        }
        var packed = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++) packed[i / 8] |= (byte)(bits[i] << (i % 8));
        return new[] { (byte)(bits.Count >> 8), (byte)bits.Count }.Concat(packed);
    }

    /// <summary>A card answer as the firmware stores it: plain bytes, bit 15 of the length set.</summary>
    private static IEnumerable<byte> CardFrame(params byte[] data) =>
        new[] { (byte)(0x80 | data.Length * 8 >> 8), (byte)(data.Length * 8) }.Concat(data);

    private static IEnumerable<byte> ShortFrame(byte value) => new byte[] { 0, 7, value };

    private static byte[] WithCrc(params byte[] data)
    {
        var crc = HfSniff.CrcA(data);
        return [.. data, (byte)crc, (byte)(crc >> 8)];
    }

    private static byte[] Be(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>A genuine authentication with <see cref="Key"/>: the reader's encrypted nr || ar and the card's at.</summary>
    private static (uint NrEnc, uint ArEnc, uint AtEnc) Authenticate(uint nt, uint nr)
    {
        var s = new Crypto1State(Key);
        Crypto1.Crypto1Word(ref s, Uid ^ nt, false);
        var nrEnc = nr ^ Crypto1.Crypto1Word(ref s, nr, false);
        var arEnc = Crypto1.PrngSuccessor(nt, 64) ^ Crypto1.Crypto1Word(ref s, 0, false);
        var atEnc = Crypto1.PrngSuccessor(nt, 96) ^ Crypto1.Crypto1Word(ref s, 0, false);
        return (nrEnc, arEnc, atEnc);
    }

    /// <summary>REQA, anticollision and SELECT of <see cref="Uid"/>, then an AUTH of block 4 with key A.</summary>
    private static IEnumerable<byte> Session(uint nt, uint nr, bool cardAnswers)
    {
        var (nrEnc, arEnc, atEnc) = Authenticate(nt, nr);
        var uid = Be(Uid);
        var bcc = (byte)(uid[0] ^ uid[1] ^ uid[2] ^ uid[3]);
        var frames = ShortFrame(0x26)
            .Concat(CardFrame(0x04, 0x00))
            .Concat(ReaderFrame(0x93, 0x20))
            .Concat(CardFrame([.. uid, bcc]))
            .Concat(ReaderFrame(WithCrc([0x93, 0x70, .. uid, bcc])))
            .Concat(CardFrame(0x08))
            .Concat(ReaderFrame(WithCrc(0x60, 0x04)))
            .Concat(CardFrame(Be(nt)))
            .Concat(ReaderFrame([.. Be(nrEnc), .. Be(arEnc)]));
        return cardAnswers ? frames.Concat(CardFrame(Be(atEnc))).Concat(ReaderFrame(0x11, 0x22, 0x33, 0x44)) : frames;
    }

    [Fact]
    public void ParsesFramesStrippingParity()
    {
        var frames = HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, cardAnswers: false).ToArray());
        Assert.Equal(9, frames.Count);
        Assert.True(frames[0].IsShort);
        Assert.Equal(7, frames[0].Bits);
        Assert.Equal("0400", frames[1].Hex);
        Assert.Equal(HfDirection.Card, frames[1].Direction);
        Assert.Equal("9320", frames[2].Hex);
        Assert.Equal(16, frames[2].Bits);
        Assert.True(frames[4].CrcOk);
        Assert.Equal(WithCrc(0x60, 0x04), frames[6].Data);
    }

    [Fact]
    public void LabelsTheSessionAndMarksItEncryptedAfterAuth()
    {
        var frames = HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, cardAnswers: true).ToArray());
        var labels = HfSniff.Annotate(frames);
        Assert.Equal("REQA", labels[0].Label);
        Assert.Equal("ATQA 0004", labels[1].Label);
        Assert.Equal("ANTICOLLISION CL1", labels[2].Label);
        Assert.StartsWith("UID 12345678, BCC ok", labels[3].Label);
        Assert.Equal("SELECT CL1: UID 12345678", labels[4].Label);
        Assert.Equal("SAK 08: MIFARE Classic 1K", labels[5].Label);
        Assert.Equal("AUTH key A, block 4 (sector 1)", labels[6].Label);
        Assert.StartsWith("Card nonce nt = 01200145", labels[7].Label);
        Assert.StartsWith("Reader answer, encrypted", labels[8].Label);
        Assert.StartsWith("Card answer, encrypted: at", labels[9].Label);
        Assert.True(labels[10].IsEncrypted);
        Assert.False(labels[6].IsEncrypted);
    }

    [Fact]
    public void SummarizesCardAndAuths()
    {
        var s = HfSniff.Summarize(HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, false).ToArray()));
        Assert.Equal("12345678", Convert.ToHexString(s.Uid!));
        Assert.Equal("0004", Convert.ToHexString(s.Atqa!));
        Assert.Equal((byte)0x08, s.Sak);
        Assert.Equal([(4, MfKeyType.A)], s.Auths);
        Assert.Equal(5, s.ReaderFrames);
        Assert.Equal(4, s.CardFrames);
    }

    [Fact]
    public void Mfkey32RecoversTheReaderKeyFromTwoAuths()
    {
        var buffer = Session(0x01200145, 0x11223344, false).Concat(Session(0x0F8C3C0B, 0x55667788, false)).ToArray();
        var groups = HfSniff.GroupAuths(HfSniff.AuthExchanges(HfSniff.ParseChameleon(buffer)));
        var group = Assert.Single(groups);
        Assert.Equal(Uid, group.Uid);
        Assert.Equal(2, group.Exchanges.Count);
        Assert.All(group.Exchanges, e => Assert.Null(e.AtEnc));
        Assert.Equal((Key, "mfkey32"), Mfkey32Recovery.Recover(group));
    }

    [Fact]
    public void Mfkey64RecoversTheKeyFromOneAcceptedAuth()
    {
        var groups = HfSniff.GroupAuths(HfSniff.AuthExchanges(
            HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, cardAnswers: true).ToArray())));
        var group = Assert.Single(groups);
        Assert.NotNull(group.Exchanges[0].AtEnc);
        Assert.Equal((Key, "mfkey64"), Mfkey32Recovery.Recover(group));
    }

    [Fact]
    public void OneUnansweredAuthIsNotEnough()
    {
        var group = Assert.Single(HfSniff.GroupAuths(HfSniff.AuthExchanges(
            HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, false).ToArray()))));
        Assert.Null(Mfkey32Recovery.Recover(group));
    }

    [Fact]
    public void ProxmarkTraceRoundTrips()
    {
        var frames = HfSniff.ParseChameleon(Session(0x01200145, 0x11223344, true).ToArray());
        var back = HfSniff.ParseTrace(HfSniff.BuildTrace(frames));
        Assert.Equal(frames.Count, back.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Direction, back[i].Direction);
            Assert.Equal(frames[i].Bits, back[i].Bits);
            Assert.Equal(frames[i].Data, back[i].Data);
        }
    }

    [Fact]
    public void LabelsIso14443Apdus()
    {
        byte[] select = [0x02, 0x00, 0xA4, 0x04, 0x00, 0x07, 0xA0, 0x00, 0x00, 0x00, 0x03, 0x10, 0x10, 0x00];
        var buffer = ReaderFrame(WithCrc(0xE0, 0x80)).Concat(CardFrame(0x05, 0x78, 0x80, 0x70, 0x02))
            .Concat(ReaderFrame(WithCrc(select))).Concat(CardFrame(0x02, 0x6A, 0x82)).ToArray();
        var labels = HfSniff.Annotate(HfSniff.ParseChameleon(buffer));
        Assert.Equal("RATS (FSDI 8, CID 0)", labels[0].Label);
        Assert.Equal("ATS", labels[1].Label);
        Assert.Equal("SELECT AID A0000000031010 (Visa)", labels[2].Label);
        Assert.Equal("Response 6A82: file not found", labels[3].Label);
        Assert.Equal(["A0000000031010 (Visa)"], HfSniff.Summarize(HfSniff.ParseChameleon(buffer)).Aids);
    }

    [Fact]
    public void CrcAMatchesTheStandardVector()
    {
        // ISO14443-3 example: the CRC_A of 00 00 is 1EA0 (sent A0 1E).
        Assert.Equal(0x1EA0, HfSniff.CrcA([0x00, 0x00]));
        Assert.Equal(WithCrc(0x50, 0x00)[2..], new byte[] { 0x57, 0xCD });
    }
}
