package dev.midfielder.android.ui

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import com.google.android.material.button.MaterialButton
import com.google.android.material.card.MaterialCardView
import com.google.android.material.chip.Chip
import com.google.android.material.color.MaterialColors
import com.google.android.material.divider.MaterialDivider
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.android.material.progressindicator.LinearProgressIndicator
import dev.midfielder.android.R
import com.google.android.material.R as M

/**
 * Factory for the app's Material 3 views, built in code. Colors come from the theme's color roles
 * (res/values/themes.xml), text from the M3 type scale, spacing from a 4/8 dp grid.
 */
class Ui(val context: Context) {
    enum class Card { ELEVATED, FILLED, OUTLINED }
    enum class Button { FILLED, TONAL, OUTLINED, TEXT }

    private val density = context.resources.displayMetrics.density

    fun dp(v: Int): Int = (v * density + 0.5f).toInt()
    fun dpf(v: Float): Float = v * density

    fun color(attr: Int): Int = MaterialColors.getColor(context, attr, Color.MAGENTA)

    val primary: Int get() = color(M.attr.colorPrimary)
    val onPrimary: Int get() = color(M.attr.colorOnPrimary)
    val primaryContainer: Int get() = color(M.attr.colorPrimaryContainer)
    val onPrimaryContainer: Int get() = color(M.attr.colorOnPrimaryContainer)
    val secondaryContainer: Int get() = color(M.attr.colorSecondaryContainer)
    val onSecondaryContainer: Int get() = color(M.attr.colorOnSecondaryContainer)
    val tertiaryContainer: Int get() = color(M.attr.colorTertiaryContainer)
    val onTertiaryContainer: Int get() = color(M.attr.colorOnTertiaryContainer)
    val errorColor: Int get() = color(M.attr.colorError)
    val errorContainer: Int get() = color(M.attr.colorErrorContainer)
    val onErrorContainer: Int get() = color(M.attr.colorOnErrorContainer)
    val surface: Int get() = color(M.attr.colorSurface)
    val onSurface: Int get() = color(M.attr.colorOnSurface)
    val onSurfaceVariant: Int get() = color(M.attr.colorOnSurfaceVariant)
    val outlineVariant: Int get() = color(M.attr.colorOutlineVariant)
    val containerLowest: Int get() = color(M.attr.colorSurfaceContainerLowest)
    val containerLow: Int get() = color(M.attr.colorSurfaceContainerLow)
    val container: Int get() = color(M.attr.colorSurfaceContainer)
    val containerHigh: Int get() = color(M.attr.colorSurfaceContainerHigh)

    // --- Text ---------------------------------------------------------------------------------------

    fun text(s: CharSequence, appearance: Int, color: Int? = null): TextView = TextView(context).apply {
        text = s
        setTextAppearance(appearance)
        if (color != null) setTextColor(color)
    }

    fun headline(s: String) = text(s, M.style.TextAppearance_Material3_HeadlineMedium, onSurface)
    fun titleLarge(s: String) = text(s, M.style.TextAppearance_Material3_TitleLarge, onSurface)
    fun title(s: String) = text(s, M.style.TextAppearance_Material3_TitleMedium, onSurface)
    fun body(s: String) = text(s, M.style.TextAppearance_Material3_BodyMedium, onSurface)
    fun supporting(s: String) = text(s, M.style.TextAppearance_Material3_BodyMedium, onSurfaceVariant)
    fun small(s: String) = text(s, M.style.TextAppearance_Material3_BodySmall, onSurfaceVariant)
    fun label(s: String) = text(s, M.style.TextAppearance_Material3_LabelLarge, onSurfaceVariant)

    fun oneLine(v: TextView, ellipsize: TextUtils.TruncateAt = TextUtils.TruncateAt.END): TextView = v.apply {
        maxLines = 1
        this.ellipsize = ellipsize
    }

    // --- Layout -------------------------------------------------------------------------------------

