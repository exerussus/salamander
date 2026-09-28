package com.exerussus.salamander.rider;

import com.intellij.notification.Notification;
import com.intellij.notification.NotificationGroupManager;
import com.intellij.notification.NotificationType;
import com.intellij.openapi.project.Project;
import org.jetbrains.annotations.Nullable;

/** Уведомления группы «Salamander» (Settings → Appearance → Notifications). */
public final class SalamanderNotifier {
    private SalamanderNotifier() {
    }

    public static Notification create(String content, NotificationType type) {
        return NotificationGroupManager.getInstance()
                .getNotificationGroup(SalamanderPlugin.NOTIFICATIONS)
                .createNotification("Salamander", content, type);
    }

    public static void show(@Nullable Project project, String content, NotificationType type) {
        create(content, type).notify(project);
    }
}
