package dev.midfielder.android.ui

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.Switch
import android.widget.TextView

/** Colors for both themes' single light look. Keep in sync with res/values/colors.xml and the Windows Theme.cs. */
object Palette {
    const val PRIMARY = 0xFF1F6F5C.toInt()
    const val PRIMARY_DARK = 0xFF174F42.toInt()
    const val HIGHLIGHT = 0xFFE6F4EE.toInt()
    const val HIGHLIGHT_STRONG = 0xFFC9E7DA.toInt()
    const val BACKGROUND = 0xFFF5F8F6.toInt()
    const val SURFACE = 0xFFFFFFFF.toInt()
    const val OUTLINE = 0xFFDDE6E1.toInt()
    const val TEXT = 0xFF1B2421.toInt()
    const val MUTED = 0xFF5E6B66.toInt()
    const val DANGER = 0xFFB3261E.toInt()
    const val DANGER_BG = 0xFFFCEEEE.toInt()
    const val WARNING = 0xFF9A6100.toInt()
    const val RIPPLE = 0x331F6F5C
}

/** Small factory for the app's views: rounded cards, pill buttons, chips, rows. Framework views only. */
class Ui(val context: Context) {
    private val density = context.resources.displayMetrics.density

    fun dp(v: Int): Int = (v * density + 0.5f).toInt()
    fun dpf(v: Float): Float = v * density

    fun rounded(color: Int, radiusDp: Int, stroke: Int? = null): GradientDrawable = GradientDrawable().apply {
        setColor(color)
        cornerRadius = dpf(radiusDp.toFloat())
        if (stroke != null) setStroke(dp(1), stroke)
    }

    fun pressable(background: Drawable?, radiusDp: Int): Drawable =
        RippleDrawable(ColorStateList.valueOf(Palette.RIPPLE), background, rounded(0xFFFFFFFF.toInt(), radiusDp))

    fun text(s: CharSequence, sizeSp: Float = 15f, color: Int = Palette.TEXT, bold: Boolean = false): TextView =
        TextView(context).apply {
            text = s
            setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
            setTextColor(color)
            if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setLineSpacing(0f, 1.1f)
        }

    fun title(s: String): TextView = text(s, 26f, bold = true).apply { setPadding(dp(4), dp(8), 0, dp(4)) }
    fun subtitle(s: String): TextView = text(s, 14f, Palette.MUTED).apply { setPadding(dp(4), 0, 0, dp(12)) }
    fun sectionLabel(s: String): TextView =
        text(s.uppercase(), 12f, Palette.MUTED, bold = true).apply {
            letterSpacing = 0.06f
            setPadding(dp(4), dp(20), 0, dp(8))
        }

    fun vertical(): LinearLayout = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    fun horizontal(): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    fun card(): LinearLayout = vertical().apply {
        background = rounded(Palette.SURFACE, 16, Palette.OUTLINE)
        setPadding(dp(16), dp(16), dp(16), dp(16))
    }

