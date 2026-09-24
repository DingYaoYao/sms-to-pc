package com.smslink

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings

/**
 * 两类"后台能不能收到短信"相关的授权：
 *  1. 短信权限（运行时权限，必须用户点同意）
 *  2. 后台运行：先把 App 加进电池优化白名单；国产 ROM 还有各自的自启动开关，
 *     没有公开 API，只能尽量跳转到对应设置页让用户手动打开。
 */
object Permissions {

    fun hasSms(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.RECEIVE_SMS) == PackageManager.PERMISSION_GRANTED

    fun ignoresBatteryOptimizations(context: Context): Boolean {
        val powerManager = context.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return false
        return powerManager.isIgnoringBatteryOptimizations(context.packageName)
    }

    fun batteryOptimizationIntent(context: Context): Intent =
        Intent(
            Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
            Uri.parse("package:${context.packageName}"),
        )

    // 各厂商的自启动/后台管理页面，按常见程度排序；不存在的会被跳过。
    private val AUTOSTART_COMPONENTS = listOf(
        "com.miui.securitycenter/com.miui.permcenter.autostart.AutoStartManagementActivity",
        "com.huawei.systemmanager/com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
        "com.huawei.systemmanager/com.huawei.systemmanager.optimize.process.ProtectActivity",
        "com.hihonor.systemmanager/com.hihonor.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
        "com.coloros.safecenter/com.coloros.safecenter.permission.startup.StartupAppListActivity",
        "com.coloros.safecenter/com.coloros.safecenter.startupapp.StartupAppListActivity",
        "com.oppo.safe/com.oppo.safe.permission.startup.StartupAppListActivity",
        "com.vivo.permissionmanager/com.vivo.permissionmanager.activity.BgStartUpManagerActivity",
        "com.iqoo.secure/com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity",
        "com.letv.android.letvsafe/com.letv.android.letvsafe.AutobootManageActivity",
        "com.samsung.android.lool/com.samsung.android.sm.ui.battery.BatteryActivity",
        "com.meizu.safe/com.meizu.safe.permission.SmartBGActivity",
        "com.asus.mobilemanager/com.asus.mobilemanager.autostart.AutoStartActivity",
    )

    /** 能跳就跳到"自启动"页面，否则退回到本应用的详情页。 */
    fun autostartIntent(context: Context): Intent {
        for (entry in AUTOSTART_COMPONENTS) {
            val parts = entry.split('/')
            if (parts.size != 2) continue
            val intent = Intent().setComponent(ComponentName(parts[0], parts[1]))
            val resolvable = runCatching {
                context.packageManager.resolveActivity(intent, PackageManager.MATCH_DEFAULT_ONLY)
            }.getOrNull()
            if (resolvable != null) return intent
        }
        return Intent(
            Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
            Uri.parse("package:${context.packageName}"),
        )
    }
}
