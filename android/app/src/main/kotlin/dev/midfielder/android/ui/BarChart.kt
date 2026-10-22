package dev.midfielder.android.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.TypedValue
import android.view.View

/** Seven rounded bars (oldest → today) with day labels and today's value on top. Draws only when values change. */
class BarChart(context: Context, private val bar: Int, private val today: Int, labelColor: Int) : View(context) {
    private val density = resources.displayMetrics.density
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val label = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = labelColor
        textSize = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP, 11f, resources.displayMetrics)
        textAlign = Paint.Align.CENTER
    }
    private val value = Paint(label).apply { color = today; isFakeBoldText = true }
    private val rect = RectF()
    private var values: List<Long> = emptyList()
    private var labels: List<String> = emptyList()

    private fun textHeight(): Float = label.fontMetrics.let { it.descent - it.ascent }

    fun set(values: List<Long>, labels: List<String>) {
        if (values == this.values && labels == this.labels) return
        this.values = values
        this.labels = labels
        contentDescription = labels.zip(values).joinToString { (l, v) -> "$l $v" }
        invalidate()
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // Room for two lines of text (value above, day below) at whatever font size the user picked.
        setMeasuredDimension(MeasureSpec.getSize(widthMeasureSpec), (88 * density + 2 * textHeight()).toInt())
    }

    override fun onDraw(canvas: Canvas) {
        if (values.isEmpty()) return
        val labelSpace = textHeight() + 6 * density
        val top = textHeight() + 4 * density
        val chartH = height - labelSpace - top
        val slot = width / values.size.toFloat()
        val barW = (slot * 0.56f).coerceAtMost(32 * density)
        val max = (values.maxOrNull() ?: 0L).coerceAtLeast(1L)
        val r = 8 * density
        values.forEachIndexed { i, v ->
            val cx = slot * i + slot / 2
            val h = if (v == 0L) 6 * density else (chartH - 6 * density) * v / max + 6 * density
            val isToday = i == values.lastIndex
            paint.color = if (isToday) today else bar
            rect.set(cx - barW / 2, top + chartH - h, cx + barW / 2, top + chartH)
            canvas.drawRoundRect(rect, r, r, paint)
            if (v > 0 && (isToday || v == max)) canvas.drawText(v.toString(), cx, rect.top - 4 * density, value)
            labels.getOrNull(i)?.let { canvas.drawText(it, cx, height - label.fontMetrics.descent - 2 * density, label) }
        }
    }
}
