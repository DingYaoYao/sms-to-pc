package com.smslink

import android.content.Context
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject

/** 所有状态都存在应用私有的 SharedPreferences 里，不会外泄。 */
class Prefs(context: Context) {
    private val sp = context.applicationContext.getSharedPreferences("smslink", Context.MODE_PRIVATE)

    var host: String
        get() = sp.getString(KEY_HOST, "") ?: ""
        set(value) = sp.edit().putString(KEY_HOST, value).apply()

    var port: Int
        get() = sp.getInt(KEY_PORT, Net.DEFAULT_PORT)
        set(value) = sp.edit().putInt(KEY_PORT, value).apply()

    var token: String
        get() = sp.getString(KEY_TOKEN, "") ?: ""
        set(value) = sp.edit().putString(KEY_TOKEN, value).apply()

    var pcName: String
        get() = sp.getString(KEY_PC_NAME, "") ?: ""
        set(value) = sp.edit().putString(KEY_PC_NAME, value).apply()

    var enabled: Boolean
        get() = sp.getBoolean(KEY_ENABLED, true)
        set(value) = sp.edit().putBoolean(KEY_ENABLED, value).apply()

    /** 首次启动的授权引导是否走完，走完就不再自动弹。 */
    var setupDone: Boolean
        get() = sp.getBoolean(KEY_SETUP_DONE, false)
        set(value) = sp.edit().putBoolean(KEY_SETUP_DONE, value).apply()

    /** 是否已经弹过一次短信权限申请，用来判断"不再询问"。 */
    var smsPrompted: Boolean
        get() = sp.getBoolean(KEY_SMS_PROMPTED, false)
        set(value) = sp.edit().putBoolean(KEY_SMS_PROMPTED, value).apply()

    var lastError: String
        get() = sp.getString(KEY_LAST_ERROR, "") ?: ""
        set(value) = sp.edit().putString(KEY_LAST_ERROR, value).apply()

    var sentCount: Int
        get() = sp.getInt(KEY_SENT_COUNT, 0)
        set(value) = sp.edit().putInt(KEY_SENT_COUNT, value).apply()

    var lastSentAt: Long
        get() = sp.getLong(KEY_LAST_SENT_AT, 0L)
        set(value) = sp.edit().putLong(KEY_LAST_SENT_AT, value).apply()

    val androidId: String
        get() {
            val existing = sp.getString(KEY_ANDROID_ID, null)
            if (!existing.isNullOrEmpty()) return existing
            val created = Crypto.randomHex(8)
            sp.edit().putString(KEY_ANDROID_ID, created).apply()
            return created
        }

    val paired: Boolean
        get() = host.isNotEmpty() && token.isNotEmpty() && linkKeyBase64 != null

    var linkKeyBase64: String?
        get() = sp.getString(KEY_LINK_KEY, null)
        set(value) = sp.edit().putString(KEY_LINK_KEY, value).apply()

    val linkKeyBytes: ByteArray?
        get() = linkKeyBase64?.let {
            runCatching { Base64.decode(it, Base64.NO_WRAP) }.getOrNull()?.takeIf { key -> key.size == 32 }
        }

    fun saveLink(host: String, port: Int, token: String, linkKeyBase64: String, pcName: String) {
        sp.edit()
            .putString(KEY_HOST, host)
            .putInt(KEY_PORT, port)
            .putString(KEY_TOKEN, token)
            .putString(KEY_LINK_KEY, linkKeyBase64)
            .putString(KEY_PC_NAME, pcName)
            .putBoolean(KEY_ENABLED, true)
            .putString(KEY_LAST_ERROR, "")
            .apply()
    }

    fun clearLink() {
        sp.edit()
            .remove(KEY_HOST)
            .remove(KEY_PORT)
            .remove(KEY_TOKEN)
            .remove(KEY_LINK_KEY)
            .remove(KEY_PC_NAME)
            .remove(KEY_PENDING)
            .remove(KEY_LAST_ERROR)
            .apply()
    }

    fun pending(): MutableList<JSONObject> {
        val raw = sp.getString(KEY_PENDING, null) ?: return mutableListOf()
        return runCatching {
            val array = JSONArray(raw)
            MutableList(array.length()) { index -> array.getJSONObject(index) }
        }.getOrElse { mutableListOf() }
    }

    fun setPending(items: List<JSONObject>) {
        val array = JSONArray()
        items.forEach { array.put(it) }
        sp.edit().putString(KEY_PENDING, array.toString()).apply()
    }

    /** 入队；同一条短信重复投递时返回 false，避免重复上报。 */
    fun enqueue(item: JSONObject): Boolean {
        val items = pending()
        val id = item.optString("id")
        if (items.any { it.optString("id") == id }) return false
        items.add(item)
        while (items.size > MAX_PENDING) items.removeAt(0)
        setPending(items)
        return true
    }

    companion object {
        private const val KEY_HOST = "host"
        private const val KEY_PORT = "port"
        private const val KEY_TOKEN = "token"
        private const val KEY_LINK_KEY = "link_key"
        private const val KEY_PC_NAME = "pc_name"
        private const val KEY_ANDROID_ID = "android_id"
        private const val KEY_ENABLED = "enabled"
        private const val KEY_SETUP_DONE = "setup_done"
        private const val KEY_SMS_PROMPTED = "sms_prompted"
        private const val KEY_PENDING = "pending"
        private const val KEY_SENT_COUNT = "sent_count"
        private const val KEY_LAST_SENT_AT = "last_sent_at"
        private const val KEY_LAST_ERROR = "last_error"
        private const val MAX_PENDING = 200
    }
}
