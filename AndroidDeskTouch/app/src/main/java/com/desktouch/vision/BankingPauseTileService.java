package com.desktouch.vision;

import android.app.PendingIntent;
import android.content.Intent;
import android.os.Build;
import android.provider.Settings;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;
import android.widget.Toast;

/**
 * One-tap Quick Settings tile so the user can pause Accessibility right before
 * opening banking or UPI apps, then jump directly back to restore it later.
 */
public final class BankingPauseTileService extends TileService {
    @Override public void onStartListening() {
        super.onStartListening();
        updateTile();
    }

    @Override public void onClick() {
        super.onClick();
        if (DeskTouchAccessibilityService.ready(this)) {
            DeskTouchAccessibilityService.pauseForBanking(this);
            updateTile();
            Toast.makeText(this, "DeskTouch paused for banking", Toast.LENGTH_SHORT).show();
            return;
        }
        DeskTouchAccessibilityService.setAllowed(this, true);
        Intent intent = new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
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
        boolean active = DeskTouchAccessibilityService.ready(this);
        tile.setState(active ? Tile.STATE_ACTIVE : Tile.STATE_INACTIVE);
        tile.setLabel(active ? "DeskTouch On" : "Banking Pause");
        if (Build.VERSION.SDK_INT >= 29) {
            tile.setSubtitle(active ? "Tap to pause" : "Tap to resume");
        }
        tile.updateTile();
    }
}
