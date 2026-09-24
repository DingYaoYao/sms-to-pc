package com.smslink

import android.util.Base64
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * 与 PC 端 pc/src/crypto.js 完全对应：
 *  - 配对码用 PBKDF2-HMAC-SHA256（20 万次）派生，暴力猜配对码在算力上不可行
 *  - 短信正文用 AES-256-GCM 加密并带完整性校验
 */
object Crypto {
    const val PBKDF2_ITERATIONS = 200_000
    const val PAIR_AAD_PREFIX = "SMSLINK-PAIR-V1|"
    const val SMS_AAD_PREFIX = "SMSLINK-SMS-V1|"

    private const val PBKDF2_SALT_PREFIX = "SMSLINK-PAIR-V1:"
    private const val PAIR_PROOF_PREFIX = "SMSLINK-PAIR-V1|"
    private const val NONCE_BYTES = 12
    private const val TAG_BITS = 128

    private val random = SecureRandom()

    fun randomBytes(size: Int): ByteArray = ByteArray(size).also { random.nextBytes(it) }

    fun randomHex(size: Int): String =
        randomBytes(size).joinToString(separator = "") { "%02x".format(it.toInt() and 0xFF) }

    fun shortHash(value: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8))
        return digest.take(8).joinToString(separator = "") { "%02x".format(it.toInt() and 0xFF) }
    }

    fun derivePairKey(pairCode: String, pcDeviceId: String): ByteArray {
        val salt = (PBKDF2_SALT_PREFIX + pcDeviceId).toByteArray(Charsets.UTF_8)
        val spec = PBEKeySpec(pairCode.toCharArray(), salt, PBKDF2_ITERATIONS, 256)
        return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
    }

    fun pairProof(pairKey: ByteArray, androidId: String, timestamp: Long, nonceHex: String): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pairKey, "HmacSHA256"))
        val material = "$PAIR_PROOF_PREFIX$androidId|$timestamp|$nonceHex"
        return mac.doFinal(material.toByteArray(Charsets.UTF_8))
    }

    /** 返回 “base64(nonce)” 到 “base64(密文+标签)”。 */
    fun encrypt(key: ByteArray, plaintext: ByteArray, aad: String): Pair<String, String> {
        val iv = randomBytes(NONCE_BYTES)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, iv))
        cipher.updateAAD(aad.toByteArray(Charsets.UTF_8))
        val body = cipher.doFinal(plaintext)
        return Base64.encodeToString(iv, Base64.NO_WRAP) to Base64.encodeToString(body, Base64.NO_WRAP)
    }

    fun decrypt(key: ByteArray, nonceBase64: String, cipherTextBase64: String, aad: String): ByteArray {
        val iv = Base64.decode(nonceBase64, Base64.NO_WRAP)
        val body = Base64.decode(cipherTextBase64, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, iv))
        cipher.updateAAD(aad.toByteArray(Charsets.UTF_8))
        return cipher.doFinal(body)
    }
}
