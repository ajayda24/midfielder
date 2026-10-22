package dev.midfielder.android

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.Image
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Size
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import android.widget.TextView
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.util.concurrent.Executor
import kotlin.math.abs

/**
 * Minimal QR scanner: Camera2 preview + ZXing on a background thread. No Google Play Services needed.
 * Everything camera-related is released in onPause, so a backgrounded scanner holds nothing.
 */
class ScanActivity : Activity() {
    private lateinit var texture: TextureView
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private val qr = QRCodeReader()
    private val hints = mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE))
    private var luminance: ByteArray? = null

    @Volatile
    private var found = false

    /** Set when the activity pauses: a camera that opens late is closed at once, frames are no longer read. */
    @Volatile
    private var paused = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        texture = TextureView(this)
        val pad = (16 * resources.displayMetrics.density).toInt()
        val hint = TextView(this).apply {
            text = "Point the camera at the QR code on your PC"
            textSize = 16f
            setPadding(pad, pad, pad, pad)
            setBackgroundColor(0x99000000.toInt())
            setTextColor(0xFFFFFFFF.toInt())
        }
        // Edge to edge (Android 15+): keep the hint above the navigation bar.
        hint.setOnApplyWindowInsetsListener { v, insets ->
            val bottom = if (android.os.Build.VERSION.SDK_INT >= 30) {
                insets.getInsets(android.view.WindowInsets.Type.systemBars()).bottom
            } else {
                @Suppress("DEPRECATION")
                insets.systemWindowInsetBottom
            }
            v.setPadding(pad, pad, pad, pad + bottom)
            insets
        }
        setContentView(
            FrameLayout(this).apply {
                setBackgroundColor(0xFF000000.toInt())
                addView(texture, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))
                addView(hint, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
            },
        )
    }

    override fun onResume() {
        super.onResume()
        paused = false
        thread = HandlerThread("qr-scan").also { it.start() }
        handler = Handler(thread!!.looper)
        if (texture.isAvailable) openCamera()
        else texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(s: SurfaceTexture, w: Int, h: Int) = openCamera()
            override fun onSurfaceTextureSizeChanged(s: SurfaceTexture, w: Int, h: Int) = Unit
            override fun onSurfaceTextureDestroyed(s: SurfaceTexture) = true
            override fun onSurfaceTextureUpdated(s: SurfaceTexture) = Unit
        }
    }

    override fun onPause() {
        paused = true
        // Close on the camera thread: a frame may be mid-decode there, and closing the reader under it crashes.
        val h = handler
        val t = thread
        if (h != null) h.post { closeCamera() } else closeCamera()
        t?.quitSafely()
        thread = null
        handler = null
        super.onPause()
    }

    private fun fail(reason: String) {
        runOnUiThread {
            if (isFinishing) return@runOnUiThread
            setResult(RESULT_CANCELED, Intent().putExtra(EXTRA_ERROR, reason))
            finish()
        }
    }

    private fun openCamera() {
        if (paused) return
        try {
            openCameraUnsafe()
        } catch (e: Exception) {
            // In use by another app (split screen), disabled by policy, or the camera service is down.
            EventLog.error("Camera unavailable for scanning", e)
            fail("The camera isn't available right now. Close other apps using it and try again.")
        }
    }

    @SuppressLint("MissingPermission")
    private fun openCameraUnsafe() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            fail("Camera access is needed to scan the code.")
            return
        }
        val manager = getSystemService(CameraManager::class.java)
        val id = manager.cameraIdList.firstOrNull {
            manager.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
        } ?: manager.cameraIdList.firstOrNull() ?: run {
            fail("This phone has no camera to scan with.")
            return
        }
        val map = manager.getCameraCharacteristics(id).get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: run {
            fail("This camera can't be used for scanning.")
            return
        }
        val size = chooseSize(map.getOutputSizes(ImageFormat.YUV_420_888))
        val h = handler ?: return
        reader = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2).apply {
            setOnImageAvailableListener({ r ->
                val image = runCatching { r.acquireLatestImage() }.getOrNull() ?: return@setOnImageAvailableListener
                try {
                    if (!found && !paused) decode(image)
                } catch (_: IllegalStateException) {
                    // The reader was closed under us while pausing: nothing to do.
                } finally {
                    runCatching { image.close() }
                }
            }, h)
        }
        fitPreview(size)
        manager.openCamera(id, object : CameraDevice.StateCallback() {
            override fun onOpened(device: CameraDevice) {
                if (paused) {
                    device.close() // left before the camera finished opening
                    return
                }
                camera = device
                startSession(device, size)
            }

            override fun onDisconnected(device: CameraDevice) {
                device.close()
                camera = null
            }

            override fun onError(device: CameraDevice, error: Int) {
                device.close()
                camera = null
                fail("The camera stopped working. Try again.")
            }
        }, h)
    }

    private fun startSession(device: CameraDevice, size: Size) {
        val st = texture.surfaceTexture ?: return
        st.setDefaultBufferSize(size.width, size.height)
        val preview = Surface(st)
        val imageSurface = reader?.surface ?: return
        val h = handler ?: return
        val executor = Executor { h.post(it) }
        val config = SessionConfiguration(
            SessionConfiguration.SESSION_REGULAR,
            listOf(OutputConfiguration(preview), OutputConfiguration(imageSurface)),
            executor,
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    session = s
                    val request = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                        addTarget(preview)
                        addTarget(imageSurface)
                        set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                    }.build()
                    runCatching { s.setRepeatingRequest(request, null, h) }
                }

                override fun onConfigureFailed(s: CameraCaptureSession) {
                    fail("The camera couldn't start. Try again.")
                }
            },
        )
        runCatching { device.createCaptureSession(config) }
    }

    /** ZXing only needs luminance: the Y plane, copied row by row when the row stride has padding. */
    private fun decode(image: Image) {
        val plane = image.planes[0]
        val w = image.width
        val h = image.height
        val data = luminance?.takeIf { it.size == w * h } ?: ByteArray(w * h).also { luminance = it }
        val buf = plane.buffer
        if (plane.rowStride == w && plane.pixelStride == 1) {
            buf.get(data, 0, w * h)
        } else {
            for (row in 0 until h) {
                buf.position(row * plane.rowStride)
                buf.get(data, row * w, w)
            }
        }
        try {
            val result = qr.decode(BinaryBitmap(HybridBinarizer(PlanarYUVLuminanceSource(data, w, h, 0, 0, w, h, false))), hints)
            val text = result.text
            if (text.startsWith("midfielder://pair")) {
                found = true
                runOnUiThread {
                    setResult(RESULT_OK, Intent().putExtra(EXTRA_URI, text))
                    finish()
                }
            }
        } catch (_: ReaderException) {
            // No code in this frame.
        } finally {
            qr.reset()
        }
    }

    /** Preview frames are landscape; the activity is portrait, so size the view to the rotated aspect. */
    private fun fitPreview(size: Size) {
        texture.post {
            val viewWidth = texture.width
            if (viewWidth == 0) return@post
            val lp = texture.layoutParams
            lp.height = viewWidth * size.width / size.height
            texture.layoutParams = lp
        }
    }

    private fun chooseSize(sizes: Array<Size>): Size =
        sizes.minByOrNull { abs(it.width * it.height - TARGET_PIXELS) + if (it.width < it.height) 1_000_000 else 0 } ?: Size(640, 480)

    private fun closeCamera() {
        runCatching { session?.close() }
        session = null
        runCatching { camera?.close() }
        camera = null
        runCatching { reader?.close() }
        reader = null
    }

    companion object {
        const val EXTRA_URI = "uri"
        const val EXTRA_ERROR = "error"
        private const val TARGET_PIXELS = 1280 * 720
    }
}
