package com.desktouch.vision;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ResolveInfo;
import android.os.Build;
import android.view.accessibility.AccessibilityManager;
import java.util.List;

/**
 * Reports the actual SYSTEM Accessibility authorization separately from the
 * app's own persisted control approval. An app cannot grant Accessibility to
 * itself or silently restore an OEM-revoked service after package replacement.
 */
final class PermissionContinuity {
    private static final String PREFS="desktouch_permission_health";
    private static final String CHANNEL="desktouch_permission_health";
    private static final int NOTICE_ID=37381;
    private PermissionContinuity(){}

    static boolean systemAccessibilityEnabled(Context context){
        AccessibilityManager manager=(AccessibilityManager)
            context.getSystemService(Context.ACCESSIBILITY_SERVICE);
        if(manager==null)return false;
        List<AccessibilityServiceInfo> enabled=manager.getEnabledAccessibilityServiceList(
            AccessibilityServiceInfo.FEEDBACK_ALL_MASK);
        if(enabled==null)return false;
        String packageName=context.getPackageName();
        String desired=DeskTouchAccessibilityService.class.getName();
        for(AccessibilityServiceInfo info:enabled){
            ResolveInfo resolved=info.getResolveInfo();
            if(resolved==null||resolved.serviceInfo==null)continue;
            String declaredPackage=resolved.serviceInfo.packageName;
            String name=resolved.serviceInfo.name;
            if(name!=null && name.startsWith("."))name=declaredPackage+name;
            if(packageName.equals(declaredPackage)&&desired.equals(name))
                return true;
        }
        return false;
    }

    static boolean needsUserReapproval(boolean appApproved,boolean systemEnabled){
        return appApproved && !systemEnabled;
    }

    static String state(Context context){
        if(!DeskTouchAccessibilityService.allowed(context))
            return "Windows control disabled in DeskTouch (user preference retained).";
        if(!systemAccessibilityEnabled(context))
            return "Android Accessibility is OFF. Xiaomi/Android may disable it after an update. " +
                "Re-enable DeskTouch Windows control in Android Accessibility settings.";
        if(!DeskTouchAccessibilityService.ready(context))
            return "Android Accessibility is enabled; waiting for the system to bind the service.";
        return "Windows control approved; Android Accessibility is active.";
    }

    static void checkpointBeforeInstaller(Context context){
        // Never recreate, export or reset Android Keystore trust identities.
        // Keep a durable marker confirming ordinary in-place upgrade state.
        SharedPreferences preferences=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE);
        boolean allowed=DeskTouchAccessibilityService.allowed(context);
        if(!preferences.edit().putBoolean("userApprovedBeforeUpgrade",allowed)
            .putBoolean("accessibilityWasEnabled",
                systemAccessibilityEnabled(context)).commit())
            throw new IllegalStateException("Could not preserve permission update checkpoint");
    }

    static void onPackageReplaced(Context context){
        SharedPreferences preferences=context.getSharedPreferences(PREFS,Context.MODE_PRIVATE);
        boolean approved=DeskTouchAccessibilityService.allowed(context);
        boolean serviceEnabled=systemAccessibilityEnabled(context);
        preferences.edit().putBoolean("systemDisabledAfterUpgrade",
            needsUserReapproval(approved,serviceEnabled)).commit();
        if(!needsUserReapproval(approved,serviceEnabled))return;
        NotificationManager manager=context.getSystemService(NotificationManager.class);
        if(manager==null)return;
        manager.createNotificationChannel(new NotificationChannel(CHANNEL,
            "DeskTouch permissions",NotificationManager.IMPORTANCE_DEFAULT));
        Intent open=new Intent(context,MainActivity.class);
        open.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK |
            Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent tap=PendingIntent.getActivity(context,37381,open,
            PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        manager.notify(NOTICE_ID,new Notification.Builder(context,CHANNEL)
            .setSmallIcon(R.drawable.ic_desktouch)
            .setAutoCancel(true)
            .setContentTitle("DeskTouch Accessibility needs approval")
            .setContentText("Android disabled the service after updating; tap to restore it in Settings.")
            .setContentIntent(tap).build());
    }
}
