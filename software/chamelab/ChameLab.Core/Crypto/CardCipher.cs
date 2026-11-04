using System;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ChameLab.Crypto;

/// <summary>Card-protocol ciphers, including the weak/default DES keys used by blank cards.</summary>
public static class CardCipher
{
    public static byte[] Cbc(bool encrypt, bool aes, byte[] key, byte[] iv, byte[] data)
    {
        if (!aes && key.Length == 8) key = [.. key, .. key];
        var cipher = CipherUtilities.GetCipher(aes ? "AES/CBC/NoPadding" : "DESede/CBC/NoPadding");
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
        return cipher.DoFinal(data);
    }
}
