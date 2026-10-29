package com.awada.screener

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.DisplayMetrics
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import androidx.core.app.NotificationCompat
import java.io.ByteArrayOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors
import kotlin.math.abs

class ShotService : Service() {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var button: View? = null
    private var host = MainActivity.DEFAULT_IP
    private var busy = false
    private var closed = false
    private var wantShot = false
    private var shotGen = 0

    private val callback = object : MediaProjection.Callback() {
        override fun onStop() {
            stopSelf()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        startInForeground()
        if (projection != null) {
            host = intent?.getStringExtra(EXTRA_HOST)?.trim().orEmpty().ifEmpty { host }
            return START_NOT_STICKY
        }
        val data = readData(intent)
        val code = intent?.getIntExtra(EXTRA_CODE, 0) ?: 0
        host = intent?.getStringExtra(EXTRA_HOST)?.trim().orEmpty().ifEmpty { MainActivity.DEFAULT_IP }
        if (data == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        val mgr = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val projection = mgr.getMediaProjection(code, data)
        if (projection == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        this.projection = projection
        projection.registerCallback(callback, handler)
        openDisplay()
        showButton()
        running = true
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        running = false
        if (!closed) {
            closed = true
            button?.let { runCatching { windowManager().removeView(it) } }
            button = null
            display?.release()
            display = null
            reader?.close()
            reader = null
            projection?.unregisterCallback(callback)
            projection?.stop()
            projection = null
        }
        io.shutdownNow()
        super.onDestroy()
    }

    private fun startInForeground() {
        val channelId = "screener"
        if (Build.VERSION.SDK_INT >= 26) {
            val nm = getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(
                NotificationChannel(channelId, "Screener", NotificationManager.IMPORTANCE_LOW)
            )
        }
        val note = NotificationCompat.Builder(this, channelId)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("Screener")
            .setContentText("Кнопка на экране")
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, note, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(1, note)
        }
    }

    private fun openDisplay() {
        val (width, height, dpi) = screen()
        val reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 3)
        this.reader = reader
        reader.setOnImageAvailableListener({ source ->
            val image = try {
                source.acquireNextImage()
            } catch (_: Exception) {
                null
            } ?: return@setOnImageAvailableListener
            if (!wantShot || closed) {
                image.close()
                return@setOnImageAvailableListener
            }
            wantShot = false
            deliver(image, shotGen)
        }, handler)
        display = projection?.createVirtualDisplay(
            "screener",
            width,
            height,
            dpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            reader.surface,
            null,
            handler
        )
    }

    private fun showButton() {
        if (button != null) return
        val wm = windowManager()
        val (width, height, _) = screen()
        val size = dp(56)
        val params = WindowManager.LayoutParams(
            size,
            size,
            overlayType(),
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
            PixelFormat.TRANSLUCENT
        )
        params.gravity = Gravity.TOP or Gravity.START
        params.x = width - size - dp(16)
        params.y = height / 3
        val view = MarkView(this)
        view.setOnTouchListener(object : View.OnTouchListener {
            private var downX = 0f
            private var downY = 0f
            private var startX = 0
            private var startY = 0
            private var moved = false

            override fun onTouch(v: View, event: MotionEvent): Boolean {
                when (event.actionMasked) {
                    MotionEvent.ACTION_DOWN -> {
                        downX = event.rawX
                        downY = event.rawY
                        startX = params.x
                        startY = params.y
                        moved = false
                    }
                    MotionEvent.ACTION_MOVE -> {
                        val dx = event.rawX - downX
                        val dy = event.rawY - downY
                        if (abs(dx) > 8 || abs(dy) > 8) moved = true
                        params.x = (startX + dx.toInt()).coerceIn(-size / 2, width - size / 2)
                        params.y = (startY + dy.toInt()).coerceIn(-size / 2, height - size / 2)
                        runCatching { wm.updateViewLayout(view, params) }
                    }
                    MotionEvent.ACTION_UP -> if (!moved) capture()
                }
                return true
            }
        })
        wm.addView(view, params)
        button = view
    }

    private fun capture() {
        if (busy || display == null) {
            if (display == null) toast("Съёмка не стартовала")
            return
        }
        busy = true
        val gen = ++shotGen
        button?.visibility = View.INVISIBLE
        handler.postDelayed({
            if (closed || gen != shotGen) return@postDelayed
            wantShot = true
            handler.postDelayed({
                if (closed || gen != shotGen || !wantShot) return@postDelayed
                wantShot = false
                button?.visibility = View.VISIBLE
                busy = false
                toast("Кадр не получен")
            }, 1000)
        }, 150)
    }

    private fun deliver(image: Image, gen: Int) {
        val target = host
        io.execute {
            val bytes = runCatching { toPng(image) }.getOrNull()
            image.close()
            val ok = bytes != null && runCatching { upload(target, bytes) }.getOrDefault(false)
            handler.post {
                if (closed || gen != shotGen) return@post
                button?.visibility = View.VISIBLE
                busy = false
                toast(if (ok) "В буфере компьютера" else "Компьютер не ответил")
            }
        }
    }

    private fun screen(): Triple<Int, Int, Int> {
        val wm = windowManager()
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = wm.maximumWindowMetrics.bounds
            val w = bounds.width() / 2 * 2
            val h = bounds.height() / 2 * 2
            Triple(w, h, resources.configuration.densityDpi)
        } else {
            val metrics = DisplayMetrics()
            @Suppress("DEPRECATION")
            wm.defaultDisplay.getRealMetrics(metrics)
            Triple(metrics.widthPixels / 2 * 2, metrics.heightPixels / 2 * 2, metrics.densityDpi)
        }
    }