    fun icon(res: Int, tint: Int = Palette.PRIMARY, sizeDp: Int = 24): ImageView = ImageView(context).apply {
        setImageResource(res)
        imageTintList = ColorStateList.valueOf(tint)
        layoutParams = LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp))
    }

    /** An icon centered in a pale-green circle. */
    fun iconBadge(res: Int, sizeDp: Int = 44, bg: Int = Palette.HIGHLIGHT, tint: Int = Palette.PRIMARY): View =
        FrameLayout(context).apply {
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(bg)
            }
            addView(icon(res, tint, sizeDp / 2), FrameLayout.LayoutParams(dp(sizeDp / 2), dp(sizeDp / 2), Gravity.CENTER))
            layoutParams = LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp))
        }

    private fun button(label: String, iconRes: Int?, bg: Int, fg: Int, stroke: Int?, onClick: () -> Unit): TextView =
        text(label, 15f, fg, bold = true).apply {
            gravity = Gravity.CENTER
            minHeight = dp(48)
            setPadding(dp(18), dp(10), dp(18), dp(10))
            background = pressable(rounded(bg, 14, stroke), 14)
            isClickable = true
            isFocusable = true
            if (iconRes != null) {
                val d = context.getDrawable(iconRes)!!.mutate().apply {
                    setTint(fg)
                    setBounds(0, 0, dp(20), dp(20))
                }
                setCompoundDrawablesRelative(d, null, null, null)
                compoundDrawablePadding = dp(8)
            }
            setOnClickListener { onClick() }
        }

    fun primaryButton(label: String, iconRes: Int? = null, onClick: () -> Unit) =
        button(label, iconRes, Palette.PRIMARY, Palette.SURFACE, null, onClick)

    fun tonalButton(label: String, iconRes: Int? = null, onClick: () -> Unit) =
        button(label, iconRes, Palette.HIGHLIGHT, Palette.PRIMARY_DARK, null, onClick)

    fun outlineButton(label: String, iconRes: Int? = null, color: Int = Palette.PRIMARY, onClick: () -> Unit) =
        button(label, iconRes, Palette.SURFACE, color, Palette.OUTLINE, onClick)

    /** Small text-only action, e.g. "Fix" at the end of a row. */
    fun smallButton(label: String, onClick: () -> Unit): TextView = text(label, 14f, Palette.PRIMARY, bold = true).apply {
        setPadding(dp(14), dp(8), dp(14), dp(8))
        background = pressable(rounded(Palette.HIGHLIGHT, 20), 20)
        setOnClickListener { onClick() }
    }

    fun iconButton(res: Int, description: String, tint: Int = Palette.MUTED, onClick: () -> Unit): ImageView =
        ImageView(context).apply {
            setImageResource(res)
            imageTintList = ColorStateList.valueOf(tint)
            contentDescription = description
            setPadding(dp(10), dp(10), dp(10), dp(10))
            background = pressable(null, 24)
            layoutParams = LinearLayout.LayoutParams(dp(44), dp(44))
            setOnClickListener { onClick() }
        }

    fun chip(label: String, selected: Boolean, onClick: () -> Unit): TextView =
        text(label, 14f, if (selected) Palette.PRIMARY_DARK else Palette.MUTED, bold = selected).apply {
            setPadding(dp(16), dp(8), dp(16), dp(8))
            background = pressable(
                if (selected) rounded(Palette.HIGHLIGHT_STRONG, 18) else rounded(Palette.SURFACE, 18, Palette.OUTLINE), 18,
            )
            setOnClickListener { onClick() }
        }

    fun progressBar(): ProgressBar = ProgressBar(context, null, android.R.attr.progressBarStyleHorizontal).apply {
        max = 100
        progressTintList = ColorStateList.valueOf(Palette.PRIMARY)
        progressBackgroundTintList = ColorStateList.valueOf(Palette.HIGHLIGHT_STRONG)
    }

    /** Title + optional subtitle on the left, an optional trailing view on the right. */
    fun row(title: String, subtitle: String? = null, leading: View? = null, trailing: View? = null, onClick: (() -> Unit)? = null): LinearLayout =
        horizontal().apply {
            setPadding(dp(4), dp(10), dp(4), dp(10))
            if (leading != null) {
                addView(leading)
                (leading.layoutParams as? LinearLayout.LayoutParams)?.marginEnd = dp(14)
            }
            val texts = vertical()
            texts.addView(text(title, 16f).apply { ellipsize = TextUtils.TruncateAt.END; maxLines = 2 })
            if (!subtitle.isNullOrEmpty()) texts.addView(text(subtitle, 13f, Palette.MUTED))
            addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            if (trailing != null) addView(trailing)
            if (onClick != null) {
                background = pressable(null, 12)
                setOnClickListener { onClick() }
            }
        }

    @Suppress("UseSwitchCompatOrMaterialCode")
    fun switchRow(title: String, subtitle: String?, checked: Boolean, onChange: (Boolean) -> Unit): LinearLayout {
        val sw = Switch(context).apply {
            isChecked = checked
            thumbTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(Palette.PRIMARY, 0xFFF2F4F3.toInt()),
            )
            trackTintList = ColorStateList(
                arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(Palette.HIGHLIGHT_STRONG, Palette.OUTLINE),
            )
            setOnCheckedChangeListener { _, on -> onChange(on) }
        }
        return row(title, subtitle, trailing = sw) { sw.toggle() }
    }

    fun divider(): View = View(context).apply {
        setBackgroundColor(Palette.OUTLINE)
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1)).apply {
            topMargin = dp(4)
            bottomMargin = dp(4)
        }
    }

    fun dot(color: Int): View = View(context).apply {
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
        layoutParams = LinearLayout.LayoutParams(dp(8), dp(8)).apply { marginEnd = dp(6) }
    }

    fun matchWidth(topMarginDp: Int = 12): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(topMarginDp)
        }

    fun weight(w: Float = 1f, startMarginDp: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, w).apply { marginStart = dp(startMarginDp) }
}
