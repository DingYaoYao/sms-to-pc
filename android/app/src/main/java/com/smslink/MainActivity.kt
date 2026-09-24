package com.smslink

import android.Manifest
import android.animation.ObjectAnimator
import android.animation.PropertyValuesHolder
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.Editable
import android.text.TextWatcher
import android.text.format.DateUtils
import android.view.View
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast

/**
 * 单页应用：配对、连接状态、权限检查、测试全在这一屏里完成。
 * 配对时用户只需要输入电脑上显示的 4 位配对码，电脑地址由自动扫描得到。
 */
class MainActivity : Activity() {

    private lateinit var prefs: Prefs

    // 配对区
    private lateinit var cardPair: View
    private lateinit var inputCode: EditText
    private lateinit var buttonConnect: Button
    private lateinit var pairDot: View
    private lateinit var pairStatus: TextView
    private lateinit var manualBox: View
    private lateinit var inputHost: EditText
    private lateinit var buttonConnectManual: Button

    // 已连接区
    private lateinit var cardConnected: View
    private lateinit var connectedTitle: TextView
    private lateinit var connectedSub: TextView
    private lateinit var statsView: TextView
    private lateinit var buttonTest: Button
    private lateinit var buttonUnpair: Button

    // 权限区
    private lateinit var permSmsDot: View
    private lateinit var permSmsStatus: TextView
    private lateinit var permBgDot: View
    private lateinit var permBgStatus: TextView
    private lateinit var autostartLink: TextView

    private val ui = Handler(Looper.getMainLooper())
    private val scanned = mutableListOf<Net.Found>()
    private var scanning = false
    private var pairing = false
    private var pairPulse: ObjectAnimator? = null
    private var smsBreath: ObjectAnimator? = null
    private var bgBreath: ObjectAnimator? = null
    private var setupFlowActive = false
    private var waitingFor = STEP_NONE

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        prefs = Prefs(this)

        cardPair = findViewById(R.id.card_pair)
        inputCode = findViewById(R.id.input_code)
        buttonConnect = findViewById(R.id.button_connect)
        pairDot = findViewById(R.id.pair_dot)
        pairStatus = findViewById(R.id.pair_status)
        manualBox = findViewById(R.id.manual_box)
        inputHost = findViewById(R.id.input_host)
        buttonConnectManual = findViewById(R.id.button_connect_manual)

        cardConnected = findViewById(R.id.card_connected)
        connectedTitle = findViewById(R.id.connected_title)
        connectedSub = findViewById(R.id.connected_sub)
        statsView = findViewById(R.id.stats)
        buttonTest = findViewById(R.id.button_test)
        buttonUnpair = findViewById(R.id.button_unpair)

        permSmsDot = findViewById(R.id.perm_sms_dot)
        permSmsStatus = findViewById(R.id.perm_sms_status)
        permBgDot = findViewById(R.id.perm_bg_dot)
        permBgStatus = findViewById(R.id.perm_bg_status)
        autostartLink = findViewById(R.id.autostart_link)

        buttonConnect.setOnClickListener { connect() }
        buttonConnectManual.setOnClickListener { connect() }
        findViewById<View>(R.id.manual_toggle).setOnClickListener { toggleManual() }

        inputCode.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit

