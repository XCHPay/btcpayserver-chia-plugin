using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Nethermind.Crypto;

namespace BTCPayServer.Plugins.Chia.Services;

public static class ChiaKeyHelper
{

    private static readonly BigInteger GROUP_ORDER = BigInteger.Parse("73EDA753299D7D483339D80809A1D80553BDA402FFFE5BFEFFFFFFFF00000001", NumberStyles.HexNumber);

    public static bool IsValidChiaKey(string chiaKey)
    {
        try
        {
            _ = new Bls.P1(Convert.FromHexString(chiaKey));
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public static Bls.P1 CalculateSyntheticPublicKey(Bls.P1 publicKey, byte[] hiddenPuzzleHash)
    {
        BigInteger syntheticOffset = CalculateSyntheticOffset(publicKey.Compress(), hiddenPuzzleHash);
        byte[] syntheticOffsetBytes = syntheticOffset.ToByteArray().Reverse().ToArray(); // Convert BigInteger to bytes (big-endian)
        
        int targetLength = 32;

        // Ensure the array is properly sized with leading zeroes
        if (syntheticOffsetBytes.Length < targetLength)
        {
            byte[] paddedBytes = new byte[targetLength];
            Array.Copy(syntheticOffsetBytes, 0, paddedBytes, targetLength - syntheticOffsetBytes.Length, syntheticOffsetBytes.Length);
            syntheticOffsetBytes = paddedBytes;
        }

        var syntheticOffsetSk = new Bls.SecretKey(syntheticOffsetBytes, Bls.ByteOrder.BigEndian);
        var syntheticOffsetPk = new Bls.P1(syntheticOffsetSk);
        
        return publicKey.Add(syntheticOffsetPk);
    }
    
    public static Bls.P1 DerivePath(Bls.P1 pk, uint[] path)
    {
        foreach (var index in path)
        {
            pk = DeriveUnhardened(pk, index);
        }
        return pk;
    }
    
    private static Bls.P1 DeriveUnhardened(Bls.P1 pk, uint index)
    {
        var sha256 = SHA256.Create();
        sha256.TransformBlock(pk.Compress(), 0, 48, null, 0);
        sha256.TransformFinalBlock(BitConverter.GetBytes(index).Reverse().ToArray(), 0, 4);
        var digest = sha256.Hash;
        
        var nonce = new Bls.Scalar(digest);
        var bte = nonce.ToBendian();
        
        var p1 = Bls.G1();
        p1 = p1.Mult(bte);
        p1 = p1.Add(pk);

        return p1;
    }
    
    private static BigInteger CalculateSyntheticOffset(byte[] publicKey, byte[] hiddenPuzzleHash)
    {
        using SHA256 sha256 = SHA256.Create();
        
        byte[] combined = new byte[publicKey.Length + hiddenPuzzleHash.Length];
        Buffer.BlockCopy(publicKey, 0, combined, 0, publicKey.Length);
        Buffer.BlockCopy(hiddenPuzzleHash, 0, combined, publicKey.Length, hiddenPuzzleHash.Length);
        byte[] hash = sha256.ComputeHash(combined);
            
        BigInteger offset = new BigInteger(hash.Reverse().ToArray()); // Convert bytes to BigInteger (little-endian correction)
        offset = (offset % GROUP_ORDER + GROUP_ORDER) % GROUP_ORDER; // Force the offset to be positive
        
        return offset;
    }
}