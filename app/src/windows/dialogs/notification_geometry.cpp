#include "notification_surface.h"
#include <math.h>

NotificationGeometry NotificationGeometry::place(float viewportWidth, float viewportHeight, float bottomInset,
    float titleWidth, float messageWidth, float messageHeight, float actionWidth, bool hasMessage, float reveal) {
    NotificationGeometry result;
    if (!isfinite(viewportWidth) || !isfinite(viewportHeight) || !isfinite(bottomInset) ||
        !isfinite(titleWidth) || !isfinite(messageWidth) || !isfinite(messageHeight) || !isfinite(actionWidth) || !isfinite(reveal) ||
        viewportWidth < 212 || viewportHeight - bottomInset < 120) return result;
    const float buttonWidth = actionWidth > 0 ? actionWidth + 24 : 0;
    const float titleNeeds = 38 + titleWidth + (buttonWidth ? 14 + buttonWidth + 6 : 8) + 28 + 8;
    const float desiredWidth = hasMessage ? fmaxf(280, fminf(440, fmaxf(titleNeeds, 38 + messageWidth + 16))) : titleNeeds;
    const float width = fminf(viewportWidth - 32, desiredWidth);
    const float height = fminf(viewportHeight - bottomInset - 76, hasMessage ? fmaxf(72, 38 + messageHeight + 14) : 44);
    const float right = viewportWidth - 16, bottom = viewportHeight - 16 - bottomInset + 6 * (1 - fmaxf(0, fminf(1, reveal)));
    result.bounds = D2D1::RectF(right - width, bottom - height, right, bottom);
    result.close = D2D1::RectF(right - 36, result.bounds.top + 8, right - 8, result.bounds.top + 36);
    if (buttonWidth) result.action = D2D1::RectF(result.close.left - 6 - buttonWidth, result.bounds.top + 6, result.close.left - 6, result.bounds.top + 38);
    result.title = D2D1::RectF(result.bounds.left + 38, result.bounds.top + 13,
        buttonWidth ? result.action.left - 10 : result.close.left - 8, result.bounds.top + 31);
    result.message = D2D1::RectF(result.bounds.left + 38, result.bounds.top + 38, right - 14, bottom - 14);
    return result;
}
NotificationHit NotificationGeometry::hit(float x, float y) const {
    const auto contains = [x, y](D2D1_RECT_F rect) { return rect.right > rect.left && rect.bottom > rect.top &&
        x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom; };
    if (!contains(bounds)) return NotificationHit::none;
    if (contains(close)) return NotificationHit::close;
    if (contains(action)) return NotificationHit::action;
    return NotificationHit::body;
}
