package com.desktouch.vision;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.graphics.Point;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.Surface;
import android.view.WindowManager;

/**
 * One physical-screen coordinate system for TLS topology, pointer overlay,
 * Accessibility gestures and node bounds, plus rich display/device telemetry.
 */
final class PhoneScreenGeometry {
    static final class Info {
        final int width;
        final int height;
        final int densityDpi;
        final int refreshHz;
        final int rotationDegrees;
        final String orientation;
        final String deviceName;
        final String osVersion;
        final String appVersion;

        Info(int width,int height,int densityDpi,int refreshHz,int rotationDegrees,
                String deviceName,String osVersion,String appVersion) {
            this.width=Math.max(1,width);
            this.height=Math.max(1,height);
            this.densityDpi=Math.max(0,densityDpi);
            this.refreshHz=Math.max(0,refreshHz);
            this.rotationDegrees=rotationDegrees;
            this.orientation=orientationName(this.width,this.height);
            this.deviceName=deviceName==null ? "" : deviceName;
            this.osVersion=osVersion==null ? "" : osVersion;
            this.appVersion=appVersion==null ? "" : appVersion;
        }
    }

    private PhoneScreenGeometry() {}

    static int rotationDegrees(int surfaceRotation) {
        switch(surfaceRotation) {
            case Surface.ROTATION_90: return 90;
            case Surface.ROTATION_180: return 180;
            case Surface.ROTATION_270: return 270;
            default: return 0;
        }
    }

    static String orientationName(int width,int height) {
        return width>height ? "Landscape" : "Portrait";
    }

    static String cleanDeviceName(String manufacturer,String model) {
        String mfg=manufacturer==null ? "" : manufacturer.trim();
        String mdl=model==null ? "" : model.trim();
        if(mfg.isEmpty()) return mdl.isEmpty() ? "Android phone" : mdl;
        if(mdl.isEmpty()) return mfg;
        if(mdl.regionMatches(true,0,mfg,0,mfg.length())) return mdl;
        return mfg+" "+mdl;
    }

    static Info readInfo(Context context) {
        Display display=null;
        DisplayManager dm=(DisplayManager)context.getSystemService(Context.DISPLAY_SERVICE);
        if(dm!=null) {
            try { display=dm.getDisplay(Display.DEFAULT_DISPLAY); }
            catch(RuntimeException ignored) {}
        }
        WindowManager manager=(WindowManager)context.getSystemService(Context.WINDOW_SERVICE);
        if(display==null && manager!=null) {
            try { display=manager.getDefaultDisplay(); }
            catch(RuntimeException ignored) {}
        }
        DisplayMetrics resMetrics=context.getResources().getDisplayMetrics();
        int width=0,height=0,dpi=resMetrics.densityDpi,refreshHz=0,rotation=0;
        if(display!=null) {
            try {
                DisplayMetrics real=new DisplayMetrics();
                display.getRealMetrics(real);
                if(real.widthPixels>0 && real.heightPixels>0) {
                    width=real.widthPixels;
                    height=real.heightPixels;
                }
                if(real.densityDpi>0) dpi=real.densityDpi;
            } catch(RuntimeException ignored) {}
            if(width<=0 || height<=0) {
                try {
                    Point actual=new Point();
                    display.getRealSize(actual);
                    if(actual.x>0 && actual.y>0) { width=actual.x; height=actual.y; }
                } catch(RuntimeException ignored) {}
            }
            try { rotation=rotationDegrees(display.getRotation()); }
            catch(RuntimeException ignored) {}
            try {
                float hz=display.getRefreshRate();
                if(hz>=20f && hz<=360f) refreshHz=Math.round(hz);
            } catch(RuntimeException ignored) {}
        }
        if((width<=0 || height<=0) && manager!=null && Build.VERSION.SDK_INT>=30) {
            try {
                Rect bounds=manager.getMaximumWindowMetrics().getBounds();
                if(bounds.width()>0 && bounds.height()>0) {
                    width=bounds.width();
                    height=bounds.height();
                }
            } catch(RuntimeException ignored) {}
        }
        if(width<=0 || height<=0) {
            width=Math.max(1,resMetrics.widthPixels);
            height=Math.max(1,resMetrics.heightPixels);
        }
        String release=Build.VERSION.RELEASE==null ? "" : Build.VERSION.RELEASE.trim();
        String osVersion=release.isEmpty() ? "Android" : "Android "+release;
        String appVersion="";
        try {
            PackageInfo pkg=context.getPackageManager()
                .getPackageInfo(context.getPackageName(),0);
            if(pkg!=null && pkg.versionName!=null) appVersion=pkg.versionName;
        } catch(Exception ignored) {}
        return new Info(width,height,dpi,refreshHz,rotation,
            cleanDeviceName(Build.MANUFACTURER,Build.MODEL),osVersion,appVersion);
    }

    static Point read(Context context) {
        Info info=readInfo(context);
        return new Point(info.width,info.height);
    }
}
