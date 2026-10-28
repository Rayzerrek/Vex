#include "notification_surface.h"
#include <math.h>
#include <stdio.h>

namespace {
int failed(const char* condition, int line) { fprintf(stderr, "Notification surface regression at %d: %s\n", line, condition); return line; }
#define CHECK_NOTICE(value) do { if (!(value)) return failed(#value, __LINE__); } while (false)
bool noticeNear(float actual, float expected) { return fabsf(actual - expected) < 0.0001f; }
}
int testNotificationSurface() {
    NotificationReveal motion;
    CHECK_NOTICE(noticeNear(motion.update(false, 0, true), 0));
    CHECK_NOTICE(noticeNear(motion.update(true, 100, true), 0));
    CHECK_NOTICE(motion.pending(100));
    CHECK_NOTICE(noticeNear(motion.update(true, 180, true), 0.875f));
    CHECK_NOTICE(noticeNear(motion.update(true, 260, true), 1) && !motion.pending(260));
    CHECK_NOTICE(noticeNear(motion.update(false, 300, true), 1));
    CHECK_NOTICE(noticeNear(motion.update(false, 380, true), 0.125f));
    CHECK_NOTICE(noticeNear(motion.update(false, 460, true), 0) && !motion.pending(460));
    CHECK_NOTICE(noticeNear(motion.update(true, 500, true), 0));
    const float interrupted = motion.update(true, 580, true);
    CHECK_NOTICE(noticeNear(motion.update(false, 580, true), interrupted));
    CHECK_NOTICE(noticeNear(motion.update(false, 660, true), interrupted * 0.5f));
    CHECK_NOTICE(noticeNear(motion.update(true, 700, false), 1) && !motion.pending(700));
    CHECK_NOTICE(noticeNear(motion.update(false, 710, false), 0) && !motion.pending(710));
    const auto update = NotificationGeometry::place(1000, 700, 0, 110, 0, 0, 90, false, 1);
    CHECK_NOTICE(noticeNear(update.bounds.right, 984) && noticeNear(update.bounds.bottom, 684));
    CHECK_NOTICE(noticeNear(update.bounds.bottom - update.bounds.top, 44));
    CHECK_NOTICE(noticeNear(update.close.right - update.close.left, 28) && noticeNear(update.close.bottom - update.close.top, 28));
    CHECK_NOTICE(update.hit(962, 662) == NotificationHit::close);
    CHECK_NOTICE(update.hit((update.action.left + update.action.right) / 2, 662) == NotificationHit::action);
    CHECK_NOTICE(update.hit(update.title.left + 2, 662) == NotificationHit::body);
    CHECK_NOTICE(update.hit(985, 662) == NotificationHit::none);
    const auto error = NotificationGeometry::place(1000, 700, 52, 170, 320, 34, 0, true, 1);
    CHECK_NOTICE(error.bounds.bottom <= update.bounds.top - 8);
    CHECK_NOTICE(error.bounds.bottom - error.bounds.top < 120);
    CHECK_NOTICE(error.title.right <= error.close.left && error.message.bottom > error.message.top);
    CHECK_NOTICE(error.hit(error.message.left + 10, error.message.top + 5) == NotificationHit::body);
    CHECK_NOTICE(error.action.right == error.action.left);
    const auto entering = NotificationGeometry::place(1000, 700, 0, 110, 0, 0, 90, false, 0);
    CHECK_NOTICE(noticeNear(entering.close.top - update.close.top, 6));
    const auto longMessage = NotificationGeometry::place(640, 400, 52, 170, 4000, 105, 0, true, 1);
    CHECK_NOTICE(noticeNear(longMessage.bounds.right - longMessage.bounds.left, 440));
    CHECK_NOTICE(longMessage.bounds.top >= 44 && longMessage.bounds.right <= 624);
    CHECK_NOTICE(NotificationGeometry::place(100, 100, 0, 10, 0, 0, 0, false, 1).hit(90, 90) == NotificationHit::none);
    CHECK_NOTICE(NotificationGeometry::place(NAN, 700, 0, 10, 0, 0, 0, false, 1).bounds.right == 0);
    return 0;
}
