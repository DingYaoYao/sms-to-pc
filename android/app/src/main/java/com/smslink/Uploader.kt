package com.smslink

import android.content.Context
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** 把待发短信排队、加密、发送；失败就留在队列里等下次重试。 */
object Uploader {
    private val executor = Executors.newSingleThreadExecutor()
    private val busy = AtomicBoolean(false)

    fun flushAsync(context: Context, done: (() -> Unit)? = null) {
        if (!busy.compareAndSet(false, true)) {
            done?.invoke()
            return
        }
        val appContext = context.applicationContext
        executor.execute {
            try {
                flushBlocking(appContext)
            } finally {
                busy.set(false)
                done?.invoke()
            }
        }
    }

    private fun flushBlocking(context: Context) {
        val prefs = Prefs(context)
        val linkKey = prefs.linkKeyBytes ?: return
        if (!prefs.paired) return

        var sent = 0
        while (true) {
            val pending = prefs.pending()
            if (pending.isEmpty()) break
            val item = pending.first()
            val ok = try {
                sendOne(prefs, linkKey, item)
                true
            } catch (error: Exception) {
                prefs.lastError = error.message ?: error.javaClass.simpleName
                false
            }
            if (!ok) break
            pending.removeAt(0)
            prefs.setPending(pending)
            sent += 1
        }

        if (sent > 0) {
            prefs.sentCount = prefs.sentCount + sent
            prefs.lastSentAt = System.currentTimeMillis()
            prefs.lastError = ""
        }
    }

    private fun sendOne(prefs: Prefs, linkKey: ByteArray, item: JSONObject) {
        val token = prefs.token
        val payload = JSONObject()
            .put("kind", "sms")
            .put("id", item.optString("id"))
            .put("from", item.optString("from", "未知"))
            .put("body", item.optString("body"))
            .put("ts", item.optLong("ts", System.currentTimeMillis()))
            .put("slot", item.optInt("slot", 0))

        val (nonce, cipherText) = Crypto.encrypt(
            linkKey,
            payload.toString().toByteArray(Charsets.UTF_8),
            Crypto.SMS_AAD_PREFIX + token,
        )
        val envelope = JSONObject()
            .put("token", token)
            .put("nonce", nonce)
            .put("ct", cipherText)
        Net.postJson(prefs.host, prefs.port, "/api/sms", envelope)
    }

    /** 生成一条测试消息，用来确认链路是否通（不是真实短信）。 */
    fun sendTestMessage(context: Context, from: String, body: String) {
        val prefs = Prefs(context)
        val now = System.currentTimeMillis()
        val id = "test-${Crypto.shortHash("$from|$body|$now")}"
        prefs.enqueue(
            JSONObject()
                .put("id", id)
                .put("from", from)
                .put("body", body)
                .put("ts", now)
                .put("slot", 0),
        )
    }
}
