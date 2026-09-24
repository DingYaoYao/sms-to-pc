package com.smslink

import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.net.URL
import java.util.Collections

class ApiException(val status: Int, message: String) : IOException(message)

object Net {
    const val DEFAULT_PORT = 8789
    const val DISCOVERY_PORT = 8788

    private const val DISCOVER_REQUEST = "SMSLINK-DISCOVER-V1"
    private const val ANNOUNCE_PREFIX = "SMSLINK-ANNOUNCE-V1 "

    data class Found(val name: String, val host: String, val port: Int, val id: String)

    fun normalizeHost(raw: String): String {
        var host = raw.trim()
        host = host.removePrefix("http://").removePrefix("https://")
        host = host.substringBefore('/')
        host = host.substringBefore(':')
        return host.trim()
    }

    fun getJson(host: String, port: Int, path: String, timeoutMs: Int = 5000): JSONObject {
        val connection = (URL("http://$host:$port$path").openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = timeoutMs
            readTimeout = timeoutMs
        }
        return try {
            val code = connection.responseCode
            val text = readText(connection, code)
            val json = runCatching { JSONObject(text) }.getOrNull()
            if (code !in 200..299 || json == null || !json.optBoolean("ok")) {
                throw ApiException(code, json?.optString("error") ?: "电脑端返回异常（HTTP $code）")
            }
            json
        } finally {
            connection.disconnect()
        }
    }

    fun postJson(host: String, port: Int, path: String, body: JSONObject, timeoutMs: Int = 8000): JSONObject {
        val payload = body.toString().toByteArray(Charsets.UTF_8)
        val connection = (URL("http://$host:$port$path").openConnection() as HttpURLConnection).apply {
            requestMethod = "POST"
            doOutput = true
            connectTimeout = timeoutMs
            readTimeout = timeoutMs
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
            setFixedLengthStreamingMode(payload.size)
        }
        return try {
            connection.outputStream.use { it.write(payload) }
            val code = connection.responseCode
            val text = readText(connection, code)
            val json = runCatching { JSONObject(text) }.getOrNull()
            if (code !in 200..299 || json == null || !json.optBoolean("ok")) {
                throw ApiException(code, json?.optString("error") ?: "电脑端返回异常（HTTP $code）")
            }
            json
        } finally {
            connection.disconnect()
        }
    }

    private fun readText(connection: HttpURLConnection, code: Int): String {
        val stream = if (code in 200..299) connection.inputStream else connection.errorStream
        return stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
    }

    /**
     * 向局域网广播，自动找出正在运行 PC 端的电脑。
     * 收到第一台之后只要再安静 [settleMs] 毫秒就返回，避免让用户干等。
     */
    fun discover(timeoutMs: Int = 3000, settleMs: Int = 500): List<Found> {
        val found = LinkedHashMap<String, Found>()
        val socket = DatagramSocket()
        socket.broadcast = true
        socket.soTimeout = 400
        try {
            val targets = mutableListOf(InetAddress.getByName("255.255.255.255"))
            runCatching {
                for (nif in Collections.list(NetworkInterface.getNetworkInterfaces())) {
                    if (!nif.isUp || nif.isLoopback) continue
                    for (address in nif.interfaceAddresses) {
                        address.broadcast?.let { targets.add(it) }
                    }
                }
            }
            val payload = DISCOVER_REQUEST.toByteArray(Charsets.UTF_8)
            for (target in targets.distinct()) {
                runCatching {
                    socket.send(DatagramPacket(payload, payload.size, target, DISCOVERY_PORT))
                }
            }

            val deadline = System.currentTimeMillis() + timeoutMs
            var lastHitAt = 0L
            val buffer = ByteArray(2048)
            while (System.currentTimeMillis() < deadline) {
                if (found.isNotEmpty() && System.currentTimeMillis() - lastHitAt > settleMs) break
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                if (!text.startsWith(ANNOUNCE_PREFIX)) continue
                runCatching {
                    val json = JSONObject(text.substring(ANNOUNCE_PREFIX.length))
                    val id = json.optString("id")
                    if (id.isNotEmpty()) {
                        found[id] = Found(
                            name = json.optString("name", "电脑"),
                            host = packet.address?.hostAddress ?: "",
                            port = json.optInt("port", DEFAULT_PORT),
                            id = id,
                        )
                        lastHitAt = System.currentTimeMillis()
                    }
                }
            }
        } finally {
            runCatching { socket.close() }
        }
        return found.values.toList()
    }
}
