package com.smslink

import android.util.Base64
import org.json.JSONObject

/**
 * 配对：只要用户输入 4 位配对码。
 * 电脑地址由自动扫描提供，配对码本身负责确认"是不是这台电脑"——
 * 扫描到多台就逐个试，谁能验证通过就是谁。
 */
object Pairing {

    sealed class Result {
        data class Success(val pcName: String, val token: String, val linkKeyBase64: String) : Result()
        object WrongCode : Result()
        data class Failed(val message: String) : Result()
    }

    fun attempt(host: String, port: Int, code: String, androidId: String, deviceName: String): Result {
        val info = try {
            Net.getJson(host, port, "/api/info", timeoutMs = 4000)
        } catch (error: ApiException) {
            return Result.Failed("电脑端返回异常")
        } catch (error: Exception) {
            return Result.Failed(error.message ?: "连不上电脑")
        }

        val pcId = info.optString("id")
        if (pcId.isEmpty()) return Result.Failed("电脑端版本过旧，请更新电脑端")

        val nonce = Crypto.randomHex(16)
        val timestamp = System.currentTimeMillis()
        val pairKey = Crypto.derivePairKey(code, pcId)
        val proof = Crypto.pairProof(pairKey, androidId, timestamp, nonce)
        val request = JSONObject()
            .put("android_id", androidId)
            .put("name", deviceName)
            .put("nonce", nonce)
            .put("ts", timestamp)
            .put("proof", Base64.encodeToString(proof, Base64.NO_WRAP))

        val response = try {
            Net.postJson(host, port, "/api/pair", request)
        } catch (error: ApiException) {
            return if (error.status == 401) Result.WrongCode else Result.Failed(error.message ?: "配对失败")
        } catch (error: Exception) {
            return Result.Failed(error.message ?: "连不上电脑")
        }

        val secret = try {
            val plain = Crypto.decrypt(
                pairKey,
                response.getString("nonce"),
                response.getString("ct"),
                Crypto.PAIR_AAD_PREFIX + androidId,
            )
            JSONObject(String(plain, Charsets.UTF_8))
        } catch (error: Exception) {
            return Result.Failed("配对数据校验失败")
        }

        return Result.Success(
            pcName = secret.optString("pc_name").ifEmpty { info.optString("name", "电脑") },
            token = secret.getString("token"),
            linkKeyBase64 = secret.getString("link_key"),
        )
    }
}
