using System;
using chia.dotnet.bech32;
using chia.dotnet.clvm;
using Nethermind.Crypto;

namespace BTCPayServer.Plugins.Chia.Services;

public static class ChiaAddressHelper
{
    private static readonly byte[] DefaultHiddenPuzzleHash = Convert.FromHexString("711d6c4e32c92e53179b199484cf8c897542bc57f2b22582799f9d657eec4699");
    private static readonly Program P2DelegatedOrHiddenPuzzle = Program.DeserializeHex(
        "ff02ffff01ff02ffff03ff0bffff01ff02ffff03ffff09ff05ffff1dff0bffff1effff0bff0bffff02ff06ffff04ff02ffff04ff17ff8080808080808080ffff01ff02ff17ff2f80ffff01ff088080ff0180ffff01ff04ffff04ff04ffff04ff05ffff04ffff02ff06ffff04ff02ffff04ff17ff80808080ff80808080ffff02ff17ff2f808080ff0180ffff04ffff01ff32ff02ffff03ffff07ff0580ffff01ff0bffff0102ffff02ff06ffff04ff02ffff04ff09ff80808080ffff02ff06ffff04ff02ffff04ff0dff8080808080ffff01ff0bffff0101ff058080ff0180ff018080");

    public static string PuzzleHashToAddress(byte[] puzzleHash)
    {
        var encoder = new Bech32M();
        return encoder.PuzzleHashToAddress(HexBytes.FromBytes(puzzleHash));
    }
    
    public static string PuzzleHashToAddress(string puzzleHash)
    {
        var encoder = new Bech32M();
        return encoder.PuzzleHashToAddress(HexBytes.FromHex(puzzleHash));
    }

    public static string AddressToPuzzleHash(string address)
    {
        return Bech32M.AddressToPuzzleHash(address).Hex;
    }

    public static string DeriveAddress(string pkHex, uint index)
    {
        return DeriveAddress(new Bls.P1(Convert.FromHexString(pkHex)), index);
    }
    public static string DeriveAddress(Bls.P1 pk, uint index)
    {
        var childPk = ChiaKeyHelper.DerivePath(pk, [12381, 8444, 2, index]);
        var syntheticPk = ChiaKeyHelper.CalculateSyntheticPublicKey(childPk, DefaultHiddenPuzzleHash);
        
        var puzzle = P2DelegatedOrHiddenPuzzle.Curry([Program.FromBytes(syntheticPk.Compress())]);
        var puzzleHash = puzzle.Hash();
        
        return PuzzleHashToAddress(puzzleHash);
    }
}