    private fun windowManager() = getSystemService(WINDOW_SERVICE) as WindowManager

    private fun overlayType(): Int {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    private fun toast(text: String) {
        Toast.makeText(applicationContext, text, Toast.LENGTH_SHORT).show()
    }

    private fun readData(intent: Intent?): Intent? {
        intent ?: return null
        return if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(EXTRA_DATA, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(EXTRA_DATA)
        }
    }

    companion object {
        const val ACTION_STOP = "stop"
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val EXTRA_HOST = "host"

        @Volatile
        var running = false

        private fun toPng(image: Image): ByteArray {
            val plane = image.planes[0]
            val buffer = plane.buffer
            buffer.rewind()
            val width = image.width
            val height = image.height
            val bmpWidth = plane.rowStride / plane.pixelStride
            val bitmap = Bitmap.createBitmap(bmpWidth, height, Bitmap.Config.ARGB_8888)
            bitmap.copyPixelsFromBuffer(buffer)
            val shot = if (bmpWidth == width) bitmap else Bitmap.createBitmap(bitmap, 0, 0, width, height)
            val stream = ByteArrayOutputStream()
            shot.compress(Bitmap.CompressFormat.PNG, 100, stream)
            if (shot !== bitmap) shot.recycle()
            bitmap.recycle()
            return stream.toByteArray()
        }

        private fun upload(host: String, bytes: ByteArray): Boolean {
            val socket = Socket()
            socket.connect(InetSocketAddress(host, 47123), 5000)
            socket.soTimeout = 20000
            return try {
                val out = socket.getOutputStream()
                val head = "POST /shot HTTP/1.1\r\nHost: $host:47123\r\nContent-Type: image/png\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n"
                out.write(head.toByteArray(Charsets.US_ASCII))
                out.write(bytes)
                out.flush()
                val line = socket.getInputStream().bufferedReader(Charsets.US_ASCII).readLine().orEmpty()
                line.contains("200")
            } finally {
                socket.close()
            }
        }
    }
}

private class MarkView(context: android.content.Context) : View(context) {
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.rgb(117, 117, 117)
        style = Paint.Style.STROKE
        strokeWidth = resources.displayMetrics.density * 1.7f
    }

    override fun onDraw(canvas: Canvas) {
        val inset = stroke.strokeWidth / 2f + 1f
        canvas.drawCircle(width / 2f, height / 2f, width / 2f - inset, stroke)
        val u = width / 24f
        val corner = u * 1.2f
        canvas.drawRoundRect(9.2f * u, 5.6f * u, 18.4f * u, 14.8f * u, corner, corner, stroke)
        canvas.drawRoundRect(5.6f * u, 9.2f * u, 14.8f * u, 18.4f * u, corner, corner, stroke)
    }
}
