package dev.midfielder.android.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.TypedValue
import android.view.View

/** Seven rounded bars (oldest → today) with day labels. Draws only when its values change. */
class BarChart(context: Context) : View(context) {
    private val density = resources.displayMetrics.density
    private val bar = Paint(Paint.ANTI_ALIAS_FLAG)
    private val label = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Palette.MUTED
        textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 11f, resources.displayMetrics)
        textAlign = Paint.Align.CENTER
    }
    private val rect = RectF()
    private var values: List<Long> = emptyList()
    private var labels: List<String> = emptyList()

    fun set(values: List<Long>, labels: List<String>) {
        if (values == this.values && labels == this.labels) return
        this.values = values
        this.labels = labels
        contentDescription = labels.zip(values).joinToString { (l, v) -> "$l $v" }
        invalidate()
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        setMeasuredDimension(MeasureSpec.getSize(widthMeasureSpec), (96 * density).toInt())
    }

    override fun onDraw(canvas: Canvas) {
        if (values.isEmpty()) return
        val labelSpace = 18 * density
        val chartH = height - labelSpace
        val slot = width / values.size.toFloat()
        val barW = (slot * 0.5f).coerceAtMost(28 * density)
        val max = (values.maxOrNull() ?: 0L).coerceAtLeast(1L)
        val r = 6 * density
        values.forEachIndexed { i, v ->
            val cx = slot * i + slot / 2
            val h = if (v == 0L) 4 * density else (chartH - 4 * density) * v / max + 4 * density
            bar.color = if (i == values.lastIndex) Palette.PRIMARY else Palette.HIGHLIGHT_STRONG
            rect.set(cx - barW / 2, chartH - h, cx + barW / 2, chartH)
            canvas.drawRoundRect(rect, r, r, bar)
            labels.getOrNull(i)?.let { canvas.drawText(it, cx, height - 4 * density, label) }
        }
    }
}
