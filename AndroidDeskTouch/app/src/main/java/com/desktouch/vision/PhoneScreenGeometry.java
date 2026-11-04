package com.desktouch.vision;

import android.content.Context;
import android.graphics.Point;
import android.graphics.Rect;
import android.os.Build;
import android.view.Display;
import android.view.WindowManager;

/**
 * One physical-screen coordinate system for TLS topology, pointer overlay,
 * Accessibility gestures and node bounds. Resource DisplayMetrics may exclude
 * the status/navigation bars and vary between an Activity and foreground Service.
 */
final class PhoneScreenGeometry {
    private PhoneScreenGeometry() {}
    static Point read(Context context) {
        WindowManager manager=(WindowManager)context.getSystemService(Context.WINDOW_SERVICE);
        if(manager!=null) {
            try {
                Display display=manager.getDefaultDisplay();
                if(display!=null) {
                    Point actual=new Point();
                    display.getRealSize(actual);
                    if(actual.x>0&&actual.y>0)return actual;
                }
            }catch(RuntimeException ignored){}
            if(Build.VERSION.SDK_INT>=30) {
                try {
                    Rect bounds=manager.getMaximumWindowMetrics().getBounds();
                    if(bounds.width()>0&&bounds.height()>0)
                        return new Point(bounds.width(),bounds.height());
                }catch(RuntimeException ignored){}
            }
        }
        android.util.DisplayMetrics metrics=context.getResources().getDisplayMetrics();
        return new Point(Math.max(1,metrics.widthPixels),Math.max(1,metrics.heightPixels));
    }
}
