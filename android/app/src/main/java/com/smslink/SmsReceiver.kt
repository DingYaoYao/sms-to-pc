package com.smslink

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Telephony
import org.json.JSONObject

/** 收到新短信：立刻入队，然后尝试加密转发到电脑。 */
class SmsReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Telephony.Sms.Intents.SMS_RECEIVED_ACTION) return

        val prefs = Prefs(context)
        if (!prefs.paired || !prefs.enabled) return

        val parts = Telephony.Sms.Intents.getMessagesFromIntent(intent) ?: return
        if (parts.isEmpty()) return

        // 长短信会被拆成多段，这里按“发送方 + 时间”拼回一整条。
        val bodies = LinkedHashMap<String, StringBuilder>()
        val meta = HashMap<String, Pair<String, Long>>()
        for (part in parts) {
            if (part == null) continue
            val from = part.displayOriginatingAddress ?: part.originatingAddress ?: "未知号码"
            val timestamp = if (part.timestampMillis > 0) part.timestampMillis else System.currentTimeMillis()
            val key = "$from|$timestamp"
            bodies.getOrPut(key) { StringBuilder() }.append(part.displayMessageBody ?: "")
            meta[key] = from to timestamp
        }

        var queued = false
        for ((key, builder) in bodies) {
            val (from, timestamp) = meta[key] ?: continue
            val body = builder.toString()
            if (body.isBlank()) continue
            val id = "sms-${Crypto.shortHash("$from|$body|$timestamp")}"
            val item = JSONObject()
                .put("id", id)
                .put("from", from)
                .put("body", body)
                .put("ts", timestamp)
                .put("slot", 0)
            if (prefs.enqueue(item)) queued = true
        }
        if (!queued) return

        val pendingResult = goAsync()
        Uploader.flushAsync(context.applicationContext) { pendingResult.finish() }
    }
}