            override fun afterTextChanged(s: Editable?) {
                val code = s?.toString().orEmpty()
                setConnectEnabled(code.length == CODE_LENGTH && !pairing)
                // 输满 4 位就自动连：普通人不用再去找按钮。
                if (code.length == CODE_LENGTH && !pairing && !manualBox.isShown) {
                    hideKeyboard()
                    connect()
                }
            }
        })

        // 自动转发默认开启且不提供开关，按使用要求固定打开。
        prefs.enabled = true
        buttonTest.setOnClickListener { sendTest() }
        buttonUnpair.setOnClickListener { confirmUnpair() }
        findViewById<View>(R.id.button_about).setOnClickListener {
            startActivity(Intent(this, AboutActivity::class.java))
        }

        findViewById<View>(R.id.perm_sms_row).setOnClickListener { askSms() }
        findViewById<View>(R.id.perm_bg_row).setOnClickListener {
            setupFlowActive = true
            askBattery()
        }
        autostartLink.setOnClickListener { openAutostart() }

        setConnectEnabled(false)
        FlushJobService.schedule(this)
        animateEntrance()
        if (!prefs.setupDone) showSetupIntro()
        if (prefs.paired) refresh() else startScan()
    }

    override fun onResume() {
        super.onResume()
        if (setupFlowActive && waitingFor == STEP_BATTERY) {
            waitingFor = STEP_NONE
            askAutostart()
        }
        refresh()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_SMS) return
        toast(if (Permissions.hasSms(this)) "已开启短信权限" else "没有短信权限就收不到短信")
        if (setupFlowActive) askBattery() else refresh()
    }

    /* ---------------- 自动扫描 + 配对 ---------------- */

    private fun startScan() {
        if (scanning || pairing) return
        scanning = true
        setPairStatus("正在自动搜索电脑…", R.color.brand, pulsing = true)
        Thread {
            val found = runCatching { Net.discover() }.getOrElse { emptyList() }
            ui.post {
                scanning = false
                scanned.clear()
                scanned.addAll(found)
                if (pairing) return@post
                if (found.isEmpty()) {
                    setPairStatus("没搜到电脑，点下面手动填地址", R.color.warn, pulsing = false)
                } else {
                    setPairStatus("已找到电脑，输入配对码就能连", R.color.ok, pulsing = false)
                }
            }
        }.start()
    }

    /** 候选电脑：手填地址优先；否则用扫描结果，扫描为空就现扫一次。 */
    private fun candidates(): List<Net.Found> {
        parseManual()?.let { (host, port) ->
            return listOf(Net.Found(name = "电脑", host = host, port = port, id = "manual"))
        }
        if (scanned.isEmpty()) {
            val found = runCatching { Net.discover() }.getOrElse { emptyList() }
            scanned.clear()
            scanned.addAll(found)
        }
        return scanned.toList()
    }

    /** 允许手填 "192.168.1.7" 或 "192.168.1.7:8899"。 */
    private fun parseManual(): Pair<String, Int>? {
        val raw = inputHost.text.toString().trim()
        if (raw.isEmpty()) return null
        val cleaned = raw.removePrefix("http://").removePrefix("https://").substringBefore('/')
        val host = cleaned.substringBefore(':').trim()
        if (host.isEmpty()) return null
        val port = cleaned.substringAfter(':', "").trim().toIntOrNull() ?: Net.DEFAULT_PORT
        return host to port
    }

    private fun connect() {
        if (pairing || prefs.paired) return
        val code = inputCode.text.toString().trim()
        if (code.length != CODE_LENGTH || code.any { !it.isDigit() }) {
            setPairStatus("配对码是电脑上显示的 $CODE_LENGTH 位数字", R.color.warn, pulsing = false)
            shake(inputCode)
            return
        }

        pairing = true
        setConnectEnabled(false)
        buttonConnectManual.isEnabled = false
        setPairStatus("正在连接…", R.color.brand, pulsing = true)

        Thread {
            val targets = candidates()
            if (targets.isEmpty()) {
                ui.post {
                    pairing = false
                    setConnectEnabled(true)
                    buttonConnectManual.isEnabled = true
                    setPairStatus("没找到电脑，请确认手机和电脑连的是同一个 WiFi", R.color.warn, pulsing = false)
                }
                return@Thread
            }

            var wrongCode = false
            var lastError: String? = null
            for (target in targets) {
                when (val result = Pairing.attempt(target.host, target.port, code, prefs.androidId, deviceName())) {
                    is Pairing.Result.Success -> {
                        prefs.saveLink(
                            host = target.host,
                            port = target.port,
                            token = result.token,
                            linkKeyBase64 = result.linkKeyBase64,
                            pcName = result.pcName,
                        )
                        ui.post { onPaired(result.pcName) }
                        return@Thread
                    }
                    Pairing.Result.WrongCode -> wrongCode = true
                    is Pairing.Result.Failed -> lastError = result.message
                }
            }

            ui.post {
                pairing = false
                setConnectEnabled(true)
                buttonConnectManual.isEnabled = true
                if (wrongCode) {
                    setPairStatus("配对码不对，请核对电脑上显示的数字", R.color.danger, pulsing = false)
                    shake(inputCode)
                } else {
                    setPairStatus(lastError ?: "连不上电脑，请确认在同一个 WiFi", R.color.warn, pulsing = false)
                }
            }
        }.start()
    }

    private fun onPaired(pcName: String) {
        pairing = false
        toast("已连接到 $pcName")
        crossFade(cardPair, cardConnected) { refresh() }
    }

    private fun crossFade(from: View, to: View, onDone: () -> Unit) {
        from.animate().alpha(0f).setDuration(150).withEndAction {
            from.visibility = View.GONE
            from.alpha = 1f
            to.visibility = View.VISIBLE
            to.alpha = 0f
            to.translationY = 16f
            to.animate().alpha(1f).translationY(0f).setDuration(240)
                .withEndAction(onDone)
                .start()
        }.start()
    }

    private fun deviceName(): String = (Build.MODEL ?: "Android").take(24)

    private fun toggleManual() {
        val show = manualBox.visibility != View.VISIBLE
        manualBox.visibility = if (show) View.VISIBLE else View.GONE
        if (show) {
            manualBox.alpha = 0f
            manualBox.animate().alpha(1f).setDuration(180).start()
            inputHost.requestFocus()
        }
    }

    /* ---------------- 界面刷新 ---------------- */

    private fun refresh() {
        if (prefs.paired) {
            if (cardPair.visibility == View.VISIBLE) {
                cardPair.visibility = View.GONE
                cardPair.alpha = 1f
                cardConnected.visibility = View.VISIBLE
            }
            connectedTitle.text = "已连接到 ${prefs.pcName.ifEmpty { "电脑" }}"
            connectedSub.text = "手机收到新短信会自动出现在电脑上"
            val parts = mutableListOf("已转发 ${prefs.sentCount} 条")
            if (prefs.lastSentAt > 0) {
                parts.add(
                    "最近 " + DateUtils.getRelativeTimeSpanString(
                        prefs.lastSentAt,
                        System.currentTimeMillis(),
                        DateUtils.MINUTE_IN_MILLIS,
                    ),
                )
            }
            val pendingCount = prefs.pending().size
            if (pendingCount > 0) parts.add("待发送 $pendingCount 条")
            statsView.text = parts.joinToString(" · ")
        } else {
            cardConnected.visibility = View.GONE
            cardPair.visibility = View.VISIBLE
        }

        refreshPermissions()
        if (!pairing && !scanning) settlePairDot()
    }

    private fun refreshPermissions() {
        smsBreath?.cancel()
        smsBreath = null
        bgBreath?.cancel()
        bgBreath = null
        permSmsDot.alpha = 1f
        permBgDot.alpha = 1f

        val sms = Permissions.hasSms(this)
        stylePermissionRow(permSmsDot, permSmsStatus, sms)
        if (!sms) smsBreath = breathing(permSmsDot)

        val background = Permissions.ignoresBatteryOptimizations(this)
        stylePermissionRow(permBgDot, permBgStatus, background)
        if (!background) bgBreath = breathing(permBgDot)
    }

    /** 已开启 = 绿色圆点 + “已开启”；没开启 = 橙色呼吸点 + “去开启 ›”。 */
    private fun stylePermissionRow(dot: View, status: TextView, granted: Boolean) {
        if (granted) {
            tint(dot, R.color.ok)
            status.text = "已开启"
            status.setTextColor(getColor(R.color.ok))
        } else {
            tint(dot, R.color.warn)
            status.text = "去开启 ›"
            status.setTextColor(getColor(R.color.warn))
        }
    }

    private fun tint(view: View, colorRes: Int) {
        view.backgroundTintList = ColorStateList.valueOf(getColor(colorRes))
    }

    private fun setPairStatus(text: String, colorRes: Int, pulsing: Boolean) {
        pairStatus.text = text
        pairStatus.setTextColor(getColor(colorRes))
        if (pulsing) startPulse() else settlePairDot()
    }

    private fun settlePairDot() {
        if (pairing || scanning) return
        pairPulse?.cancel()
        pairPulse = null
        pairDot.scaleX = 1f
        pairDot.scaleY = 1f
        pairDot.alpha = 1f
        when {
            prefs.paired -> tint(pairDot, R.color.ok)
            scanned.isNotEmpty() -> tint(pairDot, R.color.ok)
            else -> tint(pairDot, R.color.muted)
        }
    }

    private fun startPulse() {
        if (pairPulse != null) return
        tint(pairDot, R.color.brand)
        pairPulse = ObjectAnimator.ofPropertyValuesHolder(
            pairDot,
            PropertyValuesHolder.ofFloat(View.SCALE_X, 1f, 1.7f),
            PropertyValuesHolder.ofFloat(View.SCALE_Y, 1f, 1.7f),
            PropertyValuesHolder.ofFloat(View.ALPHA, 1f, 0.3f),
        ).apply {
            duration = 700
            repeatCount = ObjectAnimator.INFINITE
            repeatMode = ObjectAnimator.REVERSE
            start()
        }
    }

    private fun breathing(view: View): ObjectAnimator =
        ObjectAnimator.ofFloat(view, View.ALPHA, 1f, 0.4f).apply {
            duration = 900
            repeatCount = ObjectAnimator.INFINITE
            repeatMode = ObjectAnimator.REVERSE
            start()
        }

    private fun animateEntrance() {
        val views = mutableListOf(findViewById<View>(R.id.header), cardPair)
        if (prefs.paired) views.add(cardConnected)
        views.add(findViewById(R.id.card_perms))
        views.forEachIndexed { index, view ->
            view.alpha = 0f
            view.translationY = 28f
            view.animate()
                .alpha(1f)
                .translationY(0f)
                .setStartDelay(40L + index * 70L)
                .setDuration(300)
                .start()
        }
    }

    private fun setConnectEnabled(enabled: Boolean) {
        buttonConnect.isEnabled = enabled
        buttonConnect.alpha = if (enabled) 1f else 0.45f
    }

    private fun shake(view: View) {
        val distance = 9f * resources.displayMetrics.density
        ObjectAnimator.ofFloat(
            view,
            View.TRANSLATION_X,
            0f, distance, -distance, distance * 0.6f, -distance * 0.6f, 0f,
        ).apply { duration = 320 }.start()
    }

    private fun hideKeyboard() {
        val manager = getSystemService(INPUT_METHOD_SERVICE) as? InputMethodManager ?: return
        manager.hideSoftInputFromWindow(inputCode.windowToken, 0)
    }

    private fun toast(text: String) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
    }

    /* ---------------- 权限引导 ---------------- */

    private fun showSetupIntro() {
        AlertDialog.Builder(this)
            .setTitle("先开两个开关")
            .setMessage("① 短信权限 —— 不给自己收不到短信\n② 后台运行 —— 不然锁屏后可能被系统限制")
            .setNegativeButton("稍后", null)
            .setPositiveButton("好") { _, _ ->
                setupFlowActive = true
                askSms()
            }
            .show()
    }

    private fun askSms() {
        if (Permissions.hasSms(this)) {
            if (setupFlowActive) askBattery() else refresh()
            return
        }
        val blocked = prefs.smsPrompted && !shouldShowRequestPermissionRationale(Manifest.permission.RECEIVE_SMS)
        if (blocked) {
            setupFlowActive = false
            waitingFor = STEP_NONE
            toast("请在系统设置里打开“短信”权限")
            runCatching {
                startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")),
                )
            }
            return
        }
        prefs.smsPrompted = true
        waitingFor = STEP_SMS
        requestPermissions(arrayOf(Manifest.permission.RECEIVE_SMS), REQUEST_SMS)
    }

    private fun askBattery() {
        if (Permissions.ignoresBatteryOptimizations(this)) {
            askAutostart()
            return
        }
        waitingFor = STEP_BATTERY
        val started = runCatching { startActivity(Permissions.batteryOptimizationIntent(this)) }.isSuccess
        if (!started) {
            waitingFor = STEP_NONE
            askAutostart()
        }
    }

    private fun askAutostart() {
        AlertDialog.Builder(this)
            .setTitle("顺手看一眼自启动")
            .setMessage(
                "如果系统管家里有“自启动 / 后台运行”开关，建议也允许。\n\n" +
                    "这一项是可选的：多数手机靠上面两项就够了，小米这类系统开了会更稳。",
            )
            .setNegativeButton("不用了") { _, _ -> finishSetup() }
            .setPositiveButton("去看看") { _, _ ->
                runCatching { startActivity(Permissions.autostartIntent(this)) }
                finishSetup()
            }
            .show()
    }

    private fun openAutostart() {
        runCatching { startActivity(Permissions.autostartIntent(this)) }
            .onFailure { toast("这台手机没有自启动设置页") }
    }

    private fun finishSetup() {
        setupFlowActive = false
        waitingFor = STEP_NONE
        prefs.setupDone = true
        refresh()
    }

    /* ---------------- 其它操作 ---------------- */

    private fun sendTest() {
        if (!prefs.paired) return
        toast("正在发送测试消息…")
        val stamp = android.text.format.DateFormat.format("HH:mm:ss", System.currentTimeMillis())
        Uploader.sendTestMessage(this, "手机测试", "这是一条来自手机的消息 · $stamp")
        Uploader.flushAsync(this) {
            runOnUiThread {
                toast(if (prefs.lastError.isEmpty()) "已发送，去电脑上看看" else "发送失败：${prefs.lastError}")
                refresh()
            }
        }
    }

    private fun confirmUnpair() {
        AlertDialog.Builder(this)
            .setTitle("解除配对？")
            .setMessage("解除后手机不会再往这台电脑发短信。")
            .setNegativeButton("取消", null)
            .setPositiveButton("解除") { _, _ ->
                prefs.clearLink()
                inputCode.setText("")
                scanned.clear()
                cardConnected.visibility = View.GONE
                cardPair.visibility = View.VISIBLE
                cardPair.alpha = 0f
                cardPair.animate().alpha(1f).setDuration(220).start()
                toast("已解除配对")
                startScan()
            }
            .show()
    }

    companion object {
        private const val REQUEST_SMS = 101
        private const val STEP_NONE = 0
        private const val STEP_SMS = 1
        private const val STEP_BATTERY = 2
        private const val CODE_LENGTH = 4
    }
}