    fun vertical(): LinearLayout = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    fun horizontal(): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    fun matchWidth(topDp: Int = 12): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(topDp) }

    fun weight(w: Float = 1f, startDp: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, w).apply { marginStart = dp(startDp) }

    fun wrap(startDp: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(startDp) }

    /** A Material card with a vertical body; add content to [Box.body]. */
    class Box(val card: MaterialCardView, val body: LinearLayout)

    fun card(kind: Card = Card.FILLED, fill: Int? = null, paddingDp: Int = 16): Box {
        val attr = when (kind) {
            Card.ELEVATED -> R.attr.mfElevatedCardStyle
            Card.FILLED -> R.attr.mfFilledCardStyle
            Card.OUTLINED -> R.attr.mfOutlinedCardStyle
        }
        val card = MaterialCardView(context, null, attr).apply {
            radius = dpf(20f)
            setCardBackgroundColor(fill ?: if (kind == Card.FILLED) containerLowest else containerLowest)
            if (kind == Card.FILLED) {
                strokeWidth = 0
                cardElevation = dpf(1f)
            }
        }
        val body = vertical().apply { setPadding(dp(paddingDp), dp(paddingDp), dp(paddingDp), dp(paddingDp)) }
        card.addView(body, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        return Box(card, body)
    }

    // --- Controls -----------------------------------------------------------------------------------

    fun button(label: String, kind: Button = Button.FILLED, icon: Int? = null, onClick: () -> Unit): MaterialButton {
        val attr = when (kind) {
            Button.FILLED -> R.attr.mfFilledButtonStyle
            Button.TONAL -> R.attr.mfTonalButtonStyle
            Button.OUTLINED -> R.attr.mfOutlinedButtonStyle
            Button.TEXT -> R.attr.mfTextButtonStyle
        }
        return MaterialButton(context, null, attr).apply {
            text = label
            isAllCaps = false
            if (icon != null) {
                setIconResource(icon)
                iconSize = dp(18)
                iconPadding = dp(8)
            }
            setOnClickListener { onClick() }
        }
    }

    fun iconButton(icon: Int, description: String, tint: Int? = null, onClick: (View) -> Unit): MaterialButton =
        MaterialButton(context, null, R.attr.mfIconButtonStyle).apply {
            setIconResource(icon)
            iconTint = ColorStateList.valueOf(tint ?: onSurfaceVariant)
            contentDescription = description
            setOnClickListener { onClick(it) }
        }

    fun filterChip(label: String, checked: Boolean): Chip = Chip(context, null, R.attr.mfFilterChipStyle).apply {
        text = label
        isCheckable = true
        isChecked = checked
        id = View.generateViewId()
    }

    fun assistChip(label: String, icon: Int?, onClick: () -> Unit): Chip = Chip(context, null, R.attr.mfAssistChipStyle).apply {
        text = label
        if (icon != null) {
            setChipIconResource(icon)
            chipIconTint = ColorStateList.valueOf(primary)
        }
        setOnClickListener { onClick() }
    }

    fun switch(checked: Boolean, onChange: (Boolean) -> Unit): MaterialSwitch = MaterialSwitch(context).apply {
        isChecked = checked
        setOnCheckedChangeListener { _, on -> onChange(on) }
    }

    fun progress(): LinearProgressIndicator = LinearProgressIndicator(context).apply {
        max = 100
        trackCornerRadius = dp(4)
        trackThickness = dp(6)
        setIndicatorColor(primary)
        trackColor = secondaryContainer
    }

    fun divider(): MaterialDivider = MaterialDivider(context).apply {
        dividerColor = outlineVariant
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
    }

    fun icon(res: Int, tint: Int, sizeDp: Int = 24): ImageView = ImageView(context).apply {
        setImageResource(res)
        imageTintList = ColorStateList.valueOf(tint)
        layoutParams = LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp))
    }

    /** An icon centered in a tinted circle (or rounded square). */
    fun iconBadge(res: Int, sizeDp: Int = 40, bg: Int = secondaryContainer, fg: Int = onSecondaryContainer, cornerDp: Int? = null): View =
        FrameLayout(context).apply {
            background = GradientDrawable().apply {
                if (cornerDp == null) shape = GradientDrawable.OVAL else cornerRadius = dpf(cornerDp.toFloat())
                setColor(bg)
            }
            val iconSize = (sizeDp * 0.5f).toInt()
            addView(icon(res, fg, iconSize), FrameLayout.LayoutParams(dp(iconSize), dp(iconSize), Gravity.CENTER))
            layoutParams = LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp))
        }

    /** M3 list item: leading view, one or two lines of text, trailing view; 56/72 dp tall. */
    fun listRow(
        title: String,
        subtitle: String? = null,
        leading: View? = null,
        trailing: View? = null,
        below: View? = null,
        onClick: (() -> Unit)? = null,
    ): LinearLayout =
        horizontal().apply {
            minimumHeight = dp(if (subtitle == null) 56 else 72)
            setPadding(dp(4), dp(8), dp(4), dp(8))
            if (leading != null) {
                addView(leading)
                (leading.layoutParams as? LinearLayout.LayoutParams)?.marginEnd = dp(16)
            }
            val texts = vertical()
            texts.addView(text(title, M.style.TextAppearance_Material3_BodyLarge, onSurface))
            if (!subtitle.isNullOrEmpty()) texts.addView(text(subtitle, M.style.TextAppearance_Material3_BodyMedium, onSurfaceVariant))
            if (below != null) texts.addView(below, wrap().apply { topMargin = dp(8) })
            addView(texts, weight())
            if (trailing != null) addView(trailing, wrap(12))
            if (onClick != null) {
                isClickable = true
                isFocusable = true
                background = ripple()
                setOnClickListener { onClick() }
            }
        }

    fun switchRow(title: String, subtitle: String?, checked: Boolean, onChange: (Boolean) -> Unit): LinearLayout {
        val sw = switch(checked, onChange)
        return listRow(title, subtitle, trailing = sw) { sw.toggle() }
    }

    /** "Title ........ Action" row above a section. */
    fun sectionHeader(title: String, action: String? = null, onAction: (() -> Unit)? = null): LinearLayout = horizontal().apply {
        setPadding(dp(4), dp(20), 0, dp(4))
        addView(text(title, M.style.TextAppearance_Material3_TitleMedium, onSurface), weight())
        if (action != null && onAction != null) addView(button(action, Button.TEXT) { onAction() })
    }

    fun ripple(): android.graphics.drawable.Drawable? {
        val attrs = context.obtainStyledAttributes(intArrayOf(android.R.attr.selectableItemBackground))
        val d = attrs.getDrawable(0)
        attrs.recycle()
        return d
    }

    fun pill(color: Int, radiusDp: Int = 100): GradientDrawable = GradientDrawable().apply {
        setColor(color)
        cornerRadius = dpf(radiusDp.toFloat())
    }
}
