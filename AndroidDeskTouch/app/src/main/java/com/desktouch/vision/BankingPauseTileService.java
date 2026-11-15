package com.desktouch.vision;

import android.app.PendingIntent;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;
import android.widget.Toast;

/**
 * One-tap Quick Settings tile so the user can turn off Accessibility right before
 * opening banking or UPI apps, then jump directly back to restore it later.
 */
public final class BankingPauseTileService extends TileService {
    @Override public void onStartListening() {
        super.onStartListening();
        updateTile();
    }

    @Override public void onClick() {
        super.onClick();
        if (PermissionContinuity.systemAccessibilityEnabled(this)) {
            boolean selfDisabled = DeskTouchAccessibilityService.pauseForBanking(
                this, "Turned off via Quick Settings tile");
            updateTile();
            if (selfDisabled) {
                Toast.makeText(
                    this,
                    "DeskTouch Accessibility turned off for payment apps",
                    Toast.LENGTH_SHORT
                ).show();
                return;
            }
            Toast.makeText(
                this,
                "Open Downloaded apps -> DeskTouch Windows control -> Turn OFF",
                Toast.LENGTH_LONG
            ).show();
            launchSettingsIntent(DeskTouchAccessibilityService.accessibilitySettingsIntent(this));
            return;
        }
        DeskTouchAccessibilityService.setAllowed(this, true);
        DeskTouchRuntime.accessibilityPermissionChanged(this);
        updateTile();
        Toast.makeText(
            this,
            "Open Downloaded apps -> DeskTouch Windows control -> Turn ON",
            Toast.LENGTH_LONG
        ).show();
        launchSettingsIntent(DeskTouchAccessibilityService.accessibilitySettingsIntent(this));
    }

    private void launchSettingsIntent(Intent intent) {
        if (Build.VERSION.SDK_INT >= 34) {
            PendingIntent pending = PendingIntent.getActivity(this, 37382, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            startActivityAndCollapse(pending);
        } else {
            startActivityAndCollapse(intent);
        }
    }

    private void updateTile() {
        Tile tile = getQsTile();
        if (tile == null) return;
        boolean active = PermissionContinuity.systemAccessibilityEnabled(this);
        tile.setState(active ? Tile.STATE_ACTIVE : Tile.STATE_INACTIVE);
        tile.setLabel(active ? "DeskTouch On" : "Pay Mode Safe");
        if (Build.VERSION.SDK_INT >= 29) {
            tile.setSubtitle(active ? "Tap to turn off" : "Tap to resume");
        }
        tile.updateTile();
    }

    static void requestRefresh(Context context) {
        if (context == null) return;
        try {
            requestListeningState(
                context.getApplicationContext(),
                new ComponentName(context.getApplicationContext(), BankingPauseTileService.class));
        } catch (RuntimeException ignored) {}
    }
}
