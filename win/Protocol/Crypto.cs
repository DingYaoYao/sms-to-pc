using System.Security.Cryptography;
using System.Text;

namespace SmsLink.Protocol;

/// <summary>
/// 与安卓端 Crypto.kt (javax.crypto) 和 Node 版 pc/src/crypto.js 完全一致：
///  - 配对码用 PBKDF2-HMAC-SHA256（20 万次迭代）派生
///  - 短信正文用 AES-256-GCM 加密，密文后面直接跟 16 字节认证标签
/// </summary>
public static class Crypto
{
    public const int Pbkdf2Iterations = 200_000;
    public const string PairAadPrefix = "SMSLINK-PAIR-V1|";
    public const string SmsAadPrefix = "SMSLINK-SMS-V1|";

    private const string Pbkdf2SaltPrefix = "SMSLINK-PAIR-V1:";
    private const string PairProofPrefix = "SMSLINK-PAIR-V1|";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    public static byte[] DerivePairKey(string pairCode, string pcDeviceId)
    {
        var salt = Encoding.UTF8.GetBytes(Pbkdf2SaltPrefix + pcDeviceId);
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(pairCode), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeyBytes);
    }

    public static byte[] PairProof(byte[] pairKey, string androidId, long timestamp, string nonceHex)
    {
        using var mac = new HMACSHA256(pairKey);
        var material = $"{PairProofPrefix}{androidId}|{timestamp}|{nonceHex}";
        return mac.ComputeHash(Encoding.UTF8.GetBytes(material));
    }

    public static bool FixedTimeEquals(byte[] a, byte[] b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    public static (string Nonce, string CipherText) Encrypt(byte[] key, byte[] plaintext, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var gcm = new AesGcm(key, TagBytes))
        {
            gcm.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(aad));
        }
        var body = new byte[cipher.Length + tag.Length];
        Buffer.BlockCopy(cipher, 0, body, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, body, cipher.Length, tag.Length);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(body));
    }

    public static byte[] Decrypt(byte[] key, string nonceBase64, string cipherTextBase64, string aad)
    {
        var nonce = Convert.FromBase64String(nonceBase64);
        var body = Convert.FromBase64String(cipherTextBase64);
        if (nonce.Length != NonceBytes || body.Length < TagBytes + 1)
            throw new CryptographicException("报文长度不合法");

        var cipher = new byte[body.Length - TagBytes];
        var tag = new byte[TagBytes];
        Buffer.BlockCopy(body, 0, cipher, 0, cipher.Length);
        Buffer.BlockCopy(body, cipher.Length, tag, 0, TagBytes);

        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }

    /// <summary>4 位配对码（按使用要求缩短；配合"错 5 次锁 60 秒"，在线爆破不现实）。</summary>
    public static string RandomPairCode() => RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");

    public static byte[] RandomKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string RandomNonceHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string NewDeviceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
