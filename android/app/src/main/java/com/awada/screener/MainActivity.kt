package com.awada.screener

import android.Manifest
import android.content.Intent
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.awada.screener.ui.theme.ScreenerTheme

class MainActivity : ComponentActivity() {
    private val prefs by lazy { getSharedPreferences("screener", MODE_PRIVATE) }
    private val ipState = mutableStateOf(DEFAULT_IP)
    private val running = mutableStateOf(false)

    private val notifyPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { startCapture() }

    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val data = result.data
        if (result.resultCode != RESULT_OK || data == null) return@registerForActivityResult
        val intent = Intent(this, ShotService::class.java).apply {
            putExtra(ShotService.EXTRA_CODE, result.resultCode)
            putExtra(ShotService.EXTRA_DATA, data)
            putExtra(ShotService.EXTRA_HOST, ipState.value.trim())
        }
        ContextCompat.startForegroundService(this, intent)
        running.value = true
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val saved = prefs.getString(KEY_IP, DEFAULT_IP) ?: DEFAULT_IP
        ipState.value = if (saved == OLD_IP) DEFAULT_IP else saved
        setContent {
            val ip = ipState.value
            val on = running.value
            ScreenerTheme {
                Scaffold(modifier = Modifier.fillMaxSize()) { padding ->
                    Column(
                        modifier = Modifier
                            .padding(padding)
                            .padding(24.dp)
                            .fillMaxSize(),
                        verticalArrangement = Arrangement.spacedBy(16.dp)
                    ) {
                        Text("Скрин телефона сразу в буфер компьютера.")
                        OutlinedTextField(
                            value = ip,
                            onValueChange = { ipState.value = it },
                            label = { Text("IP компьютера") },
                            singleLine = true
                        )
                        Button(onClick = { onPrimary() }) {
                            Text(if (on) "Убрать кнопку" else "Показать кнопку")
                        }
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            Text(
                                "Один раз разрешите кнопку поверх окон и съёмку экрана. Потом на любом экране жмите",
                                modifier = Modifier.weight(1f)
                            )
                            HelpButton()
                        }
                    }
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        running.value = ShotService.running
    }

    private fun onPrimary() {
        val host = ipState.value.trim().ifEmpty { DEFAULT_IP }
        ipState.value = host
        prefs.edit().putString(KEY_IP, host).apply()
        if (ShotService.running) {
            startService(Intent(this, ShotService::class.java).setAction(ShotService.ACTION_STOP))
            running.value = false
            return
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
            != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            notifyPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            return
        }
        startCapture()
    }

    private fun startCapture() {
        if (!Settings.canDrawOverlays(this)) {
            startActivity(
                Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:$packageName")
                )
            )
            return
        }
        val mgr = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val capture = if (Build.VERSION.SDK_INT >= 34) {
            mgr.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
        } else {
            mgr.createScreenCaptureIntent()
        }
        projectionLauncher.launch(capture)
    }

    @Composable
    private fun HelpButton() {
        val gray = Color(0xFF757575)
        Canvas(Modifier.size(56.dp)) {
            val stroke = Stroke(width = 1.7.dp.toPx())
            val inset = stroke.width / 2f + 1f
            drawCircle(
                color = gray,
                radius = size.minDimension / 2f - inset,
                style = stroke
            )
            val u = size.width / 24f
            val corner = CornerRadius(u * 1.2f, u * 1.2f)
            val sheet = Size(9.2f * u, 9.2f * u)
            drawRoundRect(gray, Offset(9.2f * u, 5.6f * u), sheet, corner, style = stroke)
            drawRoundRect(gray, Offset(5.6f * u, 9.2f * u), sheet, corner, style = stroke)
        }
    }

    companion object {
        const val DEFAULT_IP = "10.205.0.95"
        private const val OLD_IP = "192.168.1.170"
        private const val KEY_IP = "ip"
    }
}
