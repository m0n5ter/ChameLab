using ChameLab.Crypto;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class Crypto1Test
{
    [Theory]
    [InlineData(0xd73A52b491AAuL, 0x009E831Fu, 0x00F236A0u)]
    [InlineData(0x9D29AE25242AuL, 0x0056f22Eu, 0x00E84C40u)]
    [InlineData(0x1FA3E73CAC0AuL, 0x00CBB67Cu, 0x00E8D640u)]
    [InlineData(0xD1C9DB532E82uL, 0x0015D8E9u, 0x00B9BB40u)]
    [InlineData(0x239186C46E88uL, 0x00A191E5u, 0x008A4550u)]
    public void Crypto1State(ulong key, uint odd, uint even)
    {
        var state = new Crypto1State(key);
        Assert.Equal(odd, state.Odd);
        Assert.Equal(even, state.Even);
    }

    [Theory]
    [InlineData(0xd73A52b491AAuL)]
    [InlineData(0x9D29AE25242AuL)]
    [InlineData(0x1FA3E73CAC0AuL)]
    [InlineData(0xD1C9DB532E82uL)]
    [InlineData(0x239186C46E88uL)]
    public void Lfsr(ulong key)
    {
        var state = new Crypto1State(key);
        Assert.Equal(key, state.Lfsr);
    }

    [Theory]
    [InlineData(0xd73A52b491AAuL)]
    [InlineData(0x9D29AE25242AuL)]
    [InlineData(0x1FA3E73CAC0AuL)]
    [InlineData(0xD1C9DB532E82uL)]
    [InlineData(0x239186C46E88uL)]
    public void PeekCrypto1Bit(ulong key)
    {
        var state = new Crypto1State(key);
        Assert.Equal(Crypto1.Filter(state.Odd), state.PeekCrypto1Bit);
    }

    [Fact]
    public void OddParity8()
    {
        for (int i = 0; i <= 0xff; i++)
        {
            var count = 1;
            var v = i;
            while (v > 0)
            {
                count += v & 1;
                v >>= 1;
            }
            Assert.Equal((byte)(count % 2), Crypto1.OddParity8((byte)i));
        }
    }

    [Fact]
    public void EvenParity8()
    {
        for (int i = 0; i <= 0xff; i++)
        {
            var count = 0;
            var v = i;
            while (v > 0)
            {
                count += v & 1;
                v >>= 1;
            }
            Assert.Equal((byte)(count % 2), Crypto1.EvenParity8((byte)i));
        }
    }
}
