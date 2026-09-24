package com.smslink

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import android.widget.TextView
import android.widget.Toast

/** 「关于」页面：开发者信息、赞赏码、使用声明。 */
class AboutActivity : Activity() {

    private val githubUrl = "https://github.com/DingYaoYao/sms-to-pc"

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_about)

        findViewById<TextView>(R.id.about_version).text = "v${versionName()}"
        findViewById<View>(R.id.button_back).setOnClickListener { finish() }

        // 点邮箱直接调用邮件 App
        findViewById<TextView>(R.id.about_email).setOnClickListener {
            val intent = Intent(Intent.ACTION_SENDTO, Uri.parse("mailto:dog8520963@163.com"))
            runCatching { startActivity(intent) }
                .onFailure { Toast.makeText(this, "没有找到邮件应用", Toast.LENGTH_SHORT).show() }
        }

        // 点 GitHub 用浏览器打开
        findViewById<TextView>(R.id.about_github).apply {
            text = "GitHub：" + githubUrl.removePrefix("https://")
            setOnClickListener {
                runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(githubUrl))) }
                    .onFailure { Toast.makeText(this@AboutActivity, "没有找到浏览器", Toast.LENGTH_SHORT).show() }
            }
        }

        animateIn()
    }

    private fun versionName(): String = runCatching {
        packageManager.getPackageInfo(packageName, 0).versionName ?: ""
    }.getOrDefault("")

    /** 和主界面一致：卡片依次淡入上浮。 */
    private fun animateIn() {
        val root = findViewById<ViewGroup>(R.id.about_root)
        for (index in 0 until root.childCount) {
            val child = root.getChildAt(index) ?: continue
            child.alpha = 0f
            child.translationY = 30f
            child.animate()
                .alpha(1f)
                .translationY(0f)
                .setStartDelay(40L + index * 70L)
                .setDuration(300)
                .start()
        }
    }
}
