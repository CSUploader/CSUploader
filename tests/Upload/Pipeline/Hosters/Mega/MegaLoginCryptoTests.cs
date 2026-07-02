// <copyright file="MegaLoginCryptoTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Known-answer tests for <see cref="MegaLoginCrypto"/>. All expected values were generated with a
/// Node reference (node:crypto pbkdf2/aes-128-ecb + BigInt modpow) that replicates the MEGA web
/// SDK / mega.py algorithms — the same gold-standard approach as <c>MegaCryptoTests</c>.
/// </summary>
public class MegaLoginCryptoTests
{
    // ===== v2 (PBKDF2) derivation =====

    [Fact]
    public void DeriveV2_MatchesReference()
    {
        byte[] salt = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        (byte[] pwKey, string uh) = MegaLoginCrypto.DeriveV2("correct horse battery staple", salt);

        Assert.Equal("88aa99bab648e0a15a6dcfd127cb7d9f", Convert.ToHexStringLower(pwKey));
        Assert.Equal("F9Gj-gyqbKnMl-KglgC7wA", uh);
    }

    // ===== v1 (legacy) derivation =====

    [Fact]
    public void PrepareKeyV1_MatchesReference()
    {
        byte[] pwKey = MegaLoginCrypto.PrepareKeyV1("legacy-pass-1");

        Assert.Equal("317665eca9bb92cb79fa7bd5a2bb2f69", Convert.ToHexStringLower(pwKey));
    }

    [Fact]
    public void StringHashV1_MatchesReference()
    {
        byte[] pwKey = Convert.FromHexString("317665eca9bb92cb79fa7bd5a2bb2f69");

        Assert.Equal("hMq2qfHH_68", MegaLoginCrypto.StringHashV1("user@example.com", pwKey));
    }

    // ===== master key unwrap =====

    [Fact]
    public void DecryptMasterKey_RoundTripsAgainstEncryptKeyEcb()
    {
        byte[] pwKey = Convert.FromHexString("88aa99bab648e0a15a6dcfd127cb7d9f");
        uint[] masterKey = [0x00112233, 0x44556677, 0x8899aabb, 0xccddeeff];
        string kB64 = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(pwKey, masterKey));

        byte[] unwrapped = MegaLoginCrypto.DecryptMasterKey(kB64, pwKey);

        Assert.Equal(MegaCrypto.A32ToBytes(masterKey), unwrapped);
    }

    // ===== RSA session-id decrypt (synthetic 2048-bit key, real privk MPI layout) =====

    private const string PrivkB64 =
        "5aAp8gizJjifKI73jMUBrs2oVTBgLaclXfvAsK25R7iPeboz0s2im_AQhFUZ96YclT-k1WORLtXT77-ufAhe5pZ5kUFJo5sA3Ou_anKrbZ6339E3pZ2dD6VQioleRCwofbNMQGyBE-yGryL94LNWhQ5SmdvJnstNd1MIRlgvkxhIRI5kbWcsvTL1e-ZOSm4DxsVtF6SmIzIbyFJN_82fm0g863hUMkSRJjNPb66nrnYLAPQQ2--sEsdR-d1VYaIj6H8t8fMKKgJBC0r3jww7Z0CEBDxchCPLzYp1Cp9ACiyoZ7rHd_DcaCJy8ckBDVdSTp0TynJzXTamsHjiR2JeZ-pIJgmdzFA3ud91oMTYMRMjeKLCdF-TUOrJoH0vk8M8IaGlpyYQLvKy8zx8R50qkdBDJvG3H1Y5CuOSOlRkhT1awfxsiiSptxm-bEEPi_VvDwRccgTqqJfppFYcMNufOs4e9ySUwC9fqGWcl97TJZvmrYqCxXuU5rzG_GLFgfhcMdqCsJrAVkIL-jPkVbM6uYP3BgpfF-mAx-CH4sEaIlCGWwxIDtbEYAtsyIIB_dr5lfmO6qrxt-v1PXfvgynqytZonsGiZD1uy11CRX10_NSnhAHCl_mfE0rpxKfYQoaY8ZzQ9bhFkuNwUsVkXH_qC6XEzsu6wXjaUkyJ3oVLT98vyCecpaniXwE4yMhHLE7WTUifPFWVF3noDDXIxSctcSyjbx4xV9NWa9ccSQtb5MacRkddusOv4RCZcqY4WtW2-jnvpOJNCP9E2UI53uyb9g-hPEMTWI7cbDK5csFHPFioLEcqNwIBz6uqG4kRJXtXo92SAf-gFNfuvnMBJoklir7h9ZBpuybuEs_Rj_d6y-c";

    private const string CsidB64 =
        "B_sGQCttyH4MsttApgbbX1zrn2REyrIssrBJ-vrpmQ45zyyKdteG7YCRx_aPwu6xM4FoRLWWpZlqqbibYSecHnvJ3X1ORhIvFhjic2723z2ag_0UR85UVsD_VSGS4CpUDDXa5n4Ejf7HGhjAlSROsfoOgmt1DA4olJKdh2UigBCY5W-bCLmvyTCK_zYSy_hmQJdZY77_397eglBmkEkcHjcI0rPVwZx14g3RoGnlf-vJV_E9E-EGTP805MsBTx_VpmrIYpHiLsBznM2wX3MtS707S_LrAEzJN27ZEKE1hF_30rXJgFZAMmo4uodkymDcgXy1dGVkkNfC0xehsrU5bMng";

    [Fact]
    public void DecryptSessionId_MatchesReference()
    {
        byte[] masterKey = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");

        string sid = MegaLoginCrypto.DecryptSessionId(PrivkB64, CsidB64, masterKey);

        Assert.Equal("AggPFh0kKzI5QEdOVVxjanF4f4aNlJuiqbC3vsXM09rh6O_2_QQLEhkgJw", sid);
    }

    [Fact]
    public void DecryptSessionId_TruncatedPrivk_ThrowsCryptographicException()
    {
        byte[] masterKey = Convert.FromHexString("101112131415161718191a1b1c1d1e1f");
        string truncated = PrivkB64[..64]; // 48 decoded bytes — first MPI's magnitude runs past the end

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => MegaLoginCrypto.DecryptSessionId(truncated, CsidB64, masterKey));
    }
}
